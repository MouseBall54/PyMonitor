using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.Protocol.Tests;

public sealed class ProtocolFramingTests
{
    [Fact]
    public async Task RoundTripPreservesHeaderAndBinary()
    {
        await using var stream = new MemoryStream();
        await ProtocolFraming.WriteAsync(stream, new JsonObject { ["requestId"] = "one" }, new byte[] { 1, 2, 3 });
        stream.Position = 0;
        var frame = await ProtocolFraming.ReadAsync(stream);
        Assert.Equal("one", frame.Header["requestId"]!.GetValue<string>());
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Binary);
        Assert.Equal(3, frame.Header["binaryLength"]!.GetValue<int>());
    }

    [Fact]
    public async Task OversizedHeaderIsRejectedBeforeAllocation()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, ProtocolFraming.MaxHeaderBytes + 1);
        await using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<ProtocolException>(() => ProtocolFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task CancellationAfterSendDrainsResponseBeforeNextRequest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverSocket = await acceptTask;
        listener.Stop();

        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var stream = serverSocket.GetStream();
            var first = await ProtocolFraming.ReadAsync(stream);
            firstReceived.SetResult();
            await releaseFirstResponse.Task;
            await WriteSuccessAsync(stream, first, "first-result");

            var second = await ProtocolFraming.ReadAsync(stream);
            await WriteSuccessAsync(stream, second, "second-result");
        });

        var inspector = new InspectorClient(clientSocket.GetStream());
        using var cancellation = new CancellationTokenSource();
        var canceledRequest = inspector.RequestAsync("first", cancellationToken: cancellation.Token);
        await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledRequest);

        var secondRequest = inspector.RequestAsync("second");
        Assert.False(secondRequest.IsCompleted);

        releaseFirstResponse.SetResult();
        var secondResponse = await secondRequest;

        Assert.Equal("second-result", secondResponse.Header["result"]!["value"]!.GetValue<string>());
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AbortUnblocksCanceledRequestAndQueuedDetachWhenTargetNeverResponds()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverSocket = await acceptTask;
        listener.Stop();

        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var stream = serverSocket.GetStream();
            var request = await ProtocolFraming.ReadAsync(stream);
            Assert.Equal("never-responds", request.Header["method"]!.GetValue<string>());
            requestReceived.SetResult();
            var buffer = new byte[1];
            return await stream.ReadAsync(buffer);
        });

        var inspector = new InspectorClient(clientSocket.GetStream());
        using var cancellation = new CancellationTokenSource();
        var stalledRequest = inspector.RequestAsync("never-responds", cancellationToken: cancellation.Token);
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stalledRequest);
        var queuedDetach = inspector.RequestAsync("session.detach");
        Assert.False(queuedDetach.IsCompleted);

        inspector.Abort();
        inspector.Abort();

        var detachException = await Record.ExceptionAsync(
            async () => await queuedDetach.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<ObjectDisposedException>(detachException);
        Assert.True(inspector.IsAborted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inspector.RequestAsync("after-abort"));
        Assert.Equal(0, await serverTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequestTimeoutAbortsUnresponsiveTransportAndReleasesQueue()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverSocket = await acceptTask;
        listener.Stop();

        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var stream = serverSocket.GetStream();
            await ProtocolFraming.ReadAsync(stream);
            requestReceived.SetResult();
            var buffer = new byte[1];
            return await stream.ReadAsync(buffer);
        });

        var inspector = new InspectorClient(clientSocket.GetStream(), TimeSpan.FromMilliseconds(100));
        var stalled = inspector.RequestAsync("times-out");
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            async () => await stalled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("times-out", exception.Message);
        Assert.True(inspector.IsAborted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inspector.RequestAsync("after-timeout"));
        Assert.Equal(0, await serverTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static Task WriteSuccessAsync(Stream stream, ProtocolFrame request, string value) =>
        ProtocolFraming.WriteAsync(stream, new JsonObject
        {
            ["requestId"] = request.Header["requestId"]!.GetValue<string>(),
            ["ok"] = true,
            ["result"] = new JsonObject { ["value"] = value },
        });

    private static void AssertTransportClosed(Exception? exception) =>
        Assert.True(
            exception is IOException or ObjectDisposedException,
            $"Expected a transport-closed exception, found {exception?.GetType().Name ?? "no exception"}.");
}
