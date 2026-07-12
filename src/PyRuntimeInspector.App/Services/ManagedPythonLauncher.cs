using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PyRuntimeInspector.App.Services;

public enum ProcessOutputKind
{
    StandardOutput,
    StandardError,
}

public sealed record ProcessOutputEventArgs(ProcessOutputKind Kind, string Text);
public sealed record ManagedProcessExitedEventArgs(int ProcessId, int ExitCode, bool WasStopped);

public sealed record ManagedLaunchOptions(
    string PythonExecutable,
    string ScriptPath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string AgentDirectory,
    int InspectorPort,
    string InspectorToken);

public sealed record ManagedLaunchHandle(int ProcessId, Task<int> Completion);

public interface IManagedPythonLauncher : IAsyncDisposable
{
    event EventHandler<ProcessOutputEventArgs>? OutputReceived;
    event EventHandler<ManagedProcessExitedEventArgs>? Exited;
    bool IsRunning { get; }
    int? ProcessId { get; }
    Task<ManagedLaunchHandle> StartAsync(ManagedLaunchOptions options, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public sealed class ManagedPythonLauncher : IManagedPythonLauncher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task<int>? _completion;
    private bool _stopRequested;

    public event EventHandler<ProcessOutputEventArgs>? OutputReceived;
    public event EventHandler<ManagedProcessExitedEventArgs>? Exited;
    public bool IsRunning => _process is { HasExited: false };
    public int? ProcessId => IsRunning ? _process!.Id : null;

    public async Task<ManagedLaunchHandle> StartAsync(ManagedLaunchOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("A managed Python target is already running.");

            var startInfo = new ProcessStartInfo
            {
                FileName = options.PythonExecutable,
                WorkingDirectory = options.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("-B");
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("pyruntime_inspector_agent.managed_launch");
            startInfo.ArgumentList.Add(options.ScriptPath);
            foreach (var argument in options.Arguments)
                startInfo.ArgumentList.Add(argument);

            foreach (var (name, value) in options.Environment)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Contains('='))
                    throw new ArgumentException($"Invalid environment variable name: {name}");
                startInfo.Environment[name] = value;
            }
            var inheritedPythonPath = startInfo.Environment.TryGetValue("PYTHONPATH", out var pythonPath) ? pythonPath : null;
            startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(inheritedPythonPath)
                ? options.AgentDirectory
                : options.AgentDirectory + Path.PathSeparator + inheritedPythonPath;
            startInfo.Environment["PY_INSPECTOR_HOST"] = "127.0.0.1";
            startInfo.Environment["PY_INSPECTOR_PORT"] = options.InspectorPort.ToString();
            startInfo.Environment["PY_INSPECTOR_TOKEN"] = options.InspectorToken;
            startInfo.Environment["PY_INSPECTOR_ATTACH_MODE"] = "managed";
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Python process did not start.");
            }
            _process = process;
            _stopRequested = false;
            _completion = ObserveProcessAsync(process);
            return new ManagedLaunchHandle(process.Id, _completion);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        Task<int>? completion;
        await _gate.WaitAsync();
        try
        {
            if (_process is null)
                return;
            completion = _completion;
            if (!_process.HasExited)
            {
                _stopRequested = true;
                _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _gate.Release();
        }
        if (completion is not null)
            await completion;
    }

    private async Task<int> ObserveProcessAsync(Process process)
    {
        var stdout = PumpAsync(process.StandardOutput, ProcessOutputKind.StandardOutput);
        var stderr = PumpAsync(process.StandardError, ProcessOutputKind.StandardError);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        var processId = process.Id;
        var exitCode = process.ExitCode;
        var wasStopped = _stopRequested;
        Exited?.Invoke(this, new ManagedProcessExitedEventArgs(processId, exitCode, wasStopped));

        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _completion = null;
            }
        }
        finally
        {
            _gate.Release();
            process.Dispose();
        }
        return exitCode;
    }

    private async Task PumpAsync(StreamReader reader, ProcessOutputKind kind)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                OutputReceived?.Invoke(this, new ProcessOutputEventArgs(kind, line));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private static void Validate(ManagedLaunchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PythonExecutable))
            throw new ArgumentException("Python executable is required.");
        if (!File.Exists(options.ScriptPath))
            throw new FileNotFoundException("Python script was not found.", options.ScriptPath);
        if (!Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException($"Working directory was not found: {options.WorkingDirectory}");
        if (!Directory.Exists(options.AgentDirectory))
            throw new DirectoryNotFoundException($"Agent directory was not found: {options.AgentDirectory}");
        if (options.InspectorPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options.InspectorPort));
        if (options.InspectorToken.Length < 64)
            throw new ArgumentException("Inspector token must contain at least 64 characters.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}

public static class WindowsCommandLine
{
    public static IReadOnlyList<string> ParseArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];
        var pointer = CommandLineToArgvW("pymonitor " + commandLine, out var count);
        if (pointer == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[Math.Max(0, count - 1)];
            for (var index = 1; index < count; index++)
            {
                var argumentPointer = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result[index - 1] = Marshal.PtrToStringUni(argumentPointer) ?? "";
            }
            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
