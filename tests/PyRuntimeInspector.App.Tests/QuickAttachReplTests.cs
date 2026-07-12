using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.App.ViewModels;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class QuickAttachReplTests
{
    [Fact]
    public async Task QuickAttachViewModelIsOneClickForPython314()
    {
        var python = Environment.GetEnvironmentVariable("PYTHON314_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
            return;

        var process = StartPython314Target(python);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(150);
            var selected = new ProcessDiscovery().GetPythonProcesses().Single(item => item.Id == process.Id);
            Assert.Equal(14, selected.PythonVersion?.Minor);
            var clipboard = new CapturingClipboard();
            await using var viewModel = new MainViewModel(
                new InspectorSession(),
                new FixedProcessDiscovery(selected),
                clipboardService: clipboard)
            {
                PortText = ReservePort().ToString(),
                SelectedProcess = selected,
            };

            await viewModel.QuickAttachCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(viewModel.IsConnected);
            Assert.Equal("Connected (live attach)", viewModel.Status);
            Assert.Null(clipboard.Text);
            Assert.Equal("Modules / __main__", viewModel.Breadcrumb);
            Assert.Contains(viewModel.Variables, row => row.Name == "example_value" && row.SafePreview == "1235");
            await viewModel.DetachCommand.ExecuteAsync();
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task QuickAttachViewModelNeedsOnlyOnePasteForPre314Repl()
    {
        var root = FindRepositoryRoot();
        var process = StartRepl(root);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync("example_value = 1235");
            await process.StandardInput.WriteLineAsync("edd = 121");
            await process.StandardInput.FlushAsync();

            var selected = new ProcessDiscovery().GetPythonProcesses().Single(item => item.Id == process.Id);
            Assert.Equal(3, selected.PythonVersion?.Major);
            Assert.InRange(selected.PythonVersion!.Minor, 10, 13);
            Assert.True(File.Exists(selected.ExecutablePath));
            var clipboard = new CapturingClipboard();
            await using var viewModel = new MainViewModel(
                new InspectorSession(),
                new FixedProcessDiscovery(selected),
                clipboardService: clipboard)
            {
                PortText = ReservePort().ToString(),
                SelectedProcess = selected,
            };

            var attach = viewModel.QuickAttachCommand.ExecuteAsync();
            await EventuallyAsync(() => clipboard.Text is not null);
            await process.StandardInput.WriteLineAsync(clipboard.Text);
            await process.StandardInput.FlushAsync();
            await attach.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(viewModel.IsConnected);
            Assert.Equal("Connected · showing __main__ globals", viewModel.Status);
            Assert.Equal("Modules / __main__", viewModel.Breadcrumb);
            Assert.Contains(viewModel.Variables, row => row.Name == "example_value" && row.SafePreview == "1235");
            Assert.Contains(viewModel.Variables, row => row.Name == "edd" && row.SafePreview == "121");

            await viewModel.DetachCommand.ExecuteAsync();
            Assert.False(process.HasExited);
            await process.StandardInput.WriteLineAsync("exit()");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task IdleInteractiveReplGlobalsAreAvailableWithoutAUserFrame()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "PyRuntimeInspector.Tests", Guid.NewGuid().ToString("N"));
        var agentDirectory = CopyAgentSources(Path.Combine(root, "agent"), temporaryRoot);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var process = StartRepl(root);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        TcpClient? socket = null;
        try
        {
            await process.StandardInput.WriteLineAsync("example_value = 1235");
            await process.StandardInput.WriteLineAsync("edd = 121");
            await process.StandardInput.WriteLineAsync(ReplBootstrap.Build(agentDirectory, port, token));
            await process.StandardInput.WriteLineAsync("bootstrap_dont_write_bytecode = sys.dont_write_bytecode");
            await process.StandardInput.FlushAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            socket = await listener.AcceptTcpClientAsync(timeout.Token);
            var client = new InspectorClient(socket.GetStream());
            await client.HelloAsync(token, timeout.Token);
            var runtime = Result(await client.RequestAsync("runtime.getInfo", cancellationToken: timeout.Token));
            Assert.Equal(process.Id, runtime["pid"]!.GetValue<int>());

            await Task.Delay(200, timeout.Token);
            var frames = Result(await client.RequestAsync("frames.list", cancellationToken: timeout.Token));
            Assert.Empty(frames["items"]!.AsArray());

            var scope = Result(await client.RequestAsync("modules.listNamespace", new JsonObject
            {
                ["moduleName"] = "__main__",
                ["pageSize"] = 200,
            }, timeout.Token));
            var items = scope["items"]!.AsArray();
            Assert.Equal("1235", Find(items, "example_value")["safePreview"]!.GetValue<string>());
            Assert.Equal("121", Find(items, "edd")["safePreview"]!.GetValue<string>());
            Assert.Equal("False", Find(items, "bootstrap_dont_write_bytecode")["safePreview"]!.GetValue<string>());

            await client.RequestAsync("session.detach", cancellationToken: timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(process.HasExited);
            await process.StandardInput.WriteLineAsync("exit()");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(Directory.EnumerateFiles(agentDirectory, "*.pyc", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(agentDirectory, "__pycache__", SearchOption.AllDirectories));
        }
        finally
        {
            socket?.Dispose();
            listener.Stop();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            await Task.WhenAll(stdout, stderr);
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static JsonObject Result(ProtocolFrame frame) => frame.Header["result"]!.AsObject();

    private static JsonObject Find(JsonArray items, string name) =>
        items.Single(item => item!["name"]!.GetValue<string>() == name)!["value"]!.AsObject();

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private static string CopyAgentSources(string sourceRoot, string temporaryRoot)
    {
        var sourcePackage = Path.Combine(sourceRoot, "pyruntime_inspector_agent");
        var agentRoot = Path.Combine(temporaryRoot, "agent");
        var destinationPackage = Path.Combine(agentRoot, "pyruntime_inspector_agent");
        Directory.CreateDirectory(destinationPackage);
        foreach (var source in Directory.EnumerateFiles(sourcePackage, "*.py"))
            File.Copy(source, Path.Combine(destinationPackage, Path.GetFileName(source)));
        return agentRoot;
    }

    private static Process StartRepl(string root)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("PYTHON_EXECUTABLE") ?? "python",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("pass");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the interactive Python test target.");
    }

    private static Process StartPython314Target(string python)
    {
        var start = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import time; example_value=1235; exec(\"while True:\\n time.sleep(0.01)\")");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the CPython 3.14 target.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "agent")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository or packaged Agent root.");
    }

    private sealed class CapturingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
    }

    private sealed class FixedProcessDiscovery(ProcessItem process) : IProcessDiscovery
    {
        public IReadOnlyList<ProcessItem> GetPythonProcesses() => [process];
        public ProcessMemoryInfo? GetMemoryInfo(int pid) => null;
    }
}
