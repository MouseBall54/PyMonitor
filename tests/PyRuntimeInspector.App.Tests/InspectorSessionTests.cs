using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class InspectorSessionTests
{
    [Fact]
    public async Task DetachAbortsTransportWhenCanceledRequestNeverReceivesResponse()
    {
        var port = ReservePort();
        await using var session = new InspectorSession();
        using var target = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stalledRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attachTask = session.AttachAsync(port, "test-token", expectedPid: null, timeout.Token);
        await target.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var targetTask = Task.Run(async () =>
        {
            var stream = target.GetStream();
            var hello = await ProtocolFraming.ReadAsync(stream, timeout.Token);
            await WriteSuccessAsync(stream, hello, CompatibleHelloResult(), timeout.Token);
            var runtime = await ProtocolFraming.ReadAsync(stream, timeout.Token);
            await WriteSuccessAsync(stream, runtime, new JsonObject { ["pid"] = Environment.ProcessId }, timeout.Token);

            var stalled = await ProtocolFraming.ReadAsync(stream, timeout.Token);
            Assert.Equal("never-responds", stalled.Header["method"]!.GetValue<string>());
            stalledRequestReceived.SetResult();
            var buffer = new byte[1];
            return await stream.ReadAsync(buffer, timeout.Token);
        });

        await attachTask;
        using var cancellation = new CancellationTokenSource();
        var stalledRequest = session.RequestAsync("never-responds", cancellationToken: cancellation.Token);
        await stalledRequestReceived.Task.WaitAsync(timeout.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stalledRequest);

        var stopwatch = Stopwatch.StartNew();
        await session.DetachAsync().WaitAsync(TimeSpan.FromSeconds(3));
        stopwatch.Stop();

        Assert.False(session.IsConnected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2.5), $"Detach took {stopwatch.Elapsed}.");
        Assert.Equal(0, await targetTask.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task RequestTimeoutDisconnectsUnresponsiveTarget()
    {
        var port = ReservePort();
        // Leave handshake headroom on loaded CI runners; the stalled method below still
        // proves that the configured request timeout disconnects an unresponsive target.
        await using var session = new InspectorSession(TimeSpan.FromSeconds(2));
        using var target = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? disconnectedMessage = null;
        session.Disconnected += (_, message) => disconnectedMessage = message;

        var attachTask = session.AttachAsync(port, "test-token", expectedPid: null, timeout.Token);
        await target.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var targetTask = Task.Run(async () =>
        {
            var stream = target.GetStream();
            var hello = await ProtocolFraming.ReadAsync(stream, timeout.Token);
            await WriteSuccessAsync(stream, hello, CompatibleHelloResult(), timeout.Token);
            var runtime = await ProtocolFraming.ReadAsync(stream, timeout.Token);
            await WriteSuccessAsync(stream, runtime, new JsonObject { ["pid"] = Environment.ProcessId }, timeout.Token);

            var stalled = await ProtocolFraming.ReadAsync(stream, timeout.Token);
            Assert.Equal("times-out", stalled.Header["method"]!.GetValue<string>());
            requestReceived.SetResult();
            var buffer = new byte[1];
            return await stream.ReadAsync(buffer, timeout.Token);
        });

        await attachTask;
        var request = session.RequestAsync("times-out");
        await requestReceived.Task.WaitAsync(timeout.Token);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            async () => await request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("times-out", exception.Message);
        Assert.False(session.IsConnected);
        Assert.Equal("Target request timed out.", disconnectedMessage);
        Assert.Equal(0, await targetTask.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task AttachRejectsAgentWithoutCurrentBootstrapCapabilityBeforeRuntimeRequest()
    {
        var port = ReservePort();
        await using var session = new InspectorSession();
        using var target = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var attachTask = session.AttachAsync(port, "test-token", expectedPid: null, timeout.Token);
        await target.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var targetTask = Task.Run(async () =>
        {
            var stream = target.GetStream();
            var hello = await ProtocolFraming.ReadAsync(stream, timeout.Token);
            await WriteSuccessAsync(stream, hello, new JsonObject
            {
                ["agentVersion"] = ReplBootstrap.ExpectedAgentVersion,
            }, timeout.Token);
            var buffer = new byte[1];
            return await stream.ReadAsync(buffer, timeout.Token);
        });

        var exception = await Assert.ThrowsAsync<RemoteInspectionException>(async () => await attachTask);

        Assert.Equal("INCOMPATIBLE_AGENT", exception.Code);
        Assert.Contains("bootstrap ABI unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.IsConnected);
        Assert.Equal(0, await targetTask.WaitAsync(timeout.Token));
    }

    private static Task WriteSuccessAsync(
        Stream stream,
        ProtocolFrame request,
        JsonObject result,
        CancellationToken cancellationToken) =>
        ProtocolFraming.WriteAsync(stream, new JsonObject
        {
            ["requestId"] = request.Header["requestId"]!.GetValue<string>(),
            ["ok"] = true,
            ["result"] = result,
        }, cancellationToken: cancellationToken);

    private static JsonObject CompatibleHelloResult() => new()
    {
        ["agentVersion"] = ReplBootstrap.ExpectedAgentVersion,
        ["bootstrapAbi"] = ReplBootstrap.ExpectedBootstrapAbi,
    };

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
