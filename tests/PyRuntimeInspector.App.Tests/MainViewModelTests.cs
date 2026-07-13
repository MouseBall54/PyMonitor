using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
        Assert.Equal("Cooperative listener", viewModel.ConnectionMode);
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
        await EventuallyAsync(() => session.ReleasedHandles.Contains("stale_value-handle"));
        Assert.DoesNotContain("new_value-handle", session.ReleasedHandles);
    }

    [Fact]
    public async Task GlobalSearchShowsExactLocationAndOpensClassMemberOwner()
    {
        var session = new FakeSession();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery())
        {
            RefreshIntervalSeconds = 1,
        };
        await viewModel.AttachCommand.ExecuteAsync();
        viewModel.GlobalSearchQuery = "calculate needle";

        await viewModel.SearchRuntimeCommand.ExecuteAsync();

        var result = Assert.Single(viewModel.GlobalSearchResults);
        Assert.Equal("method", result.Kind);
        Assert.Equal("calculate_needle_total", result.Name);
        Assert.Equal("Modules / __main__ / engine / Class demo.Engine / instance method calculate_needle_total", result.Location);
        Assert.Contains("1 results", viewModel.GlobalSearchStatus);

        await viewModel.OpenGlobalSearchResultCommand.ExecuteAsync();

        Assert.Equal(0, viewModel.SelectedWorkspaceTabIndex);
        Assert.Equal(2, viewModel.SelectedObjectDetailTabIndex);
        Assert.Equal("calculate_needle_total", viewModel.ClassTreeSearchText);
        Assert.Equal("Modules / __main__ / engine", viewModel.SelectedObjectPath);
    }

    [Fact]
    public async Task GlobalSearchCanOpenDetectedConsoleNamespaceSource()
    {
        var session = new FakeSession { ReturnConsoleSearchRoot = true };
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery());
        await viewModel.AttachCommand.ExecuteAsync();
        viewModel.GlobalSearchQuery = "EmbeddedTerminal";

        await viewModel.SearchRuntimeCommand.ExecuteAsync();
        var result = Assert.Single(viewModel.GlobalSearchResults);
        Assert.Equal("console", result.Kind);
        Assert.Equal("console-1", result.ConsoleHandle);
        Assert.True(result.CanOpen);

        await viewModel.OpenGlobalSearchResultCommand.ExecuteAsync();

        Assert.Contains("EmbeddedTerminal.namespace", viewModel.Breadcrumb, StringComparison.Ordinal);
        Assert.Contains(viewModel.Variables, row => row.Name == "terminal_value");
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
        Assert.Equal("Not connected", viewModel.ConnectionMode);
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
        Assert.Equal("Managed launch", viewModel.ConnectionMode);
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

    [Fact]
    public async Task LiveAttachUsesSelectedInterpreterAndConnectsExpectedPid()
    {
        var session = new FakeSession { DelayAttach = true };
        var liveAttach = new FakeLiveAttachService(session.CompleteAttach);
        await using var viewModel = new MainViewModel(
            session,
            new FakeProcessDiscovery(),
            liveAttachService: liveAttach)
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath),
        };

        await viewModel.LiveAttachCommand.ExecuteAsync();

        Assert.True(viewModel.IsConnected);
        Assert.Equal("Connected (live attach)", viewModel.Status);
        Assert.Equal("Live Attach", viewModel.ConnectionMode);
        Assert.Equal(1234, liveAttach.Options!.ProcessId);
        Assert.Equal(Environment.ProcessPath, liveAttach.Options.PythonExecutable);
        Assert.Equal(64, liveAttach.Options.InspectorToken.Length);

        await viewModel.DetachCommand.ExecuteAsync();
        Assert.False(viewModel.IsConnected);
        Assert.True(liveAttach.LeaseDisposed);
    }

    [Fact]
    public async Task QuickAttachForPython311CopiesOneLineAndLoadsMainGlobals()
    {
        var session = new FakeSession { DelayAttach = true };
        var clipboard = new FakeClipboardService();
        var liveAttach = new FakeLiveAttachService(session.CompleteAttach);
        await using var viewModel = new MainViewModel(
            session,
            new FakeProcessDiscovery(),
            liveAttachService: liveAttach,
            clipboardService: clipboard)
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 11, 9)),
            SelectedWorkspaceTabIndex = 6,
        };

        var quickAttach = viewModel.QuickAttachCommand.ExecuteAsync();
        await EventuallyAsync(() => clipboard.Text is not null);
        var bootstrap = Assert.IsType<string>(clipboard.Text);
        Assert.Contains("pyruntime_inspector_agent", bootstrap);
        Assert.Contains("port=", bootstrap);
        Assert.Contains("token=", bootstrap);
        Assert.DoesNotContain('\n', bootstrap);
        Assert.True(viewModel.IsAwaitingBootstrap);
        Assert.True(viewModel.IsBusy);
        Assert.Equal("Quick Attach · awaiting bootstrap", viewModel.ConnectionMode);
        Assert.Contains("VS Code Debug Console", viewModel.BootstrapInstructions);
        Assert.Contains("Python >>> REPL", viewModel.BootstrapInstructions);

        session.CompleteAttach();
        await quickAttach;

        Assert.True(viewModel.IsConnected);
        Assert.False(viewModel.IsAwaitingBootstrap);
        Assert.Equal("Quick Attach · REPL bootstrap", viewModel.ConnectionMode);
        Assert.Equal("Modules / __main__", viewModel.Breadcrumb);
        Assert.Equal(0, viewModel.SelectedWorkspaceTabIndex);
        Assert.Contains(viewModel.Variables, row => row.Name == "example_value" && row.SafePreview == "1235");
        Assert.Contains(viewModel.Variables, row => row.Name == "edd" && row.SafePreview == "121");
        Assert.Null(liveAttach.Options);
    }

    [Fact]
    public async Task QuickAttachBootstrapTimeoutCleansUpAndAllowsRetry()
    {
        var session = new FakeSession { DelayAttach = true };
        var clipboard = new FakeClipboardService();
        await using var viewModel = new MainViewModel(
            session,
            new FakeProcessDiscovery(),
            clipboardService: clipboard,
            onePasteAttachTimeout: TimeSpan.FromMilliseconds(100))
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 12, 9)),
        };

        await viewModel.QuickAttachCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsAwaitingBootstrap);
        Assert.Equal("Quick attach timed out", viewModel.Status);
        Assert.Equal("Not connected", viewModel.ConnectionMode);
        Assert.Contains("VS Code Debug Console", viewModel.ErrorMessage);
        Assert.True(viewModel.QuickAttachCommand.CanExecute(null));
        Assert.Equal(1, session.AttachCallCount);

        var retry = viewModel.QuickAttachCommand.ExecuteAsync();
        Assert.Equal(2, session.AttachCallCount);
        Assert.True(viewModel.IsAwaitingBootstrap);
        session.CompleteAttach();
        await retry.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsConnected);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsAwaitingBootstrap);
        Assert.Equal("Connected · showing __main__ globals", viewModel.Status);
        Assert.Equal("", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task QuickAttachBootstrapTimeoutReleasesListenerPort()
    {
        var port = ReservePort();
        await using var viewModel = new MainViewModel(
            new InspectorSession(),
            new FakeProcessDiscovery(),
            clipboardService: new FakeClipboardService(),
            onePasteAttachTimeout: TimeSpan.FromMilliseconds(100))
        {
            PortText = port.ToString(),
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 12, 9)),
        };

        await viewModel.QuickAttachCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Quick attach timed out", viewModel.Status);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.QuickAttachCommand.CanExecute(null));
        var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
    }

    [Fact]
    public async Task CancellingPendingQuickAttachIsNotReportedAsTimeout()
    {
        var session = new FakeSession { DelayAttach = true };
        await using var viewModel = new MainViewModel(
            session,
            new FakeProcessDiscovery(),
            clipboardService: new FakeClipboardService(),
            onePasteAttachTimeout: TimeSpan.FromSeconds(5))
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 12, 9)),
        };

        var quickAttach = viewModel.QuickAttachCommand.ExecuteAsync();
        await EventuallyAsync(() => viewModel.IsAwaitingBootstrap);

        await viewModel.DetachCommand.ExecuteAsync();
        await quickAttach.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsAwaitingBootstrap);
        Assert.Equal("Quick attach cancelled", viewModel.Status);
        Assert.Equal("Not connected", viewModel.ConnectionMode);
        Assert.Equal("", viewModel.ErrorMessage);
    }

    [Theory]
    [InlineData("STALE_AGENT", "End the Python debug session")]
    [InlineData("INCOMPATIBLE_AGENT", "run exit()")]
    [InlineData("ACTIVE_AGENT_CONFLICT", "Detach it from the original PyMonitor window")]
    public async Task QuickAttachCompatibilityFailureShowsPersistentRecoverySteps(
        string code,
        string expectedInstruction)
    {
        var session = new FakeSession
        {
            AttachException = new RemoteInspectionException(code, "Agent compatibility test failure."),
        };
        var clipboard = new FakeClipboardService();
        await using var viewModel = new MainViewModel(
            session,
            new FakeProcessDiscovery(),
            clipboardService: clipboard)
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 11, 9)),
        };

        await viewModel.QuickAttachCommand.ExecuteAsync();

        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsAwaitingBootstrap);
        Assert.Equal("Quick attach failed", viewModel.Status);
        Assert.StartsWith(code, viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.True(viewModel.HasConnectionRecovery);
        Assert.Contains(expectedInstruction, viewModel.ConnectionRecoveryMessage, StringComparison.Ordinal);
        Assert.Contains("Rescan", viewModel.ConnectionRecoveryMessage, StringComparison.Ordinal);
        Assert.Contains("Quick Attach", viewModel.ConnectionRecoveryMessage, StringComparison.Ordinal);
        Assert.NotNull(clipboard.Text);
    }

    [Fact]
    public async Task Python314CompatibilityFailureStopsBeforeManualBootstrapFallback()
    {
        var session = new FakeSession
        {
            AttachException = new RemoteInspectionException("STALE_AGENT", "Runtime sources differ."),
        };
        var clipboard = new FakeClipboardService();
        await using var viewModel = new MainViewModel(
            session,
            new FakeProcessDiscovery(),
            liveAttachService: new FakeLiveAttachService(() => { }),
            clipboardService: clipboard)
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 14, 6)),
        };

        await viewModel.QuickAttachCommand.ExecuteAsync();

        Assert.Equal("Live attach failed", viewModel.Status);
        Assert.True(viewModel.HasConnectionRecovery);
        Assert.Equal(1, session.AttachCallCount);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task QuickAttachForPython314UsesLiveAttachWithoutClipboardBootstrap()
    {
        var session = new FakeSession { DelayAttach = true };
        var clipboard = new FakeClipboardService();
        var liveAttach = new FakeLiveAttachService(session.CompleteAttach);
        await using var viewModel = new MainViewModel(
            session,
            new FakeProcessDiscovery(),
            liveAttachService: liveAttach,
            clipboardService: clipboard)
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 14, 6)),
            SelectedWorkspaceTabIndex = 6,
        };

        await viewModel.QuickAttachCommand.ExecuteAsync();

        Assert.True(viewModel.IsConnected);
        Assert.Equal("Live Attach", viewModel.ConnectionMode);
        Assert.NotNull(liveAttach.Options);
        Assert.Null(clipboard.Text);
        Assert.Equal("Modules / __main__", viewModel.Breadcrumb);
        Assert.Equal(0, viewModel.SelectedWorkspaceTabIndex);
    }

    [Fact]
    public async Task QuickAttachRejectsDetectedUnsupportedPythonVersion()
    {
        var clipboard = new FakeClipboardService();
        await using var viewModel = new MainViewModel(
            new FakeSession(),
            new FakeProcessDiscovery(),
            clipboardService: clipboard)
        {
            SelectedProcess = new ProcessItem(1234, "python", Environment.ProcessPath, new Version(3, 9)),
        };

        await viewModel.QuickAttachCommand.ExecuteAsync();

        Assert.False(viewModel.IsConnected);
        Assert.Equal("Unsupported Python version", viewModel.Status);
        Assert.Contains("3.10", viewModel.ErrorMessage);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task EmbeddedConsoleNamespaceLoadsAndTracksVariablesDeclaredLater()
    {
        var session = new FakeSession();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery())
        {
            RefreshIntervalSeconds = 1,
        };
        await viewModel.AttachCommand.ExecuteAsync();
        var root = Assert.Single(viewModel.RuntimeRoots, node => node.Label.StartsWith("Console namespaces", StringComparison.Ordinal));
        var console = Assert.Single(root.Children, node => node.Kind == RuntimeNodeKind.ConsoleNamespace);

        await viewModel.SelectTreeNodeAsync(console);

        Assert.Contains("EmbeddedTerminal.namespace", viewModel.Breadcrumb, StringComparison.Ordinal);
        Assert.Contains(viewModel.Variables, row =>
            row.Name == "terminal_value"
            && row.Scope == "console:namespace"
            && row.SafePreview == "7");
        Assert.Equal("console-1", session.LastConsoleHandle);
        Assert.Equal("namespace", session.LastConsoleAttributeName);

        session.IncludeLaterConsoleVariable = true;
        await EventuallyAsync(() =>
            session.ConsoleNamespaceRequestCount >= 2
            && viewModel.Variables.Any(row => row.Name == "declared_later"),
            TimeSpan.FromSeconds(5));

        var added = Assert.Single(viewModel.Variables, row => row.Name == "declared_later");
        Assert.Equal(VariableChangeKind.Added, added.ChangeKind);
        Assert.True(session.ConsoleNamespaceRequestCount >= 2);
    }

    [Fact]
    public async Task GcTreeSearchIsServerSideAndSkippedByAutomaticRefresh()
    {
        var session = new FakeSession();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery())
        {
            RefreshIntervalSeconds = 1,
        };
        await viewModel.AttachCommand.ExecuteAsync();
        var gcNode = Assert.Single(viewModel.RuntimeRoots, node => node.Kind == RuntimeNodeKind.GcObjects);

        await viewModel.SelectTreeNodeAsync(gcNode);

        Assert.Equal("GC-tracked objects", viewModel.Breadcrumb);
        Assert.Single(viewModel.Variables);
        Assert.Equal("gc-tracked", viewModel.Variables[0].Scope);
        Assert.Contains("scanned 3 of 3", viewModel.PageLabel);
        Assert.Equal(1, session.GcRequestCount);
        Assert.True(viewModel.SearchCurrentCommand.CanExecute(null));

        viewModel.SelectedVariable = viewModel.Variables[0];
        await EventuallyAsync(() => session.GcDetailRequestCount == 2);

        viewModel.SearchText = "example";
        Assert.Equal(1, session.GcRequestCount);
        await viewModel.SearchCurrentCommand.ExecuteAsync();
        Assert.Equal(2, session.GcRequestCount);
        Assert.Equal("example", session.GcQuery);

        await Task.Delay(1200);
        Assert.Equal(2, session.GcRequestCount);
    }

    [Fact]
    public async Task MemoryCommandsTrackSnapshotsDiffAndBoundedTimeline()
    {
        var session = new FakeSession();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery());
        await viewModel.AttachCommand.ExecuteAsync();

        await viewModel.StartTracingCommand.ExecuteAsync();
        Assert.True(viewModel.IsTracemallocTracing);
        Assert.Contains("Running", viewModel.TracemallocStatus);
        Assert.NotEmpty(viewModel.MemoryTimeline);
        for (var index = 0; index < 305; index++)
            await viewModel.RefreshMemoryCommand.ExecuteAsync();
        Assert.Equal(300, viewModel.MemoryTimeline.Count);

        await viewModel.TakeMemorySnapshotCommand.ExecuteAsync();
        await viewModel.TakeMemorySnapshotCommand.ExecuteAsync();
        Assert.Equal(2, viewModel.MemorySnapshots.Count);
        viewModel.BeforeMemorySnapshot = viewModel.MemorySnapshots[0];
        viewModel.AfterMemorySnapshot = viewModel.MemorySnapshots[1];
        await viewModel.CompareMemorySnapshotsCommand.ExecuteAsync();
        Assert.Single(viewModel.MemoryStatistics);
        Assert.Equal(512, viewModel.MemoryStatistics[0].SizeDiffBytes);

        await viewModel.StopTracingCommand.ExecuteAsync();
        Assert.False(viewModel.IsTracemallocTracing);
        Assert.Empty(viewModel.MemorySnapshots);
    }

    [Fact]
    public async Task AdvancedArrayCommandsRenderTileHistogramAndNonFinitePixel()
    {
        var session = new FakeSession { DelayFirstArrayPreview = true };
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery());
        await viewModel.AttachCommand.ExecuteAsync();
        viewModel.SelectedVariable = new VariableRow
        {
            Name = "image",
            Scope = "globals",
            HandleId = "array-handle",
            TypeName = "ndarray",
            ModuleName = "numpy",
            QualifiedTypeName = "numpy.ndarray",
            SafePreview = "ndarray(shape=(10, 10), dtype=float32)",
            Address = "0x1",
            ShallowSize = 128,
            AdapterKind = "numpy.ndarray",
            ChangeToken = "array",
            Expandable = true,
        };
        await session.FirstArrayPreviewStarted.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.NormalizationMode = "MINMAX";
        await viewModel.ReloadPreviewCommand.ExecuteAsync();
        Assert.Contains("MINMAX", viewModel.Normalization);
        session.ReleaseFirstArrayPreview();
        await EventuallyAsync(() => viewModel.InspectorState == InspectorPaneState.Ready);
        Assert.Contains("MINMAX", viewModel.Normalization);

        viewModel.TileX = 3;
        viewModel.TileY = 4;
        await viewModel.LoadTileCommand.ExecuteAsync();
        viewModel.UpdateCursor(0, 0);
        Assert.Equal("x=3, y=4", viewModel.CursorCoordinate);

        viewModel.HistogramBinCount = 2;
        await viewModel.LoadHistogramCommand.ExecuteAsync();
        Assert.Equal(2, viewModel.HistogramBins.Count);
        Assert.Contains("NaN 1", viewModel.HistogramSummary);

        await viewModel.LoadPixelAsync(0, 0);
        Assert.Contains("NaN", viewModel.RawPixelValue);
        Assert.Equal("0", viewModel.DisplayPixelValue);

        await viewModel.DetachCommand.ExecuteAsync();
        Assert.Null(viewModel.ArrayPreview);
        Assert.Equal(0, viewModel.PreviewWidth);
        Assert.Null(viewModel.TargetPid);
        Assert.Equal("—", viewModel.PrivateBytes);
    }

    [Fact]
    public async Task ExecutionMonitoringCommandsStreamAndClearBoundedEvents()
    {
        var session = new FakeSession();
        await using var viewModel = new MainViewModel(session, new FakeProcessDiscovery());
        await viewModel.AttachCommand.ExecuteAsync();
        Assert.True(viewModel.ExecutionMonitoringAvailable);

        viewModel.MonitorLine = true;
        await viewModel.StartExecutionMonitoringCommand.ExecuteAsync();
        Assert.True(viewModel.ExecutionMonitoringActive);
        await viewModel.RefreshExecutionEventsCommand.ExecuteAsync();
        Assert.Single(viewModel.ExecutionEvents);
        Assert.Equal("LINE", viewModel.ExecutionEvents[0].EventName);

        await viewModel.StopExecutionMonitoringCommand.ExecuteAsync();
        Assert.False(viewModel.ExecutionMonitoringActive);
        await viewModel.ClearExecutionEventsCommand.ExecuteAsync();
        Assert.Empty(viewModel.ExecutionEvents);
    }

    private static async Task EventuallyAsync(Func<bool> predicate, TimeSpan? wait = null)
    {
        var timeout = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(2));
        while (!predicate() && DateTime.UtcNow < timeout)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeProcessDiscovery : IProcessDiscovery
    {
        public IReadOnlyList<ProcessItem> GetPythonProcesses() => [];
        public ProcessMemoryInfo? GetMemoryInfo(int pid) => new(
            24 * 1024 * 1024,
            16 * 1024 * 1024,
            64 * 1024 * 1024,
            32 * 1024 * 1024);
    }

    private sealed class FakeSession : IInspectorSession
    {
        private readonly TaskCompletionSource<JsonObject> _attach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtocolFrame>> _scopes = [];
        private bool _tracing;
        private int _snapshotNumber;
        private bool _monitoring;
        private bool _executionEventCleared;
        private readonly TaskCompletionSource _firstArrayPreviewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstArrayPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrayPreviewRequestCount;
        public event EventHandler<string>? Disconnected;
        public bool IsConnected { get; private set; }
        public bool DelayAttach { get; init; }
        public Exception? AttachException { get; init; }
        public int AttachCallCount { get; private set; }
        public int GcRequestCount { get; private set; }
        public int GcDetailRequestCount { get; private set; }
        public string? GcQuery { get; private set; }
        public bool IncludeLaterConsoleVariable { get; set; }
        public int ConsoleNamespaceRequestCount { get; private set; }
        public string? LastConsoleHandle { get; private set; }
        public string? LastConsoleAttributeName { get; private set; }
        public ConcurrentQueue<string> ReleasedHandles { get; } = [];
        public bool DelayFirstArrayPreview { get; init; }
        public bool ReturnConsoleSearchRoot { get; init; }
        public Task FirstArrayPreviewStarted => _firstArrayPreviewStarted.Task;

        public void ReleaseFirstArrayPreview() => _releaseFirstArrayPreview.TrySetResult();

        public Task<JsonObject> AttachAsync(int port, string token, int? expectedPid, CancellationToken cancellationToken)
        {
            AttachCallCount++;
            if (AttachException is not null)
                return Task.FromException<JsonObject>(AttachException);
            if (DelayAttach)
                return CompleteStateWhenAttachedAsync(_attach.Task, cancellationToken);
            IsConnected = true;
            return Task.FromResult(Runtime());
        }

        public async Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
        {
            if (method == "threads.list" || method == "frames.list")
                return Frame(new JsonObject { ["items"] = new JsonArray() });
            if (method == "modules.list")
                return Frame(new JsonObject
                {
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "__main__",
                        ["filename"] = null,
                        ["entryCount"] = 2,
                        ["isMain"] = true,
                    }),
                    ["total"] = 1,
                });
            if (method == "modules.listNamespace")
                return Frame(new JsonObject
                {
                    ["moduleName"] = "__main__",
                    ["scopeType"] = "module",
                    ["items"] = new JsonArray(
                        ModuleVariable("example_value", "1235"),
                        ModuleVariable("edd", "121")),
                    ["total"] = 2,
                });
            if (method == "consoles.list")
                return Frame(new JsonObject
                {
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["consoleHandle"] = "console-1",
                        ["displayName"] = "demo.EmbeddedTerminal.namespace",
                        ["ownerType"] = "demo.EmbeddedTerminal",
                        ["ownerAddressHex"] = "0xabc",
                        ["attributeName"] = "namespace",
                        ["namespaceName"] = "namespace",
                        ["kind"] = "custom",
                        ["entryCount"] = IncludeLaterConsoleVariable ? 2 : 1,
                    }),
                    ["total"] = 1,
                    ["trackedTotal"] = 20,
                    ["scannedCount"] = 20,
                    ["scanComplete"] = true,
                });
            if (method == "consoles.listNamespace")
            {
                ConsoleNamespaceRequestCount++;
                LastConsoleHandle = parameters?["consoleHandle"]?.GetValue<string>();
                LastConsoleAttributeName = parameters?["attributeName"]?.GetValue<string>();
                var items = new JsonArray(ModuleVariable("terminal_value", "7"));
                if (IncludeLaterConsoleVariable)
                    items.Add(ModuleVariable("declared_later", "9"));
                return Frame(new JsonObject
                {
                    ["consoleHandle"] = LastConsoleHandle,
                    ["attributeName"] = LastConsoleAttributeName,
                    ["scopeType"] = "console",
                    ["items"] = items,
                    ["total"] = items.Count,
                });
            }
            if (method == "gc.listObjects")
            {
                GcRequestCount++;
                GcQuery = parameters?["query"]?.GetValue<string>();
                return Frame(new JsonObject
                {
                    ["scopeType"] = "gc-tracked",
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "sample.Example",
                        ["value"] = new JsonObject
                        {
                            ["handleId"] = "gc-handle",
                            ["typeName"] = "Example",
                            ["moduleName"] = "sample",
                            ["qualifiedTypeName"] = "sample.Example",
                            ["safePreview"] = "<sample.Example object>",
                            ["addressHex"] = "0x1234",
                            ["shallowSizeBytes"] = 48L,
                            ["expandable"] = true,
                            ["adapterKind"] = null,
                            ["changeToken"] = "identity:1234",
                        },
                    }),
                    ["total"] = 1,
                    ["trackedTotal"] = 3,
                    ["scannedCount"] = 3,
                    ["truncated"] = false,
                    ["durationMilliseconds"] = 1.25,
                });
            }
            if (method == "runtime.search")
            {
                if (ReturnConsoleSearchRoot)
                    return Frame(new JsonObject
                    {
                        ["items"] = new JsonArray(new JsonObject
                        {
                            ["kind"] = "console",
                            ["name"] = "demo.EmbeddedTerminal.namespace",
                            ["location"] = "Console namespaces / demo.EmbeddedTerminal.namespace @0xabc",
                            ["objectPath"] = "Console namespaces / demo.EmbeddedTerminal.namespace @0xabc",
                            ["matchFields"] = new JsonArray("name"),
                            ["depth"] = 0,
                            ["sourceKind"] = "console",
                            ["scopeType"] = "console",
                            ["consoleHandle"] = "console-1",
                            ["consoleAttributeName"] = "namespace",
                            ["value"] = null,
                        }),
                        ["objectsScanned"] = 0,
                        ["rootsScanned"] = 1,
                        ["scanComplete"] = true,
                        ["durationMilliseconds"] = 1.0,
                    });
                var value = ModuleVariable("engine", "<demo.Engine object>")["value"]!.DeepClone().AsObject();
                value["handleId"] = "global-search-engine";
                value["typeName"] = "Engine";
                value["moduleName"] = "demo";
                value["qualifiedTypeName"] = "demo.Engine";
                value["expandable"] = true;
                return Frame(new JsonObject
                {
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["kind"] = "method",
                        ["name"] = "calculate_needle_total",
                        ["location"] = "Modules / __main__ / engine / Class demo.Engine / instance method calculate_needle_total",
                        ["objectPath"] = "Modules / __main__ / engine",
                        ["matchFields"] = new JsonArray("member", "signature"),
                        ["depth"] = 0,
                        ["sourceKind"] = "module",
                        ["moduleName"] = "__main__",
                        ["scopeType"] = "module",
                        ["rootName"] = "engine",
                        ["value"] = value,
                    }),
                    ["objectsScanned"] = 12,
                    ["rootsScanned"] = 2,
                    ["scanComplete"] = true,
                    ["durationMilliseconds"] = 3.5,
                });
            }
            if (method == "runtime.getInfo")
                return Frame(Runtime());
            if (method == "memory.status")
                return Frame(MemoryStatus());
            if (method == "memory.start")
            {
                _tracing = true;
                return Frame(MemoryStatus());
            }
            if (method == "memory.stop")
            {
                _tracing = false;
                return Frame(MemoryStatus());
            }
            if (method == "memory.snapshot")
            {
                _snapshotNumber++;
                return Frame(new JsonObject
                {
                    ["snapshotId"] = $"snapshot-{_snapshotNumber}",
                    ["label"] = parameters?["label"]?.GetValue<string>() ?? "snapshot",
                    ["createdAt"] = DateTime.UtcNow.ToString("O"),
                    ["traceCount"] = 10L,
                    ["totalBytes"] = 2048L * _snapshotNumber,
                });
            }
            if (method == "memory.statistics" || method == "memory.diff")
                return Frame(new JsonObject
                {
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["filename"] = "sample.py",
                        ["lineNumber"] = 12,
                        ["sizeBytes"] = 2048L,
                        ["count"] = 2L,
                        ["sizeDiffBytes"] = 512L,
                        ["countDiff"] = 1L,
                    }),
                });
            if (method == "objects.listChildren")
            {
                if (parameters?["handleId"]?.GetValue<string>() == "gc-handle")
                    GcDetailRequestCount++;
                return Frame(new JsonObject { ["items"] = new JsonArray() });
            }
            if (method == "classes.describe")
            {
                if (parameters?["handleId"]?.GetValue<string>() == "gc-handle")
                    GcDetailRequestCount++;
                return Frame(new JsonObject { ["members"] = new JsonArray() });
            }
            if (method == "arrays.describe")
                return Frame(new JsonObject
                {
                    ["shape"] = new JsonArray(10, 10),
                    ["dtype"] = "float32",
                    ["strides"] = new JsonArray(40, 4),
                    ["dataAddressHex"] = "0x2",
                    ["ownsData"] = true,
                    ["layoutGuess"] = "GRAY",
                    ["layoutConfidence"] = "certain",
                });
            if (method == "arrays.preview" || method == "arrays.tile")
            {
                var originX = method == "arrays.tile" ? parameters!["x"]!.GetValue<int>() : 0;
                var originY = method == "arrays.tile" ? parameters!["y"]!.GetValue<int>() : 0;
                var mode = parameters?["normalization"]?.GetValue<string>() ?? "AUTO";
                if (method == "arrays.preview"
                    && DelayFirstArrayPreview
                    && Interlocked.Increment(ref _arrayPreviewRequestCount) == 1)
                {
                    _firstArrayPreviewStarted.TrySetResult();
                    await _releaseFirstArrayPreview.Task.WaitAsync(cancellationToken);
                }
                return Frame(new JsonObject
                {
                    ["width"] = 2,
                    ["height"] = 2,
                    ["stride"] = 2,
                    ["pixelFormat"] = "Gray8",
                    ["rowStep"] = 1,
                    ["columnStep"] = 1,
                    ["originX"] = originX,
                    ["originY"] = originY,
                    ["sourceWidth"] = 10,
                    ["sourceHeight"] = 10,
                    ["normalization"] = new JsonObject
                    {
                        ["mode"] = mode,
                        ["displayMinimum"] = -1.0,
                        ["displayMaximum"] = 1.0,
                        ["nanCount"] = 1,
                        ["positiveInfinityCount"] = 1,
                        ["negativeInfinityCount"] = 0,
                    },
                }, [0, 64, 128, 255]);
            }
            if (method == "arrays.histogram")
                return Frame(new JsonObject
                {
                    ["counts"] = new JsonArray(4L, 5L),
                    ["binEdges"] = new JsonArray(-1.0, 0.0, 1.0),
                    ["sampleCount"] = 10,
                    ["nanCount"] = 1,
                    ["positiveInfinityCount"] = 1,
                    ["negativeInfinityCount"] = 0,
                });
            if (method == "arrays.pixel")
                return Frame(new JsonObject { ["value"] = new JsonObject { ["kind"] = "NaN" } });
            if (method == "execution.status")
                return Frame(ExecutionStatus());
            if (method == "execution.start")
            {
                _monitoring = true;
                _executionEventCleared = false;
                return Frame(ExecutionStatus());
            }
            if (method == "execution.stop")
            {
                _monitoring = false;
                return Frame(ExecutionStatus());
            }
            if (method == "execution.clear")
            {
                _executionEventCleared = true;
                return Frame(ExecutionStatus());
            }
            if (method == "execution.list")
            {
                var after = parameters?["afterSequence"]?.GetValue<long>() ?? 0;
                var items = !_executionEventCleared && after < 1
                    ? new JsonArray(new JsonObject
                    {
                        ["sequence"] = 1L,
                        ["timestampUnixNanoseconds"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                        ["threadId"] = 42L,
                        ["eventName"] = "LINE",
                        ["functionName"] = "worker",
                        ["qualifiedName"] = "worker",
                        ["filename"] = "sample.py",
                        ["lineNumber"] = 12,
                        ["instructionOffset"] = null,
                        ["detail"] = null,
                    })
                    : new JsonArray();
                return Frame(new JsonObject
                {
                    ["items"] = items,
                    ["nextSequence"] = items.Count == 0 ? after : 1L,
                    ["droppedCount"] = 0L,
                    ["bufferCapacity"] = 100,
                });
            }
            if (method == "scopes.list")
            {
                var handle = parameters!["frameHandle"]!.GetValue<string>();
                var source = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                _scopes[handle] = source;
                return await source.Task;
            }
            if (method == "objects.release")
            {
                ReleasedHandles.Enqueue(parameters!["handleId"]!.GetValue<string>());
                return Frame(new JsonObject { ["released"] = true });
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
                ["handleId"] = $"{variableName}-handle",
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

        private async Task<JsonObject> CompleteStateWhenAttachedAsync(Task<JsonObject> task, CancellationToken cancellationToken)
        {
            var result = await task.WaitAsync(cancellationToken);
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

        private JsonObject MemoryStatus() => new()
        {
            ["tracing"] = _tracing,
            ["startedAt"] = _tracing ? DateTime.UtcNow.ToString("O") : null,
            ["startedByInspector"] = _tracing,
            ["tracebackDepth"] = _tracing ? 1 : 0,
            ["currentBytes"] = _tracing ? 1024L : 0L,
            ["peakBytes"] = _tracing ? 4096L : 0L,
            ["overheadBytes"] = _tracing ? 256L : 0L,
        };

        private JsonObject ExecutionStatus() => new()
        {
            ["available"] = true,
            ["active"] = _monitoring,
            ["toolId"] = _monitoring ? 3 : null,
            ["bufferedCount"] = _executionEventCleared ? 0 : 1,
            ["bufferCapacity"] = 100,
            ["droppedCount"] = 0L,
        };

        private static ProtocolFrame Frame(JsonObject result, byte[]? binary = null) => new(new JsonObject
        {
            ["ok"] = true,
            ["result"] = result,
        }, binary ?? []);

        private static JsonObject ModuleVariable(string name, string preview) => new()
        {
            ["name"] = name,
            ["value"] = new JsonObject
            {
                ["handleId"] = Guid.NewGuid().ToString(),
                ["typeName"] = "int",
                ["moduleName"] = "builtins",
                ["qualifiedTypeName"] = "builtins.int",
                ["safePreview"] = preview,
                ["addressHex"] = "0x1",
                ["shallowSizeBytes"] = 28L,
                ["expandable"] = false,
                ["adapterKind"] = null,
                ["changeToken"] = $"identity:{preview}",
            },
        };
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
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

    private sealed class FakeLiveAttachService(Action connected) : ILiveAttachService
    {
        public LiveAttachOptions? Options { get; private set; }
        public bool LeaseDisposed { get; private set; }

        public Task<IAsyncDisposable> StartAsync(LiveAttachOptions options, CancellationToken cancellationToken = default)
        {
            Options = options;
            connected();
            return Task.FromResult<IAsyncDisposable>(new CallbackAsyncDisposable(() => LeaseDisposed = true));
        }
    }

    private sealed class CallbackAsyncDisposable(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}
