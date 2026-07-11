using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.IntegrationTests;

public sealed class CooperativeAgentTests
{
    [Fact]
    public async Task CSharpControllerInspectsRealPythonTargetAndDetachLeavesItRunning()
    {
        await using var target = await TargetProcess.StartAsync();
        var client = target.Client;
        await client.HelloAsync(target.Token);

        var runtime = Result(await client.RequestAsync("runtime.getInfo"));
        var versionInfo = runtime["versionInfo"]!.AsArray();
        Assert.Equal(3, versionInfo[0]!.GetValue<int>());
        Assert.InRange(versionInfo[1]!.GetValue<int>(), 10, 14);
        Assert.Equal("cpython", runtime["implementationName"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(runtime["executable"]!.GetValue<string>()));

        var frames = Result(await client.RequestAsync("frames.list"))["items"]!.AsArray();
        var workerFrame = frames.Single(node => node!["functionName"]!.GetValue<string>() == "worker")!;
        var frameHandle = workerFrame["frameHandle"]!.GetValue<string>();
        var locals = await ScopeAsync(client, frameHandle, "locals");
        Assert.Equal("'frame-local'", FindValue(locals, "local_value")["safePreview"]!.GetValue<string>());

        var globals = await ScopeAsync(client, frameHandle, "globals");
        Assert.Equal("123", FindValue(globals, "GLOBAL_NUMBER")["safePreview"]!.GetValue<string>());
        var detector = FindValue(globals, "detector");
        var classDescription = Result(await client.RequestAsync("classes.describe", new JsonObject { ["handleId"] = detector["handleId"]!.GetValue<string>() }));
        var members = classDescription["members"]!.AsArray();
        Assert.Equal("property", members.Single(node => node!["name"]!.GetValue<string>() == "dangerous_property")!["kind"]!.GetValue<string>());
        Assert.Equal("(self, image: numpy.ndarray) -> list", members.Single(node => node!["name"]!.GetValue<string>() == "predict")!["signature"]!.GetValue<string>());

        var image = FindValue(globals, "GLOBAL_IMAGE");
        var handle = image["handleId"]!.GetValue<string>();
        var description = Result(await client.RequestAsync("arrays.describe", new JsonObject { ["handleId"] = handle }));
        Assert.Equal(new[] { 480, 640, 3 }, description["shape"]!.AsArray().Select(node => node!.GetValue<int>()));
        Assert.Equal(921600, description["nbytes"]!.GetValue<int>());
        var pixel = Result(await client.RequestAsync("arrays.pixel", new JsonObject { ["handleId"] = handle, ["coordinates"] = new JsonArray(100, 200), ["layout"] = "HWC" }));
        Assert.Equal(new[] { 10, 20, 30 }, pixel["value"]!.AsArray().Select(node => node!.GetValue<int>()));
        var preview = await client.RequestAsync("arrays.preview", new JsonObject { ["handleId"] = handle, ["maxWidth"] = 64, ["maxHeight"] = 48, ["layout"] = "HWC" });
        Assert.Equal("RGB24", Result(preview)["pixelFormat"]!.GetValue<string>());
        Assert.Equal(64 * 48 * 3, preview.Binary.Length);

        await client.RequestAsync("session.detach");
        await Task.Delay(200);
        Assert.False(target.Process.HasExited);
    }

    [Fact]
    public async Task InvalidTokenCannotInspectTarget()
    {
        await using var target = await TargetProcess.StartAsync();
        var exception = await Assert.ThrowsAsync<RemoteInspectionException>(() => target.Client.HelloAsync(new string('0', 64)));
        Assert.Equal("AUTH_FAILED", exception.Code);
        await Task.Delay(100);
        Assert.False(target.Process.HasExited);
    }

    private static JsonObject Result(ProtocolFrame frame) => frame.Header["result"]!.AsObject();

    private static async Task<JsonArray> ScopeAsync(InspectorClient client, string frameHandle, string scopeType)
    {
        var response = await client.RequestAsync("scopes.list", new JsonObject
        {
            ["frameHandle"] = frameHandle,
            ["scopeType"] = scopeType,
            ["pageSize"] = 1000,
        });
        return Result(response)["items"]!.AsArray();
    }

    private static JsonObject FindValue(JsonArray scope, string name) =>
        scope.Single(node => node!["name"]!.GetValue<string>() == name)!["value"]!.AsObject();

    private sealed class TargetProcess : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _socket;
        public Process Process { get; }
        public string Token { get; }
        public InspectorClient Client { get; }

        private TargetProcess(TcpListener listener, TcpClient socket, Process process, string token)
        {
            _listener = listener;
            _socket = socket;
            Process = process;
            Token = token;
            Client = new InspectorClient(socket.GetStream());
        }

        public static async Task<TargetProcess> StartAsync()
        {
            var root = FindRepositoryRoot();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("PYTHON_EXECUTABLE") ?? "python",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = root,
            };
            start.ArgumentList.Add(Path.Combine(root, "samples", "target_sample.py"));
            start.Environment["PY_INSPECTOR_HOST"] = "127.0.0.1";
            start.Environment["PY_INSPECTOR_PORT"] = port.ToString();
            start.Environment["PY_INSPECTOR_TOKEN"] = token;
            var agentPath = Path.Combine(root, "agent");
            var priorPythonPath = start.Environment.TryGetValue("PYTHONPATH", out var existing) ? existing : null;
            start.Environment["PYTHONPATH"] = string.IsNullOrEmpty(priorPythonPath) ? agentPath : agentPath + Path.PathSeparator + priorPythonPath;
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python target.");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                return new TargetProcess(listener, socket, process, token);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                var stderr = await process.StandardError.ReadToEndAsync();
                process.Dispose();
                listener.Stop();
                throw new InvalidOperationException($"Python agent did not connect. stderr: {stderr}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _socket.Dispose();
            _listener.Stop();
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
            Process.Dispose();
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "samples", "target_sample.py")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }
    }
}
