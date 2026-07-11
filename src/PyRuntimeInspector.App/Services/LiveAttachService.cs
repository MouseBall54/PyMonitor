using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PyRuntimeInspector.App.Services;

public sealed record LiveAttachOptions(
    int ProcessId,
    string PythonExecutable,
    string AgentDirectory,
    int InspectorPort,
    string InspectorToken,
    bool ElevateHelper = false);

public sealed class LiveAttachException(string code, string message, string? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Details { get; } = details;
}

public sealed class LiveAttachLease : IAsyncDisposable
{
    private readonly string _temporaryDirectory;

    internal LiveAttachLease(string temporaryDirectory)
    {
        _temporaryDirectory = temporaryDirectory;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return ValueTask.CompletedTask;
    }
}

public interface ILiveAttachService
{
    Task<IAsyncDisposable> StartAsync(LiveAttachOptions options, CancellationToken cancellationToken = default);
}

public sealed class LiveAttachService : ILiveAttachService
{
    private const string HelperCode = """
import json
import sys

result_path = sys.argv[3]

def finish(payload, exit_code):
    with open(result_path, "w", encoding="utf-8") as result_file:
        json.dump(payload, result_file)
    print(json.dumps(payload), flush=True)
    raise SystemExit(exit_code)

try:
    remote_exec = sys.remote_exec
except AttributeError:
    finish({"ok": False, "code": "UNSUPPORTED_PYTHON", "message": "Live attach requires CPython 3.14 or newer."}, 10)

try:
    remote_exec(int(sys.argv[1]), sys.argv[2])
except PermissionError as exc:
    finish({"ok": False, "code": "PERMISSION_DENIED", "message": str(exc)}, 11)
except ProcessLookupError as exc:
    finish({"ok": False, "code": "TARGET_NOT_FOUND", "message": str(exc)}, 12)
except RuntimeError as exc:
    message = str(exc)
    code = "REMOTE_DEBUG_DISABLED" if "remote debugging is not enabled" in message.lower() else "REMOTE_EXEC_FAILED"
    finish({"ok": False, "code": code, "message": "RuntimeError: " + message}, 13)
except Exception as exc:
    finish({"ok": False, "code": "REMOTE_EXEC_FAILED", "message": type(exc).__name__ + ": " + str(exc)}, 13)

finish({"ok": True}, 0)
""";

    public async Task<IAsyncDisposable> StartAsync(LiveAttachOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options);
        EnsureTargetExists(options.ProcessId);

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PyRuntimeInspector",
            $"live-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var bootstrapPath = Path.Combine(temporaryDirectory, "bootstrap.py");
        var resultPath = Path.Combine(temporaryDirectory, "helper-result.json");

        try
        {
            await File.WriteAllTextAsync(
                bootstrapPath,
                BuildBootstrap(options),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = options.PythonExecutable,
                CreateNoWindow = true,
                UseShellExecute = options.ElevateHelper,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (options.ElevateHelper)
                startInfo.Verb = "runas";
            else
            {
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
            }
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(HelperCode);
            startInfo.ArgumentList.Add(options.ProcessId.ToString());
            startInfo.ArgumentList.Add(bootstrapPath);
            startInfo.ArgumentList.Add(resultPath);

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new LiveAttachException("HELPER_START_FAILED", "The Python live-attach helper did not start.");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                var code = exception is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                    ? "HELPER_ELEVATION_CANCELLED"
                    : "HELPER_START_FAILED";
                throw new LiveAttachException(code, "The selected Python executable could not start the live-attach helper.", exception.Message);
            }

            var stdoutTask = options.ElevateHelper
                ? Task.FromResult("")
                : process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = options.ElevateHelper
                ? Task.FromResult("")
                : process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var result = File.Exists(resultPath)
                ? await File.ReadAllTextAsync(resultPath, cancellationToken)
                : stdout;
            ValidateHelperResult(process.ExitCode, result, stderr);
            return new LiveAttachLease(temporaryDirectory);
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private static string BuildBootstrap(LiveAttachOptions options)
    {
        var agentDirectory = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(options.AgentDirectory)));
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(options.InspectorToken));
        return $$"""
import base64
import sys

_agent_directory = base64.b64decode("{{agentDirectory}}").decode("utf-8")
if _agent_directory not in sys.path:
    sys.path.insert(0, _agent_directory)
from pyruntime_inspector_agent import start_inspector as _start_inspector
_start_inspector(
    host="127.0.0.1",
    port={{options.InspectorPort}},
    token=base64.b64decode("{{token}}").decode("utf-8"),
    attach_mode="live",
)
del _agent_directory, _start_inspector
""";
    }

    private static void ValidateHelperResult(int exitCode, string stdout, string stderr)
    {
        JsonDocument? document = null;
        try
        {
            var json = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(json))
                document = JsonDocument.Parse(json);
            if (document?.RootElement.TryGetProperty("ok", out var ok) == true && ok.GetBoolean() && exitCode == 0)
                return;
            var code = document?.RootElement.TryGetProperty("code", out var codeNode) == true
                ? codeNode.GetString() ?? "HELPER_FAILED"
                : "HELPER_FAILED";
            var message = document?.RootElement.TryGetProperty("message", out var messageNode) == true
                ? messageNode.GetString() ?? "The live-attach helper failed."
                : "The live-attach helper failed.";
            throw new LiveAttachException(code, message, string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim());
        }
        catch (JsonException exception)
        {
            throw new LiveAttachException(
                "HELPER_PROTOCOL_ERROR",
                $"The live-attach helper returned an invalid response (exit code {exitCode}).",
                string.IsNullOrWhiteSpace(stderr) ? exception.Message : stderr.Trim());
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static void Validate(LiveAttachOptions options)
    {
        if (options.ProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.ProcessId));
        if (!File.Exists(options.PythonExecutable))
            throw new FileNotFoundException("The selected process executable was not found.", options.PythonExecutable);
        if (!Directory.Exists(options.AgentDirectory))
            throw new DirectoryNotFoundException($"Agent directory was not found: {options.AgentDirectory}");
        if (options.InspectorPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options.InspectorPort));
        if (options.InspectorToken.Length < 64)
            throw new ArgumentException("Inspector token must contain at least 64 characters.");
    }

    private static void EnsureTargetExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                throw new ArgumentException($"Target process {processId} has exited.");
        }
        catch (ArgumentException)
        {
            throw new LiveAttachException("TARGET_NOT_FOUND", $"Target process {processId} is not running.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
