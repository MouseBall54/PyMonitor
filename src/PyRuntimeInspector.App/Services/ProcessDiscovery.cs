using System.Diagnostics;

namespace PyRuntimeInspector.App.Services;

public sealed record ProcessItem(int Id, string Name, string? ExecutablePath)
{
    public string DisplayName => $"{Name} ({Id})";
}

public interface IProcessDiscovery
{
    IReadOnlyList<ProcessItem> GetPythonProcesses();
    long? GetPrivateBytes(int pid);
}

public sealed class ProcessDiscovery : IProcessDiscovery
{
    public IReadOnlyList<ProcessItem> GetPythonProcesses()
    {
        var result = new List<ProcessItem>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!process.ProcessName.Contains("python", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
                result.Add(new ProcessItem(process.Id, process.ProcessName, path));
            }
        }
        return result.OrderBy(item => item.Id).ToArray();
    }

    public long? GetPrivateBytes(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.PrivateMemorySize64;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
