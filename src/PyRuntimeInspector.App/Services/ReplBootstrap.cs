using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PyRuntimeInspector.App.Services;

public static class ReplBootstrap
{
    public const int ExpectedBootstrapAbi = 2;
    public static readonly string ExpectedAgentVersion =
        typeof(ReplBootstrap).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? throw new InvalidOperationException("The PyMonitor product version is unavailable.");

    public static string Build(string agentDirectory, int port, string token)
    {
        var fullAgentDirectory = Path.GetFullPath(agentDirectory);
        var path = JsonSerializer.Serialize(fullAgentDirectory);
        var bootstrapPath = JsonSerializer.Serialize(Path.Combine(
            fullAgentDirectory,
            "pyruntime_inspector_agent",
            "bootstrap.py"));
        var expectedVersion = JsonSerializer.Serialize(ExpectedAgentVersion);
        var serializedToken = JsonSerializer.Serialize(token);
        var script = $"""
_pymonitor_previous_dont_write_bytecode = sys.dont_write_bytecode
try:
    sys.dont_write_bytecode = True
    sys.path.insert(0, {path})
    __import__('runpy').run_path({bootstrapPath}, run_name='_pymonitor_fresh_bootstrap')['start_bootstrap'](agent_directory={path}, expected_version={expectedVersion}, expected_bootstrap_abi={ExpectedBootstrapAbi}, host='127.0.0.1', port={port}, token={serializedToken}, attach_mode='cooperative')
finally:
    sys.dont_write_bytecode = _pymonitor_previous_dont_write_bytecode
    del _pymonitor_previous_dont_write_bytecode
""";
        return $"import sys; exec({JsonSerializer.Serialize(script)})";
    }
}
