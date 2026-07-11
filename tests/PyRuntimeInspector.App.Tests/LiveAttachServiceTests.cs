using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PyRuntimeInspector.App.Services;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class LiveAttachServiceTests
{
    [Fact]
    public async Task Python314LiveAttachConnectsAndDetachLeavesTargetRunning()
    {
        var python = Environment.GetEnvironmentVariable("PYTHON314_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("import time; example_value=1235; print('ready', flush=True); exec(\"while True:\\n time.sleep(0.05)\")");
        using var target = Process.Start(startInfo) ?? throw new InvalidOperationException("Python target did not start.");
        try
        {
            Assert.Equal("ready", await target.StandardOutput.ReadLineAsync());
            var port = GetAvailablePort();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var session = new InspectorSession();
            var connection = session.AttachAsync(port, token, target.Id, timeout.Token);

            var service = new LiveAttachService();
            await using var lease = await service.StartAsync(new LiveAttachOptions(
                target.Id,
                python,
                FindAgentDirectory(),
                port,
                token), timeout.Token);
            var runtime = await connection;

            Assert.Equal(target.Id, runtime["pid"]!.GetValue<int>());
            Assert.Equal("live", runtime["attachMode"]!.GetValue<string>());
            var mainScope = await session.RequestAsync("modules.listNamespace", new JsonObject
            {
                ["moduleName"] = "__main__",
                ["pageSize"] = 1000,
            }, timeout.Token);
            var items = mainScope.Header["result"]!["items"]!.AsArray();
            var example = items.Single(item => item!["name"]!.GetValue<string>() == "example_value")!;
            Assert.Equal("1235", example["value"]!["safePreview"]!.GetValue<string>());
            await session.DetachAsync();
            await Task.Delay(100);
            Assert.False(target.HasExited);
        }
        finally
        {
            if (!target.HasExited)
                target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Python314DisabledRemoteDebugReturnsStructuredFailure()
    {
        var python = Environment.GetEnvironmentVariable("PYTHON314_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("disable_remote_debug");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("import time; exec(\"while True:\\n time.sleep(0.05)\")");
        startInfo.Environment["PYTHON_DISABLE_REMOTE_DEBUG"] = "1";
        using var target = Process.Start(startInfo) ?? throw new InvalidOperationException("Python target did not start.");
        try
        {
            await Task.Delay(100);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var service = new LiveAttachService();
            var exception = await Assert.ThrowsAsync<LiveAttachException>(() => service.StartAsync(new LiveAttachOptions(
                target.Id,
                python,
                FindAgentDirectory(),
                49152,
                token)));
            Assert.Equal("REMOTE_DEBUG_DISABLED", exception.Code);
            Assert.False(target.HasExited);
        }
        finally
        {
            if (!target.HasExited)
                target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task MissingTargetReturnsStructuredErrorBeforeCreatingBootstrap()
    {
        var service = new LiveAttachService();
        var exception = await Assert.ThrowsAsync<LiveAttachException>(() => service.StartAsync(new LiveAttachOptions(
            int.MaxValue,
            Environment.ProcessPath!,
            FindAgentDirectory(),
            49152,
            new string('A', 64))));

        Assert.Equal("TARGET_NOT_FOUND", exception.Code);
    }

    private static string FindAgentDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "agent");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository agent directory was not found.");
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
