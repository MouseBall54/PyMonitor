using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PyRuntimeInspector.Protocol;

var portText = Environment.GetEnvironmentVariable("PY_INSPECTOR_PORT");
var token = Environment.GetEnvironmentVariable("PY_INSPECTOR_TOKEN");
if (!int.TryParse(portText, out var port) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(token) || token.Length < 64)
{
    Console.Error.WriteLine("Set PY_INSPECTOR_PORT and a 256-bit PY_INSPECTOR_TOKEN before starting the controller.");
    return 2;
}

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"Listening on 127.0.0.1:{port}. Start a cooperative target with the same environment.");
try
{
    using var socket = await listener.AcceptTcpClientAsync();
    await using var stream = socket.GetStream();
    var client = new InspectorClient(stream);
    await client.HelloAsync(token);
    foreach (var method in new[] { "runtime.getInfo", "threads.list", "frames.list" })
    {
        var response = await client.RequestAsync(method);
        Console.WriteLine(response.Header["result"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    await client.RequestAsync("session.detach");
    return 0;
}
finally
{
    listener.Stop();
}
