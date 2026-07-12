using System.IO;
using System.Text.Json;

namespace PyRuntimeInspector.App.Services;

public static class ReplBootstrap
{
    public static string Build(string agentDirectory, int port, string token)
    {
        var path = JsonSerializer.Serialize(Path.GetFullPath(agentDirectory));
        var serializedToken = JsonSerializer.Serialize(token);
        var script = $"""
_pymonitor_previous_dont_write_bytecode = sys.dont_write_bytecode
try:
    sys.dont_write_bytecode = True
    sys.path.insert(0, {path})
    __import__('pyruntime_inspector_agent').start_inspector(host='127.0.0.1', port={port}, token={serializedToken}, attach_mode='cooperative')
finally:
    sys.dont_write_bytecode = _pymonitor_previous_dont_write_bytecode
    del _pymonitor_previous_dont_write_bytecode
""";
        return $"import sys; exec({JsonSerializer.Serialize(script)})";
    }
}
