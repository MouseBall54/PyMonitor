using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyRuntimeInspector.Protocol;

namespace PyRuntimeInspector.App.Services;

public sealed class InspectorSession(TimeSpan? requestTimeout = null) : IInspectorSession
{
    private static readonly TimeSpan CooperativeDetachTimeout = TimeSpan.FromSeconds(1);
    private TcpListener? _listener;
    private TcpClient? _socket;
    private InspectorClient? _client;

    public event EventHandler<string>? Disconnected;
    public bool IsConnected { get; private set; }

    public async Task<JsonObject> AttachAsync(int port, string token, int? expectedPid, CancellationToken cancellationToken)
    {
        if (IsConnected || _listener is not null)
            throw new InvalidOperationException("A connection is already active.");
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        try
        {
            _socket = await _listener.AcceptTcpClientAsync(cancellationToken);
        }
        finally
        {
            _listener.Stop();
            _listener = null;
        }

        _client = new InspectorClient(_socket.GetStream(), requestTimeout);
        try
        {
            await _client.HelloAsync(token, cancellationToken);
            var runtimeFrame = await _client.RequestAsync("runtime.getInfo", cancellationToken: cancellationToken);
            var runtime = runtimeFrame.Header["result"]?.AsObject()
                ?? throw new ProtocolException("runtime.getInfo returned no result.");
            var actualPid = runtime["pid"]?.GetValue<int>()
                ?? throw new ProtocolException("runtime.getInfo returned no PID.");
            if (expectedPid is not null && expectedPid != actualPid)
            {
                await _client.RequestAsync("session.detach", cancellationToken: cancellationToken);
                throw new InvalidOperationException($"Connected PID {actualPid} does not match selected PID {expectedPid}.");
            }
            IsConnected = true;
            return runtime;
        }
        catch
        {
            CloseSocket();
            throw;
        }
    }

    public async Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _client is null)
            throw new InvalidOperationException("The inspector is not connected.");
        try
        {
            return await _client.RequestAsync(method, parameters, cancellationToken);
        }
        catch (RemoteInspectionException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            MarkDisconnected("Target request timed out.");
            throw;
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or ProtocolException)
        {
            MarkDisconnected("Target connection closed.");
            throw;
        }
    }

    public async Task DetachAsync()
    {
        var client = _client;
        var wasConnected = IsConnected;
        IsConnected = false;
        try
        {
            if (client is not null && wasConnected)
            {
                var detachTask = client.RequestAsync("session.detach");
                try
                {
                    await detachTask.WaitAsync(CooperativeDetachTimeout);
                }
                catch (TimeoutException)
                {
                    client.Abort();
                    ObserveFailure(detachTask);
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or ProtocolException)
                {
                }
            }
        }
        finally
        {
            CloseSocket();
        }
    }

    public async ValueTask DisposeAsync() => await DetachAsync();

    private void MarkDisconnected(string message)
    {
        if (!IsConnected)
            return;
        IsConnected = false;
        CloseSocket();
        Disconnected?.Invoke(this, message);
    }

    private void CloseSocket()
    {
        var client = _client;
        _client = null;
        client?.Abort();
        var socket = _socket;
        _socket = null;
        socket?.Dispose();
    }

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
