using System.Diagnostics;
using System.IO;

namespace PyRuntimeInspector.App.Services;

public sealed record ProcessItem(int Id, string Name, string? ExecutablePath, Version? PythonVersion = null)
{
    public string DisplayName => PythonVersion is null
        ? $"{Name} ({Id})"
        : $"{Name} {PythonVersion.Major}.{PythonVersion.Minor} ({Id})";
}

public sealed record ProcessMemoryInfo(
    long WorkingSetBytes,
    long PrivateBytes,
    long VirtualBytes,
    long PeakWorkingSetBytes);

public interface IProcessDiscovery
{
    IReadOnlyList<ProcessItem> GetPythonProcesses();
    ProcessMemoryInfo? GetMemoryInfo(int pid);
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
                result.Add(new ProcessItem(process.Id, process.ProcessName, path, ReadPythonVersion(path)));
            }
        }
        return result.OrderBy(item => item.Id).ToArray();
    }

    private static Version? ReadPythonVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return version.FileMajorPart > 0
                ? new Version(version.FileMajorPart, version.FileMinorPart)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ProcessMemoryInfo? GetMemoryInfo(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessMemoryInfo(
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.VirtualMemorySize64,
                process.PeakWorkingSet64);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
