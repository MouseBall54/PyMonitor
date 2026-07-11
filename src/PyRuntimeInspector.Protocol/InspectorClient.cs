using System.Text.Json.Nodes;

namespace PyRuntimeInspector.Protocol;

public sealed class InspectorClient(Stream stream)
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    public Task<ProtocolFrame> HelloAsync(string token, CancellationToken cancellationToken = default) =>
        RequestAsync("session.hello", new JsonObject { ["token"] = token }, cancellationToken);

    public async Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        await _requestLock.WaitAsync(cancellationToken);
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
            await ProtocolFraming.WriteAsync(stream, request, cancellationToken: cancellationToken);
            var response = await ProtocolFraming.ReadAsync(stream, cancellationToken);
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
}
