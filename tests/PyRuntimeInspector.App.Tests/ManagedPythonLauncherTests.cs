using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class ManagedPythonLauncherTests
{
    [Fact]
    public void WindowsCommandLinePreservesQuotedAndEmptyArguments()
    {
        var arguments = WindowsCommandLine.ParseArguments("one \"two words\" \"\" escaped\\path");
        Assert.Equal(new[] { "one", "two words", "", "escaped\\path" }, arguments);
    }

    [Fact]
    public async Task ManagedLaunchPreservesProcessContractAndDetachDoesNotStopTarget()
    {
        var root = FindRepositoryRoot();
        var python = ResolvePythonExecutable();
        var port = ReservePort();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var session = new InspectorSession();
        await using var launcher = new ManagedPythonLauncher();
        var stdout = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ConcurrentQueue<ProcessOutputEventArgs>();
        launcher.OutputReceived += (_, line) =>
        {
            output.Enqueue(line);
            if (line.Kind == ProcessOutputKind.StandardOutput && line.Text == "managed-stdout") stdout.TrySetResult(line.Text);
            if (line.Kind == ProcessOutputKind.StandardError && line.Text == "managed-stderr") stderr.TrySetResult(line.Text);
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var attachTask = session.AttachAsync(port, token, expectedPid: null, timeout.Token);
        var script = Path.Combine(root, "samples", "target_managed.py");
        var handle = await launcher.StartAsync(new ManagedLaunchOptions(
            python,
            script,
            root,
            ["first", "two words"],
            new Dictionary<string, string>
            {
                ["PYMONITOR_TEST_ENV"] = "phase-two",
                ["PYMONITOR_TEST_WAIT"] = "3.0",
                ["PYMONITOR_TEST_EXIT_CODE"] = "7",
            },
            Path.Combine(root, "agent"),
            port,
            token), timeout.Token);

        var runtime = await attachTask;
        Assert.Equal(handle.ProcessId, runtime["pid"]!.GetValue<int>());
        Assert.Equal("managed", runtime["attachMode"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(python), Path.GetFullPath(runtime["executable"]!.GetValue<string>()), ignoreCase: true);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime["currentWorkingDirectory"]!.GetValue<string>())),
            ignoreCase: true);
        Assert.Equal(new[] { Path.GetFullPath(script), "first", "two words" }, runtime["argv"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("managed-stdout", await stdout.Task.WaitAsync(timeout.Token));
        Assert.Equal("managed-stderr", await stderr.Task.WaitAsync(timeout.Token));

        var frames = Result(await session.RequestAsync("frames.list", cancellationToken: timeout.Token))["items"]!.AsArray();
        var moduleFrame = frames.First(node => node!["filename"]!.GetValue<string>().EndsWith("target_managed.py", StringComparison.OrdinalIgnoreCase))!;
        var globals = Result(await session.RequestAsync("scopes.list", new JsonObject
        {
            ["frameHandle"] = moduleFrame["frameHandle"]!.GetValue<string>(),
            ["scopeType"] = "globals",
            ["pageSize"] = 200,
        }, timeout.Token))["items"]!.AsArray();
        Assert.Equal("'phase-two'", FindScopeValue(globals, "MANAGED_ENV")["safePreview"]!.GetValue<string>());
        Assert.Contains("PyMonitor", FindScopeValue(globals, "MANAGED_CWD")["safePreview"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("False", FindScopeValue(globals, "MANAGED_DONT_WRITE_BYTECODE")["safePreview"]!.GetValue<string>());

        await session.DetachAsync();
        await Task.Delay(100, timeout.Token);
        Assert.False(handle.Completion.IsCompleted);
        Assert.Equal(7, await handle.Completion.WaitAsync(timeout.Token));
        Assert.Contains(output, line => line.Kind == ProcessOutputKind.StandardOutput);
        Assert.Contains(output, line => line.Kind == ProcessOutputKind.StandardError);
    }

    [Fact]
    public async Task ManagedLaunchDoesNotWriteBytecodeIntoBundledAgent()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "PyRuntimeInspector.Tests", Guid.NewGuid().ToString("N"));
        var agentDirectory = CopyAgentSources(Path.Combine(root, "agent"), temporaryRoot);
        try
        {
            var port = ReservePort();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using var session = new InspectorSession();
            await using var launcher = new ManagedPythonLauncher();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var attachTask = session.AttachAsync(port, token, expectedPid: null, timeout.Token);
            var handle = await launcher.StartAsync(new ManagedLaunchOptions(
                ResolvePythonExecutable(),
                Path.Combine(root, "samples", "target_managed.py"),
                root,
                [],
                new Dictionary<string, string>
                {
                    ["PYMONITOR_TEST_WAIT"] = "1.0",
                    ["PYMONITOR_TEST_EXIT_CODE"] = "0",
                },
                agentDirectory,
                port,
                token), timeout.Token);

            await attachTask;
            await session.DetachAsync();
            Assert.Equal(0, await handle.Completion.WaitAsync(timeout.Token));
            Assert.Empty(Directory.EnumerateFiles(agentDirectory, "*.pyc", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(agentDirectory, "__pycache__", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ManagedLaunchUsesSelectedVirtualEnvironmentInterpreter()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "PyRuntimeInspector.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var environmentDirectory = Path.Combine(temporaryRoot, "venv");
            await CreateVirtualEnvironmentAsync(ResolvePythonExecutable(), environmentDirectory);
            var environmentPython = Path.Combine(environmentDirectory, "Scripts", "python.exe");
            Assert.True(File.Exists(environmentPython));

            var port = ReservePort();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using var session = new InspectorSession();
            await using var launcher = new ManagedPythonLauncher();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var attachTask = session.AttachAsync(port, token, expectedPid: null, timeout.Token);
            var handle = await launcher.StartAsync(new ManagedLaunchOptions(
                environmentPython,
                Path.Combine(root, "samples", "target_managed.py"),
                root,
                [],
                new Dictionary<string, string>
                {
                    ["PYMONITOR_TEST_WAIT"] = "0.5",
                    ["PYMONITOR_TEST_EXIT_CODE"] = "0",
                },
                Path.Combine(root, "agent"),
                port,
                token), timeout.Token);

            var runtime = await attachTask;
            Assert.Equal(Path.GetFullPath(environmentPython), Path.GetFullPath(runtime["executable"]!.GetValue<string>()), ignoreCase: true);
            Assert.True(runtime["isVirtualEnvironment"]!.GetValue<bool>());
            await session.DetachAsync();
            Assert.Equal(0, await handle.Completion.WaitAsync(timeout.Token));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static JsonObject Result(ProtocolFrame frame) => frame.Header["result"]!.AsObject();

    private static JsonObject FindScopeValue(JsonArray scope, string name) =>
        scope.Single(node => node!["name"]!.GetValue<string>() == name)!["value"]!.AsObject();

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolvePythonExecutable()
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("PYTHON_EXECUTABLE") ?? "python",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; print(sys.executable)");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not resolve Python executable.");
        var path = process.StandardOutput.ReadLine();
        process.WaitForExit();
        return path ?? throw new InvalidOperationException("Python did not report sys.executable.");
    }

    private static async Task CreateVirtualEnvironmentAsync(string python, string directory)
    {
        var start = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-m");
        start.ArgumentList.Add("venv");
        start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create test virtual environment.");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"venv creation failed with code {process.ExitCode}: {error}");
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "samples", "target_managed.py")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
