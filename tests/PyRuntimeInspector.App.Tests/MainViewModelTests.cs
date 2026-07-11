using System.Text.Json.Nodes;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.App.ViewModels;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task AttachCommandReturnsImmediatelyWhileWaitingForTarget()
    {
        var session = new FakeSession { DelayAttach = true };
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery());

        viewModel.AttachCommand.Execute(null);

        Assert.True(viewModel.IsBusy);
        Assert.Equal("Waiting for cooperative target", viewModel.Status);
        session.CompleteAttach();
        await EventuallyAsync(() => viewModel.IsConnected);
        Assert.Equal("Connected", viewModel.Status);
    }

    [Fact]
    public async Task LaterScopeSelectionWinsWhenEarlierResponseArrivesLast()
    {
        var session = new FakeSession();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery());
        await viewModel.AttachCommand.ExecuteAsync();
        var first = new RuntimeTreeNode("first / Locals", RuntimeNodeKind.Scope) { FrameHandle = "first", ScopeType = "locals" };
        var second = new RuntimeTreeNode("second / Locals", RuntimeNodeKind.Scope) { FrameHandle = "second", ScopeType = "locals" };

        var firstLoad = viewModel.LoadScopeAsync(first, resetPage: true);
        var secondLoad = viewModel.LoadScopeAsync(second, resetPage: true);
        session.CompleteScope("second", "new_value");
        await secondLoad;
        session.CompleteScope("first", "stale_value");
        await firstLoad;

        Assert.Single(viewModel.Variables);
        Assert.Equal("new_value", viewModel.Variables[0].Name);
        Assert.Contains("second", viewModel.Breadcrumb);
    }

    [Fact]
    public async Task TargetExitTransitionsToDisconnectedWithoutThrowing()
    {
        var session = new FakeSession();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery());
        await viewModel.AttachCommand.ExecuteAsync();

        session.RaiseDisconnected("Target exited");

        await EventuallyAsync(() => !viewModel.IsConnected);
        Assert.False(viewModel.IsConnected);
        Assert.Equal("Target exited", viewModel.Status);
        Assert.Empty(viewModel.RuntimeRoots);
    }

    [Fact]
    public async Task ManagedLaunchCommandWiresOptionsAndDetachLeavesProcessRunning()
    {
        var session = new FakeSession();
        var launcher = new FakeManagedLauncher();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery(), launcher)
        {
            ScriptArguments = "one \"two words\"",
        };
        viewModel.LaunchEnvironment.Add(new EnvironmentVariableRow { Name = "DEMO_ENV", Value = "demo-value" });

        await viewModel.LaunchCommand.ExecuteAsync();

        Assert.True(viewModel.IsConnected);
        Assert.True(viewModel.IsManagedRunning);
        Assert.Equal(new[] { "one", "two words" }, launcher.Options!.Arguments);
        Assert.Equal("demo-value", launcher.Options.Environment["DEMO_ENV"]);
        launcher.EmitOutput(ProcessOutputKind.StandardError, "sample-error");
        await EventuallyAsync(() => viewModel.LaunchOutput.Count == 1);

        await viewModel.DetachCommand.ExecuteAsync();
        Assert.False(viewModel.IsConnected);
        Assert.True(launcher.IsRunning);

        await viewModel.StopCommand.ExecuteAsync();
        Assert.False(launcher.IsRunning);
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < timeout)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class FakeProcessDiscovery : IProcessDiscovery
    {
        public IReadOnlyList<ProcessItem> GetPythonProcesses() => [];
        public long? GetPrivateBytes(int pid) => 16 * 1024 * 1024;
    }

    private sealed class FakeSession : IInspectorSession
    {
        private readonly TaskCompletionSource<JsonObject> _attach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, TaskCompletionSource<ProtocolFrame>> _scopes = [];
        public event EventHandler<string>? Disconnected;
        public bool IsConnected { get; private set; }
        public bool DelayAttach { get; init; }

        public Task<JsonObject> AttachAsync(int port, string token, int? expectedPid, CancellationToken cancellationToken)
        {
            if (DelayAttach)
                return CompleteStateWhenAttachedAsync(_attach.Task);
            IsConnected = true;
            return Task.FromResult(Runtime());
        }

        public async Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
        {
            if (method == "threads.list" || method == "frames.list")
                return Frame(new JsonObject { ["items"] = new JsonArray() });
            if (method == "runtime.getInfo")
                return Frame(Runtime());
            if (method == "scopes.list")
            {
                var handle = parameters!["frameHandle"]!.GetValue<string>();
                var source = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                _scopes[handle] = source;
                return await source.Task;
            }
            return Frame(new JsonObject());
        }

        public Task DetachAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void CompleteAttach() => _attach.TrySetResult(Runtime());

        public void CompleteScope(string handle, string variableName)
        {
            var summary = new JsonObject
            {
                ["handleId"] = Guid.NewGuid().ToString(),
                ["typeName"] = "int",
                ["moduleName"] = "builtins",
                ["qualifiedTypeName"] = "builtins.int",
                ["safePreview"] = "1",
                ["addressHex"] = "0x1",
                ["shallowSizeBytes"] = 28L,
                ["expandable"] = false,
                ["adapterKind"] = null,
                ["changeToken"] = variableName,
            };
            _scopes[handle].TrySetResult(Frame(new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["name"] = variableName, ["value"] = summary }),
                ["total"] = 1,
            }));
        }

        public void RaiseDisconnected(string message)
        {
            IsConnected = false;
            Disconnected?.Invoke(this, message);
        }

        private async Task<JsonObject> CompleteStateWhenAttachedAsync(Task<JsonObject> task)
        {
            var result = await task;
            IsConnected = true;
            return result;
        }

        private static JsonObject Runtime() => new()
        {
            ["pid"] = 1234,
            ["version"] = "3.12.9",
            ["processArchitecture"] = "64-bit",
            ["executable"] = "python.exe",
        };

        private static ProtocolFrame Frame(JsonObject result) => new(new JsonObject
        {
            ["ok"] = true,
            ["result"] = result,
        }, []);
    }

    private sealed class FakeManagedLauncher : IManagedPythonLauncher
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<ProcessOutputEventArgs>? OutputReceived;
        public event EventHandler<ManagedProcessExitedEventArgs>? Exited;
        public bool IsRunning { get; private set; }
        public int? ProcessId => IsRunning ? 1234 : null;
        public ManagedLaunchOptions? Options { get; private set; }

        public Task<ManagedLaunchHandle> StartAsync(ManagedLaunchOptions options, CancellationToken cancellationToken = default)
        {
            Options = options;
            IsRunning = true;
            return Task.FromResult(new ManagedLaunchHandle(1234, _completion.Task));
        }

        public Task StopAsync()
        {
            if (IsRunning)
            {
                IsRunning = false;
                _completion.TrySetResult(-1);
                Exited?.Invoke(this, new ManagedProcessExitedEventArgs(1234, -1, true));
            }
            return Task.CompletedTask;
        }

        public void EmitOutput(ProcessOutputKind kind, string text) =>
            OutputReceived?.Invoke(this, new ProcessOutputEventArgs(kind, text));

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
