using System.Text.Json.Nodes;

namespace PyRuntimeInspector.Protocol;

public sealed class InspectorClient(Stream stream, TimeSpan? requestTimeout = null)
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly TimeSpan _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    private int _aborted;

    public bool IsAborted => Volatile.Read(ref _aborted) != 0;

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) == 0)
            stream.Dispose();
    }

    public Task<ProtocolFrame> HelloAsync(string token, CancellationToken cancellationToken = default) =>
        RequestAsync("session.hello", new JsonObject { ["token"] = token }, cancellationToken);

    public async Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfAborted();
        await _requestLock.WaitAsync(cancellationToken);
        Task<ProtocolFrame> exchange;
        try
        {
            ThrowIfAborted();
            cancellationToken.ThrowIfCancellationRequested();
            exchange = ExchangeAndReleaseAsync(method, parameters);
        }
        catch
        {
            _requestLock.Release();
            throw;
        }

        try
        {
            return await exchange.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller can stop waiting immediately, but the serialized exchange keeps
            // draining its matching response so the next request cannot consume it.
            // A permanently unresponsive target is aborted by the bounded request timeout.
            ObserveFailure(exchange);
            throw;
        }
    }

    private async Task<ProtocolFrame> ExchangeAndReleaseAsync(string method, JsonObject? parameters)
    {
        try
        {
            var requestId = Guid.NewGuid().ToString();
            var request = new JsonObject
            {
                ["protocolVersion"] = "1.0",
                ["messageType"] = "request",
                ["requestId"] = requestId,
                ["method"] = method,
                ["params"] = parameters ?? new JsonObject(),
            };
            var roundTrip = RoundTripAsync(request);
            ProtocolFrame response;
            try
            {
                response = await roundTrip.WaitAsync(_requestTimeout);
            }
            catch (TimeoutException)
            {
                Abort();
                ObserveFailure(roundTrip);
                throw new TimeoutException($"Inspector request '{method}' timed out after {_requestTimeout.TotalSeconds:0.#} seconds.");
            }

            if (response.Header["requestId"]?.GetValue<string>() != requestId)
                throw new ProtocolException("Response requestId does not match the request.");
            if (response.Header["ok"]?.GetValue<bool>() != true)
            {
                var error = response.Header["error"] as JsonObject;
                throw new RemoteInspectionException(
                    error?["code"]?.GetValue<string>() ?? "UNKNOWN",
                    error?["message"]?.GetValue<string>() ?? "Inspection failed.");
            }
            return response;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<ProtocolFrame> RoundTripAsync(JsonObject request)
    {
        await ProtocolFraming.WriteAsync(stream, request, cancellationToken: CancellationToken.None);
        return await ProtocolFraming.ReadAsync(stream, CancellationToken.None);
    }

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void ThrowIfAborted()
    {
        if (IsAborted)
            throw new ObjectDisposedException(nameof(InspectorClient), "The inspector transport has been aborted.");
    }
}
