using System.Text.Json.Nodes;
using PyRuntimeInspector.Protocol;

namespace PyRuntimeInspector.App.Services;

public interface IInspectorSession : IAsyncDisposable
{
    event EventHandler<string>? Disconnected;
    bool IsConnected { get; }
    Task<JsonObject> AttachAsync(int port, string token, int? expectedPid, CancellationToken cancellationToken);
    Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default);
    Task DetachAsync();
}
