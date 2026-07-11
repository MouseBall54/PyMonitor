using System.IO;
using System.Text.Json;

namespace PyRuntimeInspector.App.Services;

public static class ReplBootstrap
{
    public static string Build(string agentDirectory, int port, string token)
    {
        var path = JsonSerializer.Serialize(Path.GetFullPath(agentDirectory));
        var serializedToken = JsonSerializer.Serialize(token);
        return $"import sys; sys.path.insert(0, {path}); __import__('pyruntime_inspector_agent').start_inspector(host='127.0.0.1', port={port}, token={serializedToken}, attach_mode='cooperative')";
    }
}
