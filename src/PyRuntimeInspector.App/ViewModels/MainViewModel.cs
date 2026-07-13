using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PyRuntimeInspector.App.Infrastructure;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.Protocol;

namespace PyRuntimeInspector.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IInspectorSession _session;
    private readonly IProcessDiscovery _processDiscovery;
    private readonly IManagedPythonLauncher _launcher;
    private readonly ILiveAttachService _liveAttachService;
    private readonly IClipboardService _clipboardService;
    private readonly SynchronizationContext? _uiContext;
    private readonly Dictionary<string, ScopeSnapshot> _scopeSnapshots = [];
    private readonly Dictionary<string, ChangeMarker> _activeChanges = [];
    private readonly HashSet<string> _pinnedKeys = [];
    private readonly Dictionary<string, NavigationContext> _pinnedContexts = [];
    private readonly List<NavigationContext> _navigationHistory = [];
    private readonly Dictionary<string, bool> _objectTreeExpansionBeforeSearch = [];
    private readonly Dictionary<string, bool> _classTreeExpansionBeforeSearch = [];
    private readonly object _handleLifetimeSync = new();
    private readonly HashSet<string> _knownObjectHandles = new(StringComparer.Ordinal);
    private HashSet<string> _referencedObjectHandles = new(StringComparer.Ordinal);
    private int _activeHandleResponses;
    private int _handleSessionGeneration;
    private bool _handleReleaseInProgress;
    private bool _handleReleaseRequested;
    private bool _handleSessionClosing;
    private TaskCompletionSource? _handleReleaseCompleted;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _selectionCts;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _classTreeSearchDebounceCts;
    private CancellationTokenSource? _runtimeSearchCts;
    private RuntimeTreeNode? _currentScope;
    private long _selectionGeneration;
    private long _detailGeneration;
    private long _arrayPreviewGeneration;
    private long _matplotlibRefreshGeneration;
    private int _pageOffset;
    private int _pageTotal;
    private const int PageSize = 200;
    private const int ObjectPageSize = 100;
    private const int DataFrameRowPageSize = 50;
    private const int DataFrameColumnPageSize = 20;
    private const int MatplotlibMaxPreviewDimension = 1024;
    private const int MatplotlibMaxPreviewBytes = 4 * 1024 * 1024;
    private const int MaxObjectDepth = 8;
    // Bound retained navigation contexts so history cannot outgrow the agent handle store.
    private const int MaxNavigationHistory = 128;
    private const int GcScanLimit = 100_000;
    private const int ClassTreeSearchDebounceThreshold = 2_000;
    private const int MaxHandleReleaseBatch = 8;
    private const int MaxDetachHandleReleaseBatch = 32;
    // Leave a small render/refresh margin so users see every change for at least ten seconds.
    private static readonly TimeSpan ChangeHighlightDuration = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ClassTreeSearchDebounceDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DetachHandleReleaseBudget = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DefaultOnePasteAttachTimeout = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _onePasteAttachTimeout;
    private double _fitViewportWidth;
    private double _fitViewportHeight;
    private bool _fitMode = true;

    private ProcessItem? _selectedProcess;
    private VariableRow? _selectedVariable;
    private ObjectTreeNode? _selectedObjectNode;
    private PinnedObjectRow? _selectedPinnedObject;
    private NavigationContext? _currentObject;
    private int _navigationIndex = -1;
    private bool _suppressVariableNavigation;
    private string _portText;
    private string _token;
    private string _status = "Disconnected";
    private string _errorMessage = "";
    private string _connectionRecoveryMessage = "";
    private bool _isConnected;
    private bool _isBusy;
    private bool _isAwaitingBootstrap;
    private string _pythonVersion = "—";
    private string _architecture = "—";
    private string _executable = "—";
    private int? _targetPid;
    private string _privateBytes = "—";
    private string _workingSet = "—";
    private string _virtualSize = "—";
    private string _peakWorkingSet = "—";
    private string _tracemallocStatus = "Stopped";
    private string _pythonCurrentMemory = "—";
    private string _pythonPeakMemory = "—";
    private string _tracemallocOverhead = "—";
    private string _tracemallocCoverage = "Not tracing";
    private int _tracebackDepth = 1;
    private bool _isTracemallocTracing;
    private MemorySnapshotRow? _beforeMemorySnapshot;
    private MemorySnapshotRow? _afterMemorySnapshot;
    private int _snapshotSequence;
    private ProcessMemoryInfo? _lastProcessMemory;
    private long? _pythonCurrentBytes;
    private long? _pythonPeakBytes;
    private double _refreshIntervalSeconds = 1;
    private string _breadcrumb = "Select a frame scope";
    private string _selectedObjectPath = "Select a variable to inspect";
    private string _backNavigationLabel = "Back";
    private string _forwardNavigationLabel = "Forward";
    private string _parentNavigationLabel = "Parent";
    private string _backNavigationToolTip = "No earlier object in navigation history";
    private string _forwardNavigationToolTip = "No later object in navigation history";
    private string _parentNavigationToolTip = "The selected object is already at the root";
    private string _objectDepthLabel = "No level";
    private string _navigationHistoryLabel = "History 0 / 0";
    private string _navigationLocationDescription = "No object selected";
    private string _selectedObjectName = "No object selected";
    private string _selectedObjectPreview = "Choose a variable from the table to inspect its structure.";
    private string _selectedObjectStatus = "No selection";
    private string _classSummary = "Select an object to inspect its class.";
    private InspectorPaneState _inspectorState = InspectorPaneState.NoSelection;
    private bool _hasSelectedObject;
    private bool _hasArraySelection;
    private bool _hasDataFrameSelection;
    private bool _hasMatplotlibSelection;
    private bool _autoRefreshEnabled = true;
    private bool _isScopeLoading;
    private bool _isSearchPending;
    private DateTime? _lastSnapshotAt;
    private string _connectionMode = "Not connected";
    private string _searchText = "";
    private string _globalSearchQuery = "";
    private string _globalSearchStatus = "Connect to a Python runtime to search it.";
    private bool _isGlobalSearchRunning;
    private GlobalSearchResultRow? _selectedGlobalSearchResult;
    private string _objectChildrenSearchText = "";
    private string _objectTreeSearchText = "";
    private string _classTreeSearchText = "";
    private string _objectChildrenSearchResultLabel = "0 loaded names";
    private string _objectTreeSearchResultLabel = "0 loaded names";
    private string _classTreeSearchResultLabel = "0 loaded class details";
    private bool _isClassTreeSearchEmpty;
    private bool _classTreeMembersTruncated;
    private int _classTreeLoadedSearchableCount;
    private string _classTreeBoundedSummary = "";
    private string _scopeFilter = "All scopes";
    private string _changeFilter = "All changes";
    private string _typeFilter = "All types";
    private bool _arraysOnly;
    private bool _expandableOnly;
    private bool _pinnedOnly;
    private string _filterResultLabel = "0 visible";
    private string _pageLabel = "0 items";
    private string _lastLatency = "—";
    private int _selectedWorkspaceTabIndex;
    private int _selectedObjectDetailTabIndex;
    private string _selectedType = "—";
    private string _selectedModule = "—";
    private string _selectedQualifiedName = "—";
    private string _selectedAddress = "—";
    private string _selectedShallowSize = "—";
    private string _selectedPayloadSize = "—";
    private string _arrayShape = "—";
    private string _arrayDType = "—";
    private string _arrayStrides = "—";
    private string _arrayDataAddress = "—";
    private string _arrayOwner = "—";
    private string _arrayLayout = "HWC";
    private string _layoutConfidence = "—";
    private string _colorOrder = "RGB";
    private bool _channelR = true;
    private bool _channelG = true;
    private bool _channelB = true;
    private int _sliceAxis;
    private int _sliceIndex;
    private int _sliceMaximum;
    private ImageSource? _arrayPreview;
    private int _previewWidth;
    private int _previewHeight;
    private double _zoom = 1;
    private string _cursorCoordinate = "—";
    private string _rawPixelValue = "—";
    private string _displayPixelValue = "—";
    private string _normalization = "None (uint8)";
    private string _normalizationMode = "AUTO";
    private double _percentileLow = 1;
    private double _percentileHigh = 99;
    private int _histogramBins = 256;
    private int _histogramChannel;
    private int _tileX;
    private int _tileY;
    private int _tileWidth = 512;
    private int _tileHeight = 512;
    private int _previewOriginX;
    private int _previewOriginY;
    private int _sourceImageWidth;
    private int _sourceImageHeight;
    private double? _displayMinimum;
    private double? _displayMaximum;
    private string _histogramSummary = "Not loaded";
    private bool _executionMonitoringAvailable;
    private bool _executionMonitoringActive;
    private string _executionStatus = "Unavailable until connected";
    private string _executionPathPrefix;
    private int _executionBufferCapacity = 5000;
    private long _executionDroppedCount;
    private long _lastExecutionSequence;
    private bool _monitorPyStart = true;
    private bool _monitorPyReturn = true;
    private bool _monitorPyYield;
    private bool _monitorPyUnwind = true;
    private bool _monitorRaise = true;
    private bool _monitorLine;
    private bool _monitorCall;
    private string? _arrayHandle;
    private bool _arrayPreviewSupported;
    private string? _dataFrameHandle;
    private string? _matplotlibHandle;
    private ImageSource? _matplotlibPreview;
    private MatplotlibPaneState _matplotlibState = MatplotlibPaneState.NoSelection;
    private string _matplotlibSourceKind = "—";
    private string _matplotlibSourceDimensions = "—";
    private string _matplotlibCanvasType = "—";
    private string _matplotlibAvailabilityReason = "—";
    private string _matplotlibStatus = "Select a Matplotlib Figure or Axes to preview it.";
    private string _matplotlibNextAction = "";
    private string _matplotlibErrorMessage = "";
    private bool _matplotlibUsesOwningFigure;
    private DataTable? _dataFrameTable;
    private DataView? _dataFrameRows;
    private DataFramePaneState _dataFrameState = DataFramePaneState.NoSelection;
    private int _dataFrameRowOffset;
    private int _dataFrameColumnOffset;
    private int _dataFrameRowCount;
    private int _dataFrameColumnCount;
    private int _dataFrameTotalRows;
    private int _dataFrameTotalColumns;
    private bool _dataFrameHasMoreRows;
    private bool _dataFrameHasMoreColumns;
    private string _dataFrameShape = "—";
    private string _dataFrameStatus = "Select a pandas DataFrame to preview it.";
    private string _dataFrameRowPageLabel = "Rows 0 of 0";
    private string _dataFrameColumnPageLabel = "Columns 0 of 0";
    private string _dataFrameErrorMessage = "";
    private int[] _arrayDimensions = [];
    private int _previewRowStep = 1;
    private int _previewColumnStep = 1;
    private string _pythonExecutable = "python";
    private string _scriptPath = "";
    private string _scriptArguments = "";
    private string _workingDirectory = "";
    private EnvironmentVariableRow? _selectedEnvironmentVariable;
    private string _managedStatus = "Not started";
    private string _managedExitCode = "—";
    private int? _managedProcessId;
    private bool _isManagedRunning;
    private bool _elevateLiveAttach;

    public MainViewModel() : this(new InspectorSession(), new ProcessDiscovery(), new ManagedPythonLauncher(), new LiveAttachService(), new WpfClipboardService())
    {
    }

    public MainViewModel(
        IInspectorSession session,
        IProcessDiscovery processDiscovery,
        IManagedPythonLauncher? launcher = null,
        ILiveAttachService? liveAttachService = null,
        IClipboardService? clipboardService = null,
        TimeSpan? onePasteAttachTimeout = null)
    {
        _onePasteAttachTimeout = onePasteAttachTimeout ?? DefaultOnePasteAttachTimeout;
        if (_onePasteAttachTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(onePasteAttachTimeout), "Quick Attach timeout must be positive.");
        _session = session;
        _processDiscovery = processDiscovery;
        _launcher = launcher ?? new ManagedPythonLauncher();
        _liveAttachService = liveAttachService ?? new LiveAttachService();
        _clipboardService = clipboardService ?? new WpfClipboardService();
        _uiContext = SynchronizationContext.Current;
        var repositoryRoot = FindRepositoryRoot();
        _pythonExecutable = Environment.GetEnvironmentVariable("PYTHON_EXECUTABLE") ?? "python";
        _scriptPath = Path.Combine(repositoryRoot, "samples", "target_managed.py");
        _workingDirectory = repositoryRoot;
        _executionPathPrefix = Path.Combine(repositoryRoot, "samples");
        _portText = Environment.GetEnvironmentVariable("PY_INSPECTOR_PORT") ?? "49152";
        _token = Environment.GetEnvironmentVariable("PY_INSPECTOR_TOKEN")
            ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _session.Disconnected += OnSessionDisconnected;
        _launcher.OutputReceived += OnManagedOutput;
        _launcher.Exited += OnManagedExited;

        AttachCommand = new AsyncCommand(AttachAsync, () => !IsConnected && !IsBusy);
        QuickAttachCommand = new AsyncCommand(QuickAttachAsync, () => !IsConnected && !IsBusy && SelectedProcess is not null);
        LiveAttachCommand = new AsyncCommand(LiveAttachAsync, () => !IsConnected && !IsBusy && SelectedProcess?.ExecutablePath is not null);
        DetachCommand = new AsyncCommand(DetachAsync, () => IsConnected || IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected);
        PreviousPageCommand = new AsyncCommand(PreviousPageAsync, () => IsConnected && _pageOffset > 0);
        NextPageCommand = new AsyncCommand(NextPageAsync, () => IsConnected && _pageOffset + PageSize < _pageTotal);
        SearchCurrentCommand = new AsyncCommand(SearchCurrentAsync, () => IsConnected && _currentScope is not null);
        SearchRuntimeCommand = new AsyncCommand(
            SearchRuntimeAsync,
            () => IsConnected && !IsGlobalSearchRunning && !string.IsNullOrWhiteSpace(GlobalSearchQuery));
        OpenGlobalSearchResultCommand = new AsyncCommand(
            OpenGlobalSearchResultAsync,
            () => IsConnected && SelectedGlobalSearchResult?.CanOpen == true);
        NavigateBackCommand = new AsyncCommand(NavigateBackAsync, () => _navigationIndex > 0);
        NavigateForwardCommand = new AsyncCommand(NavigateForwardAsync, () => _navigationIndex >= 0 && _navigationIndex < _navigationHistory.Count - 1);
        NavigateParentCommand = new AsyncCommand(NavigateParentAsync, () => _currentObject?.Parent is not null);
        NavigatePinnedCommand = new AsyncCommand(NavigatePinnedAsync, () => SelectedPinnedObject is not null && IsConnected);
        ReloadPreviewCommand = new AsyncCommand(ReloadArrayPreviewAsync, () => IsConnected && _arrayHandle is not null && _arrayPreviewSupported);
        LoadTileCommand = new AsyncCommand(LoadArrayTileAsync, () => IsConnected && _arrayHandle is not null && _arrayPreviewSupported);
        LoadHistogramCommand = new AsyncCommand(LoadHistogramAsync, () => IsConnected && _arrayHandle is not null && _arrayPreviewSupported);
        RefreshDataFrameCommand = new AsyncCommand(RefreshDataFrameAsync, () => IsConnected && _dataFrameHandle is not null);
        RefreshMatplotlibCommand = new AsyncCommand(RefreshMatplotlibAsync, () => IsConnected && _matplotlibHandle is not null);
        PreviousDataFrameRowsCommand = new AsyncCommand(
            () => MoveDataFramePageAsync(rowDelta: -DataFrameRowPageSize, columnDelta: 0),
            () => IsConnected && _dataFrameHandle is not null && _dataFrameRowOffset > 0);
        NextDataFrameRowsCommand = new AsyncCommand(
            () => MoveDataFramePageAsync(rowDelta: DataFrameRowPageSize, columnDelta: 0),
            () => IsConnected && _dataFrameHandle is not null && _dataFrameHasMoreRows);
        PreviousDataFrameColumnsCommand = new AsyncCommand(
            () => MoveDataFramePageAsync(rowDelta: 0, columnDelta: -DataFrameColumnPageSize),
            () => IsConnected && _dataFrameHandle is not null && _dataFrameColumnOffset > 0);
        NextDataFrameColumnsCommand = new AsyncCommand(
            () => MoveDataFramePageAsync(rowDelta: 0, columnDelta: DataFrameColumnPageSize),
            () => IsConnected && _dataFrameHandle is not null && _dataFrameHasMoreColumns);
        StartTracingCommand = new AsyncCommand(StartTracingAsync, () => IsConnected && !IsTracemallocTracing);
        StopTracingCommand = new AsyncCommand(StopTracingAsync, () => IsConnected && IsTracemallocTracing);
        TakeMemorySnapshotCommand = new AsyncCommand(TakeMemorySnapshotAsync, () => IsConnected && IsTracemallocTracing);
        CompareMemorySnapshotsCommand = new AsyncCommand(CompareMemorySnapshotsAsync, () => IsConnected && BeforeMemorySnapshot is not null && AfterMemorySnapshot is not null && BeforeMemorySnapshot != AfterMemorySnapshot);
        RefreshMemoryCommand = new AsyncCommand(() => RefreshMemoryAsync(_connectionCts?.Token ?? CancellationToken.None, includeStatistics: true), () => IsConnected);
        StartExecutionMonitoringCommand = new AsyncCommand(StartExecutionMonitoringAsync, () => IsConnected && ExecutionMonitoringAvailable && !ExecutionMonitoringActive);
        StopExecutionMonitoringCommand = new AsyncCommand(StopExecutionMonitoringAsync, () => IsConnected && ExecutionMonitoringActive);
        RefreshExecutionEventsCommand = new AsyncCommand(RefreshExecutionEventsAsync, () => IsConnected && ExecutionMonitoringAvailable);
        ClearExecutionEventsCommand = new AsyncCommand(ClearExecutionEventsAsync, () => IsConnected && ExecutionMonitoringAvailable);
        LaunchCommand = new AsyncCommand(LaunchAsync, () => !IsConnected && !IsBusy && !IsManagedRunning);
        StopCommand = new AsyncCommand(StopManagedAsync, () => IsManagedRunning);
        RestartCommand = new AsyncCommand(RestartManagedAsync, () => !IsBusy && (!IsConnected || IsManagedRunning));
        RefreshProcessesCommand = new RelayCommand(RefreshProcesses);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        ClearGlobalSearchCommand = new RelayCommand(ClearGlobalSearch);
        CancelGlobalSearchCommand = new RelayCommand(CancelGlobalSearch, () => IsGlobalSearchRunning);
        ClearObjectChildrenSearchCommand = new RelayCommand(() => ObjectChildrenSearchText = "");
        ClearObjectTreeSearchCommand = new RelayCommand(() => ObjectTreeSearchText = "");
        ClearClassTreeSearchCommand = new RelayCommand(() => ClassTreeSearchText = "");
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ResetBaselineCommand = new RelayCommand(ResetCurrentBaseline, () => _currentScope is not null);
        TogglePinCommand = new RelayCommand(ToggleSelectedPin, () => _currentObject is not null && !_currentObject.Row.IsRemoved);
        CopyObjectPathCommand = new RelayCommand(CopySelectedObjectPath, () => _currentObject is not null);
        CopyObjectTypeCommand = new RelayCommand(CopySelectedObjectType, () => _currentObject is not null);
        CopyObjectAddressCommand = new RelayCommand(CopySelectedObjectAddress, () => _currentObject is not null);
        CopyEnvironmentCommand = new RelayCommand(CopyEnvironment);
        BrowsePythonCommand = new RelayCommand(BrowsePython);
        BrowseScriptCommand = new RelayCommand(BrowseScript);
        BrowseWorkingDirectoryCommand = new RelayCommand(BrowseWorkingDirectory);
        AddEnvironmentCommand = new RelayCommand(AddEnvironmentVariable);
        RemoveEnvironmentCommand = new RelayCommand(RemoveEnvironmentVariable, () => SelectedEnvironmentVariable is not null);
        ClearOutputCommand = new RelayCommand(LaunchOutput.Clear);
        FitCommand = new RelayCommand(() => { _fitMode = true; ApplyFitZoom(); });
        OneToOneCommand = new RelayCommand(() => { _fitMode = false; SetZoom(1); });
        RefreshProcesses();
    }

    public ObservableCollection<ProcessItem> Processes { get; } = [];
    public ObservableCollection<RuntimeTreeNode> RuntimeRoots { get; } = [];
    public ObservableCollection<VariableRow> Variables { get; } = [];
    public ObservableCollection<VariableRow> FilteredVariables { get; } = [];
    public ObservableCollection<GlobalSearchResultRow> GlobalSearchResults { get; } = [];
    public ObservableCollection<ObjectChildRow> ObjectChildren { get; } = [];
    public ObservableCollection<ObjectChildRow> FilteredObjectChildren { get; } = [];
    public ObservableCollection<ClassMemberRow> ClassMembers { get; } = [];
    public ObservableCollection<ObjectTreeNode> ObjectRoots { get; } = [];
    public ObservableCollection<ObjectBreadcrumbItem> ObjectBreadcrumbs { get; } = [];
    public ObservableCollection<DataFrameColumnInfo> DataFrameColumns { get; } = [];
    public ObservableCollection<ClassTreeNode> ClassTree { get; } = [];
    public ObservableCollection<PinnedObjectRow> PinnedObjects { get; } = [];
    public ObservableCollection<string> TypeFilters { get; } = ["All types"];
    public ObservableCollection<MemorySnapshotRow> MemorySnapshots { get; } = [];
    public ObservableCollection<MemoryStatisticRow> MemoryStatistics { get; } = [];
    public ObservableCollection<MemorySampleRow> MemoryTimeline { get; } = [];
    public ObservableCollection<HistogramBinRow> HistogramBins { get; } = [];
    public ObservableCollection<ExecutionEventRow> ExecutionEvents { get; } = [];
    public ObservableCollection<EnvironmentVariableRow> LaunchEnvironment { get; } = [];
    public ObservableCollection<LaunchOutputLine> LaunchOutput { get; } = [];
    public IReadOnlyList<double> RefreshIntervals { get; } = [1, 2, 5, 10];
    public IReadOnlyList<string> Layouts { get; } = ["GRAY", "HWC", "CHW", "VOLUME"];
    public IReadOnlyList<string> ColorOrders { get; } = ["RGB", "BGR"];
    public IReadOnlyList<string> NormalizationModes { get; } = ["AUTO", "NONE", "MINMAX", "PERCENTILE", "LABEL"];
    public IReadOnlyList<int> SliceAxes { get; } = [0, 1, 2];
    public IReadOnlyList<string> ScopeFilters { get; } = ["All scopes", "locals", "globals", "builtins", "module", "gc-tracked"];
    public IReadOnlyList<string> ChangeFilters { get; } = ["All changes", "Changed only", "Added", "Removed", "Rebound", "Updated"];

    public AsyncCommand AttachCommand { get; }
    public AsyncCommand QuickAttachCommand { get; }
    public AsyncCommand LiveAttachCommand { get; }
    public AsyncCommand DetachCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand SearchCurrentCommand { get; }
    public AsyncCommand SearchRuntimeCommand { get; }
    public AsyncCommand OpenGlobalSearchResultCommand { get; }
    public AsyncCommand NavigateBackCommand { get; }
    public AsyncCommand NavigateForwardCommand { get; }
    public AsyncCommand NavigateParentCommand { get; }
    public AsyncCommand NavigatePinnedCommand { get; }
    public AsyncCommand ReloadPreviewCommand { get; }
    public AsyncCommand LoadTileCommand { get; }
    public AsyncCommand LoadHistogramCommand { get; }
    public AsyncCommand RefreshDataFrameCommand { get; }
    public AsyncCommand RefreshMatplotlibCommand { get; }
    public AsyncCommand PreviousDataFrameRowsCommand { get; }
    public AsyncCommand NextDataFrameRowsCommand { get; }
    public AsyncCommand PreviousDataFrameColumnsCommand { get; }
    public AsyncCommand NextDataFrameColumnsCommand { get; }
    public AsyncCommand StartTracingCommand { get; }
    public AsyncCommand StopTracingCommand { get; }
    public AsyncCommand TakeMemorySnapshotCommand { get; }
    public AsyncCommand CompareMemorySnapshotsCommand { get; }
    public AsyncCommand RefreshMemoryCommand { get; }
    public AsyncCommand StartExecutionMonitoringCommand { get; }
    public AsyncCommand StopExecutionMonitoringCommand { get; }
    public AsyncCommand RefreshExecutionEventsCommand { get; }
    public AsyncCommand ClearExecutionEventsCommand { get; }
    public AsyncCommand LaunchCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RestartCommand { get; }
    public RelayCommand RefreshProcessesCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ClearGlobalSearchCommand { get; }
    public RelayCommand CancelGlobalSearchCommand { get; }
    public RelayCommand ClearObjectChildrenSearchCommand { get; }
    public RelayCommand ClearObjectTreeSearchCommand { get; }
    public RelayCommand ClearClassTreeSearchCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand ResetBaselineCommand { get; }
    public RelayCommand TogglePinCommand { get; }
    public RelayCommand CopyObjectPathCommand { get; }
    public RelayCommand CopyObjectTypeCommand { get; }
    public RelayCommand CopyObjectAddressCommand { get; }
    public RelayCommand CopyEnvironmentCommand { get; }
    public RelayCommand BrowsePythonCommand { get; }
    public RelayCommand BrowseScriptCommand { get; }
    public RelayCommand BrowseWorkingDirectoryCommand { get; }
    public RelayCommand AddEnvironmentCommand { get; }
    public RelayCommand RemoveEnvironmentCommand { get; }
    public RelayCommand ClearOutputCommand { get; }
    public RelayCommand FitCommand { get; }
    public RelayCommand OneToOneCommand { get; }

    public ProcessItem? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetProperty(ref _selectedProcess, value))
            {
                if (!IsConnected)
                    ApplySelectedProcessPreview();
                UpdateCommandStates();
            }
        }
    }
    public string PortText { get => _portText; set => SetProperty(ref _portText, value); }
    public string Token { get => _token; set => SetProperty(ref _token, value); }
    public string PythonExecutable { get => _pythonExecutable; set => SetProperty(ref _pythonExecutable, value); }
    public string ScriptPath { get => _scriptPath; set => SetProperty(ref _scriptPath, value); }
    public string ScriptArguments { get => _scriptArguments; set => SetProperty(ref _scriptArguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public EnvironmentVariableRow? SelectedEnvironmentVariable
    {
        get => _selectedEnvironmentVariable;
        set
        {
            if (SetProperty(ref _selectedEnvironmentVariable, value))
                RemoveEnvironmentCommand.RaiseCanExecuteChanged();
        }
    }
    public string ManagedStatus { get => _managedStatus; private set => SetProperty(ref _managedStatus, value); }
    public string ManagedExitCode { get => _managedExitCode; private set => SetProperty(ref _managedExitCode, value); }
    public int? ManagedProcessId { get => _managedProcessId; private set => SetProperty(ref _managedProcessId, value); }
    public bool IsManagedRunning
    {
        get => _isManagedRunning;
        private set
        {
            if (SetProperty(ref _isManagedRunning, value))
                UpdateCommandStates();
        }
    }
    public bool ElevateLiveAttach { get => _elevateLiveAttach; set => SetProperty(ref _elevateLiveAttach, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string ConnectionRecoveryMessage
    {
        get => _connectionRecoveryMessage;
        private set
        {
            if (SetProperty(ref _connectionRecoveryMessage, value))
                OnPropertyChanged(nameof(HasConnectionRecovery));
        }
    }
    public bool HasConnectionRecovery => !string.IsNullOrWhiteSpace(ConnectionRecoveryMessage);
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                UpdateCommandStates();
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                UpdateCommandStates();
        }
    }
    public bool IsAwaitingBootstrap
    {
        get => _isAwaitingBootstrap;
        private set => SetProperty(ref _isAwaitingBootstrap, value);
    }
    public string BootstrapInstructions =>
        "Paste the copied bootstrap into the paused target's VS Code Debug Console, or into its Python >>> REPL, then press Enter. Do not paste it into a shell prompt. Use Detach to cancel.";
    public string PythonVersion { get => _pythonVersion; private set => SetProperty(ref _pythonVersion, value); }
    public string Architecture { get => _architecture; private set => SetProperty(ref _architecture, value); }
    public string Executable { get => _executable; private set => SetProperty(ref _executable, value); }
    public int? TargetPid { get => _targetPid; private set => SetProperty(ref _targetPid, value); }
    public string PrivateBytes { get => _privateBytes; private set => SetProperty(ref _privateBytes, value); }
    public string WorkingSet { get => _workingSet; private set => SetProperty(ref _workingSet, value); }
    public string VirtualSize { get => _virtualSize; private set => SetProperty(ref _virtualSize, value); }
    public string PeakWorkingSet { get => _peakWorkingSet; private set => SetProperty(ref _peakWorkingSet, value); }
    public string TracemallocStatus { get => _tracemallocStatus; private set => SetProperty(ref _tracemallocStatus, value); }
    public string PythonCurrentMemory { get => _pythonCurrentMemory; private set => SetProperty(ref _pythonCurrentMemory, value); }
    public string PythonPeakMemory { get => _pythonPeakMemory; private set => SetProperty(ref _pythonPeakMemory, value); }
    public string TracemallocOverhead { get => _tracemallocOverhead; private set => SetProperty(ref _tracemallocOverhead, value); }
    public string TracemallocCoverage { get => _tracemallocCoverage; private set => SetProperty(ref _tracemallocCoverage, value); }
    public int TracebackDepth { get => _tracebackDepth; set => SetProperty(ref _tracebackDepth, Math.Clamp(value, 1, 25)); }
    public bool IsTracemallocTracing
    {
        get => _isTracemallocTracing;
        private set
        {
            if (SetProperty(ref _isTracemallocTracing, value))
                UpdateCommandStates();
        }
    }
    public MemorySnapshotRow? BeforeMemorySnapshot
    {
        get => _beforeMemorySnapshot;
        set
        {
            if (SetProperty(ref _beforeMemorySnapshot, value))
                CompareMemorySnapshotsCommand.RaiseCanExecuteChanged();
        }
    }
    public MemorySnapshotRow? AfterMemorySnapshot
    {
        get => _afterMemorySnapshot;
        set
        {
            if (SetProperty(ref _afterMemorySnapshot, value))
                CompareMemorySnapshotsCommand.RaiseCanExecuteChanged();
        }
    }
    public double RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set
        {
            if (SetProperty(ref _refreshIntervalSeconds, Math.Max(1, value)))
                NotifySnapshotState();
        }
    }
    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set
        {
            if (SetProperty(ref _autoRefreshEnabled, value))
                NotifySnapshotState();
        }
    }
    public string Breadcrumb { get => _breadcrumb; private set => SetProperty(ref _breadcrumb, value); }
    public DateTime? LastSnapshotAt { get => _lastSnapshotAt; private set { if (SetProperty(ref _lastSnapshotAt, value)) NotifySnapshotState(); } }
    public string ConnectionMode { get => _connectionMode; private set => SetProperty(ref _connectionMode, value); }
    public bool IsSnapshotStale => LastSnapshotAt is DateTime value
        && DateTime.Now - value > TimeSpan.FromSeconds(Math.Max(AutoRefreshEnabled ? RefreshIntervalSeconds * 3 : 10, 5));
    public string SnapshotStatus => LastSnapshotAt is DateTime value
        ? $"Snapshot {value:HH:mm:ss} · {(IsSnapshotStale ? "stale" : "fresh")} · {(AutoRefreshEnabled ? $"auto {RefreshIntervalSeconds:0.#}s" : "manual")}"
        : "No snapshot";
    public string SnapshotStatusBar => LastSnapshotAt is DateTime value
        ? $"Snapshot {value:HH:mm:ss} · {(IsSnapshotStale ? "stale" : "fresh")}"
        : "Snapshot —";
    public bool IsScopeLoading
    {
        get => _isScopeLoading;
        private set
        {
            if (SetProperty(ref _isScopeLoading, value))
            {
                OnPropertyChanged(nameof(ShowVariableListEmpty));
                OnPropertyChanged(nameof(VariableListStatusMessage));
            }
        }
    }
    public bool IsSearchPending
    {
        get => _isSearchPending;
        private set
        {
            if (SetProperty(ref _isSearchPending, value))
            {
                OnPropertyChanged(nameof(ShowVariableListEmpty));
                OnPropertyChanged(nameof(VariableListStatusMessage));
            }
        }
    }
    public bool ShowVariableListEmpty => !IsScopeLoading && !IsSearchPending && FilteredVariables.Count == 0;
    public string VariableListStatusMessage => _currentScope is null
        ? "Select a frame, module, console namespace, or GC source to load variables."
        : Variables.Count == 0
            ? "No variables are available in this scope."
            : "No variables match the current search and filters.";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
                return;
            if (_currentScope?.Kind == RuntimeNodeKind.GcObjects)
            {
                CancelSearchDebounce();
                return;
            }
            ScheduleSearchFilter();
        }
    }
    public string GlobalSearchQuery
    {
        get => _globalSearchQuery;
        set
        {
            if (SetProperty(ref _globalSearchQuery, value ?? ""))
                SearchRuntimeCommand.RaiseCanExecuteChanged();
        }
    }
    public string GlobalSearchStatus
    {
        get => _globalSearchStatus;
        private set => SetProperty(ref _globalSearchStatus, value);
    }
    public bool IsGlobalSearchRunning
    {
        get => _isGlobalSearchRunning;
        private set
        {
            if (!SetProperty(ref _isGlobalSearchRunning, value))
                return;
            SearchRuntimeCommand.RaiseCanExecuteChanged();
            CancelGlobalSearchCommand.RaiseCanExecuteChanged();
        }
    }
    public GlobalSearchResultRow? SelectedGlobalSearchResult
    {
        get => _selectedGlobalSearchResult;
        set
        {
            if (SetProperty(ref _selectedGlobalSearchResult, value))
                OpenGlobalSearchResultCommand.RaiseCanExecuteChanged();
        }
    }
    public string ObjectChildrenSearchText
    {
        get => _objectChildrenSearchText;
        set
        {
            if (SetProperty(ref _objectChildrenSearchText, value ?? ""))
                ApplyObjectChildrenSearch();
        }
    }
    public string ObjectTreeSearchText
    {
        get => _objectTreeSearchText;
        set
        {
            var next = value ?? "";
            var wasSearching = !string.IsNullOrWhiteSpace(_objectTreeSearchText);
            if (!SetProperty(ref _objectTreeSearchText, next))
                return;
            var isSearching = !string.IsNullOrWhiteSpace(next);
            if (!wasSearching && isSearching)
                CaptureObjectTreeExpansion();
            else if (wasSearching && !isSearching)
                RestoreObjectTreeExpansion();
            ApplyObjectTreeSearch();
        }
    }
    public string ClassTreeSearchText
    {
        get => _classTreeSearchText;
        set
        {
            var next = value ?? "";
            var wasSearching = !string.IsNullOrWhiteSpace(_classTreeSearchText);
            if (!SetProperty(ref _classTreeSearchText, next))
                return;
            CancelClassTreeSearchDebounce();
            var isSearching = !string.IsNullOrWhiteSpace(next);
            if (!wasSearching && isSearching)
                CaptureClassTreeExpansion();
            else if (wasSearching && !isSearching)
                RestoreClassTreeExpansion();
            if (isSearching && _classTreeLoadedSearchableCount >= ClassTreeSearchDebounceThreshold)
                ScheduleClassTreeSearch();
            else
                ApplyClassTreeSearch();
        }
    }
    public string ObjectChildrenSearchResultLabel
    {
        get => _objectChildrenSearchResultLabel;
        private set => SetProperty(ref _objectChildrenSearchResultLabel, value);
    }
    public string ObjectTreeSearchResultLabel
    {
        get => _objectTreeSearchResultLabel;
        private set => SetProperty(ref _objectTreeSearchResultLabel, value);
    }
    public string ClassTreeSearchResultLabel
    {
        get => _classTreeSearchResultLabel;
        private set => SetProperty(ref _classTreeSearchResultLabel, value);
    }
    public bool IsClassTreeSearchEmpty
    {
        get => _isClassTreeSearchEmpty;
        private set => SetProperty(ref _isClassTreeSearchEmpty, value);
    }
    public string PageLabel { get => _pageLabel; private set => SetProperty(ref _pageLabel, value); }
    public string FilterResultLabel { get => _filterResultLabel; private set => SetProperty(ref _filterResultLabel, value); }
    public string ScopeFilter { get => _scopeFilter; set { if (SetProperty(ref _scopeFilter, value)) ApplyFilter(); } }
    public string ChangeFilter { get => _changeFilter; set { if (SetProperty(ref _changeFilter, value)) ApplyFilter(); } }
    public string TypeFilter { get => _typeFilter; set { if (SetProperty(ref _typeFilter, value)) ApplyFilter(); } }
    public bool ArraysOnly { get => _arraysOnly; set { if (SetProperty(ref _arraysOnly, value)) ApplyFilter(); } }
    public bool ExpandableOnly { get => _expandableOnly; set { if (SetProperty(ref _expandableOnly, value)) ApplyFilter(); } }
    public bool PinnedOnly { get => _pinnedOnly; set { if (SetProperty(ref _pinnedOnly, value)) ApplyFilter(); } }
    public string LastLatency { get => _lastLatency; private set => SetProperty(ref _lastLatency, value); }
    public int SelectedWorkspaceTabIndex { get => _selectedWorkspaceTabIndex; set => SetProperty(ref _selectedWorkspaceTabIndex, Math.Max(0, value)); }
    public int SelectedObjectDetailTabIndex { get => _selectedObjectDetailTabIndex; set => SetProperty(ref _selectedObjectDetailTabIndex, Math.Max(0, value)); }

    public VariableRow? SelectedVariable
    {
        get => _selectedVariable;
        set
        {
            if (!SetProperty(ref _selectedVariable, value))
                return;
            if (_suppressVariableNavigation)
                return;
            if (value is null || value.IsRemoved)
                ClearSelectedObject();
            else
                _ = NavigateToObjectAsync(value, $"{Breadcrumb} / {value.Name}", null, addHistory: true);
        }
    }

    public ObjectTreeNode? SelectedObjectNode
    {
        get => _selectedObjectNode;
        set => SetProperty(ref _selectedObjectNode, value);
    }
    public PinnedObjectRow? SelectedPinnedObject
    {
        get => _selectedPinnedObject;
        set
        {
            if (SetProperty(ref _selectedPinnedObject, value))
                NavigatePinnedCommand.RaiseCanExecuteChanged();
        }
    }
    public string SelectedObjectPath { get => _selectedObjectPath; private set => SetProperty(ref _selectedObjectPath, value); }
    public string BackNavigationLabel { get => _backNavigationLabel; private set => SetProperty(ref _backNavigationLabel, value); }
    public string ForwardNavigationLabel { get => _forwardNavigationLabel; private set => SetProperty(ref _forwardNavigationLabel, value); }
    public string ParentNavigationLabel { get => _parentNavigationLabel; private set => SetProperty(ref _parentNavigationLabel, value); }
    public string BackNavigationToolTip { get => _backNavigationToolTip; private set => SetProperty(ref _backNavigationToolTip, value); }
    public string ForwardNavigationToolTip { get => _forwardNavigationToolTip; private set => SetProperty(ref _forwardNavigationToolTip, value); }
    public string ParentNavigationToolTip { get => _parentNavigationToolTip; private set => SetProperty(ref _parentNavigationToolTip, value); }
    public string ObjectDepthLabel { get => _objectDepthLabel; private set => SetProperty(ref _objectDepthLabel, value); }
    public string NavigationHistoryLabel { get => _navigationHistoryLabel; private set => SetProperty(ref _navigationHistoryLabel, value); }
    public string NavigationLocationDescription { get => _navigationLocationDescription; private set => SetProperty(ref _navigationLocationDescription, value); }
    public string SelectedObjectName { get => _selectedObjectName; private set => SetProperty(ref _selectedObjectName, value); }
    public string SelectedObjectPreview { get => _selectedObjectPreview; private set => SetProperty(ref _selectedObjectPreview, value); }
    public string SelectedObjectStatus { get => _selectedObjectStatus; private set => SetProperty(ref _selectedObjectStatus, value); }
    public string ClassSummary { get => _classSummary; private set => SetProperty(ref _classSummary, value); }
    public InspectorPaneState InspectorState { get => _inspectorState; private set => SetProperty(ref _inspectorState, value); }
    public bool HasSelectedObject { get => _hasSelectedObject; private set => SetProperty(ref _hasSelectedObject, value); }
    public bool HasArraySelection { get => _hasArraySelection; private set => SetProperty(ref _hasArraySelection, value); }
    public bool HasDataFrameSelection { get => _hasDataFrameSelection; private set => SetProperty(ref _hasDataFrameSelection, value); }
    public bool HasMatplotlibSelection { get => _hasMatplotlibSelection; private set => SetProperty(ref _hasMatplotlibSelection, value); }
    public bool IsSelectedObjectPinned => _currentObject is not null && _pinnedKeys.Contains(_currentObject.PinKey);

    public DataView? DataFrameRows { get => _dataFrameRows; private set => SetProperty(ref _dataFrameRows, value); }
    public DataFramePaneState DataFrameState { get => _dataFrameState; private set => SetProperty(ref _dataFrameState, value); }
    public string DataFrameShape { get => _dataFrameShape; private set => SetProperty(ref _dataFrameShape, value); }
    public string DataFrameStatus { get => _dataFrameStatus; private set => SetProperty(ref _dataFrameStatus, value); }
    public string DataFrameRowPageLabel { get => _dataFrameRowPageLabel; private set => SetProperty(ref _dataFrameRowPageLabel, value); }
    public string DataFrameColumnPageLabel { get => _dataFrameColumnPageLabel; private set => SetProperty(ref _dataFrameColumnPageLabel, value); }
    public string DataFrameErrorMessage { get => _dataFrameErrorMessage; private set => SetProperty(ref _dataFrameErrorMessage, value); }

    public ImageSource? MatplotlibPreview { get => _matplotlibPreview; private set => SetProperty(ref _matplotlibPreview, value); }
    public MatplotlibPaneState MatplotlibState { get => _matplotlibState; private set => SetProperty(ref _matplotlibState, value); }
    public string MatplotlibSourceKind { get => _matplotlibSourceKind; private set => SetProperty(ref _matplotlibSourceKind, value); }
    public string MatplotlibSourceDimensions { get => _matplotlibSourceDimensions; private set => SetProperty(ref _matplotlibSourceDimensions, value); }
    public string MatplotlibCanvasType { get => _matplotlibCanvasType; private set => SetProperty(ref _matplotlibCanvasType, value); }
    public string MatplotlibAvailabilityReason { get => _matplotlibAvailabilityReason; private set => SetProperty(ref _matplotlibAvailabilityReason, value); }
    public string MatplotlibStatus { get => _matplotlibStatus; private set => SetProperty(ref _matplotlibStatus, value); }
    public string MatplotlibNextAction { get => _matplotlibNextAction; private set => SetProperty(ref _matplotlibNextAction, value); }
    public string MatplotlibErrorMessage { get => _matplotlibErrorMessage; private set => SetProperty(ref _matplotlibErrorMessage, value); }
    public bool MatplotlibUsesOwningFigure { get => _matplotlibUsesOwningFigure; private set => SetProperty(ref _matplotlibUsesOwningFigure, value); }

    public string SelectedType { get => _selectedType; private set => SetProperty(ref _selectedType, value); }
    public string SelectedModule { get => _selectedModule; private set => SetProperty(ref _selectedModule, value); }
    public string SelectedQualifiedName { get => _selectedQualifiedName; private set => SetProperty(ref _selectedQualifiedName, value); }
    public string SelectedAddress { get => _selectedAddress; private set => SetProperty(ref _selectedAddress, value); }
    public string SelectedShallowSize { get => _selectedShallowSize; private set => SetProperty(ref _selectedShallowSize, value); }
    public string SelectedPayloadSize { get => _selectedPayloadSize; private set => SetProperty(ref _selectedPayloadSize, value); }
    public string ArrayShape { get => _arrayShape; private set => SetProperty(ref _arrayShape, value); }
    public string ArrayDType { get => _arrayDType; private set => SetProperty(ref _arrayDType, value); }
    public string ArrayStrides { get => _arrayStrides; private set => SetProperty(ref _arrayStrides, value); }
    public string ArrayDataAddress { get => _arrayDataAddress; private set => SetProperty(ref _arrayDataAddress, value); }
    public string ArrayOwner { get => _arrayOwner; private set => SetProperty(ref _arrayOwner, value); }
    public string LayoutConfidence { get => _layoutConfidence; private set => SetProperty(ref _layoutConfidence, value); }
    public string ArrayLayout { get => _arrayLayout; set => SetProperty(ref _arrayLayout, value); }
    public string ColorOrder { get => _colorOrder; set => SetProperty(ref _colorOrder, value); }
    public bool ChannelR { get => _channelR; set => SetProperty(ref _channelR, value); }
    public bool ChannelG { get => _channelG; set => SetProperty(ref _channelG, value); }
    public bool ChannelB { get => _channelB; set => SetProperty(ref _channelB, value); }
    public int SliceAxis
    {
        get => _sliceAxis;
        set
        {
            if (SetProperty(ref _sliceAxis, Math.Clamp(value, 0, 2)))
                UpdateSliceMaximum();
        }
    }
    public int SliceIndex { get => _sliceIndex; set => SetProperty(ref _sliceIndex, Math.Clamp(value, 0, SliceMaximum)); }
    public int SliceMaximum { get => _sliceMaximum; private set => SetProperty(ref _sliceMaximum, value); }
    public ImageSource? ArrayPreview { get => _arrayPreview; private set => SetProperty(ref _arrayPreview, value); }
    public int PreviewWidth { get => _previewWidth; private set { if (SetProperty(ref _previewWidth, value)) NotifyDisplaySize(); } }
    public int PreviewHeight { get => _previewHeight; private set { if (SetProperty(ref _previewHeight, value)) NotifyDisplaySize(); } }
    public double Zoom
    {
        get => _zoom;
        set
        {
            _fitMode = false;
            SetZoom(value);
        }
    }
    public double ImageDisplayWidth => Math.Max(1, PreviewWidth * Zoom);
    public double ImageDisplayHeight => Math.Max(1, PreviewHeight * Zoom);
    public string CursorCoordinate { get => _cursorCoordinate; private set => SetProperty(ref _cursorCoordinate, value); }
    public string RawPixelValue { get => _rawPixelValue; private set => SetProperty(ref _rawPixelValue, value); }
    public string DisplayPixelValue { get => _displayPixelValue; private set => SetProperty(ref _displayPixelValue, value); }
    public string Normalization { get => _normalization; private set => SetProperty(ref _normalization, value); }
    public string NormalizationMode { get => _normalizationMode; set => SetProperty(ref _normalizationMode, value); }
    public double PercentileLow { get => _percentileLow; set => SetProperty(ref _percentileLow, Math.Clamp(value, 0, 100)); }
    public double PercentileHigh { get => _percentileHigh; set => SetProperty(ref _percentileHigh, Math.Clamp(value, 0, 100)); }
    public int HistogramBinCount { get => _histogramBins; set => SetProperty(ref _histogramBins, Math.Clamp(value, 2, 512)); }
    public int HistogramChannel { get => _histogramChannel; set => SetProperty(ref _histogramChannel, Math.Max(0, value)); }
    public int TileX { get => _tileX; set => SetProperty(ref _tileX, Math.Max(0, value)); }
    public int TileY { get => _tileY; set => SetProperty(ref _tileY, Math.Max(0, value)); }
    public int TileWidth { get => _tileWidth; set => SetProperty(ref _tileWidth, Math.Clamp(value, 1, 1024)); }
    public int TileHeight { get => _tileHeight; set => SetProperty(ref _tileHeight, Math.Clamp(value, 1, 1024)); }
    public int SourceImageWidth { get => _sourceImageWidth; private set => SetProperty(ref _sourceImageWidth, value); }
    public int SourceImageHeight { get => _sourceImageHeight; private set => SetProperty(ref _sourceImageHeight, value); }
    public string HistogramSummary { get => _histogramSummary; private set => SetProperty(ref _histogramSummary, value); }
    public bool ExecutionMonitoringAvailable
    {
        get => _executionMonitoringAvailable;
        private set
        {
            if (SetProperty(ref _executionMonitoringAvailable, value))
                UpdateCommandStates();
        }
    }
    public bool ExecutionMonitoringActive
    {
        get => _executionMonitoringActive;
        private set
        {
            if (SetProperty(ref _executionMonitoringActive, value))
                UpdateCommandStates();
        }
    }
    public string ExecutionStatus { get => _executionStatus; private set => SetProperty(ref _executionStatus, value); }
    public string ExecutionPathPrefix { get => _executionPathPrefix; set => SetProperty(ref _executionPathPrefix, value); }
    public int ExecutionBufferCapacity { get => _executionBufferCapacity; set => SetProperty(ref _executionBufferCapacity, Math.Clamp(value, 100, 10000)); }
    public long ExecutionDroppedCount { get => _executionDroppedCount; private set => SetProperty(ref _executionDroppedCount, value); }
    public bool MonitorPyStart { get => _monitorPyStart; set => SetProperty(ref _monitorPyStart, value); }
    public bool MonitorPyReturn { get => _monitorPyReturn; set => SetProperty(ref _monitorPyReturn, value); }
    public bool MonitorPyYield { get => _monitorPyYield; set => SetProperty(ref _monitorPyYield, value); }
    public bool MonitorPyUnwind { get => _monitorPyUnwind; set => SetProperty(ref _monitorPyUnwind, value); }
    public bool MonitorRaise { get => _monitorRaise; set => SetProperty(ref _monitorRaise, value); }
    public bool MonitorLine { get => _monitorLine; set => SetProperty(ref _monitorLine, value); }
    public bool MonitorCall { get => _monitorCall; set => SetProperty(ref _monitorCall, value); }

    public async Task SelectTreeNodeAsync(RuntimeTreeNode? node)
    {
        if (node?.Kind is RuntimeNodeKind.Scope or RuntimeNodeKind.Module or RuntimeNodeKind.ConsoleNamespace or RuntimeNodeKind.GcObjects)
            await LoadScopeAsync(node, resetPage: true);
        else if (node?.Kind == RuntimeNodeKind.Frame)
        {
            var locals = node.Children.FirstOrDefault(child => child.ScopeType == "locals");
            if (locals is not null)
                await LoadScopeAsync(locals, resetPage: true);
        }
    }

    public async Task LoadScopeAsync(
        RuntimeTreeNode node,
        bool resetPage = false,
        bool showLoadingOverlay = true,
        bool preserveSelectionAcrossScopeChange = false)
    {
        var isModule = node.Kind == RuntimeNodeKind.Module && node.ModuleName is not null;
        var isFrameScope = node.Kind == RuntimeNodeKind.Scope && node.FrameHandle is not null && node.ScopeType is not null;
        var isConsoleNamespace = node.Kind == RuntimeNodeKind.ConsoleNamespace
            && node.ConsoleHandle is not null
            && node.ConsoleAttributeName is not null;
        var isGcObjects = node.Kind == RuntimeNodeKind.GcObjects;
        if (!IsConnected || (!isModule && !isFrameScope && !isConsoleNamespace && !isGcObjects))
            return;
        if (resetPage)
            _pageOffset = 0;
        _currentScope = node;
        OnPropertyChanged(nameof(VariableListStatusMessage));
        OnPropertyChanged(nameof(ShowVariableListEmpty));
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts?.Token ?? CancellationToken.None);
        var generation = Interlocked.Increment(ref _selectionGeneration);
        var token = _selectionCts.Token;
        // Automatic refresh keeps the existing table interactive. Initial, navigational,
        // and explicit refreshes still expose the full loading state.
        IsScopeLoading = showLoadingOverlay;
        Breadcrumb = isGcObjects
            ? "GC-tracked objects"
            : isModule
                ? $"Modules / {node.ModuleName}"
                : isConsoleNamespace
                    ? $"Console namespaces / {node.Label}"
                    : $"Threads / {node.Label}";
        try
        {
            var parameters = isGcObjects
                ? new JsonObject
                {
                    ["query"] = SearchText,
                    ["offset"] = _pageOffset,
                    ["pageSize"] = PageSize,
                    ["maxObjects"] = GcScanLimit,
                }
                : isModule
                ? new JsonObject
                {
                    ["moduleName"] = node.ModuleName,
                    ["offset"] = _pageOffset,
                    ["pageSize"] = PageSize,
                }
                : isConsoleNamespace
                ? new JsonObject
                {
                    ["consoleHandle"] = node.ConsoleHandle,
                    ["attributeName"] = node.ConsoleAttributeName,
                    ["offset"] = _pageOffset,
                    ["pageSize"] = PageSize,
                }
                : new JsonObject
                {
                    ["frameHandle"] = node.FrameHandle,
                    ["scopeType"] = node.ScopeType,
                    ["offset"] = _pageOffset,
                    ["pageSize"] = PageSize,
            };
            var method = isGcObjects
                ? "gc.listObjects"
                : isModule
                    ? "modules.listNamespace"
                    : isConsoleNamespace
                        ? "consoles.listNamespace"
                        : "scopes.list";
            using var handleResponse = await RequestHandleResponseAsync(method, parameters, token);
            var frame = handleResponse.Frame;
            if (generation != _selectionGeneration || token.IsCancellationRequested)
                return;
            try
            {
                ApplyScopeResult(
                    frame.Header["result"]!.AsObject(),
                    node,
                    preserveSelectionAcrossScopeChange);
                if (!showLoadingOverlay)
                    await RefreshSelectedDetailInBackgroundAsync(token);
            }
            finally
            {
                RefreshObjectHandleReferences();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            if (generation == _selectionGeneration)
                IsScopeLoading = false;
        }
    }

    public void UpdateFitViewport(double width, double height)
    {
        _fitViewportWidth = Math.Max(1, width - 24);
        _fitViewportHeight = Math.Max(1, height - 24);
        if (_fitMode)
            ApplyFitZoom();
    }

    public void UpdateCursor(int x, int y)
    {
        if (PreviewWidth == 0 || PreviewHeight == 0)
            return;
        x = _previewOriginX + Math.Clamp(x, 0, PreviewWidth - 1) * _previewColumnStep;
        y = _previewOriginY + Math.Clamp(y, 0, PreviewHeight - 1) * _previewRowStep;
        CursorCoordinate = $"x={x}, y={y}";
    }

    public async Task LoadPixelAsync(int x, int y)
    {
        if (!IsConnected || _arrayHandle is null || PreviewWidth == 0 || PreviewHeight == 0)
            return;
        x = Math.Clamp(x, 0, PreviewWidth - 1);
        y = Math.Clamp(y, 0, PreviewHeight - 1);
        x = _previewOriginX + Math.Clamp(x, 0, PreviewWidth - 1) * _previewColumnStep;
        y = _previewOriginY + Math.Clamp(y, 0, PreviewHeight - 1) * _previewRowStep;
        CursorCoordinate = $"x={x}, y={y}";
        try
        {
            var response = await RequestAsync("arrays.pixel", new JsonObject
            {
                ["handleId"] = _arrayHandle,
                ["coordinates"] = new JsonArray(y, x),
                ["layout"] = ArrayLayout,
                ["sliceAxis"] = SliceAxis,
                ["sliceIndex"] = SliceIndex,
            }, _connectionCts?.Token ?? CancellationToken.None);
            var value = response.Header["result"]!["value"];
            RawPixelValue = value?.ToJsonString() ?? "null";
            DisplayPixelValue = FormatDisplayValue(value);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task LaunchAsync()
    {
        if (IsConnected || IsBusy || IsManagedRunning)
            return;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!TryGetConnectionSettings(out var port))
            return;
        if (string.IsNullOrWhiteSpace(PythonExecutable))
        {
            ErrorMessage = "Select a Python executable.";
            return;
        }
        if (string.IsNullOrWhiteSpace(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
        {
            ErrorMessage = "Select an existing working directory.";
            return;
        }
        var script = Path.IsPathRooted(ScriptPath) ? ScriptPath : Path.GetFullPath(ScriptPath, WorkingDirectory);
        if (!File.Exists(script))
        {
            ErrorMessage = "Select an existing Python script.";
            return;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in LaunchEnvironment)
        {
            var name = row.Name.Trim();
            if (string.IsNullOrEmpty(name) || name.Contains('='))
            {
                ErrorMessage = $"Invalid environment variable name: {row.Name}";
                return;
            }
            environment[name] = row.Value;
        }

        ErrorMessage = "";
        LaunchOutput.Clear();
        ManagedExitCode = "—";
        ManagedStatus = "Starting";
        Status = "Starting managed target";
        IsBusy = true;
        _connectionCts = new CancellationTokenSource();
        Task<JsonObject>? attachTask = null;
        try
        {
            attachTask = _session.AttachAsync(port, Token, expectedPid: null, _connectionCts.Token);
            if (attachTask.IsFaulted)
                await attachTask;

            var options = new ManagedLaunchOptions(
                PythonExecutable,
                script,
                Path.GetFullPath(WorkingDirectory),
                WindowsCommandLine.ParseArguments(ScriptArguments),
                environment,
                Path.Combine(FindRepositoryRoot(), "agent"),
                port,
                Token);
            var handle = await _launcher.StartAsync(options, _connectionCts.Token);
            ManagedProcessId = handle.ProcessId;
            IsManagedRunning = true;
            ManagedStatus = $"Running (PID {handle.ProcessId})";

            var completed = await Task.WhenAny(attachTask, handle.Completion);
            if (completed == handle.Completion)
            {
                var exitCode = await handle.Completion;
                _connectionCts.Cancel();
                try { await attachTask; } catch (OperationCanceledException) { }
                throw new InvalidOperationException($"Python exited with code {exitCode} before the inspector agent connected.");
            }

            var runtime = await attachTask;
            var actualPid = runtime["pid"]!.GetValue<int>();
            if (actualPid != handle.ProcessId)
            {
                await DetachInspectorSessionAsync();
                await _launcher.StopAsync();
                throw new InvalidOperationException($"Connected PID {actualPid} does not match launched PID {handle.ProcessId}.");
            }
            ApplyRuntime(runtime);
            IsConnected = true;
            ConnectionMode = "Managed launch";
            Status = "Connected (managed launch)";
            await LoadRuntimeTreeAsync(_connectionCts.Token, autoSelectMain: true);
            StartRefreshLoop();
        }
        catch (OperationCanceledException)
        {
            Status = "Managed launch cancelled";
        }
        catch (Exception exception)
        {
            Status = "Managed launch failed";
            SetError(exception);
            if (_launcher.IsRunning)
                await _launcher.StopAsync();
            if (_session.IsConnected)
                await DetachInspectorSessionAsync();
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private async Task StopManagedAsync()
    {
        _connectionCts?.Cancel();
        if (_session.IsConnected)
            await DetachInspectorSessionAsync();
        await _launcher.StopAsync();
        ResetConnection("Managed target stopped");
    }

    private async Task RestartManagedAsync()
    {
        if (IsBusy)
            return;
        if (_session.IsConnected)
            await DetachInspectorSessionAsync();
        if (_launcher.IsRunning)
            await _launcher.StopAsync();
        ResetConnection("Restarting managed target");
        await LaunchAsync();
    }

    private async Task AttachAsync()
    {
        if (IsConnected || IsBusy)
            return;
        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
        {
            ErrorMessage = "Port must be between 1 and 65535.";
            return;
        }
        if (Token.Length < 64)
        {
            ErrorMessage = "Token must contain at least 64 characters (256 bits in hex).";
            return;
        }
        ErrorMessage = "";
        IsBusy = true;
        Status = "Waiting for cooperative target";
        _connectionCts = new CancellationTokenSource();
        try
        {
            var runtime = await _session.AttachAsync(port, Token, SelectedProcess?.Id, _connectionCts.Token);
            ApplyRuntime(runtime);
            IsConnected = true;
            ConnectionMode = "Cooperative listener";
            Status = "Connected";
            await LoadRuntimeTreeAsync(_connectionCts.Token, autoSelectMain: true);
            StartRefreshLoop();
        }
        catch (OperationCanceledException)
        {
            Status = "Disconnected";
        }
        catch (Exception exception)
        {
            Status = "Attach failed";
            SetError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task QuickAttachAsync()
    {
        if (SelectedProcess is null || IsConnected || IsBusy)
            return;
        ConnectionRecoveryMessage = "";

        if (SelectedProcess.PythonVersion is Version unsupported
            && (unsupported.Major != 3 || unsupported.Minor < 10))
        {
            Status = "Unsupported Python version";
            ErrorMessage = "Quick Attach requires CPython 3.10 or newer.";
            return;
        }

        if (SelectedProcess.PythonVersion is { Major: 3, Minor: >= 14 })
        {
            await LiveAttachAsync();
            if (IsConnected || Status == "Live attach cancelled" || HasConnectionRecovery)
                return;
        }

        await OnePasteAttachAsync();
    }

    private async Task OnePasteAttachAsync()
    {
        if (SelectedProcess is null || IsConnected || IsBusy)
            return;

        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!TryGetConnectionSettings(out var port))
            return;

        var agentDirectory = Path.Combine(FindRepositoryRoot(), "agent");
        if (!Directory.Exists(agentDirectory))
        {
            ErrorMessage = $"Bundled Python Agent was not found: {agentDirectory}";
            return;
        }

        ErrorMessage = "";
        ConnectionRecoveryMessage = "";
        IsBusy = true;
        _connectionCts = new CancellationTokenSource();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token);
        timeoutCts.CancelAfter(_onePasteAttachTimeout);
        Task<JsonObject>? attachTask = null;
        try
        {
            attachTask = _session.AttachAsync(port, Token, SelectedProcess.Id, timeoutCts.Token);
            _clipboardService.SetText(ReplBootstrap.Build(agentDirectory, port, Token));
            IsAwaitingBootstrap = true;
            ConnectionMode = "Quick Attach · awaiting bootstrap";
            Status = "Bootstrap copied · paste it into Debug Console or Python REPL";

            var runtime = await attachTask;
            IsAwaitingBootstrap = false;
            ApplyRuntime(runtime);
            IsConnected = true;
            ConnectionMode = "Quick Attach · REPL bootstrap";
            Status = "Connected · showing __main__ globals";
            await LoadRuntimeTreeAsync(_connectionCts.Token, autoSelectMain: true);
            StartRefreshLoop();
        }
        catch (OperationCanceledException) when (!_connectionCts.IsCancellationRequested)
        {
            Status = "Quick attach timed out";
            ErrorMessage = $"No Agent connected within {_onePasteAttachTimeout.TotalSeconds:0.#} seconds. Run Quick Attach again, paste the copied line into the target's VS Code Debug Console or Python REPL, and press Enter.";
            ConnectionRecoveryMessage = "";
            _connectionCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            Status = "Quick attach cancelled";
            ErrorMessage = "";
            ConnectionRecoveryMessage = "";
        }
        catch (Exception exception)
        {
            Status = "Quick attach failed";
            SetError(exception);
            SetConnectionRecovery(exception);
            _connectionCts.Cancel();
        }
        finally
        {
            IsAwaitingBootstrap = false;
            if (!IsConnected && attachTask is not null)
            {
                try { await attachTask; } catch { }
            }
            if (!IsConnected)
                ConnectionMode = "Not connected";
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private async Task LiveAttachAsync()
    {
        if (IsConnected || IsBusy)
            return;
        if (SelectedProcess is null)
        {
            ErrorMessage = "Select a running Python process.";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedProcess.ExecutablePath) || !File.Exists(SelectedProcess.ExecutablePath))
        {
            ErrorMessage = "The selected process executable is unavailable. Try running PyMonitor with sufficient permission.";
            return;
        }

        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!TryGetConnectionSettings(out var port))
            return;

        ErrorMessage = "";
        ConnectionRecoveryMessage = "";
        IsBusy = true;
        Status = $"Scheduling live attach to PID {SelectedProcess.Id}";
        _connectionCts = new CancellationTokenSource();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        Task<JsonObject>? attachTask = null;
        try
        {
            attachTask = _session.AttachAsync(port, Token, SelectedProcess.Id, timeoutCts.Token);
            if (attachTask.IsFaulted)
                await attachTask;

            await using var lease = await _liveAttachService.StartAsync(new LiveAttachOptions(
                SelectedProcess.Id,
                SelectedProcess.ExecutablePath,
                Path.Combine(FindRepositoryRoot(), "agent"),
                port,
                Token,
                ElevateLiveAttach), timeoutCts.Token);

            Status = "Waiting for Python safe point — press Enter once if the REPL is idle";
            var runtime = await attachTask;
            ApplyRuntime(runtime);
            IsConnected = true;
            ConnectionMode = "Live Attach";
            Status = "Connected (live attach)";
            await LoadRuntimeTreeAsync(_connectionCts.Token, autoSelectMain: true);
            StartRefreshLoop();
        }
        catch (OperationCanceledException)
        {
            if (_connectionCts.IsCancellationRequested)
                Status = "Live attach cancelled";
            else
            {
                Status = "Live attach timed out";
                ErrorMessage = "The target did not reach a Python safe execution point within 30 seconds.";
            }
            _connectionCts.Cancel();
        }
        catch (Exception exception)
        {
            Status = "Live attach failed";
            SetError(exception);
            SetConnectionRecovery(exception);
            _connectionCts.Cancel();
        }
        finally
        {
            if (!IsConnected && attachTask is not null)
            {
                try { await attachTask; } catch { }
            }
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private async Task DetachAsync()
    {
        var cancellingQuickAttach = IsAwaitingBootstrap;
        _connectionCts?.Cancel();
        _refreshCts?.Cancel();
        try
        {
            await DetachInspectorSessionAsync();
        }
        finally
        {
            ResetConnection(cancellingQuickAttach ? "Quick attach cancelled" : "Disconnected");
        }
    }

    private async Task RefreshAsync()
    {
        if (!IsConnected)
            return;
        try
        {
            await LoadRuntimeTreeAsync(_connectionCts?.Token ?? CancellationToken.None);
            if (_currentScope is not null)
                await LoadScopeAsync(_currentScope);
            if (_currentObject is not null)
                await LoadObjectContextAsync(
                    _currentObject,
                    preserveSearches: true,
                    preserveDetailTab: true);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task StartTracingAsync()
    {
        try
        {
            var response = await RequestAsync("memory.start", new JsonObject
            {
                ["tracebackDepth"] = TracebackDepth,
            }, _connectionCts?.Token ?? CancellationToken.None);
            ApplyMemoryStatus(response.Header["result"]!.AsObject());
            MemorySnapshots.Clear();
            MemoryStatistics.Clear();
            BeforeMemorySnapshot = null;
            AfterMemorySnapshot = null;
            await RefreshMemoryAsync(_connectionCts?.Token ?? CancellationToken.None, includeStatistics: true);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task StopTracingAsync()
    {
        try
        {
            var response = await RequestAsync("memory.stop", cancellationToken: _connectionCts?.Token ?? CancellationToken.None);
            ApplyMemoryStatus(response.Header["result"]!.AsObject());
            MemorySnapshots.Clear();
            MemoryStatistics.Clear();
            BeforeMemorySnapshot = null;
            AfterMemorySnapshot = null;
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task TakeMemorySnapshotAsync()
    {
        try
        {
            var response = await RequestAsync("memory.snapshot", new JsonObject
            {
                ["label"] = $"Snapshot {++_snapshotSequence}",
            }, _connectionCts?.Token ?? CancellationToken.None);
            var row = ParseMemorySnapshot(response.Header["result"]!.AsObject());
            MemorySnapshots.Add(row);
            while (MemorySnapshots.Count > 8)
                MemorySnapshots.RemoveAt(0);
            BeforeMemorySnapshot ??= row;
            AfterMemorySnapshot = row;
            await LoadMemoryStatisticsAsync(_connectionCts?.Token ?? CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task CompareMemorySnapshotsAsync()
    {
        if (BeforeMemorySnapshot is null || AfterMemorySnapshot is null)
            return;
        try
        {
            var response = await RequestAsync("memory.diff", new JsonObject
            {
                ["beforeSnapshotId"] = BeforeMemorySnapshot.SnapshotId,
                ["afterSnapshotId"] = AfterMemorySnapshot.SnapshotId,
                ["limit"] = 100,
                ["groupBy"] = "lineno",
            }, _connectionCts?.Token ?? CancellationToken.None);
            ApplyMemoryStatistics(response.Header["result"]!["items"]!.AsArray(), includeDiff: true);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task RefreshMemoryAsync(CancellationToken token, bool includeStatistics = false)
    {
        RefreshProcessMemory();
        if (!IsConnected)
            return;
        var response = await RequestAsync("memory.status", cancellationToken: token);
        ApplyMemoryStatus(response.Header["result"]!.AsObject());
        if (IsTracemallocTracing && includeStatistics)
            await LoadMemoryStatisticsAsync(token);
        AddMemoryTimelineSample();
    }

    private async Task LoadMemoryStatisticsAsync(CancellationToken token)
    {
        var response = await RequestAsync("memory.statistics", new JsonObject
        {
            ["limit"] = 100,
            ["groupBy"] = "lineno",
        }, token);
        ApplyMemoryStatistics(response.Header["result"]!["items"]!.AsArray(), includeDiff: false);
    }

    private void ApplyMemoryStatus(JsonObject status)
    {
        var tracing = status["tracing"]?.GetValue<bool>() ?? false;
        IsTracemallocTracing = tracing;
        var depth = status["tracebackDepth"]?.GetValue<int>() ?? 0;
        TracemallocStatus = tracing ? $"Running (depth {depth})" : "Stopped";
        _pythonCurrentBytes = tracing ? status["currentBytes"]?.GetValue<long>() ?? 0 : null;
        _pythonPeakBytes = tracing ? status["peakBytes"]?.GetValue<long>() ?? 0 : null;
        PythonCurrentMemory = _pythonCurrentBytes is long current ? FormatBytes(current) : "—";
        PythonPeakMemory = _pythonPeakBytes is long peak ? FormatBytes(peak) : "—";
        TracemallocOverhead = tracing ? FormatBytes(status["overheadBytes"]?.GetValue<long>() ?? 0) : "—";
        var startedAt = status["startedAt"]?.GetValue<string>();
        var startedByInspector = status["startedByInspector"]?.GetValue<bool>() ?? false;
        TracemallocCoverage = !tracing
            ? "Not tracing"
            : startedAt is not null
                ? $"Allocations after {startedAt} ({(startedByInspector ? "started by Inspector" : "pre-existing")})"
                : "Tracing was already active before Inspector; exact start time is unknown.";
        if (!tracing)
            MemoryStatistics.Clear();
    }

    private void ApplyMemoryStatistics(JsonArray items, bool includeDiff)
    {
        var rows = items.Select(item => new MemoryStatisticRow(
            item!["filename"]?.GetValue<string>() ?? "<unknown>",
            item["lineNumber"]?.GetValue<int>() ?? 0,
            item["sizeBytes"]?.GetValue<long>() ?? 0,
            item["count"]?.GetValue<long>() ?? 0,
            includeDiff ? item["sizeDiffBytes"]?.GetValue<long>() : null,
            includeDiff ? item["countDiff"]?.GetValue<long>() : null));
        Replace(MemoryStatistics, rows);
    }

    private static MemorySnapshotRow ParseMemorySnapshot(JsonObject snapshot)
    {
        var createdAt = DateTime.TryParse(snapshot["createdAt"]?.GetValue<string>(), out var parsed)
            ? parsed.ToLocalTime()
            : DateTime.Now;
        return new MemorySnapshotRow(
            snapshot["snapshotId"]!.GetValue<string>(),
            snapshot["label"]?.GetValue<string>() ?? "Snapshot",
            createdAt,
            snapshot["traceCount"]?.GetValue<long>() ?? 0,
            snapshot["totalBytes"]?.GetValue<long>() ?? 0);
    }

    private void AddMemoryTimelineSample()
    {
        MemoryTimeline.Add(new MemorySampleRow(
            DateTime.Now,
            _lastProcessMemory?.WorkingSetBytes,
            _lastProcessMemory?.PrivateBytes,
            _lastProcessMemory?.VirtualBytes,
            _pythonCurrentBytes,
            _pythonPeakBytes));
        while (MemoryTimeline.Count > 300)
            MemoryTimeline.RemoveAt(0);
    }

    private async Task StartExecutionMonitoringAsync()
    {
        var names = new JsonArray();
        if (MonitorPyStart) names.Add("PY_START");
        if (MonitorPyReturn) names.Add("PY_RETURN");
        if (MonitorPyYield) names.Add("PY_YIELD");
        if (MonitorPyUnwind) names.Add("PY_UNWIND");
        if (MonitorRaise) names.Add("RAISE");
        if (MonitorLine) names.Add("LINE");
        if (MonitorCall) names.Add("CALL");
        if (names.Count == 0)
        {
            ErrorMessage = "Select at least one execution event.";
            return;
        }
        try
        {
            var response = await RequestAsync("execution.start", new JsonObject
            {
                ["eventNames"] = names,
                ["bufferCapacity"] = ExecutionBufferCapacity,
                ["includePathPrefix"] = ExecutionPathPrefix,
            }, _connectionCts?.Token ?? CancellationToken.None);
            ExecutionEvents.Clear();
            _lastExecutionSequence = 0;
            ApplyExecutionStatus(response.Header["result"]!.AsObject());
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task StopExecutionMonitoringAsync()
    {
        try
        {
            var response = await RequestAsync("execution.stop", cancellationToken: _connectionCts?.Token ?? CancellationToken.None);
            ApplyExecutionStatus(response.Header["result"]!.AsObject());
            await LoadExecutionEventsAsync(_connectionCts?.Token ?? CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task RefreshExecutionEventsAsync()
    {
        try
        {
            await RefreshExecutionStatusAsync(_connectionCts?.Token ?? CancellationToken.None);
            if (ExecutionMonitoringAvailable)
                await LoadExecutionEventsAsync(_connectionCts?.Token ?? CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task ClearExecutionEventsAsync()
    {
        try
        {
            var response = await RequestAsync("execution.clear", cancellationToken: _connectionCts?.Token ?? CancellationToken.None);
            ExecutionEvents.Clear();
            ExecutionDroppedCount = 0;
            ApplyExecutionStatus(response.Header["result"]!.AsObject());
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task RefreshExecutionStatusAsync(CancellationToken token)
    {
        var response = await RequestAsync("execution.status", cancellationToken: token);
        ApplyExecutionStatus(response.Header["result"]!.AsObject());
    }

    private async Task LoadExecutionEventsAsync(CancellationToken token)
    {
        var response = await RequestAsync("execution.list", new JsonObject
        {
            ["afterSequence"] = _lastExecutionSequence,
            ["limit"] = 1000,
        }, token);
        var result = response.Header["result"]!.AsObject();
        foreach (var item in result["items"]!.AsArray())
        {
            var nanoseconds = item!["timestampUnixNanoseconds"]!.GetValue<long>();
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(nanoseconds / 1_000_000).LocalDateTime;
            ExecutionEvents.Add(new ExecutionEventRow(
                item["sequence"]!.GetValue<long>(),
                timestamp,
                item["threadId"]!.GetValue<long>(),
                item["eventName"]!.GetValue<string>(),
                item["qualifiedName"]?.GetValue<string>() ?? item["functionName"]!.GetValue<string>(),
                item["filename"]!.GetValue<string>(),
                item["lineNumber"]!.GetValue<int>(),
                item["instructionOffset"]?.GetValue<int>(),
                item["detail"]?.GetValue<string>()));
        }
        _lastExecutionSequence = result["nextSequence"]?.GetValue<long>() ?? _lastExecutionSequence;
        ExecutionDroppedCount = result["droppedCount"]?.GetValue<long>() ?? 0;
        var capacity = result["bufferCapacity"]?.GetValue<int>() ?? ExecutionBufferCapacity;
        while (ExecutionEvents.Count > capacity)
            ExecutionEvents.RemoveAt(0);
        ExecutionStatus = $"{(ExecutionMonitoringActive ? "Active" : "Stopped")} · "
            + $"{ExecutionEvents.Count:N0}/{capacity:N0} buffered · {ExecutionDroppedCount:N0} dropped";
    }

    private void ApplyExecutionStatus(JsonObject status)
    {
        ExecutionMonitoringAvailable = status["available"]?.GetValue<bool>() ?? false;
        ExecutionMonitoringActive = status["active"]?.GetValue<bool>() ?? false;
        ExecutionDroppedCount = status["droppedCount"]?.GetValue<long>() ?? ExecutionDroppedCount;
        if (!ExecutionMonitoringAvailable)
        {
            ExecutionStatus = "Unavailable: Python 3.12 or newer is required.";
            return;
        }
        var toolId = status["toolId"]?.GetValue<int>();
        var buffered = status["bufferedCount"]?.GetValue<int>() ?? ExecutionEvents.Count;
        var capacity = status["bufferCapacity"]?.GetValue<int>() ?? ExecutionBufferCapacity;
        ExecutionStatus = ExecutionMonitoringActive
            ? $"Active on tool ID {toolId} · {buffered:N0}/{capacity:N0} buffered · {ExecutionDroppedCount:N0} dropped"
            : $"Stopped · {buffered:N0}/{capacity:N0} buffered · {ExecutionDroppedCount:N0} dropped";
    }

    private async Task PreviousPageAsync()
    {
        if (_currentScope is null || _pageOffset == 0)
            return;
        _pageOffset = Math.Max(0, _pageOffset - PageSize);
        await LoadScopeAsync(_currentScope);
    }

    private async Task NextPageAsync()
    {
        if (_currentScope is null || _pageOffset + PageSize >= _pageTotal)
            return;
        _pageOffset += PageSize;
        await LoadScopeAsync(_currentScope);
    }

    private async Task SearchCurrentAsync()
    {
        if (_currentScope?.Kind == RuntimeNodeKind.GcObjects)
            await LoadScopeAsync(_currentScope, resetPage: true);
        else
            ApplyFilter();
    }

    private async Task SearchRuntimeAsync()
    {
        var query = GlobalSearchQuery.Trim();
        if (!IsConnected || string.IsNullOrEmpty(query))
            return;
        var isAddressSearch = query.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        ErrorMessage = "";

        _runtimeSearchCts?.Cancel();
        var searchCts = CancellationTokenSource.CreateLinkedTokenSource(
            _connectionCts?.Token ?? CancellationToken.None);
        _runtimeSearchCts = searchCts;
        var token = searchCts.Token;
        IsGlobalSearchRunning = true;
        GlobalSearchStatus = isAddressSearch
            ? $"Finding variables, containers, instances, and classes that reference {query}…"
            : $"Searching the connected runtime recursively for “{query}”…";
        SelectedGlobalSearchResult = null;
        GlobalSearchResults.Clear();
        RefreshObjectHandleReferences();
        try
        {
            var method = isAddressSearch ? "runtime.findAddress" : "runtime.search";
            var parameters = new JsonObject
            {
                [isAddressSearch ? "address" : "query"] = query,
                ["exhaustive"] = true,
            };
            using var response = await RequestHandleResponseAsync(method, parameters, token);
            if (!ReferenceEquals(_runtimeSearchCts, searchCts) || token.IsCancellationRequested)
                return;
            var result = response.Frame.Header["result"]!.AsObject();
            var rows = result["items"]!.AsArray()
                .Select(item => CreateGlobalSearchResult(item!.AsObject()))
                .ToArray();
            Replace(GlobalSearchResults, rows);
            SelectedGlobalSearchResult = GlobalSearchResults.FirstOrDefault();
            var complete = result["scanComplete"]?.GetValue<bool>() == true
                && result["responseTruncated"]?.GetValue<bool>() != true;
            var scanned = result["objectsScanned"]?.GetValue<int>() ?? 0;
            var roots = result["rootsScanned"]?.GetValue<int>() ?? 0;
            var duration = result["durationMilliseconds"]?.GetValue<double>() ?? 0;
            if (isAddressSearch)
            {
                var address = result["addressHex"]?.GetValue<string>() ?? query;
                var targetFound = result["targetFound"]?.GetValue<bool>() == true;
                var referenceCount = rows.Count(row => row.Relation != "GC-tracked object");
                var noun = referenceCount == 1 ? "reference location" : "reference locations";
                GlobalSearchStatus = complete
                    ? targetFound
                        ? referenceCount > 0
                            ? $"{referenceCount:N0} {noun} for {address} · {scanned:N0} objects in {roots:N0} runtime roots · {duration:N0} ms"
                            : $"Object {address} found; no structural references located · {scanned:N0} objects scanned · {duration:N0} ms"
                        : $"No live object or references found for {address} · {scanned:N0} objects scanned · {duration:N0} ms"
                    : $"{referenceCount:N0} {noun} for {address} (incomplete results) · {scanned:N0} objects in {roots:N0} runtime roots · "
                        + BuildGlobalSearchLimitSummary(result);
            }
            else
            {
                GlobalSearchStatus = complete
                    ? $"{rows.Length:N0} results · {scanned:N0} objects in {roots:N0} runtime roots · {duration:N0} ms"
                    : $"{rows.Length:N0} results (incomplete) · {scanned:N0} objects in {roots:N0} runtime roots · "
                        + BuildGlobalSearchLimitSummary(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_runtimeSearchCts, searchCts))
                GlobalSearchStatus = "Runtime search canceled. No partial results were applied.";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_runtimeSearchCts, searchCts))
            {
                GlobalSearchStatus = $"Runtime search failed: {exception.Message}";
                SetError(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_runtimeSearchCts, searchCts))
            {
                _runtimeSearchCts = null;
                IsGlobalSearchRunning = false;
                RefreshObjectHandleReferences();
            }
            searchCts.Dispose();
        }
    }

    private async Task OpenGlobalSearchResultAsync()
    {
        var result = SelectedGlobalSearchResult;
        if (!IsConnected || result is null)
            return;

        if (result.Kind == "module" && result.ModuleName is not null)
        {
            var moduleNode = EnumerateRuntimeNodes().FirstOrDefault(node =>
                node.Kind == RuntimeNodeKind.Module
                && string.Equals(node.ModuleName, result.ModuleName, StringComparison.Ordinal));
            if (moduleNode is not null)
                await LoadScopeAsync(moduleNode, resetPage: true);
            SelectedWorkspaceTabIndex = 0;
            return;
        }
        if (result.Kind == "frame" && result.FrameHandle is not null && result.ScopeType is not null)
        {
            var scopeNode = EnumerateRuntimeNodes().FirstOrDefault(node =>
                node.Kind == RuntimeNodeKind.Scope
                && string.Equals(node.FrameHandle, result.FrameHandle, StringComparison.Ordinal)
                && string.Equals(node.ScopeType, result.ScopeType, StringComparison.Ordinal));
            scopeNode ??= new RuntimeTreeNode(result.Location, RuntimeNodeKind.Scope)
            {
                FrameHandle = result.FrameHandle,
                ScopeType = result.ScopeType,
            };
            await LoadScopeAsync(scopeNode, resetPage: true);
            SelectedWorkspaceTabIndex = 0;
            return;
        }
        if (result.Kind == "console" && result.ConsoleHandle is not null && result.ConsoleAttributeName is not null)
        {
            var consoleNode = EnumerateRuntimeNodes().FirstOrDefault(node =>
                node.Kind == RuntimeNodeKind.ConsoleNamespace
                && string.Equals(node.ConsoleHandle, result.ConsoleHandle, StringComparison.Ordinal)
                && string.Equals(node.ConsoleAttributeName, result.ConsoleAttributeName, StringComparison.Ordinal));
            consoleNode ??= new RuntimeTreeNode(result.Location, RuntimeNodeKind.ConsoleNamespace)
            {
                ConsoleHandle = result.ConsoleHandle,
                ConsoleAttributeName = result.ConsoleAttributeName,
                ScopeType = "console",
            };
            await LoadScopeAsync(consoleNode, resetPage: true);
            SelectedWorkspaceTabIndex = 0;
            return;
        }
        if (result.Value is null)
            return;

        await NavigateToObjectAsync(result.Value, result.ObjectPath, null, addHistory: true);
        SelectedWorkspaceTabIndex = 0;
        if (result.Kind is "class" or "method" or "property" or "class attribute")
        {
            SelectedObjectDetailTabIndex = 2;
            ClassTreeSearchText = result.Name;
        }
    }

    private void CancelGlobalSearch() => _runtimeSearchCts?.Cancel();

    private void ClearGlobalSearch()
    {
        var searchCts = _runtimeSearchCts;
        _runtimeSearchCts = null;
        searchCts?.Cancel();
        IsGlobalSearchRunning = false;
        GlobalSearchQuery = "";
        GlobalSearchResults.Clear();
        SelectedGlobalSearchResult = null;
        GlobalSearchStatus = IsConnected
            ? "Enter a name, value, type, class, method, property, path, or exact object address such as 0x7ff1234."
            : "Connect to a Python runtime to search it.";
        RefreshObjectHandleReferences();
    }

    private async Task LoadRuntimeTreeAsync(CancellationToken token, bool autoSelectMain = false)
    {
        RefreshProcessMemory();
        var threadsFrame = await RequestAsync("threads.list", cancellationToken: token);
        var framesFrame = await RequestAsync("frames.list", cancellationToken: token);
        var modulesFrame = await RequestAsync("modules.list", new JsonObject
        {
            ["offset"] = 0,
            ["pageSize"] = 1000,
        }, token);
        using var consolesResponse = await RequestHandleResponseAsync("consoles.list", null, token);
        var consolesFrame = consolesResponse.Frame;
        var threads = threadsFrame.Header["result"]!["items"]!.AsArray();
        var frames = framesFrame.Header["result"]!["items"]!.AsArray();
        var modules = modulesFrame.Header["result"]!["items"]!.AsArray();
        var consolesResult = consolesFrame.Header["result"]!.AsObject();
        var consoles = consolesResult["items"]!.AsArray();
        var processRoot = new RuntimeTreeNode($"Process  PID {TargetPid}");
        processRoot.Children.Add(new RuntimeTreeNode($"Private bytes  {PrivateBytes}", RuntimeNodeKind.Placeholder));
        var interpreterRoot = new RuntimeTreeNode("Interpreter");
        interpreterRoot.Children.Add(new RuntimeTreeNode(PythonVersion, RuntimeNodeKind.Placeholder));
        interpreterRoot.Children.Add(new RuntimeTreeNode(Executable, RuntimeNodeKind.Placeholder));
        var threadsRoot = new RuntimeTreeNode("Threads");
        foreach (var thread in threads)
        {
            var threadId = thread!["threadId"]!.GetValue<long>();
            var threadNode = new RuntimeTreeNode($"{thread["name"]!.GetValue<string>()}  [{threadId}]", RuntimeNodeKind.Thread);
            foreach (var frame in frames.Where(item => item!["threadId"]!.GetValue<long>() == threadId))
            {
                var handle = frame!["frameHandle"]!.GetValue<string>();
                var label = $"{frame["functionName"]!.GetValue<string>()} — {Path.GetFileName(frame["filename"]!.GetValue<string>())}:{frame["lineNumber"]!.GetValue<int>()}";
                var frameNode = new RuntimeTreeNode(label, RuntimeNodeKind.Frame) { FrameHandle = handle };
                frameNode.Children.Add(new RuntimeTreeNode($"{label} / Locals", RuntimeNodeKind.Scope) { FrameHandle = handle, ScopeType = "locals" });
                frameNode.Children.Add(new RuntimeTreeNode($"{label} / Globals", RuntimeNodeKind.Scope) { FrameHandle = handle, ScopeType = "globals" });
                frameNode.Children.Add(new RuntimeTreeNode($"{label} / Built-ins", RuntimeNodeKind.Scope) { FrameHandle = handle, ScopeType = "builtins" });
                threadNode.Children.Add(frameNode);
            }
            threadsRoot.Children.Add(threadNode);
        }
        var consolesRoot = new RuntimeTreeNode($"Console namespaces  ({consoles.Count})");
        foreach (var console in consoles)
        {
            var displayName = console!["displayName"]!.GetValue<string>();
            var address = console["ownerAddressHex"]!.GetValue<string>();
            var count = console["entryCount"]?.GetValue<int>() ?? 0;
            consolesRoot.Children.Add(new RuntimeTreeNode(
                $"{displayName}  @{address}  ({count} variables)",
                RuntimeNodeKind.ConsoleNamespace)
            {
                ConsoleHandle = console["consoleHandle"]!.GetValue<string>(),
                ConsoleAttributeName = console["attributeName"]!.GetValue<string>(),
                ScopeType = "console",
            });
        }
        if (consoles.Count == 0)
            consolesRoot.Children.Add(new RuntimeTreeNode("No in-process console namespaces detected.", RuntimeNodeKind.Placeholder));
        if (consolesResult["truncated"]?.GetValue<bool>() == true)
        {
            var scanned = consolesResult["scannedCount"]?.GetValue<int>() ?? 0;
            var tracked = consolesResult["trackedTotal"]?.GetValue<int>() ?? 0;
            consolesRoot.Children.Add(new RuntimeTreeNode(
                $"Detection scan limited to {scanned:N0} of {tracked:N0} GC-tracked objects.",
                RuntimeNodeKind.Placeholder));
        }
        if (consolesResult["namespaceLimitReached"]?.GetValue<bool>() == true)
        {
            consolesRoot.Children.Add(new RuntimeTreeNode(
                $"Detection result limited to {consoles.Count:N0} console namespaces.",
                RuntimeNodeKind.Placeholder));
        }
        var modulesRoot = new RuntimeTreeNode("Modules");
        RuntimeTreeNode? mainModuleNode = null;
        foreach (var module in modules)
        {
            var name = module!["name"]!.GetValue<string>();
            var count = module["entryCount"]?.GetValue<int>() ?? 0;
            var label = name == "__main__" ? $"__main__  ({count} variables)" : name;
            var moduleNode = new RuntimeTreeNode(label, RuntimeNodeKind.Module)
            {
                ModuleName = name,
                ScopeType = "module",
            };
            modulesRoot.Children.Add(moduleNode);
            if (name == "__main__")
                mainModuleNode = moduleNode;
        }
        Replace(RuntimeRoots, [
            processRoot,
            interpreterRoot,
            threadsRoot,
            consolesRoot,
            modulesRoot,
            PlaceholderRoot("Classes", "Select a variable to inspect its class."),
            new RuntimeTreeNode("GC-tracked objects", RuntimeNodeKind.GcObjects),
        ]);
        RefreshObjectHandleReferences();
        await RefreshMemoryAsync(token);
        await RefreshExecutionStatusAsync(token);
        if (ExecutionMonitoringActive)
            await LoadExecutionEventsAsync(token);
        if (autoSelectMain && mainModuleNode is not null)
        {
            await LoadScopeAsync(mainModuleNode, resetPage: true);
            SelectedWorkspaceTabIndex = 0;
        }
    }

    private void ApplyRuntime(JsonObject runtime)
    {
        BeginObjectHandleSession();
        var targetPid = runtime["pid"]!.GetValue<int>();
        TargetPid = targetPid;
        Replace(Processes, _processDiscovery.GetPythonProcesses());
        SetProperty(ref _selectedProcess, Processes.FirstOrDefault(process => process.Id == targetPid), nameof(SelectedProcess));
        UpdateCommandStates();
        PythonVersion = runtime["version"]!.GetValue<string>().Split('\n')[0];
        Architecture = runtime["processArchitecture"]?.GetValue<string>() ?? "—";
        Executable = runtime["executable"]?.GetValue<string>() ?? "—";
        GlobalSearchStatus = "Enter a name, value, type, class, method, property, path, or exact object address such as 0x7ff1234.";
        RefreshProcessMemory();
    }

    private void ApplyScopeResult(
        JsonObject result,
        RuntimeTreeNode node,
        bool preserveSelectionAcrossScopeChange)
    {
        var isGcObjects = node.Kind == RuntimeNodeKind.GcObjects;
        var isConsoleNamespace = node.Kind == RuntimeNodeKind.ConsoleNamespace;
        _pageTotal = result["total"]!.GetValue<int>();
        var scopeLabel = isGcObjects
            ? "gc-tracked"
            : isConsoleNamespace
                ? $"console:{node.ConsoleAttributeName}"
                : node.ModuleName is null ? node.ScopeType! : $"module:{node.ModuleName}";
        var scopeKey = isGcObjects
            ? "gc-tracked"
            : isConsoleNamespace
                ? $"console:{node.ConsoleHandle}:{node.ConsoleAttributeName}"
                : node.ModuleName is null ? $"{node.FrameHandle}:{node.ScopeType}" : $"module:{node.ModuleName}";
        var snapshotKey = $"{scopeKey}|page:{_pageOffset}|query:{(isGcObjects ? SearchText.Trim() : "")}";
        var now = DateTime.Now;
        ScopeSnapshot? priorSnapshot = null;
        var hasPrior = !isGcObjects && _scopeSnapshots.TryGetValue(snapshotKey, out priorSnapshot);
        var prior = hasPrior ? priorSnapshot!.Items : new Dictionary<string, VariableSnapshot>(StringComparer.Ordinal);
        var completeSnapshot = !isGcObjects && _pageOffset == 0 && _pageTotal <= PageSize;
        var rows = new List<VariableRow>();
        foreach (var item in result["items"]!.AsArray())
        {
            var name = item!["name"]!.GetValue<string>();
            var value = item["value"]!.AsObject();
            var identityToken = value["identityToken"]?.GetValue<string>()
                ?? value["changeToken"]?.GetValue<string>()
                ?? value["addressHex"]?.GetValue<string>()
                ?? "";
            var changeToken = identityToken;
            var metadataToken = value["metadataToken"]?.GetValue<string>() ?? BuildMetadataToken(value);
            var markerKey = $"{scopeKey}:{name}";
            var changeKind = VariableChangeKind.Unchanged;
            DateTime? changedAt = null;
            if (hasPrior)
            {
                if (!prior.TryGetValue(name, out var previous))
                    changeKind = VariableChangeKind.Added;
                else if (previous.Row.IsRemoved)
                    changeKind = VariableChangeKind.Added;
                else if (!string.Equals(previous.IdentityToken, identityToken, StringComparison.Ordinal))
                    changeKind = VariableChangeKind.Rebound;
                else if (!string.Equals(previous.MetadataToken, metadataToken, StringComparison.Ordinal))
                    changeKind = VariableChangeKind.MetadataChanged;
            }
            if (changeKind != VariableChangeKind.Unchanged)
                _activeChanges[markerKey] = new ChangeMarker(changeKind, now);
            if (_activeChanges.TryGetValue(markerKey, out var marker))
            {
                if (now - marker.ChangedAt <= ChangeHighlightDuration)
                {
                    changeKind = marker.Kind;
                    changedAt = marker.ChangedAt;
                }
                else
                {
                    _activeChanges.Remove(markerKey);
                }
            }
            rows.Add(CreateVariableRow(
                name,
                scopeLabel,
                value,
                changeKind,
                changedAt,
                _pinnedKeys.Contains($"{scopeKey}:{name}"),
                scopeKey));
        }

        if (hasPrior && priorSnapshot!.IsComplete && completeSnapshot)
        {
            var currentNames = rows.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in prior.Where(pair => !currentNames.Contains(pair.Key)))
            {
                var markerKey = $"{scopeKey}:{removed.Key}";
                if (!_activeChanges.TryGetValue(markerKey, out var marker) || marker.Kind != VariableChangeKind.Removed)
                {
                    marker = new ChangeMarker(VariableChangeKind.Removed, now);
                    _activeChanges[markerKey] = marker;
                }
                if (now - marker.ChangedAt <= ChangeHighlightDuration)
                    rows.Add(CloneRemovedRow(removed.Value.Row, marker.ChangedAt));
                else
                    _activeChanges.Remove(markerKey);
            }
        }

        if (!isGcObjects)
            _scopeSnapshots[snapshotKey] = new ScopeSnapshot(
                rows.ToDictionary(
                    row => row.Name,
                    row => new VariableSnapshot(row.ChangeToken, row.MetadataToken, row),
                    StringComparer.Ordinal),
                completeSnapshot);

        var selectedKey = SelectedVariable?.StableKey;
        var selectedContext = _currentObject;
        var selectedIdentityToken = selectedContext?.Row.IdentityToken;
        var selectedMetadataToken = selectedContext?.Row.MetadataToken;
        VariableRow? replacement = null;
        _suppressVariableNavigation = true;
        try
        {
            var currentRows = ReconcileVariableRows(rows);
            replacement = selectedKey is null ? null : currentRows.FirstOrDefault(row => row.StableKey == selectedKey);
            SelectedVariable = replacement;
        }
        finally
        {
            _suppressVariableNavigation = false;
        }
        var typeOptions = new[] { "All types" }.Concat(Variables
            .Where(row => !row.IsRemoved)
            .Select(row => row.TypeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (!TypeFilters.SequenceEqual(typeOptions, StringComparer.OrdinalIgnoreCase))
        {
            var selectedTypeFilter = string.IsNullOrWhiteSpace(TypeFilter) ? "All types" : TypeFilter;
            Replace(TypeFilters, typeOptions);
            TypeFilter = typeOptions.Contains(selectedTypeFilter, StringComparer.OrdinalIgnoreCase)
                ? selectedTypeFilter
                : "All types";
        }
        var selectedScopeChanged = selectedContext is not null
            && !string.Equals(selectedContext.RootScopeKey, scopeKey, StringComparison.Ordinal);
        if (selectedScopeChanged && !preserveSelectionAcrossScopeChange)
        {
            ClearSelectedObject();
        }
        else if (selectedKey is not null)
        {
            if (replacement is { IsRemoved: false } && selectedContext is { Parent: null } context && context.PinKey == replacement.StableKey)
            {
                var identityChanged = !string.Equals(selectedIdentityToken, replacement.IdentityToken, StringComparison.Ordinal);
                var metadataChanged = !string.Equals(selectedMetadataToken, replacement.MetadataToken, StringComparison.Ordinal);
                if (identityChanged || InspectorState != InspectorPaneState.Ready)
                    _ = NavigateToObjectAsync(replacement, context.Path, null, addHistory: false);
                else if (metadataChanged)
                    UpdateSelectedObjectSummary(context with { Row = replacement });
            }
            else if (replacement?.IsRemoved == true && selectedContext is { Parent: null })
                ClearSelectedObject("The selected variable was removed from the latest snapshot.", InspectorPaneState.Expired);
        }
        ApplyFilter();
        LastSnapshotAt = ParseTimestamp(result["snapshotTimestamp"]) ?? DateTime.Now;
        var first = _pageTotal == 0 ? 0 : _pageOffset + 1;
        var last = Math.Min(_pageOffset + PageSize, _pageTotal);
        if (isGcObjects)
        {
            var tracked = result["trackedTotal"]?.GetValue<int>() ?? 0;
            var scanned = result["scannedCount"]?.GetValue<int>() ?? 0;
            var truncated = result["truncated"]?.GetValue<bool>() ?? false;
            var duration = result["durationMilliseconds"]?.GetValue<double>() ?? 0;
            PageLabel = $"{first}–{last} of {_pageTotal} matches · scanned {scanned:N0} of {tracked:N0}"
                + (truncated ? " (limit reached)" : "")
                + $" · {duration:F1} ms";
        }
        else
        {
            PageLabel = $"{first}–{last} of {_pageTotal}";
        }
        UpdateCommandStates();
    }

    private async Task NavigateToObjectAsync(
        VariableRow row,
        string path,
        NavigationContext? parent,
        bool addHistory,
        bool preserveDetailTab = false)
    {
        var ancestors = parent is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.Address }
            : new HashSet<string>(parent.AncestorAddresses, StringComparer.OrdinalIgnoreCase) { row.Address };
        var ancestorIdentityTokens = parent is null
            ? new List<string>()
            : new List<string>(parent.AncestorIdentityTokens);
        if (!string.IsNullOrWhiteSpace(row.IdentityToken))
            ancestorIdentityTokens.Add(row.IdentityToken);
        var context = new NavigationContext(row, path, parent?.Depth + 1 ?? 0, parent, ancestors, ancestorIdentityTokens);
        if (addHistory)
        {
            if (_navigationIndex < _navigationHistory.Count - 1)
                _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
            _navigationHistory.Add(context);
            _navigationIndex = _navigationHistory.Count - 1;
            TrimNavigationHistory();
        }
        else if (_navigationIndex >= 0 && _navigationIndex < _navigationHistory.Count && parent is null)
        {
            _navigationHistory[_navigationIndex] = context;
        }
        RefreshObjectHandleReferences();
        await LoadObjectContextAsync(context, preserveDetailTab: preserveDetailTab);
    }

    private void TrimNavigationHistory()
    {
        var overflow = _navigationHistory.Count - MaxNavigationHistory;
        if (overflow <= 0)
            return;
        _navigationHistory.RemoveRange(0, overflow);
        _navigationIndex = Math.Max(-1, _navigationIndex - overflow);
    }

    private async Task LoadObjectContextAsync(
        NavigationContext context,
        bool preserveSearches = false,
        bool preserveDetailTab = false)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts?.Token ?? CancellationToken.None);
        var token = _detailCts.Token;
        var generation = Interlocked.Increment(ref _detailGeneration);
        if (!preserveSearches)
        {
            ResetObjectSearches();
            ResetClassTreeSearch();
        }
        _currentObject = context;
        UpdateObjectNavigationPresentation(context);
        var row = context.Row;
        HasSelectedObject = true;
        HasArraySelection = row.AdapterKind == "numpy.ndarray";
        HasDataFrameSelection = row.AdapterKind == "pandas.DataFrame";
        HasMatplotlibSelection = row.AdapterKind is "matplotlib.Figure" or "matplotlib.Axes";
        if (!preserveDetailTab)
        {
            SelectedObjectDetailTabIndex = HasDataFrameSelection
                ? 3
                : HasMatplotlibSelection
                    ? 4
                    : HasArraySelection
                        ? 5
                        : 0;
        }
        InspectorState = InspectorPaneState.Loading;
        SelectedObjectName = row.Name;
        SelectedObjectPath = context.Path;
        SelectedObjectPreview = row.SafePreview;
        SelectedObjectStatus = "Loading object structure…";
        SelectedType = row.TypeName;
        SelectedModule = row.ModuleName;
        SelectedQualifiedName = row.QualifiedTypeName;
        SelectedAddress = row.Address;
        SelectedShallowSize = FormatBytes(row.ShallowSize);
        SelectedPayloadSize = row.PayloadSize is long payload ? FormatBytes(payload) : "—";
        ObjectChildren.Clear();
        FilteredObjectChildren.Clear();
        ObjectRoots.Clear();
        ClassMembers.Clear();
        PrepareClassTreeRebuild(preserveSearches);
        ClearArrayDetails();
        ClearDataFrameDetails(resetOffsets: true);
        ClearMatplotlibDetails();
        _arrayHandle = HasArraySelection ? row.HandleId : null;
        _dataFrameHandle = HasDataFrameSelection ? row.HandleId : null;
        _matplotlibHandle = HasMatplotlibSelection ? row.HandleId : null;
        OnPropertyChanged(nameof(IsSelectedObjectPinned));
        UpdateCommandStates();

        var root = new ObjectTreeNode
        {
            Label = row.Name,
            Origin = "selected",
            Path = context.Path,
            Depth = context.Depth,
            Kind = ObjectNodeKind.Object,
            Value = row,
            IsExpanded = true,
        };
        ObjectRoots.Add(root);
        RefreshObjectHandleReferences();
        try
        {
            using var childResponse = await RequestHandleResponseAsync("objects.listChildren", new JsonObject
            {
                ["handleId"] = row.HandleId,
                ["offset"] = 0,
                ["pageSize"] = ObjectPageSize,
                ["depth"] = context.Depth,
                ["ancestorIdentityTokens"] = ToJsonArray(context.AncestorIdentityTokens),
            }, token);
            var children = childResponse.Frame;
            if (generation != _detailGeneration || token.IsCancellationRequested)
                return;
            try
            {
                ApplyObjectChildren(root, context, children.Header["result"]!.AsObject(), clear: true);
            }
            finally
            {
                RefreshObjectHandleReferences();
            }

            var classFrame = await RequestAsync("classes.describe", new JsonObject { ["handleId"] = row.HandleId }, token);
            if (generation != _detailGeneration || token.IsCancellationRequested)
                return;
            ApplyClassDescription(classFrame.Header["result"]!.AsObject());

            var arrayPreviewAvailable = true;
            if (_arrayHandle is not null)
                arrayPreviewAvailable = await LoadArrayDescriptionAndPreviewAsync(generation, token);
            var dataFramePreviewAvailable = true;
            if (_dataFrameHandle is not null)
            {
                try
                {
                    dataFramePreviewAvailable = await LoadDataFrameDescriptionAndPreviewAsync(generation, token, showLoading: true);
                }
                catch (RemoteInspectionException exception) when (exception.Code == "OBJECT_EXPIRED")
                {
                    throw;
                }
                catch (Exception exception)
                {
                    dataFramePreviewAvailable = false;
                    SetDataFrameError(exception);
                }
            }
            var matplotlibPreviewAvailable = true;
            if (_matplotlibHandle is not null)
            {
                try
                {
                    matplotlibPreviewAvailable = await LoadMatplotlibDescriptionAndPreviewAsync(
                        generation,
                        token,
                        showLoading: true);
                }
                catch (RemoteInspectionException exception) when (exception.Code == "OBJECT_EXPIRED")
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (generation != _detailGeneration || token.IsCancellationRequested)
                        return;
                    matplotlibPreviewAvailable = false;
                    SetMatplotlibError(exception);
                }
            }
            if (generation != _detailGeneration || token.IsCancellationRequested)
                return;
            var hasInspectableDetails = ObjectChildren.Count > 0
                || ClassMembers.Count > 0
                || HasArraySelection
                || HasDataFrameSelection
                || HasMatplotlibSelection;
            InspectorState = hasInspectableDetails ? InspectorPaneState.Ready : InspectorPaneState.Empty;
            SelectedObjectStatus = !hasInspectableDetails
                ? "No safe child values, class members, array, DataFrame, or Matplotlib details"
                : _arrayHandle is not null && !arrayPreviewAvailable
                    ? "Ready · array metadata only; preview unavailable for this dtype or shape"
                : _dataFrameHandle is not null && !dataFramePreviewAvailable
                    ? "Ready · DataFrame metadata available; preview could not be read consistently"
                : _dataFrameHandle is not null
                    ? $"Ready · {DataFrameShape}"
                : _matplotlibHandle is not null && !matplotlibPreviewAvailable
                    ? MatplotlibState == MatplotlibPaneState.Unavailable
                        ? $"Ready · Matplotlib render unavailable ({MatplotlibAvailabilityReason})"
                        : "Ready · Matplotlib preview request failed"
                : _matplotlibHandle is not null
                    ? $"Ready · {MatplotlibSourceKind} render {MatplotlibSourceDimensions}"
                : ObjectChildren.Count == 0
                    ? "Ready · class, array, DataFrame, or Matplotlib details available"
                    : $"Ready · {ObjectChildren.Count:N0} children";
        }
        catch (OperationCanceledException)
        {
        }
        catch (RemoteInspectionException exception) when (exception.Code == "OBJECT_EXPIRED")
        {
            if (generation != _detailGeneration)
                return;
            InspectorState = InspectorPaneState.Expired;
            SelectedObjectStatus = "Object expired · refresh the scope and select it again";
            if (HasMatplotlibSelection)
                SetMatplotlibError(exception);
            ObjectRoots.Clear();
            ObjectChildren.Clear();
            FilteredObjectChildren.Clear();
            RefreshObjectHandleReferences();
        }
        catch (Exception exception)
        {
            if (generation != _detailGeneration)
                return;
            InspectorState = InspectorPaneState.Error;
            SelectedObjectStatus = "Object inspection failed";
            if (HasMatplotlibSelection)
                SetMatplotlibError(exception);
            SetError(exception);
        }
        finally
        {
            UpdateCommandStates();
        }
    }

    public async Task SelectObjectNodeAsync(ObjectTreeNode? node)
    {
        SelectedObjectNode = node;
        if (node is null)
            return;
        if (node.Kind == ObjectNodeKind.LoadMore)
        {
            await LoadMoreObjectChildrenAsync(node);
            return;
        }
        if (!node.CanNavigate || node.Value is null || _currentObject is null)
            return;
        var parent = BuildNavigationParent(node);
        await NavigateToObjectAsync(
            node.Value,
            node.Path,
            parent,
            addHistory: true,
            preserveDetailTab: true);
    }

    public async Task NavigateBreadcrumbAsync(ObjectBreadcrumbItem? item)
    {
        if (item is null || item.IsCurrent || _currentObject is null)
            return;

        var target = _currentObject;
        while (target is not null && target.Depth > item.Depth)
            target = target.Parent;
        if (target is null
            || target.Depth != item.Depth
            || !string.Equals(target.Path, item.Path, StringComparison.Ordinal))
        {
            return;
        }

        await NavigateToObjectAsync(
            target.Row,
            target.Path,
            target.Parent,
            addHistory: true,
            preserveDetailTab: true);
    }

    public async Task ExpandObjectNodeAsync(ObjectTreeNode? node)
    {
        if (node is null || node.Kind != ObjectNodeKind.Object || node.Value is null || node.IsLoaded || node.IsCycle || node.Depth >= MaxObjectDepth)
            return;
        node.IsLoading = true;
        try
        {
            var context = CreateNavigationContext(node.Value, node.Path, node.Depth, BuildNavigationParent(node));
            using var handleResponse = await RequestHandleResponseAsync("objects.listChildren", new JsonObject
            {
                ["handleId"] = node.Value.HandleId,
                ["offset"] = 0,
                ["pageSize"] = ObjectPageSize,
                ["depth"] = context.Depth,
                ["ancestorIdentityTokens"] = ToJsonArray(context.AncestorIdentityTokens),
            }, _connectionCts?.Token ?? CancellationToken.None);
            var frame = handleResponse.Frame;
            try
            {
                ApplyObjectChildren(node, context, frame.Header["result"]!.AsObject(), clear: true);
            }
            finally
            {
                RefreshObjectHandleReferences();
            }
        }
        catch (RemoteInspectionException exception) when (exception.Code == "OBJECT_EXPIRED")
        {
            node.Children.Clear();
            node.Children.Add(StatusNode("Object expired · refresh the scope", node));
        }
        catch (Exception exception)
        {
            node.Children.Clear();
            node.Children.Add(StatusNode("Could not load children", node));
            SetError(exception);
        }
        finally
        {
            node.IsLoading = false;
            node.IsLoaded = true;
            RefreshObjectHandleReferences();
        }
    }

    private void ApplyObjectChildren(ObjectTreeNode parent, NavigationContext context, JsonObject result, bool clear)
    {
        if (clear)
            parent.Children.Clear();
        var items = result["items"]?.AsArray() ?? [];
        var offset = result["offset"]?.GetValue<int>() ?? 0;
        var total = result["total"]?.GetValue<int>() ?? items.Count;
        var isCurrentRoot = ObjectRoots.FirstOrDefault() == parent;
        if (clear && isCurrentRoot)
        {
            ObjectChildren.Clear();
            FilteredObjectChildren.Clear();
        }

        foreach (var item in items)
        {
            var value = item!["value"]!.AsObject();
            var name = item["name"]?.GetValue<string>() ?? "?";
            var origin = item["origin"]?.GetValue<string>() ?? "item";
            var pathSegment = item["pathSegment"]?.GetValue<string>() ?? name;
            var path = AppendObjectPath(context.Path, pathSegment);
            var childRow = CreateVariableRow(name, "object", value, VariableChangeKind.Unchanged, null, false);
            var cycle = item["isCycle"]?.GetValue<bool>() ?? context.AncestorAddresses.Contains(childRow.Address);
            var childDepth = item["depth"]?.GetValue<int>() ?? context.Depth + 1;
            var canExpand = item["canExpand"]?.GetValue<bool>() ?? childRow.Expandable;
            var depthLimited = childDepth >= MaxObjectDepth;
            var child = new ObjectTreeNode
            {
                Label = cycle ? $"{name}  · cycle" : depthLimited && childRow.Expandable ? $"{name}  · depth limit" : name,
                Origin = origin,
                Path = path,
                Depth = childDepth,
                Kind = ObjectNodeKind.Object,
                Value = childRow,
                Parent = parent,
                IsCycle = cycle,
                IsLoaded = !canExpand || cycle || depthLimited,
            };
            if (canExpand && !cycle && !depthLimited)
                child.Children.Add(StatusNode("Expand to load", child));

            var groupLabel = ObjectGroupLabel(origin);
            var group = parent.Children.FirstOrDefault(node => node.Kind == ObjectNodeKind.Group && node.Label == groupLabel);
            if (group is null)
            {
                group = new ObjectTreeNode
                {
                    Label = groupLabel,
                    Kind = ObjectNodeKind.Group,
                    Path = context.Path,
                    Depth = context.Depth,
                    Parent = parent,
                    IsExpanded = true,
                    IsLoaded = true,
                };
                parent.Children.Add(group);
            }
            group.Children.Add(child);
            if (isCurrentRoot)
                ObjectChildren.Add(new ObjectChildRow(name, origin, childRow.TypeName, childRow.SafePreview, childRow.Address, childRow.HandleId, childRow.Expandable, path));
        }

        parent.TotalChildren = total;
        parent.LoadedChildren = Math.Min(total, offset + items.Count);
        parent.IsLoaded = true;
        if (parent.LoadedChildren < total)
        {
            parent.Children.Add(new ObjectTreeNode
            {
                Label = $"Load more…  ({parent.LoadedChildren:N0} of {total:N0})",
                Kind = ObjectNodeKind.LoadMore,
                Path = context.Path,
                Depth = context.Depth,
                Parent = parent,
                Offset = parent.LoadedChildren,
                IsLoaded = true,
            });
        }
        if (parent.Children.Count == 0)
            parent.Children.Add(StatusNode("No safe child values", parent));
        ApplyObjectChildrenSearch();
        ApplyObjectTreeSearch();
    }

    private async Task LoadMoreObjectChildrenAsync(ObjectTreeNode loadMore)
    {
        if (loadMore.Parent?.Value is not VariableRow parentRow)
            return;
        var parent = loadMore.Parent;
        var context = ContextForNode(parent);
        try
        {
            using var handleResponse = await RequestHandleResponseAsync("objects.listChildren", new JsonObject
            {
                ["handleId"] = parentRow.HandleId,
                ["offset"] = loadMore.Offset,
                ["pageSize"] = ObjectPageSize,
                ["depth"] = context.Depth,
                ["ancestorIdentityTokens"] = ToJsonArray(context.AncestorIdentityTokens),
            }, _connectionCts?.Token ?? CancellationToken.None);
            var frame = handleResponse.Frame;
            parent.Children.Remove(loadMore);
            try
            {
                ApplyObjectChildren(parent, context, frame.Header["result"]!.AsObject(), clear: false);
            }
            finally
            {
                RefreshObjectHandleReferences();
            }
        }
        catch (RemoteInspectionException exception) when (exception.Code == "OBJECT_EXPIRED")
        {
            parent.Children.Remove(loadMore);
            parent.Children.Add(StatusNode("Object expired · refresh the scope", parent));
            InspectorState = InspectorPaneState.Expired;
            SelectedObjectStatus = "Object expired · refresh and select it again";
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private void ApplyClassDescription(JsonObject description)
    {
        var className = description["qualifiedName"]?.GetValue<string>()
            ?? description["name"]?.GetValue<string>()
            ?? SelectedType;
        var classModule = description["module"]?.GetValue<string>() ?? SelectedModule;
        var metaclass = ReadClassReference(description["metaclassRef"] ?? description["metaclass"]) ?? "—";
        var docstring = description["docstring"]?.GetValue<string>();
        _classTreeMembersTruncated = description["membersTruncated"]?.GetValue<bool>() == true;
        ClassSummary = $"{classModule}.{className} · metaclass {metaclass}";
        if (!string.IsNullOrWhiteSpace(docstring))
            ClassSummary += $" · {docstring}";

        var overview = new ClassTreeNode { Label = "Class overview", Kind = "group" };
        overview.Children.Add(new ClassTreeNode { Label = "Metaclass", Kind = "metadata", Detail = metaclass });
        AddClassReferenceGroup(overview, "Base classes", description["baseClassRefs"] ?? description["baseClasses"]);
        AddClassReferenceGroup(overview, "MRO", description["mroRefs"] ?? description["mro"]);
        if (description["mroTruncated"]?.GetValue<bool>() == true)
            overview.Children.Add(new ClassTreeNode { Label = "Additional MRO entries omitted", Kind = "status" });
        ClassTree.Add(overview);

        var fields = new ClassTreeNode { Label = "Instance fields", Kind = "group" };
        foreach (var field in ObjectChildren.Where(child => child.Origin == "instance"))
            fields.Children.Add(new ClassTreeNode { Label = field.Name, Kind = "field", Detail = $"{field.Type} · {field.Preview}" });
        ClassTree.Add(fields);

        var groups = new Dictionary<string, ClassTreeNode>(StringComparer.Ordinal)
        {
            ["instance method"] = new() { Label = "Instance methods", Kind = "group" },
            ["staticmethod"] = new() { Label = "Static methods", Kind = "group" },
            ["classmethod"] = new() { Label = "Class methods", Kind = "group" },
            ["property"] = new() { Label = "Properties & descriptors", Kind = "group" },
            ["descriptor"] = new() { Label = "Properties & descriptors", Kind = "group" },
            ["attribute"] = new() { Label = "Class attributes", Kind = "group" },
            ["inherited"] = new() { Label = "Inherited members", Kind = "group" },
        };

        var memberRows = new List<ClassMemberRow>();
        foreach (var memberNode in description["members"]?.AsArray() ?? [])
        {
            var member = memberNode!.AsObject();
            var name = member["name"]?.GetValue<string>() ?? "?";
            var kind = member["kind"]?.GetValue<string>() ?? "class attribute";
            var declaredBy = ReadClassReference(member["declaredByRef"] ?? member["declaredBy"]) ?? "?";
            var inherited = member["inherited"]?.GetValue<bool>() ?? false;
            var signature = ReadSignatureDisplay(member["signatureDetails"] ?? member["signature"]);
            var source = ReadSource(member["source"]);
            memberRows.Add(new ClassMemberRow(name, kind, declaredBy, signature, inherited, source));
            var groupKey = inherited ? "inherited" : ClassGroupKey(kind);
            var group = groups[groupKey];
            var item = new ClassTreeNode
            {
                Label = name,
                Kind = kind,
                DeclaredBy = declaredBy,
                Detail = signature,
                Source = source,
            };
            var parameters = member["parameters"] as JsonArray
                ?? (member["signatureDetails"] as JsonObject)?["parameters"] as JsonArray;
            if (parameters is not null)
            {
                var parameterGroup = new ClassTreeNode { Label = "Parameters", Kind = "parameters" };
                foreach (var parameterNode in parameters)
                {
                    var parameter = parameterNode!.AsObject();
                    var parameterName = parameter["name"]?.GetValue<string>() ?? "?";
                    var parameterKind = parameter["kind"]?.GetValue<string>() ?? "parameter";
                    var annotation = parameter["annotationText"]?.GetValue<string>();
                    var defaultValue = parameter["defaultPreview"]?.GetValue<string>();
                    var detail = parameterKind;
                    if (!string.IsNullOrEmpty(annotation)) detail += $" · {annotation}";
                    if (!string.IsNullOrEmpty(defaultValue)) detail += $" = {defaultValue}";
                    parameterGroup.Children.Add(new ClassTreeNode { Label = parameterName, Kind = "parameter", Detail = detail });
                }
                if (parameterGroup.Children.Count > 0)
                    item.Children.Add(parameterGroup);
            }
            group.Children.Add(item);
        }
        Replace(ClassMembers, memberRows);
        foreach (var group in groups.Values.Distinct().Where(group => group.Children.Count > 0))
            ClassTree.Add(group);
        _classTreeBoundedSummary = "";
        if (_classTreeMembersTruncated)
        {
            var total = description["memberTotal"]?.ToString() ?? "unknown";
            var limit = description["memberLimit"]?.GetValue<int>() ?? memberRows.Count;
            _classTreeBoundedSummary = total == "unknown"
                ? $" · additional class members omitted; {memberRows.Count:N0} members loaded"
                : $" · additional class members omitted; {memberRows.Count:N0} of {total} members loaded";
            ClassTree.Add(new ClassTreeNode
            {
                Label = "Additional class members omitted",
                Kind = "status",
                Detail = $"Showing {memberRows.Count:N0} of {total} members (limit {limit:N0})",
            });
        }
        SetClassTreeHierarchy(ClassTree, parent: null, depth: 0);
        _classTreeLoadedSearchableCount = ClassTree.Sum(CountClassTreeSearchableNodes);
        if (!string.IsNullOrWhiteSpace(ClassTreeSearchText)
            && _classTreeExpansionBeforeSearch.Count == 0)
            CaptureClassTreeExpansion();
        ApplyClassTreeSearch();
    }

    private async Task NavigateBackAsync()
    {
        if (_navigationIndex <= 0)
            return;
        _navigationIndex--;
        await LoadObjectContextAsync(_navigationHistory[_navigationIndex], preserveDetailTab: true);
    }

    private async Task NavigateForwardAsync()
    {
        if (_navigationIndex < 0 || _navigationIndex >= _navigationHistory.Count - 1)
            return;
        _navigationIndex++;
        await LoadObjectContextAsync(_navigationHistory[_navigationIndex], preserveDetailTab: true);
    }

    private async Task NavigateParentAsync()
    {
        if (_currentObject?.Parent is not NavigationContext parent)
            return;
        await NavigateToObjectAsync(
            parent.Row,
            parent.Path,
            parent.Parent,
            addHistory: true,
            preserveDetailTab: true);
    }

    private async Task NavigatePinnedAsync()
    {
        if (SelectedPinnedObject is null || !_pinnedContexts.TryGetValue(SelectedPinnedObject.StableKey, out var context))
            return;
        await NavigateToObjectAsync(
            context.Row,
            context.Path,
            context.Parent,
            addHistory: true,
            preserveDetailTab: true);
    }

    private void UpdateObjectNavigationPresentation(NavigationContext context)
    {
        var ancestry = new List<NavigationContext>();
        for (NavigationContext? current = context; current is not null; current = current.Parent)
            ancestry.Add(current);
        ancestry.Reverse();
        Replace(ObjectBreadcrumbs, ancestry.Select((item, index) => new ObjectBreadcrumbItem(
            item.Row.Name,
            item.Path,
            item.Depth,
            IsCurrent: index == ancestry.Count - 1)));

        ObjectDepthLabel = context.Depth == 0 ? "Level 0 · Root" : $"Level {context.Depth}";
        NavigationHistoryLabel = _navigationIndex >= 0
            ? $"History {_navigationIndex + 1} / {_navigationHistory.Count}"
            : "History 0 / 0";

        var previous = _navigationIndex > 0 ? _navigationHistory[_navigationIndex - 1] : null;
        var next = _navigationIndex >= 0 && _navigationIndex < _navigationHistory.Count - 1
            ? _navigationHistory[_navigationIndex + 1]
            : null;
        BackNavigationLabel = previous is null ? "Back" : $"Back · {CompactNavigationName(previous.Row.Name)}";
        ForwardNavigationLabel = next is null ? "Forward" : $"Forward · {CompactNavigationName(next.Row.Name)}";
        ParentNavigationLabel = context.Parent is null
            ? "Parent"
            : $"Parent · {CompactNavigationName(context.Parent.Row.Name)}";
        BackNavigationToolTip = previous is null
            ? "No earlier object in navigation history"
            : $"Back to {previous.Path} (Alt+Left)";
        ForwardNavigationToolTip = next is null
            ? "No later object in navigation history"
            : $"Forward to {next.Path} (Alt+Right)";
        ParentNavigationToolTip = context.Parent is null
            ? "The selected object is already at the root"
            : $"Go to parent {context.Parent.Path}";
        NavigationLocationDescription = context.Parent is null
            ? $"Current object {context.Path}, root level, {NavigationHistoryLabel}"
            : $"Current object {context.Path}, level {context.Depth}, parent {context.Parent.Row.Name}, {NavigationHistoryLabel}";
    }

    private static string CompactNavigationName(string name) =>
        name.Length <= 18 ? name : $"{name[..17]}…";

    private async Task RefreshMatplotlibAsync()
    {
        if (_matplotlibHandle is null || !IsConnected)
            return;
        var generation = Volatile.Read(ref _detailGeneration);
        try
        {
            var previewAvailable = await LoadMatplotlibDescriptionAndPreviewAsync(
                generation,
                _detailCts?.Token ?? _connectionCts?.Token ?? CancellationToken.None,
                showLoading: true);
            if (generation != _detailGeneration)
                return;
            SelectedObjectStatus = previewAvailable
                ? $"Matplotlib preview refreshed · {DateTime.Now:HH:mm:ss}"
                : $"Matplotlib render unavailable · {MatplotlibAvailabilityReason}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (RemoteInspectionException exception) when (exception.Code == "OBJECT_EXPIRED")
        {
            if (generation != _detailGeneration)
                return;
            InspectorState = InspectorPaneState.Expired;
            SelectedObjectStatus = "Object expired · refresh the scope and select it again";
            SetMatplotlibError(exception);
        }
        catch (Exception exception)
        {
            if (generation != _detailGeneration)
                return;
            SetMatplotlibError(exception);
            SelectedObjectStatus = "Matplotlib preview refresh failed · use Refresh preview to retry";
        }
    }

    private async Task<bool> LoadMatplotlibDescriptionAndPreviewAsync(
        long generation,
        CancellationToken token,
        bool showLoading)
    {
        var handle = _matplotlibHandle;
        if (handle is null)
            return false;
        var refreshGeneration = Interlocked.Increment(ref _matplotlibRefreshGeneration);
        try
        {
            if (showLoading)
            {
                MatplotlibPreview = null;
                MatplotlibState = MatplotlibPaneState.Loading;
                MatplotlibStatus = "Inspecting the current Agg render…";
                MatplotlibNextAction = "";
                MatplotlibErrorMessage = "";
            }

            using var handleResponse = await RequestHandleResponseAsync(
                "figures.describe",
                new JsonObject { ["handleId"] = handle },
                token);
            if (!IsCurrentMatplotlibRequest(generation, refreshGeneration, handle, token))
                throw MatplotlibRequestSuperseded(token);
            var description = handleResponse.Frame.Header["result"]!.AsObject();
            if (!ApplyMatplotlibMetadata(description, preservePreviewOnTransientChange: !showLoading))
                return false;

            var frame = await RequestAsync("figures.preview", new JsonObject
            {
                ["handleId"] = handle,
                ["maxWidth"] = MatplotlibMaxPreviewDimension,
                ["maxHeight"] = MatplotlibMaxPreviewDimension,
            }, token);
            if (!IsCurrentMatplotlibRequest(generation, refreshGeneration, handle, token))
                throw MatplotlibRequestSuperseded(token);
            return ApplyMatplotlibPreview(frame, preservePreviewOnTransientChange: !showLoading);
        }
        catch (Exception exception) when (!IsCurrentMatplotlibRequest(generation, refreshGeneration, handle, token))
        {
            throw MatplotlibRequestSuperseded(token, exception);
        }
    }

    private static OperationCanceledException MatplotlibRequestSuperseded(
        CancellationToken token,
        Exception? innerException = null) =>
        new("The Matplotlib preview request was superseded by a newer request.", innerException, token);

    private bool IsCurrentMatplotlibRequest(
        long generation,
        long refreshGeneration,
        string handle,
        CancellationToken token) =>
        generation == _detailGeneration
        && refreshGeneration == Volatile.Read(ref _matplotlibRefreshGeneration)
        && !token.IsCancellationRequested
        && string.Equals(handle, _matplotlibHandle, StringComparison.Ordinal);

    private bool ApplyMatplotlibMetadata(
        JsonObject metadata,
        bool preservePreviewOnTransientChange = false)
    {
        var adapterKind = metadata["adapterKind"]?.GetValue<string>();
        if (adapterKind is not ("matplotlib.Figure" or "matplotlib.Axes"))
            throw new InvalidOperationException("The Figure response has an unsupported adapter kind.");
        var sourceKind = metadata["sourceKind"]?.GetValue<string>();
        if (sourceKind is not ("Figure" or "Axes"))
            throw new InvalidOperationException("The Figure response has an unsupported source kind.");
        if ((adapterKind == "matplotlib.Figure") != (sourceKind == "Figure"))
            throw new InvalidOperationException("The Figure response adapter and source kinds do not match.");
        if (!string.Equals(metadata["renderedKind"]?.GetValue<string>(), "Figure", StringComparison.Ordinal))
            throw new InvalidOperationException("The Matplotlib response must describe a rendered Figure.");
        if (!string.Equals(metadata["sourcePixelFormat"]?.GetValue<string>(), "RGBA32", StringComparison.Ordinal))
            throw new InvalidOperationException("The Matplotlib source pixel format must be RGBA32.");
        var axesUsesOwningFigure = metadata["axesUsesOwningFigure"]?.GetValue<bool>() ?? false;
        if (axesUsesOwningFigure != (sourceKind == "Axes"))
            throw new InvalidOperationException("The Matplotlib owning-Figure metadata is inconsistent.");
        var availability = metadata["availability"] as JsonObject
            ?? throw new InvalidOperationException("The Figure response is missing availability metadata.");
        var availabilityState = availability["state"]?.GetValue<string>();
        var previewAvailable = metadata["previewAvailable"]?.GetValue<bool>() ?? false;
        var reason = availability["reason"]?.GetValue<string>();

        MatplotlibSourceKind = sourceKind;
        MatplotlibUsesOwningFigure = axesUsesOwningFigure
            && !string.IsNullOrWhiteSpace(metadata["figureAddressHex"]?.GetValue<string>());
        MatplotlibCanvasType = metadata["canvasType"]?.GetValue<string>() ?? "—";
        var sourceWidth = metadata["sourceWidth"]?.GetValue<int?>();
        var sourceHeight = metadata["sourceHeight"]?.GetValue<int?>();
        MatplotlibSourceDimensions = sourceWidth is > 0 && sourceHeight is > 0
            ? $"{sourceWidth:N0} × {sourceHeight:N0} px"
            : "—";
        MatplotlibAvailabilityReason = reason ?? (availabilityState == "ready" ? "ready" : "unknown");
        MatplotlibStatus = availability["message"]?.GetValue<string>()
            ?? (availabilityState == "ready"
                ? "A current, completed Agg render is available."
                : "The Matplotlib render is unavailable.");
        MatplotlibNextAction = availability["nextAction"]?.GetValue<string>() ?? "";
        MatplotlibErrorMessage = "";

        if (availabilityState == "unavailable" && !previewAvailable)
        {
            if (preservePreviewOnTransientChange
                && string.Equals(reason, "buffer-changed", StringComparison.Ordinal)
                && MatplotlibPreview is not null)
            {
                MatplotlibState = MatplotlibPaneState.Ready;
                MatplotlibStatus = "The target render changed during capture; keeping the last complete preview.";
                MatplotlibNextAction = "PyMonitor will retry automatically on the next refresh.";
                UpdateCommandStates();
                return false;
            }
            MatplotlibPreview = null;
            MatplotlibState = MatplotlibPaneState.Unavailable;
            UpdateCommandStates();
            return false;
        }
        if (availabilityState != "ready" || !previewAvailable)
            throw new InvalidOperationException("The Figure availability metadata is internally inconsistent.");
        if (sourceWidth is not > 0 || sourceHeight is not > 0)
            throw new InvalidOperationException("The ready Figure response has invalid source dimensions.");
        if (sourceKind == "Axes" && !MatplotlibUsesOwningFigure)
            throw new InvalidOperationException("The ready Axes response is missing its owning Figure address.");
        return true;
    }

    private bool ApplyMatplotlibPreview(
        ProtocolFrame frame,
        bool preservePreviewOnTransientChange = false)
    {
        var metadata = frame.Header["result"]!.AsObject();
        if (!ApplyMatplotlibMetadata(metadata, preservePreviewOnTransientChange))
            return false;
        if (metadata["snapshotConsistent"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The Figure preview was not a consistent render snapshot.");
        var width = metadata["width"]?.GetValue<int>() ?? 0;
        var height = metadata["height"]?.GetValue<int>() ?? 0;
        var stride = metadata["stride"]?.GetValue<int>() ?? 0;
        if (width is < 1 or > MatplotlibMaxPreviewDimension
            || height is < 1 or > MatplotlibMaxPreviewDimension)
        {
            throw new InvalidOperationException("The Figure preview dimensions exceed the bounded contract.");
        }
        if (!string.Equals(metadata["pixelFormat"]?.GetValue<string>(), "BGRA32", StringComparison.Ordinal))
            throw new InvalidOperationException("The Figure preview pixel format must be BGRA32.");
        var expectedStride = checked(width * 4);
        if (stride != expectedStride)
            throw new InvalidOperationException("The Figure preview stride does not match BGRA32 pixels.");
        var expectedLength = checked(stride * height);
        if (expectedLength > MatplotlibMaxPreviewBytes || frame.Binary.Length != expectedLength)
            throw new InvalidOperationException("The Figure preview binary length does not match its bounded metadata.");

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Binary,
            stride);
        bitmap.Freeze();
        MatplotlibPreview = bitmap;
        MatplotlibState = MatplotlibPaneState.Ready;
        MatplotlibStatus += $" Preview {width:N0} × {height:N0} px.";
        MatplotlibNextAction = "";
        MatplotlibErrorMessage = "";
        UpdateCommandStates();
        return true;
    }

    private void SetMatplotlibError(Exception exception)
    {
        MatplotlibPreview = null;
        MatplotlibState = MatplotlibPaneState.Error;
        MatplotlibStatus = "Matplotlib preview unavailable because the inspection request failed.";
        MatplotlibNextAction = "Use Refresh preview to retry; PyMonitor never calls draw() in the target.";
        MatplotlibErrorMessage = exception switch
        {
            RemoteInspectionException remote => $"{remote.Code}: {remote.Message}",
            _ => exception.Message,
        };
        UpdateCommandStates();
    }

    private async Task RefreshDataFrameAsync()
    {
        if (_dataFrameHandle is null || !IsConnected)
            return;
        try
        {
            await LoadDataFrameDescriptionAndPreviewAsync(
                Volatile.Read(ref _detailGeneration),
                _detailCts?.Token ?? _connectionCts?.Token ?? CancellationToken.None,
                showLoading: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetDataFrameError(exception);
        }
    }

    private async Task MoveDataFramePageAsync(int rowDelta, int columnDelta)
    {
        if (_dataFrameHandle is null || !IsConnected)
            return;
        var previousRowOffset = _dataFrameRowOffset;
        var previousColumnOffset = _dataFrameColumnOffset;
        _dataFrameRowOffset = NormalizePageOffset(
            _dataFrameRowOffset + rowDelta,
            _dataFrameTotalRows,
            DataFrameRowPageSize);
        _dataFrameColumnOffset = NormalizePageOffset(
            _dataFrameColumnOffset + columnDelta,
            _dataFrameTotalColumns,
            DataFrameColumnPageSize);
        try
        {
            await LoadDataFramePreviewAsync(
                Volatile.Read(ref _detailGeneration),
                _detailCts?.Token ?? _connectionCts?.Token ?? CancellationToken.None,
                showLoading: true);
        }
        catch (OperationCanceledException)
        {
            _dataFrameRowOffset = previousRowOffset;
            _dataFrameColumnOffset = previousColumnOffset;
        }
        catch (Exception exception)
        {
            _dataFrameRowOffset = previousRowOffset;
            _dataFrameColumnOffset = previousColumnOffset;
            SetDataFrameError(exception);
        }
        finally
        {
            UpdateCommandStates();
        }
    }

    private async Task<bool> LoadDataFrameDescriptionAndPreviewAsync(
        long generation,
        CancellationToken token,
        bool showLoading)
    {
        if (_dataFrameHandle is null)
            return false;
        if (showLoading)
        {
            DataFrameState = DataFramePaneState.Loading;
            DataFrameStatus = "Loading bounded DataFrame preview…";
            DataFrameErrorMessage = "";
        }

        using var handleResponse = await RequestHandleResponseAsync(
            "dataframes.describe",
            new JsonObject { ["handleId"] = _dataFrameHandle },
            token);
        if (generation != _detailGeneration || token.IsCancellationRequested)
            return false;
        var description = handleResponse.Frame.Header["result"]!.AsObject();
        _dataFrameTotalRows = description["totalRows"]?.GetValue<int>() ?? 0;
        _dataFrameTotalColumns = description["totalColumns"]?.GetValue<int>() ?? 0;
        _dataFrameRowOffset = NormalizePageOffset(
            _dataFrameRowOffset,
            _dataFrameTotalRows,
            DataFrameRowPageSize);
        _dataFrameColumnOffset = NormalizePageOffset(
            _dataFrameColumnOffset,
            _dataFrameTotalColumns,
            DataFrameColumnPageSize);
        DataFrameShape = $"{_dataFrameTotalRows:N0} rows × {_dataFrameTotalColumns:N0} columns";
        return await LoadDataFramePreviewAsync(generation, token, showLoading);
    }

    private async Task<bool> LoadDataFramePreviewAsync(
        long generation,
        CancellationToken token,
        bool showLoading)
    {
        if (_dataFrameHandle is null)
            return false;
        if (showLoading)
        {
            DataFrameState = DataFramePaneState.Loading;
            DataFrameStatus = "Loading bounded DataFrame preview…";
            DataFrameErrorMessage = "";
        }

        var frame = await RequestAsync("dataframes.preview", new JsonObject
        {
            ["handleId"] = _dataFrameHandle,
            ["rowOffset"] = _dataFrameRowOffset,
            ["rowCount"] = DataFrameRowPageSize,
            ["columnOffset"] = _dataFrameColumnOffset,
            ["columnCount"] = DataFrameColumnPageSize,
        }, token);
        if (generation != _detailGeneration || token.IsCancellationRequested)
            return false;
        ApplyDataFramePreview(frame.Header["result"]!.AsObject());
        return true;
    }

    private void ApplyDataFramePreview(JsonObject result)
    {
        var columns = (result["columns"]?.AsArray() ?? [])
            .Select(column => new DataFrameColumnInfo(
                column!["position"]?.GetValue<int>() ?? 0,
                column["name"]?.GetValue<string>() ?? "<unnamed>",
                column["dtype"]?.GetValue<string>() ?? "unknown"))
            .ToArray();
        var indexLabels = result["indexLabels"]?.AsArray() ?? [];
        var rows = result["rows"]?.AsArray() ?? [];
        _dataFrameRowOffset = result["rowOffset"]?.GetValue<int>() ?? _dataFrameRowOffset;
        _dataFrameColumnOffset = result["columnOffset"]?.GetValue<int>() ?? _dataFrameColumnOffset;
        _dataFrameRowCount = result["rowCount"]?.GetValue<int>() ?? rows.Count;
        _dataFrameColumnCount = result["columnCount"]?.GetValue<int>() ?? columns.Length;
        _dataFrameTotalRows = result["totalRows"]?.GetValue<int>() ?? _dataFrameTotalRows;
        _dataFrameTotalColumns = result["totalColumns"]?.GetValue<int>() ?? _dataFrameTotalColumns;
        _dataFrameHasMoreRows = result["hasMoreRows"]?.GetValue<bool>() ?? false;
        _dataFrameHasMoreColumns = result["hasMoreColumns"]?.GetValue<bool>() ?? false;
        var snapshotConsistent = result["snapshotConsistent"]?.GetValue<bool>() ?? true;
        var cellLimitApplied = result["cellLimitApplied"]?.GetValue<bool>() ?? false;

        var columnMetadataChanged = !DataFrameColumns.SequenceEqual(columns);
        if (columnMetadataChanged)
            Replace(DataFrameColumns, columns);
        ApplyDataFrameTable(columns, indexLabels, rows, forceSchemaRefresh: columnMetadataChanged);

        DataFrameShape = $"{_dataFrameTotalRows:N0} rows × {_dataFrameTotalColumns:N0} columns";
        DataFrameRowPageLabel = PageRangeLabel(
            "Rows",
            _dataFrameRowOffset,
            _dataFrameRowCount,
            _dataFrameTotalRows);
        DataFrameColumnPageLabel = PageRangeLabel(
            "Columns",
            _dataFrameColumnOffset,
            _dataFrameColumnCount,
            _dataFrameTotalColumns);
        var consistency = snapshotConsistent
            ? "Consistent bounded snapshot"
            : "Data changed while reading · refresh recommended";
        DataFrameStatus = $"{DataFrameRowPageLabel} · {DataFrameColumnPageLabel} · {consistency}"
            + (cellLimitApplied ? " · cell safety limit applied" : "");
        DataFrameErrorMessage = "";
        DataFrameState = rows.Count == 0 || columns.Length == 0
            ? DataFramePaneState.Empty
            : DataFramePaneState.Ready;
        UpdateCommandStates();
    }

    private void ApplyDataFrameTable(
        IReadOnlyList<DataFrameColumnInfo> columns,
        JsonArray indexLabels,
        JsonArray rows,
        bool forceSchemaRefresh)
    {
        var expectedNames = new[] { "__index__" }
            .Concat(columns.Select(column => column.DataColumnName))
            .ToArray();
        var schemaMatches = !forceSchemaRefresh
            && _dataFrameTable is not null
            && _dataFrameTable.Columns.Cast<DataColumn>()
                .Select(column => column.ColumnName)
                .SequenceEqual(expectedNames, StringComparer.Ordinal);
        if (!schemaMatches)
        {
            _dataFrameTable?.Dispose();
            _dataFrameTable = new DataTable("DataFramePreview")
            {
                CaseSensitive = true,
                Locale = CultureInfo.InvariantCulture,
            };
            _dataFrameTable.Columns.Add("__index__", typeof(string));
            foreach (var column in columns)
            {
                var dataColumn = _dataFrameTable.Columns.Add(column.DataColumnName, typeof(string));
                dataColumn.Caption = column.Name;
                dataColumn.ExtendedProperties["dtype"] = column.DType;
            }
            DataFrameRows = _dataFrameTable.DefaultView;
        }

        var table = _dataFrameTable!;
        table.BeginLoadData();
        try
        {
            while (table.Rows.Count > rows.Count)
                table.Rows.RemoveAt(table.Rows.Count - 1);
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var target = rowIndex < table.Rows.Count ? table.Rows[rowIndex] : table.NewRow();
                target[0] = indexLabels.Count > rowIndex
                    ? indexLabels[rowIndex]?.GetValue<string>() ?? ""
                    : "";
                var values = rows[rowIndex]?.AsArray() ?? [];
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    target[columnIndex + 1] = values.Count > columnIndex
                        ? values[columnIndex]?.GetValue<string>() ?? ""
                        : "";
                }
                if (rowIndex >= table.Rows.Count)
                    table.Rows.Add(target);
            }
        }
        finally
        {
            table.EndLoadData();
        }
    }

    private void SetDataFrameError(Exception exception)
    {
        DataFrameState = DataFramePaneState.Error;
        DataFrameErrorMessage = exception switch
        {
            RemoteInspectionException remote => $"{remote.Code}: {remote.Message}",
            _ => exception.Message,
        };
        DataFrameStatus = "DataFrame preview unavailable · refresh to retry";
        UpdateCommandStates();
    }

    private static int NormalizePageOffset(int requested, int total, int pageSize)
    {
        if (total <= 0)
            return 0;
        var lastPageOffset = ((total - 1) / pageSize) * pageSize;
        return Math.Clamp(requested, 0, lastPageOffset);
    }

    private static string PageRangeLabel(string label, int offset, int count, int total) =>
        total == 0 ? $"{label} 0 of 0" : $"{label} {offset + 1:N0}–{offset + count:N0} of {total:N0}";

    private async Task RefreshSelectedDetailInBackgroundAsync(CancellationToken scopeToken)
    {
        if (!IsConnected || InspectorState == InspectorPaneState.Loading || _currentObject is not NavigationContext context)
            return;

        if (context.Parent is null)
        {
            var selected = SelectedVariable;
            if (selected is null
                || selected.IsRemoved
                || !string.Equals(context.PinKey, selected.StableKey, StringComparison.Ordinal)
                || !string.Equals(context.Row.IdentityToken, selected.IdentityToken, StringComparison.Ordinal))
            {
                return;
            }
            context = context with { Row = selected };
            UpdateSelectedObjectSummary(context, markDetailsPending: false);
        }

        var isArray = string.Equals(context.Row.AdapterKind, "numpy.ndarray", StringComparison.Ordinal);
        var isDataFrame = string.Equals(context.Row.AdapterKind, "pandas.DataFrame", StringComparison.Ordinal);
        var isMatplotlib = context.Row.AdapterKind is "matplotlib.Figure" or "matplotlib.Axes";
        if (!isArray && !isDataFrame && !isMatplotlib)
            return;

        var generation = Volatile.Read(ref _detailGeneration);
        var detailToken = _detailCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scopeToken, detailToken);
        try
        {
            bool previewAvailable;
            if (isArray)
            {
                _arrayHandle = context.Row.HandleId;
                HasArraySelection = true;
                RefreshObjectHandleReferences();
                previewAvailable = await LoadArrayDescriptionAndPreviewAsync(
                    generation,
                    linked.Token,
                    preserveViewConfiguration: true);
            }
            else if (isDataFrame)
            {
                _dataFrameHandle = context.Row.HandleId;
                HasDataFrameSelection = true;
                RefreshObjectHandleReferences();
                previewAvailable = await LoadDataFrameDescriptionAndPreviewAsync(
                    generation,
                    linked.Token,
                    showLoading: false);
            }
            else
            {
                _matplotlibHandle = context.Row.HandleId;
                HasMatplotlibSelection = true;
                RefreshObjectHandleReferences();
                previewAvailable = await LoadMatplotlibDescriptionAndPreviewAsync(
                    generation,
                    linked.Token,
                    showLoading: false);
            }
            if (generation != _detailGeneration || linked.IsCancellationRequested)
                return;
            SelectedObjectStatus = isMatplotlib
                ? previewAvailable
                    ? $"Live Matplotlib preview refreshed · {DateTime.Now:HH:mm:ss}"
                    : $"Live Matplotlib render unavailable · {MatplotlibAvailabilityReason}"
                : isDataFrame
                ? previewAvailable
                    ? $"Live DataFrame refreshed · {DateTime.Now:HH:mm:ss}"
                    : "Live DataFrame refresh returned no consistent preview"
                : previewAvailable
                    ? $"Live image refreshed · {DateTime.Now:HH:mm:ss}"
                    : "Live array metadata refreshed · preview unavailable for this dtype or shape";
        }
        catch (OperationCanceledException)
        {
        }
        catch (RemoteInspectionException exception) when (exception.Code == "OBJECT_EXPIRED")
        {
            if (generation == _detailGeneration)
            {
                InspectorState = InspectorPaneState.Expired;
                SelectedObjectStatus = "Object expired · refresh the scope and select it again";
                if (isDataFrame)
                {
                    DataFrameState = DataFramePaneState.Error;
                    DataFrameErrorMessage = "OBJECT_EXPIRED: The selected DataFrame is no longer available.";
                }
                else if (isMatplotlib)
                {
                    SetMatplotlibError(exception);
                }
            }
        }
        catch (Exception exception)
        {
            if (generation == _detailGeneration)
            {
                if (isDataFrame)
                {
                    SetDataFrameError(exception);
                    SelectedObjectStatus = "Live DataFrame refresh paused · use Refresh in the DataFrame tab to retry";
                }
                else if (isMatplotlib)
                {
                    SetMatplotlibError(exception);
                    SelectedObjectStatus = "Live Matplotlib refresh paused · use Refresh preview to retry";
                }
                else
                {
                    SelectedObjectStatus = "Live image refresh paused · press F5 to retry";
                }
            }
        }
    }

    private async Task<bool> LoadArrayDescriptionAndPreviewAsync(
        long generation,
        CancellationToken token,
        bool preserveViewConfiguration = false)
    {
        using var handleResponse = await RequestHandleResponseAsync(
            "arrays.describe",
            new JsonObject { ["handleId"] = _arrayHandle },
            token);
        var descriptionFrame = handleResponse.Frame;
        if (generation != _detailGeneration || token.IsCancellationRequested)
            return false;
        var description = descriptionFrame.Header["result"]!.AsObject();
        _arrayDimensions = description["shape"]!.AsArray().Select(item => item!.GetValue<int>()).ToArray();
        ArrayShape = "(" + string.Join(", ", _arrayDimensions) + ")";
        ArrayDType = description["dtype"]!.GetValue<string>();
        ArrayStrides = "(" + string.Join(", ", description["strides"]!.AsArray().Select(item => item!.GetValue<int>())) + ")";
        ArrayDataAddress = description["dataAddressHex"]!.GetValue<string>();
        ArrayOwner = description["ownsData"]!.GetValue<bool>() ? "Owns data" : "View / shared owner";
        var guess = description["layoutGuess"]!.GetValue<string>();
        var suggestedLayout = guess switch { "GRAY" => "GRAY", "CHW" => "CHW", "volume" => "VOLUME", _ => "HWC" };
        LayoutConfidence = description["layoutConfidence"]!.GetValue<string>();
        var supportedModes = description["supportedPreviewModes"] as JsonArray;
        var supportedLayouts = supportedModes?
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!preserveViewConfiguration
            || string.IsNullOrWhiteSpace(ArrayLayout)
            || supportedLayouts is { Count: > 0 } && !supportedLayouts.Contains(ArrayLayout))
        {
            ArrayLayout = suggestedLayout;
        }
        if (!preserveViewConfiguration)
            SliceAxis = 0;
        UpdateSliceMaximum();
        SliceIndex = preserveViewConfiguration
            ? Math.Min(SliceIndex, SliceMaximum)
            : ArrayLayout == "VOLUME" && _arrayDimensions.Length == 3 ? _arrayDimensions[0] / 2 : 0;
        _arrayPreviewSupported = !string.Equals(guess, "unsupported", StringComparison.OrdinalIgnoreCase)
            && (supportedModes is null || supportedModes.Count > 0);
        RefreshObjectHandleReferences();
        if (!_arrayPreviewSupported)
        {
            ArrayPreview = null;
            Normalization = "Preview unavailable for this dtype or shape";
            UpdateCommandStates();
            return false;
        }
        try
        {
            return await LoadArrayPreviewAsync(token, generation);
        }
        catch (RemoteInspectionException exception) when (exception.Code == "INVALID_ARGUMENT")
        {
            _arrayPreviewSupported = false;
            ArrayPreview = null;
            Normalization = "Preview unavailable for this dtype or shape";
            UpdateCommandStates();
            return false;
        }
    }

    private async Task ReloadArrayPreviewAsync()
    {
        if (_arrayHandle is null || !IsConnected)
            return;
        try
        {
            await LoadArrayPreviewAsync(_detailCts?.Token ?? _connectionCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task LoadArrayTileAsync()
    {
        if (_arrayHandle is null || !IsConnected)
            return;
        try
        {
            var handle = _arrayHandle;
            var token = _detailCts?.Token ?? _connectionCts?.Token ?? CancellationToken.None;
            var previewGeneration = Interlocked.Increment(ref _arrayPreviewGeneration);
            var parameters = BuildArrayViewParameters();
            parameters["x"] = TileX;
            parameters["y"] = TileY;
            parameters["width"] = TileWidth;
            parameters["height"] = TileHeight;
            var frame = await RequestAsync("arrays.tile", parameters, token);
            if (!IsCurrentArrayPreviewRequest(previewGeneration, handle, token))
                return;
            ApplyArrayBitmap(frame);
            _fitMode = false;
            SetZoom(1);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task LoadHistogramAsync()
    {
        if (_arrayHandle is null || !IsConnected)
            return;
        try
        {
            var frame = await RequestAsync("arrays.histogram", new JsonObject
            {
                ["handleId"] = _arrayHandle,
                ["channel"] = HistogramChannel,
                ["bins"] = HistogramBinCount,
                ["layout"] = ArrayLayout,
                ["sliceAxis"] = SliceAxis,
                ["sliceIndex"] = SliceIndex,
            }, _detailCts?.Token ?? _connectionCts?.Token ?? CancellationToken.None);
            var result = frame.Header["result"]!.AsObject();
            var counts = result["counts"]!.AsArray();
            var edges = result["binEdges"]!.AsArray();
            var rows = Enumerable.Range(0, counts.Count).Select(index => new HistogramBinRow(
                index,
                edges[index]!.GetValue<double>(),
                edges[index + 1]!.GetValue<double>(),
                counts[index]!.GetValue<long>()));
            Replace(HistogramBins, rows);
            HistogramSummary = $"{result["sampleCount"]!.GetValue<int>():N0} sampled, "
                + $"NaN {result["nanCount"]!.GetValue<int>():N0}, "
                + $"+Inf {result["positiveInfinityCount"]!.GetValue<int>():N0}, "
                + $"-Inf {result["negativeInfinityCount"]!.GetValue<int>():N0}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task<bool> LoadArrayPreviewAsync(CancellationToken token, long? expectedGeneration = null)
    {
        var handle = _arrayHandle;
        var previewGeneration = Interlocked.Increment(ref _arrayPreviewGeneration);
        var parameters = BuildArrayViewParameters();
        parameters["maxWidth"] = 1024;
        parameters["maxHeight"] = 1024;
        var frame = await RequestAsync("arrays.preview", parameters, token);
        if (!IsCurrentArrayPreviewRequest(previewGeneration, handle, token, expectedGeneration))
            return ArrayPreview is not null;
        ApplyArrayBitmap(frame);
        if (_fitMode)
            ApplyFitZoom();
        return true;
    }

    private bool IsCurrentArrayPreviewRequest(
        long previewGeneration,
        string? handle,
        CancellationToken token,
        long? expectedDetailGeneration = null) =>
        previewGeneration == Volatile.Read(ref _arrayPreviewGeneration)
        && !token.IsCancellationRequested
        && string.Equals(handle, _arrayHandle, StringComparison.Ordinal)
        && (expectedDetailGeneration is null || expectedDetailGeneration == _detailGeneration);

    private JsonObject BuildArrayViewParameters()
    {
        var channelCount = ArrayLayout switch
        {
            "GRAY" or "VOLUME" => 1,
            "HWC" when _arrayDimensions.Length == 3 => _arrayDimensions[2],
            "CHW" when _arrayDimensions.Length == 3 => _arrayDimensions[0],
            _ => 3,
        };
        var enabled = channelCount == 1
            ? new JsonArray(true)
            : channelCount == 4
                ? new JsonArray(ChannelR, ChannelG, ChannelB, true)
                : new JsonArray(ChannelR, ChannelG, ChannelB);
        return new JsonObject
        {
            ["handleId"] = _arrayHandle,
            ["layout"] = ArrayLayout,
            ["colorOrder"] = ColorOrder,
            ["enabledChannels"] = enabled,
            ["sliceAxis"] = SliceAxis,
            ["sliceIndex"] = SliceIndex,
            ["normalization"] = NormalizationMode,
            ["percentileLow"] = PercentileLow,
            ["percentileHigh"] = PercentileHigh,
        };
    }

    private void ApplyArrayBitmap(ProtocolFrame frame)
    {
        var metadata = frame.Header["result"]!.AsObject();
        var width = metadata["width"]!.GetValue<int>();
        var height = metadata["height"]!.GetValue<int>();
        var stride = metadata["stride"]!.GetValue<int>();
        var formatName = metadata["pixelFormat"]!.GetValue<string>();
        var pixelFormat = formatName switch
        {
            "Gray8" => PixelFormats.Gray8,
            "RGB24" => PixelFormats.Rgb24,
            "BGRA32" => PixelFormats.Bgra32,
            _ => throw new InvalidOperationException($"Unsupported pixel format: {formatName}"),
        };
        var bitmap = new WriteableBitmap(width, height, 96, 96, pixelFormat, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame.Binary, stride, 0);
        bitmap.Freeze();
        PreviewWidth = width;
        PreviewHeight = height;
        _previewRowStep = metadata["rowStep"]?.GetValue<int>() ?? 1;
        _previewColumnStep = metadata["columnStep"]?.GetValue<int>() ?? 1;
        _previewOriginX = metadata["originX"]?.GetValue<int>() ?? 0;
        _previewOriginY = metadata["originY"]?.GetValue<int>() ?? 0;
        SourceImageWidth = metadata["sourceWidth"]?.GetValue<int>() ?? width;
        SourceImageHeight = metadata["sourceHeight"]?.GetValue<int>() ?? height;
        ArrayPreview = bitmap;
        if (metadata["normalization"] is JsonObject normalization)
        {
            _displayMinimum = normalization["displayMinimum"]?.GetValue<double>();
            _displayMaximum = normalization["displayMaximum"]?.GetValue<double>();
            var mode = normalization["mode"]?.GetValue<string>() ?? "NONE";
            Normalization = $"{mode} [{FormatNumber(_displayMinimum)}, {FormatNumber(_displayMaximum)}] · "
                + $"NaN {normalization["nanCount"]?.GetValue<int>() ?? 0}, "
                + $"+Inf {normalization["positiveInfinityCount"]?.GetValue<int>() ?? 0}, "
                + $"-Inf {normalization["negativeInfinityCount"]?.GetValue<int>() ?? 0}";
        }
        else
        {
            _displayMinimum = 0;
            _displayMaximum = 255;
            Normalization = "NONE [0, 255]";
        }
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(RefreshIntervalSeconds), token);
                NotifySnapshotState();
                if (AutoRefreshEnabled && _currentScope is not null && _currentScope.Kind != RuntimeNodeKind.GcObjects)
                    await LoadScopeAsync(
                        _currentScope,
                        showLoadingOverlay: false,
                        preserveSelectionAcrossScopeChange: true);
                else if (IsConnected)
                    await RequestAsync("runtime.getInfo", cancellationToken: token);
                await RefreshMemoryAsync(token);
                if (ExecutionMonitoringActive)
                    await LoadExecutionEventsAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                if (_session.IsConnected)
                    SetError(exception);
                return;
            }
        }
    }

    private void StartRefreshLoop()
    {
        _refreshCts?.Cancel();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts?.Token ?? CancellationToken.None);
        _ = RefreshLoopAsync(_refreshCts.Token);
    }

    private async Task<HandleResponseLease> RequestHandleResponseAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        HandleActivityLease? activity = await EnterHandleActivityAsync(cancellationToken);
        Task<ProtocolFrame>? requestTask = null;
        try
        {
            // Transport cancellation would discard a response that may contain newly-created
            // strong handles. Keep draining it, while allowing the UI operation to cancel.
            requestTask = RequestDrainableAsync(method, parameters, cancellationToken);
            try
            {
                var frame = await requestTask.WaitAsync(cancellationToken);
                TrackObjectHandles(frame, activity.Generation);
                var response = new HandleResponseLease(frame, activity);
                activity = null;
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DrainCanceledHandleResponse(requestTask, activity!);
                activity = null;
                throw;
            }
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private async Task<HandleActivityLease> EnterHandleActivityAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? releaseCompleted;
            lock (_handleLifetimeSync)
            {
                if (_handleSessionClosing)
                    throw new OperationCanceledException(cancellationToken);
                if (!_handleReleaseInProgress)
                {
                    _activeHandleResponses++;
                    return new HandleActivityLease(this, _handleSessionGeneration);
                }
                releaseCompleted = _handleReleaseCompleted?.Task;
            }
            if (releaseCompleted is not null)
                await releaseCompleted.WaitAsync(cancellationToken);
            else
                await Task.Yield();
        }
    }

    private void DrainCanceledHandleResponse(Task<ProtocolFrame> requestTask, HandleActivityLease activity) =>
        _ = DrainCanceledHandleResponseAsync(requestTask, activity);

    private async Task DrainCanceledHandleResponseAsync(Task<ProtocolFrame> requestTask, HandleActivityLease activity)
    {
        try
        {
            var frame = await requestTask.ConfigureAwait(false);
            TrackObjectHandles(frame, activity.Generation);
        }
        catch
        {
            // Transport/session error handling owns the user-visible state. This continuation
            // exists only to collect handles from a response the canceled UI no longer needs.
        }
        finally
        {
            activity.Dispose();
        }
    }

    private void TrackObjectHandles(ProtocolFrame frame, int generation)
    {
        var handles = EnumerateObjectHandles(frame.Header["result"]).ToArray();
        if (handles.Length == 0)
            return;
        lock (_handleLifetimeSync)
        {
            if (generation != _handleSessionGeneration)
                return;
            _knownObjectHandles.UnionWith(handles);
            _handleReleaseRequested = true;
        }
    }

    private void EndHandleActivity()
    {
        int? releaseGeneration = null;
        lock (_handleLifetimeSync)
        {
            if (_activeHandleResponses > 0)
                _activeHandleResponses--;
            if (TryBeginHandleReleaseLocked(out var generation))
                releaseGeneration = generation;
        }
        if (releaseGeneration is int value)
            _ = ReleaseObsoleteHandlesBatchAsync(value);
    }

    private void RefreshObjectHandleReferences()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Variables.Where(row => !row.IsRemoved))
            AddHandle(referenced, row.HandleId);
        foreach (var result in GlobalSearchResults)
        {
            AddHandle(referenced, result.Value?.HandleId);
            AddHandle(referenced, result.ConsoleHandle);
        }
        foreach (var node in EnumerateRuntimeNodes())
            AddHandle(referenced, node.ConsoleHandle);
        AddHandle(referenced, _currentScope?.ConsoleHandle);
        if (SelectedVariable is { IsRemoved: false } selected)
            AddHandle(referenced, selected.HandleId);
        AddContextHandles(referenced, _currentObject);
        foreach (var context in _navigationHistory)
            AddContextHandles(referenced, context);
        foreach (var context in _pinnedContexts.Values)
            AddContextHandles(referenced, context);
        foreach (var root in ObjectRoots)
            AddTreeHandles(referenced, root);
        foreach (var child in ObjectChildren)
            AddHandle(referenced, child.HandleId);
        AddHandle(referenced, _arrayHandle);
        AddHandle(referenced, _dataFrameHandle);
        AddHandle(referenced, _matplotlibHandle);

        int? releaseGeneration = null;
        lock (_handleLifetimeSync)
        {
            _referencedObjectHandles = referenced;
            _handleReleaseRequested = true;
            if (TryBeginHandleReleaseLocked(out var generation))
                releaseGeneration = generation;
        }
        if (releaseGeneration is int value)
            _ = ReleaseObsoleteHandlesBatchAsync(value);
    }

    private bool TryBeginHandleReleaseLocked(out int generation)
    {
        generation = _handleSessionGeneration;
        if (_handleSessionClosing
            || _handleReleaseInProgress
            || _activeHandleResponses != 0
            || !_handleReleaseRequested
            || !_session.IsConnected
            || !_knownObjectHandles.Any(handle => !_referencedObjectHandles.Contains(handle)))
        {
            return false;
        }

        _handleReleaseInProgress = true;
        _handleReleaseRequested = false;
        _handleReleaseCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    private async Task ReleaseObsoleteHandlesBatchAsync(int generation)
    {
        var failed = false;
        try
        {
            string[] candidates;
            lock (_handleLifetimeSync)
            {
                candidates = generation == _handleSessionGeneration
                    ? _knownObjectHandles
                        .Where(handle => !_referencedObjectHandles.Contains(handle))
                        .Take(MaxHandleReleaseBatch)
                        .ToArray()
                    : [];
            }

            foreach (var handle in candidates)
            {
                lock (_handleLifetimeSync)
                {
                    if (generation != _handleSessionGeneration
                        || _handleSessionClosing
                        || !_knownObjectHandles.Contains(handle)
                        || _referencedObjectHandles.Contains(handle))
                    {
                        continue;
                    }
                }
                if (!_session.IsConnected)
                    break;
                try
                {
                    await _session.RequestAsync(
                        "objects.release",
                        new JsonObject { ["handleId"] = handle },
                        CancellationToken.None).ConfigureAwait(false);
                    lock (_handleLifetimeSync)
                    {
                        if (generation == _handleSessionGeneration)
                            _knownObjectHandles.Remove(handle);
                    }
                }
                catch
                {
                    failed = true;
                    break;
                }
            }
        }
        finally
        {
            FinishHandleRelease(generation, retryRemaining: !failed);
        }
    }

    private void FinishHandleRelease(int generation, bool retryRemaining)
    {
        TaskCompletionSource? completed;
        var retry = false;
        lock (_handleLifetimeSync)
        {
            _handleReleaseInProgress = false;
            completed = _handleReleaseCompleted;
            _handleReleaseCompleted = null;
            if (retryRemaining
                && generation == _handleSessionGeneration
                && !_handleSessionClosing
                && _activeHandleResponses == 0
                && _session.IsConnected
                && _knownObjectHandles.Any(handle => !_referencedObjectHandles.Contains(handle)))
            {
                _handleReleaseRequested = true;
                retry = true;
            }
        }
        completed?.TrySetResult();
        if (retry)
            _ = QueueNextHandleReleaseBatchAsync();
    }

    private async Task QueueNextHandleReleaseBatchAsync()
    {
        await Task.Delay(1).ConfigureAwait(false);
        int? releaseGeneration = null;
        lock (_handleLifetimeSync)
        {
            if (TryBeginHandleReleaseLocked(out var generation))
                releaseGeneration = generation;
        }
        if (releaseGeneration is int value)
            await ReleaseObsoleteHandlesBatchAsync(value).ConfigureAwait(false);
    }

    private async Task DetachInspectorSessionAsync()
    {
        _selectionCts?.Cancel();
        _detailCts?.Cancel();
        _refreshCts?.Cancel();
        await ReleaseKnownHandlesBeforeDetachAsync();
        await _session.DetachAsync();
    }

    private async Task ReleaseKnownHandlesBeforeDetachAsync()
    {
        if (!_session.IsConnected)
            return;

        lock (_handleLifetimeSync)
        {
            _handleSessionClosing = true;
            _handleReleaseRequested = false;
        }
        using var budget = new CancellationTokenSource(DetachHandleReleaseBudget);
        var acquired = false;
        var generation = 0;
        try
        {
            while (!budget.IsCancellationRequested)
            {
                Task? currentRelease;
                lock (_handleLifetimeSync)
                {
                    if (_activeHandleResponses == 0 && !_handleReleaseInProgress)
                    {
                        _handleReleaseInProgress = true;
                        _handleReleaseCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        generation = _handleSessionGeneration;
                        acquired = true;
                        break;
                    }
                    currentRelease = _handleReleaseCompleted?.Task;
                }
                if (currentRelease is not null)
                    await currentRelease.WaitAsync(budget.Token);
                else
                    await Task.Delay(10, budget.Token);
            }

            if (!acquired)
                return;
            string[] handles;
            lock (_handleLifetimeSync)
                handles = _knownObjectHandles.Take(MaxDetachHandleReleaseBatch).ToArray();
            foreach (var handle in handles)
            {
                budget.Token.ThrowIfCancellationRequested();
                await _session.RequestAsync(
                    "objects.release",
                    new JsonObject { ["handleId"] = handle },
                    budget.Token);
                lock (_handleLifetimeSync)
                {
                    if (generation == _handleSessionGeneration)
                        _knownObjectHandles.Remove(handle);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Detach still has to proceed if best-effort handle cleanup fails.
        }
        finally
        {
            if (acquired)
                FinishHandleRelease(generation, retryRemaining: false);
        }
    }

    private void BeginObjectHandleSession()
    {
        lock (_handleLifetimeSync)
        {
            _handleSessionGeneration++;
            _knownObjectHandles.Clear();
            _referencedObjectHandles.Clear();
            _handleReleaseRequested = false;
            _handleSessionClosing = false;
        }
    }

    private static IEnumerable<string> EnumerateObjectHandles(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            foreach (var property in value)
            {
                if (property.Key is "handleId" or "consoleHandle"
                    && property.Value is JsonValue handleValue
                    && handleValue.TryGetValue<string>(out var handle)
                    && !string.IsNullOrWhiteSpace(handle))
                {
                    yield return handle;
                }
                foreach (var nested in EnumerateObjectHandles(property.Value))
                    yield return nested;
            }
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items)
            foreach (var nested in EnumerateObjectHandles(item))
                yield return nested;
        }
    }

    private static void AddHandle(ISet<string> handles, string? handle)
    {
        if (!string.IsNullOrWhiteSpace(handle))
            handles.Add(handle);
    }

    private static void AddContextHandles(ISet<string> handles, NavigationContext? context)
    {
        while (context is not null)
        {
            AddHandle(handles, context.Row.HandleId);
            context = context.Parent;
        }
    }

    private static void AddTreeHandles(ISet<string> handles, ObjectTreeNode node)
    {
        AddHandle(handles, node.Value?.HandleId);
        foreach (var child in node.Children)
            AddTreeHandles(handles, child);
    }

    private async Task<ProtocolFrame> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _session.RequestAsync(method, parameters, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            LastLatency = $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms";
        }
    }

    private async Task<ProtocolFrame> RequestDrainableAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _session.RequestDrainableAsync(method, parameters, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            LastLatency = $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms";
        }
    }

    private void RefreshProcesses()
    {
        var selectedId = SelectedProcess?.Id;
        Replace(Processes, _processDiscovery.GetPythonProcesses());
        SelectedProcess = Processes.FirstOrDefault(item => item.Id == selectedId);
    }

    private void RefreshProcessMemory()
    {
        if (TargetPid is not int pid)
        {
            _lastProcessMemory = null;
            PrivateBytes = WorkingSet = VirtualSize = PeakWorkingSet = "—";
            return;
        }
        _lastProcessMemory = _processDiscovery.GetMemoryInfo(pid);
        if (_lastProcessMemory is null)
        {
            PrivateBytes = WorkingSet = VirtualSize = PeakWorkingSet = "Unavailable";
            return;
        }
        PrivateBytes = FormatBytes(_lastProcessMemory.PrivateBytes);
        WorkingSet = FormatBytes(_lastProcessMemory.WorkingSetBytes);
        VirtualSize = FormatBytes(_lastProcessMemory.VirtualBytes);
        PeakWorkingSet = FormatBytes(_lastProcessMemory.PeakWorkingSetBytes);
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        foreach (var row in Variables)
            row.IsPinned = _pinnedKeys.Contains(row.StableKey);

        IEnumerable<VariableRow> filtered = Variables;
        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.ModuleName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.QualifiedTypeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Address.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.SafePreview.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.Equals(ScopeFilter, "All scopes", StringComparison.OrdinalIgnoreCase))
        {
            filtered = ScopeFilter == "module"
                ? filtered.Where(row => row.Scope.StartsWith("module:", StringComparison.OrdinalIgnoreCase))
                : filtered.Where(row => string.Equals(row.Scope, ScopeFilter, StringComparison.OrdinalIgnoreCase));
        }
        filtered = ChangeFilter switch
        {
            "Changed only" => filtered.Where(row => row.Changed),
            "Added" => filtered.Where(row => row.ChangeKind == VariableChangeKind.Added),
            "Removed" => filtered.Where(row => row.ChangeKind == VariableChangeKind.Removed),
            "Rebound" => filtered.Where(row => row.ChangeKind == VariableChangeKind.Rebound),
            "Updated" => filtered.Where(row => row.ChangeKind == VariableChangeKind.MetadataChanged),
            _ => filtered,
        };
        if (!string.Equals(TypeFilter, "All types", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(row => string.Equals(row.TypeName, TypeFilter, StringComparison.OrdinalIgnoreCase));
        if (ArraysOnly)
            filtered = filtered.Where(row => row.AdapterKind == "numpy.ndarray");
        if (ExpandableOnly)
            filtered = filtered.Where(row => row.Expandable);
        if (PinnedOnly)
            filtered = filtered.Where(row => row.IsPinned);

        var visible = filtered.ToList();
        var preferredSelectionKey = SelectedVariable?.StableKey
            ?? (_currentObject is { Parent: null } context ? context.Row.StableKey : null);
        _suppressVariableNavigation = true;
        try
        {
            ReconcileVariableCollection(FilteredVariables, visible);
            SelectedVariable = preferredSelectionKey is null
                ? null
                : visible.FirstOrDefault(row => row.StableKey == preferredSelectionKey);
        }
        finally
        {
            _suppressVariableNavigation = false;
        }
        var changed = visible.Count(row => row.Changed);
        FilterResultLabel = $"{visible.Count:N0} of {Variables.Count:N0} visible" + (changed > 0 ? $" · {changed:N0} changed" : "");
        OnPropertyChanged(nameof(ShowVariableListEmpty));
        OnPropertyChanged(nameof(VariableListStatusMessage));
    }

    private void ApplyObjectChildrenSearch()
    {
        var query = ObjectChildrenSearchText.Trim();
        var visible = string.IsNullOrEmpty(query)
            ? ObjectChildren.ToList()
            : ObjectChildren
                .Where(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        Replace(FilteredObjectChildren, visible);
        ObjectChildrenSearchResultLabel = string.IsNullOrEmpty(query)
            ? $"{ObjectChildren.Count:N0} loaded names"
            : $"{visible.Count:N0} of {ObjectChildren.Count:N0} names match";
    }

    private void ApplyObjectTreeSearch()
    {
        var query = ObjectTreeSearchText.Trim();
        var matchCount = 0;
        foreach (var root in ObjectRoots)
        {
            if (string.IsNullOrEmpty(query))
            {
                ResetObjectTreeSearchState(root);
                continue;
            }

            ApplyObjectTreeSearch(root, query, ref matchCount, isRoot: true);
        }
        var loadedCount = ObjectRoots.Sum(CountObjectTreeValues);
        ObjectTreeSearchResultLabel = string.IsNullOrEmpty(query)
            ? $"{loadedCount:N0} loaded names"
            : $"{matchCount:N0} of {loadedCount:N0} loaded names match";
    }

    private static bool ApplyObjectTreeSearch(ObjectTreeNode node, string query, ref int matchCount, bool isRoot = false)
    {
        var isObject = node.Kind == ObjectNodeKind.Object && node.Value is not null;
        var selfMatches = isObject && node.Value!.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (selfMatches)
            matchCount++;
        var descendantMatches = false;
        foreach (var child in node.Children)
            descendantMatches |= ApplyObjectTreeSearch(child, query, ref matchCount);

        var branchMatches = selfMatches || descendantMatches;
        node.IsSearchMatch = selfMatches;
        node.IsSearchAncestor = descendantMatches && !selfMatches;
        node.IsSearchVisible = isRoot || branchMatches;
        if (descendantMatches)
            node.IsExpanded = true;
        return branchMatches;
    }

    private static void ResetObjectTreeSearchState(ObjectTreeNode node)
    {
        node.IsSearchVisible = true;
        node.IsSearchMatch = false;
        node.IsSearchAncestor = false;
        foreach (var child in node.Children)
            ResetObjectTreeSearchState(child);
    }

    private static int CountObjectTreeValues(ObjectTreeNode node) =>
        (node.Kind == ObjectNodeKind.Object && node.Value is not null ? 1 : 0)
        + node.Children.Sum(CountObjectTreeValues);

    private void CaptureObjectTreeExpansion()
    {
        _objectTreeExpansionBeforeSearch.Clear();
        foreach (var root in ObjectRoots)
            CaptureObjectTreeExpansion(root);
    }

    private void CaptureObjectTreeExpansion(ObjectTreeNode node)
    {
        _objectTreeExpansionBeforeSearch[ObjectTreeExpansionKey(node)] = node.IsExpanded;
        foreach (var child in node.Children)
            CaptureObjectTreeExpansion(child);
    }

    private void RestoreObjectTreeExpansion()
    {
        foreach (var root in ObjectRoots)
            RestoreObjectTreeExpansion(root);
        _objectTreeExpansionBeforeSearch.Clear();
    }

    private void RestoreObjectTreeExpansion(ObjectTreeNode node)
    {
        if (_objectTreeExpansionBeforeSearch.TryGetValue(ObjectTreeExpansionKey(node), out var isExpanded))
            node.IsExpanded = isExpanded;
        foreach (var child in node.Children)
            RestoreObjectTreeExpansion(child);
    }

    private static string ObjectTreeExpansionKey(ObjectTreeNode node) =>
        $"{node.Kind}\u001f{node.Depth}\u001f{node.Path}\u001f{node.Label}";

    private void ApplyClassTreeSearch()
    {
        var tokens = ClassTreeSearchText.Trim().Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matchCount = 0;
        var hasVisibleResult = false;
        foreach (var root in ClassTree)
        {
            if (tokens.Length == 0)
                ResetClassTreeSearchState(root);
            else
                hasVisibleResult |= ApplyClassTreeSearch(root, tokens, ref matchCount);
        }

        ClassTreeSearchResultLabel = tokens.Length == 0
            ? $"{_classTreeLoadedSearchableCount:N0} loaded class details{_classTreeBoundedSummary}"
            : $"{matchCount:N0} of {_classTreeLoadedSearchableCount:N0} loaded class details match{_classTreeBoundedSummary}";
        IsClassTreeSearchEmpty = tokens.Length > 0 && !hasVisibleResult;
    }

    private static bool ApplyClassTreeSearch(
        ClassTreeNode node,
        IReadOnlyList<string> tokens,
        ref int matchCount)
    {
        var textMatches = tokens.All(token => ClassTreeNodeContains(node, token));
        var selfMatches = IsClassTreeSearchableNode(node) && textMatches;
        if (selfMatches)
            matchCount++;

        var descendantMatches = false;
        foreach (var child in node.Children)
            descendantMatches |= ApplyClassTreeSearch(child, tokens, ref matchCount);

        var branchMatches = selfMatches || descendantMatches;
        node.IsSearchMatch = selfMatches;
        node.IsSearchAncestor = !selfMatches && descendantMatches;
        node.IsSearchVisible = branchMatches;
        if (descendantMatches)
            node.IsExpanded = true;
        return branchMatches;
    }

    private static bool ClassTreeNodeContains(ClassTreeNode node, string token) =>
        node.Label.Contains(token, StringComparison.OrdinalIgnoreCase)
        || node.Kind.Contains(token, StringComparison.OrdinalIgnoreCase)
        || node.DeclaredBy.Contains(token, StringComparison.OrdinalIgnoreCase)
        || node.Detail.Contains(token, StringComparison.OrdinalIgnoreCase)
        || node.Source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static void ResetClassTreeSearchState(ClassTreeNode node)
    {
        node.IsSearchVisible = true;
        node.IsSearchMatch = false;
        node.IsSearchAncestor = false;
        foreach (var child in node.Children)
            ResetClassTreeSearchState(child);
    }

    private static bool IsClassTreeSearchableNode(ClassTreeNode node) =>
        node.Kind is not ("group" or "metadata-group" or "parameters" or "status");

    private static int CountClassTreeSearchableNodes(ClassTreeNode node) =>
        (IsClassTreeSearchableNode(node) ? 1 : 0)
        + node.Children.Sum(CountClassTreeSearchableNodes);

    private static void SetClassTreeHierarchy(
        IEnumerable<ClassTreeNode> nodes,
        ClassTreeNode? parent,
        int depth)
    {
        foreach (var node in nodes)
        {
            node.Parent = parent;
            node.Depth = depth;
            SetClassTreeHierarchy(node.Children, node, depth + 1);
        }
    }

    private void CaptureClassTreeExpansion()
    {
        _classTreeExpansionBeforeSearch.Clear();
        VisitClassTree(ClassTree, "", (node, path) =>
            _classTreeExpansionBeforeSearch[path] = node.IsExpanded);
    }

    private void RestoreClassTreeExpansion()
    {
        VisitClassTree(ClassTree, "", (node, path) =>
        {
            if (_classTreeExpansionBeforeSearch.TryGetValue(path, out var isExpanded))
                node.IsExpanded = isExpanded;
        });
        _classTreeExpansionBeforeSearch.Clear();
    }

    private static void VisitClassTree(
        IReadOnlyList<ClassTreeNode> nodes,
        string parentPath,
        Action<ClassTreeNode, string> visitor)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var signature = $"{node.Kind.Length}:{node.Kind}{node.Label.Length}:{node.Label}";
            occurrences.TryGetValue(signature, out var occurrence);
            occurrences[signature] = occurrence + 1;
            var path = $"{parentPath}/{signature}#{occurrence}";
            visitor(node, path);
            VisitClassTree(node.Children, path, visitor);
        }
    }

    private void PrepareClassTreeRebuild(bool preserveSearches)
    {
        CancelClassTreeSearchDebounce();
        if (!preserveSearches)
            _classTreeExpansionBeforeSearch.Clear();
        _classTreeMembersTruncated = false;
        _classTreeLoadedSearchableCount = 0;
        _classTreeBoundedSummary = "";
        ClassTree.Clear();
        ApplyClassTreeSearch();
    }

    private void ResetClassTreeSearch()
    {
        CancelClassTreeSearchDebounce();
        ClassTreeSearchText = "";
        _classTreeExpansionBeforeSearch.Clear();
        _classTreeMembersTruncated = false;
        _classTreeLoadedSearchableCount = 0;
        _classTreeBoundedSummary = "";
        ApplyClassTreeSearch();
    }

    private void ResetObjectSearches()
    {
        ObjectChildrenSearchText = "";
        ObjectTreeSearchText = "";
        _objectTreeExpansionBeforeSearch.Clear();
        ApplyObjectChildrenSearch();
        ApplyObjectTreeSearch();
    }

    private void ScheduleClassTreeSearch()
    {
        _classTreeSearchDebounceCts = new CancellationTokenSource();
        _ = ApplyClassTreeSearchAfterDelayAsync(_classTreeSearchDebounceCts.Token);
    }

    private async Task ApplyClassTreeSearchAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ClassTreeSearchDebounceDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        void Apply(object? _)
        {
            if (!cancellationToken.IsCancellationRequested)
                ApplyClassTreeSearch();
        }
        if (_uiContext is null)
            Apply(null);
        else
            _uiContext.Post(Apply, null);
    }

    private void CancelClassTreeSearchDebounce()
    {
        _classTreeSearchDebounceCts?.Cancel();
        _classTreeSearchDebounceCts?.Dispose();
        _classTreeSearchDebounceCts = null;
    }

    private void ScheduleSearchFilter()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        IsSearchPending = true;
        _ = ApplySearchFilterAfterDelayAsync(_searchDebounceCts.Token);
    }

    private async Task ApplySearchFilterAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(175), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        void Apply(object? _)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            ApplyFilter();
            IsSearchPending = false;
        }
        if (_uiContext is null)
            Apply(null);
        else
            _uiContext.Post(Apply, null);
    }

    private void CancelSearchDebounce()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
        IsSearchPending = false;
    }

    private void ClearSearch()
    {
        SearchText = "";
        if (_currentScope?.Kind == RuntimeNodeKind.GcObjects)
            _ = LoadScopeAsync(_currentScope, resetPage: true);
    }

    private void ClearFilters()
    {
        ScopeFilter = "All scopes";
        ChangeFilter = "All changes";
        TypeFilter = "All types";
        ArraysOnly = false;
        ExpandableOnly = false;
        PinnedOnly = false;
        ApplyFilter();
    }

    private void ResetCurrentBaseline()
    {
        if (_currentScope is null)
            return;
        var scopeKey = GetScopeKey(_currentScope);
        foreach (var key in _scopeSnapshots.Keys.Where(key => key.StartsWith(scopeKey + "|", StringComparison.Ordinal)).ToArray())
            _scopeSnapshots.Remove(key);
        foreach (var key in _activeChanges.Keys.Where(key => key.StartsWith(scopeKey + ":", StringComparison.Ordinal)).ToArray())
            _activeChanges.Remove(key);
        Status = "Change baseline reset for the current scope";
        _ = LoadScopeAsync(_currentScope);
    }

    private void ToggleSelectedPin()
    {
        if (_currentObject is null || _currentObject.Row.IsRemoved)
            return;
        var context = _currentObject;
        if (_pinnedKeys.Remove(context.PinKey))
        {
            _pinnedContexts.Remove(context.PinKey);
            var existing = PinnedObjects.FirstOrDefault(item => item.StableKey == context.PinKey);
            if (existing is not null)
                PinnedObjects.Remove(existing);
            Status = $"Unpinned {context.Row.Name}";
        }
        else
        {
            _pinnedKeys.Add(context.PinKey);
            _pinnedContexts[context.PinKey] = context;
            PinnedObjects.Add(new PinnedObjectRow(
                context.PinKey,
                context.Row.Name,
                context.Path,
                context.Row.TypeName,
                context.Row.SafePreview,
                context.Row.Address));
            Status = $"Pinned {context.Row.Name}";
        }
        ApplyFilter();
        OnPropertyChanged(nameof(IsSelectedObjectPinned));
        RefreshObjectHandleReferences();
        UpdateCommandStates();
    }

    private void CopySelectedObjectPath()
    {
        if (_currentObject is null)
            return;
        _clipboardService.SetText(_currentObject.Path);
        Status = "Object path copied";
    }

    private void CopySelectedObjectType()
    {
        if (_currentObject is null)
            return;
        var typeName = string.IsNullOrWhiteSpace(_currentObject.Row.QualifiedTypeName)
            ? _currentObject.Row.TypeName
            : _currentObject.Row.QualifiedTypeName;
        _clipboardService.SetText(typeName);
        Status = "Object type copied";
    }

    private void CopySelectedObjectAddress()
    {
        if (_currentObject is null)
            return;
        _clipboardService.SetText(_currentObject.Row.Address);
        Status = "Object address copied";
    }

    public void CopyDisplayedValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        _clipboardService.SetText(value);
        Status = "Value copied";
    }

    private void CopyEnvironment()
    {
        var text = $"$env:PY_INSPECTOR_HOST='127.0.0.1'\r\n$env:PY_INSPECTOR_PORT='{PortText}'\r\n$env:PY_INSPECTOR_TOKEN='{Token}'\r\n$env:PYTHONPATH='{FindRepositoryRoot()}\\agent'";
        _clipboardService.SetText(text);
        Status = "Connection environment copied";
    }

    private void BrowsePython()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Python executable",
            Filter = "Python executable (python.exe)|python.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
            PythonExecutable = dialog.FileName;
    }

    private void BrowseScript()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Python script",
            Filter = "Python scripts (*.py)|*.py|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : null,
        };
        if (dialog.ShowDialog() == true)
        {
            ScriptPath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(WorkingDirectory))
                WorkingDirectory = Path.GetDirectoryName(dialog.FileName) ?? "";
        }
    }

    private void BrowseWorkingDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select working directory",
            InitialDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : null,
        };
        if (dialog.ShowDialog() == true)
            WorkingDirectory = dialog.FolderName;
    }

    private void AddEnvironmentVariable()
    {
        var row = new EnvironmentVariableRow();
        LaunchEnvironment.Add(row);
        SelectedEnvironmentVariable = row;
    }

    private void RemoveEnvironmentVariable()
    {
        if (SelectedEnvironmentVariable is null)
            return;
        LaunchEnvironment.Remove(SelectedEnvironmentVariable);
        SelectedEnvironmentVariable = null;
    }

    private void OnManagedOutput(object? sender, ProcessOutputEventArgs args)
    {
        void Apply(object? _)
        {
            LaunchOutput.Add(new LaunchOutputLine(DateTime.Now, args.Kind, args.Text));
            while (LaunchOutput.Count > 5000)
                LaunchOutput.RemoveAt(0);
        }
        if (_uiContext is null)
            Apply(null);
        else
            _uiContext.Post(Apply, null);
    }

    private void OnManagedExited(object? sender, ManagedProcessExitedEventArgs args)
    {
        void Apply(object? _)
        {
            IsManagedRunning = false;
            ManagedProcessId = null;
            ManagedExitCode = args.ExitCode.ToString();
            ManagedStatus = args.WasStopped ? $"Stopped (exit code {args.ExitCode})" : $"Exited (code {args.ExitCode})";
            LaunchOutput.Add(new LaunchOutputLine(DateTime.Now, ProcessOutputKind.StandardOutput, $"Process {args.ProcessId} exited with code {args.ExitCode}."));
            UpdateCommandStates();
        }
        if (_uiContext is null)
            Apply(null);
        else
            _uiContext.Post(Apply, null);
    }

    private bool TryGetConnectionSettings(out int port)
    {
        if (!int.TryParse(PortText, out port) || port is < 1 or > 65535)
        {
            ErrorMessage = "Port must be between 1 and 65535.";
            return false;
        }
        if (Token.Length < 64)
        {
            ErrorMessage = "Token must contain at least 64 characters (256 bits in hex).";
            return false;
        }
        return true;
    }

    private void OnSessionDisconnected(object? sender, string message)
    {
        void Apply(object? _)
        {
            ErrorMessage = "";
            ResetConnection(message);
        }
        if (_uiContext is null)
            Apply(null);
        else
            _uiContext.Post(Apply, null);
    }

    private void ResetConnection(string status)
    {
        BeginObjectHandleSession();
        _selectionCts?.Cancel();
        _detailCts?.Cancel();
        _refreshCts?.Cancel();
        var searchCts = _runtimeSearchCts;
        _runtimeSearchCts = null;
        searchCts?.Cancel();
        CancelSearchDebounce();
        IsConnected = false;
        IsBusy = false;
        IsAwaitingBootstrap = false;
        ConnectionRecoveryMessage = "";
        Status = status;
        RuntimeRoots.Clear();
        Variables.Clear();
        FilteredVariables.Clear();
        GlobalSearchResults.Clear();
        SelectedGlobalSearchResult = null;
        GlobalSearchStatus = "Connect to a Python runtime to search it.";
        IsGlobalSearchRunning = false;
        ObjectChildren.Clear();
        FilteredObjectChildren.Clear();
        ObjectRoots.Clear();
        ResetObjectSearches();
        ClassMembers.Clear();
        ClassTree.Clear();
        ResetClassTreeSearch();
        PinnedObjects.Clear();
        TypeFilters.Clear();
        TypeFilters.Add("All types");
        MemorySnapshots.Clear();
        MemoryStatistics.Clear();
        MemoryTimeline.Clear();
        ExecutionEvents.Clear();
        BeforeMemorySnapshot = null;
        AfterMemorySnapshot = null;
        IsTracemallocTracing = false;
        _pythonCurrentBytes = _pythonPeakBytes = null;
        TracemallocStatus = "Stopped";
        PythonCurrentMemory = PythonPeakMemory = TracemallocOverhead = "—";
        TracemallocCoverage = "Not tracing";
        ExecutionMonitoringAvailable = false;
        ExecutionMonitoringActive = false;
        ExecutionDroppedCount = 0;
        ExecutionStatus = "Unavailable until connected";
        _lastExecutionSequence = 0;
        TargetPid = null;
        ConnectionMode = "Not connected";
        PythonVersion = Architecture = Executable = "—";
        PrivateBytes = WorkingSet = VirtualSize = PeakWorkingSet = "—";
        _lastProcessMemory = null;
        _scopeSnapshots.Clear();
        _activeChanges.Clear();
        _pinnedKeys.Clear();
        _pinnedContexts.Clear();
        _navigationHistory.Clear();
        _navigationIndex = -1;
        _suppressVariableNavigation = true;
        try { SelectedVariable = null; }
        finally { _suppressVariableNavigation = false; }
        ClearSelectedObject();
        LastSnapshotAt = null;
        PageLabel = "0 items";
        FilterResultLabel = "0 visible";
        _currentScope = null;
        IsScopeLoading = false;
        OnPropertyChanged(nameof(ShowVariableListEmpty));
        OnPropertyChanged(nameof(VariableListStatusMessage));
        _arrayHandle = null;
        _dataFrameHandle = null;
        _matplotlibHandle = null;
        UpdateCommandStates();
        ClearArrayDetails();
        ClearDataFrameDetails(resetOffsets: true);
        ClearMatplotlibDetails();
        ApplySelectedProcessPreview();
    }

    private void ClearArrayDetails()
    {
        Interlocked.Increment(ref _arrayPreviewGeneration);
        _arrayPreviewSupported = false;
        ArrayShape = ArrayDType = ArrayStrides = ArrayDataAddress = ArrayOwner = "—";
        ArrayPreview = null;
        PreviewWidth = PreviewHeight = 0;
        SourceImageWidth = SourceImageHeight = 0;
        _previewOriginX = _previewOriginY = 0;
        _displayMinimum = _displayMaximum = null;
        CursorCoordinate = RawPixelValue = DisplayPixelValue = "—";
        Normalization = "—";
        HistogramBins.Clear();
        HistogramSummary = "Not loaded";
        _arrayDimensions = [];
    }

    private void ClearDataFrameDetails(bool resetOffsets)
    {
        _dataFrameTable?.Dispose();
        _dataFrameTable = null;
        DataFrameRows = null;
        DataFrameColumns.Clear();
        if (resetOffsets)
        {
            _dataFrameRowOffset = 0;
            _dataFrameColumnOffset = 0;
        }
        _dataFrameRowCount = 0;
        _dataFrameColumnCount = 0;
        _dataFrameTotalRows = 0;
        _dataFrameTotalColumns = 0;
        _dataFrameHasMoreRows = false;
        _dataFrameHasMoreColumns = false;
        DataFrameShape = "—";
        DataFrameRowPageLabel = "Rows 0 of 0";
        DataFrameColumnPageLabel = "Columns 0 of 0";
        DataFrameErrorMessage = "";
        DataFrameState = HasDataFrameSelection
            ? DataFramePaneState.Loading
            : DataFramePaneState.NoSelection;
        DataFrameStatus = HasDataFrameSelection
            ? "Loading bounded DataFrame preview…"
            : "Select a pandas DataFrame to preview it.";
    }

    private void ClearMatplotlibDetails()
    {
        Interlocked.Increment(ref _matplotlibRefreshGeneration);
        MatplotlibPreview = null;
        MatplotlibSourceKind = "—";
        MatplotlibSourceDimensions = "—";
        MatplotlibCanvasType = "—";
        MatplotlibAvailabilityReason = HasMatplotlibSelection ? "pending" : "—";
        MatplotlibStatus = HasMatplotlibSelection
            ? "Inspecting the current Agg render…"
            : "Select a Matplotlib Figure or Axes to preview it.";
        MatplotlibNextAction = "";
        MatplotlibErrorMessage = "";
        MatplotlibUsesOwningFigure = false;
        MatplotlibState = HasMatplotlibSelection
            ? MatplotlibPaneState.Loading
            : MatplotlibPaneState.NoSelection;
    }

    private void UpdateSliceMaximum()
    {
        SliceMaximum = _arrayDimensions.Length == 3 ? Math.Max(0, _arrayDimensions[SliceAxis] - 1) : 0;
        SliceIndex = Math.Clamp(SliceIndex, 0, SliceMaximum);
    }

    private void ApplyFitZoom()
    {
        if (PreviewWidth == 0 || PreviewHeight == 0 || _fitViewportWidth == 0 || _fitViewportHeight == 0)
            return;
        _fitMode = true;
        SetZoom(Math.Min(_fitViewportWidth / PreviewWidth, _fitViewportHeight / PreviewHeight));
    }

    private void SetZoom(double value)
    {
        value = Math.Clamp(value, 0.05, 32);
        if (SetProperty(ref _zoom, value, nameof(Zoom)))
            NotifyDisplaySize();
    }

    private void NotifyDisplaySize()
    {
        OnPropertyChanged(nameof(ImageDisplayWidth));
        OnPropertyChanged(nameof(ImageDisplayHeight));
    }

    private string FormatDisplayValue(JsonNode? value)
    {
        if (value is not JsonArray channels)
            return FormatDisplayedScalar(value);
        var values = channels.Select(FormatDisplayedScalar).ToArray();
        if (values.Length >= 3 && ColorOrder == "BGR")
            (values[0], values[2]) = (values[2], values[0]);
        if (values.Length >= 3)
        {
            if (!ChannelR) values[0] = "0";
            if (!ChannelG) values[1] = "0";
            if (!ChannelB) values[2] = "0";
        }
        return "[" + string.Join(", ", values) + "]";
    }

    private string FormatDisplayedScalar(JsonNode? value)
    {
        if (value is JsonObject special)
            return special["kind"]?.GetValue<string>() switch
            {
                "+Infinity" => "255",
                _ => "0",
            };
        if (value is JsonValue booleanValue && booleanValue.TryGetValue<bool>(out var boolean))
            return boolean ? "255" : "0";
        if (value is not JsonValue scalar || !scalar.TryGetValue<double>(out var number))
            return value?.ToJsonString() ?? "null";
        if (_displayMinimum is not double minimum || _displayMaximum is not double maximum)
            return $"{number:G6}";
        if (maximum <= minimum)
            return "0";
        var displayed = (int)Math.Round(Math.Clamp((number - minimum) / (maximum - minimum), 0, 1) * 255);
        return displayed.ToString();
    }

    private static VariableRow CreateVariableRow(
        string name,
        string scope,
        JsonObject value,
        VariableChangeKind changeKind,
        DateTime? changedAt,
        bool isPinned,
        string? stableScopeKey = null)
    {
        var identityToken = value["identityToken"]?.GetValue<string>()
            ?? value["changeToken"]?.GetValue<string>()
            ?? value["addressHex"]?.GetValue<string>()
            ?? "";
        var metadataToken = value["metadataToken"]?.GetValue<string>() ?? BuildMetadataToken(value);
        var shape = value["shape"] is JsonArray dimensions
            ? "(" + string.Join(", ", dimensions.Select(dimension => dimension?.ToString() ?? "?")) + ")"
            : "";
        long? payloadSize = null;
        if (value["payloadSizeBytes"] is JsonValue payloadValue && payloadValue.TryGetValue<long>(out var payload))
            payloadSize = payload;
        return new VariableRow
        {
            Name = name,
            Scope = scope,
            StableScopeKey = stableScopeKey ?? scope,
            HandleId = value["handleId"]?.GetValue<string>() ?? "",
            TypeName = value["typeName"]?.GetValue<string>() ?? "unknown",
            ModuleName = value["moduleName"]?.GetValue<string>() ?? "",
            QualifiedTypeName = value["qualifiedTypeName"]?.GetValue<string>() ?? "unknown",
            SafePreview = value["safePreview"]?.GetValue<string>() ?? "",
            Address = value["addressHex"]?.GetValue<string>() ?? "—",
            ShallowSize = value["shallowSizeBytes"]?.GetValue<long>() ?? 0,
            PayloadSize = payloadSize,
            Shape = shape,
            DType = value["dtype"]?.GetValue<string>() ?? "",
            Expandable = value["expandable"]?.GetValue<bool>() ?? false,
            AdapterKind = value["adapterKind"]?.GetValue<string>(),
            ChangeToken = identityToken,
            IdentityToken = identityToken,
            MetadataToken = metadataToken,
            ChangeKind = changeKind,
            ChangedAt = changedAt,
            IsRemoved = false,
            IsPinned = isPinned,
        };
    }

    private static string BuildMetadataToken(JsonObject value)
    {
        var shape = value["shape"]?.ToJsonString() ?? "";
        return string.Join("|",
            value["qualifiedTypeName"]?.ToString() ?? "",
            value["safePreview"]?.ToString() ?? "",
            value["shallowSizeBytes"]?.ToString() ?? "",
            value["payloadSizeBytes"]?.ToString() ?? "",
            shape,
            value["dtype"]?.ToString() ?? "");
    }

    private static VariableRow CloneRemovedRow(VariableRow row, DateTime changedAt) => new()
    {
        Name = row.Name,
        Scope = row.Scope,
        StableScopeKey = row.StableScopeKey,
        HandleId = row.HandleId,
        TypeName = row.TypeName,
        ModuleName = row.ModuleName,
        QualifiedTypeName = row.QualifiedTypeName,
        SafePreview = row.SafePreview,
        Address = row.Address,
        ShallowSize = row.ShallowSize,
        PayloadSize = row.PayloadSize,
        Shape = row.Shape,
        DType = row.DType,
        Expandable = false,
        AdapterKind = row.AdapterKind,
        ChangeToken = row.ChangeToken,
        IdentityToken = row.IdentityToken,
        MetadataToken = row.MetadataToken,
        ChangeKind = VariableChangeKind.Removed,
        ChangedAt = changedAt,
        IsRemoved = true,
        IsPinned = row.IsPinned,
    };

    private static DateTime? ParseTimestamp(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !DateTimeOffset.TryParse(text, out var timestamp))
            return null;
        return timestamp.LocalDateTime;
    }

    private void NotifySnapshotState()
    {
        OnPropertyChanged(nameof(IsSnapshotStale));
        OnPropertyChanged(nameof(SnapshotStatus));
        OnPropertyChanged(nameof(SnapshotStatusBar));
    }

    private NavigationContext CreateNavigationContext(VariableRow row, string path, int depth, NavigationContext? parent)
    {
        var addresses = parent is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(parent.AncestorAddresses, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(row.Address))
            addresses.Add(row.Address);
        var identities = parent is null
            ? new List<string>()
            : new List<string>(parent.AncestorIdentityTokens);
        if (!string.IsNullOrWhiteSpace(row.IdentityToken))
            identities.Add(row.IdentityToken);
        return new NavigationContext(row, path, depth, parent, addresses, identities);
    }

    private NavigationContext? BuildNavigationParent(ObjectTreeNode node)
    {
        if (node.Parent is null)
            return _currentObject?.Parent;
        return ContextForNode(node.Parent);
    }

    private NavigationContext ContextForNode(ObjectTreeNode node)
    {
        if (node.Parent is null && _currentObject is not null
            && string.Equals(node.Value?.HandleId, _currentObject.Row.HandleId, StringComparison.Ordinal))
            return _currentObject;
        var parent = node.Parent is null ? null : ContextForNode(node.Parent);
        if (node.Value is null)
            return parent ?? _currentObject ?? throw new InvalidOperationException("Object navigation context is unavailable.");
        return CreateNavigationContext(node.Value, node.Path, node.Depth, parent);
    }

    private IReadOnlySet<string> BuildAncestorAddresses(ObjectTreeNode node) =>
        new HashSet<string>(ContextForNode(node).AncestorAddresses, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> BuildAncestorIdentityTokens(ObjectTreeNode node) =>
        new List<string>(ContextForNode(node).AncestorIdentityTokens);

    private static ObjectTreeNode StatusNode(string label, ObjectTreeNode parent) => new()
    {
        Label = label,
        Kind = ObjectNodeKind.Status,
        Path = parent.Path,
        Depth = parent.Depth,
        Parent = parent,
        IsLoaded = true,
    };

    private static string AppendObjectPath(string root, string segment)
    {
        if (string.IsNullOrWhiteSpace(root))
            return segment;
        return segment.StartsWith("[", StringComparison.Ordinal) ? root + segment : root + "." + segment;
    }

    private static string ObjectGroupLabel(string origin) => origin switch
    {
        "instance" => "Instance fields",
        "instance-dict" => "Instance dictionary",
        "mapping" => "Mapping values",
        "item" => "Collection items",
        _ => "Other values",
    };

    private static string? ReadClassReference(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        if (node is not JsonObject reference)
            return null;
        if (reference["displayName"] is JsonValue display && display.TryGetValue<string>(out var displayName))
            return displayName;
        var module = reference["module"]?.GetValue<string>();
        var qualifiedName = reference["qualifiedName"]?.GetValue<string>() ?? reference["name"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(module) ? qualifiedName : $"{module}.{qualifiedName}";
    }

    private static void AddClassReferenceGroup(ClassTreeNode parent, string label, JsonNode? node)
    {
        if (node is not JsonArray values || values.Count == 0)
            return;
        var group = new ClassTreeNode { Label = label, Kind = "metadata-group" };
        foreach (var value in values)
        {
            var reference = ReadClassReference(value);
            if (!string.IsNullOrWhiteSpace(reference))
                group.Children.Add(new ClassTreeNode { Label = reference, Kind = "class-reference" });
        }
        if (group.Children.Count > 0)
            parent.Children.Add(group);
    }

    private static string ClassGroupKey(string kind) => kind switch
    {
        "instance method" or "function" or "method descriptor" => "instance method",
        "staticmethod" => "staticmethod",
        "classmethod" => "classmethod",
        "property" or "data descriptor" or "unknown descriptor" => "property",
        _ => "attribute",
    };

    private static string ReadSignatureDisplay(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return node is JsonObject signature
            ? signature["display"]?.GetValue<string>() ?? "—"
            : "—";
    }

    private static string ReadSource(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        if (node is not JsonObject source)
            return "";
        var file = source["file"]?.GetValue<string>() ?? "";
        var line = source["line"]?.GetValue<int>() ?? 0;
        return line > 0 ? $"{file}:{line}" : file;
    }

    private void UpdateSelectedObjectSummary(NavigationContext context, bool markDetailsPending = true)
    {
        _currentObject = context;
        var row = context.Row;
        SelectedObjectPreview = row.SafePreview;
        SelectedAddress = row.Address;
        SelectedShallowSize = FormatBytes(row.ShallowSize);
        SelectedPayloadSize = row.PayloadSize is long payload ? FormatBytes(payload) : "—";
        if (markDetailsPending)
            SelectedObjectStatus = "Values changed · press F5 to refresh details";
        if (_pinnedKeys.Contains(context.PinKey))
            _pinnedContexts[context.PinKey] = context;
        OnPropertyChanged(nameof(IsSelectedObjectPinned));
        RefreshObjectHandleReferences();
    }

    private void ClearSelectedObject(string? message = null, InspectorPaneState state = InspectorPaneState.NoSelection)
    {
        _detailCts?.Cancel();
        Interlocked.Increment(ref _detailGeneration);
        _currentObject = null;
        SelectedObjectNode = null;
        ObjectRoots.Clear();
        ObjectBreadcrumbs.Clear();
        ObjectChildren.Clear();
        FilteredObjectChildren.Clear();
        ResetObjectSearches();
        ClassTree.Clear();
        ResetClassTreeSearch();
        ClassMembers.Clear();
        ClearArrayDetails();
        HasSelectedObject = false;
        HasArraySelection = false;
        HasDataFrameSelection = false;
        HasMatplotlibSelection = false;
        ClearDataFrameDetails(resetOffsets: true);
        ClearMatplotlibDetails();
        InspectorState = state;
        SelectedObjectName = state == InspectorPaneState.Expired ? "Selection expired" : "No object selected";
        SelectedObjectPath = "Select a variable to inspect";
        var previous = _navigationIndex > 0 ? _navigationHistory[_navigationIndex - 1] : null;
        var next = _navigationIndex >= 0 && _navigationIndex < _navigationHistory.Count - 1
            ? _navigationHistory[_navigationIndex + 1]
            : null;
        BackNavigationLabel = previous is null ? "Back" : $"Back · {CompactNavigationName(previous.Row.Name)}";
        ForwardNavigationLabel = next is null ? "Forward" : $"Forward · {CompactNavigationName(next.Row.Name)}";
        ParentNavigationLabel = "Parent";
        BackNavigationToolTip = previous is null
            ? "No earlier object in navigation history"
            : $"Back to {previous.Path} (Alt+Left)";
        ForwardNavigationToolTip = next is null
            ? "No later object in navigation history"
            : $"Forward to {next.Path} (Alt+Right)";
        ParentNavigationToolTip = "The selected object is already at the root";
        ObjectDepthLabel = "No level";
        NavigationHistoryLabel = _navigationHistory.Count == 0
            ? "History 0 / 0"
            : $"History {Math.Max(0, _navigationIndex + 1)} / {_navigationHistory.Count}";
        NavigationLocationDescription = "No object selected";
        SelectedObjectPreview = message ?? "Choose a variable from the table to inspect its structure.";
        SelectedObjectStatus = message ?? "No selection";
        ClassSummary = "Select an object to inspect its class.";
        SelectedType = SelectedModule = SelectedQualifiedName = SelectedAddress = "—";
        SelectedShallowSize = SelectedPayloadSize = "—";
        _arrayHandle = null;
        _dataFrameHandle = null;
        _matplotlibHandle = null;
        OnPropertyChanged(nameof(IsSelectedObjectPinned));
        RefreshObjectHandleReferences();
        UpdateCommandStates();
    }

    private static string GetScopeKey(RuntimeTreeNode node)
    {
        if (node.Kind == RuntimeNodeKind.GcObjects)
            return "gc-tracked";
        if (node.Kind == RuntimeNodeKind.ConsoleNamespace)
            return $"console:{node.ConsoleHandle}:{node.ConsoleAttributeName}";
        if (node.ModuleName is not null)
            return $"module:{node.ModuleName}";
        return $"{node.FrameHandle}:{node.ScopeType}";
    }

    private IEnumerable<RuntimeTreeNode> EnumerateRuntimeNodes()
    {
        var pending = new Stack<RuntimeTreeNode>(RuntimeRoots.Reverse());
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
                pending.Push(node.Children[index]);
        }
    }

    private static GlobalSearchResultRow CreateGlobalSearchResult(JsonObject item)
    {
        var name = item["name"]?.GetValue<string>() ?? "<unnamed>";
        var location = item["location"]?.GetValue<string>() ?? "<unknown>";
        var sourceKind = item["sourceKind"]?.GetValue<string>() ?? "unknown";
        var value = item["value"] as JsonObject;
        return new GlobalSearchResultRow
        {
            Kind = item["kind"]?.GetValue<string>() ?? "object",
            Name = name,
            Relation = FormatGlobalSearchRelation(item["relation"]?.GetValue<string>()),
            Location = location,
            ObjectPath = item["objectPath"]?.GetValue<string>() ?? location,
            MatchFields = item["matchFields"] is JsonArray fields
                ? string.Join(", ", fields.Select(field => field?.GetValue<string>() ?? ""))
                : "",
            SourceKind = sourceKind,
            ModuleName = item["moduleName"]?.GetValue<string>(),
            FrameHandle = item["frameHandle"]?.GetValue<string>(),
            ScopeType = item["scopeType"]?.GetValue<string>(),
            ConsoleHandle = item["consoleHandle"]?.GetValue<string>(),
            ConsoleAttributeName = item["consoleAttributeName"]?.GetValue<string>(),
            RootName = item["rootName"]?.GetValue<string>(),
            Depth = item["depth"]?.GetValue<int>() ?? 0,
            Value = value is null
                ? null
                : CreateVariableRow(
                    name,
                    $"global-search:{sourceKind}",
                    value,
                    VariableChangeKind.Unchanged,
                    null,
                    false,
                    $"global-search:{location}"),
        };
    }

    private static string FormatGlobalSearchRelation(string? relation) => relation switch
    {
        "moduleVariable" or "moduleGlobal" => "Module variable",
        "frameVariable" => "Frame variable",
        "frameLocal" => "Frame local",
        "frameGlobal" => "Frame global",
        "frameBuiltin" => "Frame builtin",
        "consoleVariable" => "Console variable",
        "listItem" => "List item",
        "tupleItem" => "Tuple item",
        "setItem" => "Set item",
        "frozensetItem" => "Frozen set item",
        "collectionItem" => "Collection item",
        "dictKey" or "mappingKey" => "Mapping key",
        "dictValue" or "mappingValue" => "Mapping value",
        "instanceField" => "Instance field",
        "instanceDictionaryEntry" => "Instance dictionary entry",
        "classAttribute" => "Class attribute",
        "gcObject" => "GC-tracked object",
        null or "" => "—",
        _ => relation,
    };

    private static string BuildGlobalSearchLimitSummary(JsonObject result)
    {
        var limits = new List<string>();
        if (result["resultLimitReached"]?.GetValue<bool>() == true)
            limits.Add($"result limit {result["maxResults"]?.GetValue<int>() ?? 0:N0}");
        if (result["objectLimitReached"]?.GetValue<bool>() == true)
            limits.Add($"object limit {result["maxObjects"]?.GetValue<int>() ?? 0:N0}");
        if (result["depthLimitReached"]?.GetValue<bool>() == true)
            limits.Add($"depth limit {result["maxDepth"]?.GetValue<int>() ?? 0:N0}");
        if (result["childrenTruncated"]?.GetValue<bool>() == true)
            limits.Add("large child collection limit");
        if (result["edgeLimitReached"]?.GetValue<bool>() == true)
            limits.Add($"edge limit {result["maxEdges"]?.GetValue<int>() ?? 0:N0}");
        if (result["rootBudgetReached"]?.GetValue<bool>() == true)
            limits.Add("root scan budget");
        if (result["deadlineReached"]?.GetValue<bool>() == true)
            limits.Add("time limit");
        if (result["consoleDiscoveryTruncated"]?.GetValue<bool>() == true)
        {
            var scanned = result["consoleDiscoveryScannedCount"]?.GetValue<int>() ?? 0;
            var tracked = result["consoleDiscoveryTrackedTotal"]?.GetValue<int>() ?? 0;
            limits.Add($"console discovery object limit ({scanned:N0}/{tracked:N0})");
        }
        if (result["consoleNamespaceLimitReached"]?.GetValue<bool>() == true)
        {
            var returned = result["consoleNamespacesReturned"]?.GetValue<int>() ?? 0;
            limits.Add($"console namespace limit ({returned:N0} returned)");
        }
        if (result["consoleRawEntryLimitReached"]?.GetValue<bool>() == true)
            limits.Add("console raw-entry limit");
        if (result["consoleMutationDetected"]?.GetValue<bool>() == true)
            limits.Add("console namespace mutation");
        if (result["consoleDeadlineReached"]?.GetValue<bool>() == true)
            limits.Add("console discovery time limit");
        if (result["frameRootLimitReached"]?.GetValue<bool>() == true)
        {
            var included = result["frameRootsIncluded"]?.GetValue<int>() ?? 0;
            limits.Add($"frame root limit ({included:N0} frames)");
        }
        if (result["moduleRootLimitReached"]?.GetValue<bool>() == true)
        {
            var included = result["moduleRootsIncluded"]?.GetValue<int>() ?? 0;
            limits.Add($"module root limit ({included:N0} included)");
        }
        if (result["moduleRootDeadlineReached"]?.GetValue<bool>() == true)
            limits.Add("module root time limit");
        if (result["moduleRegistryMutationDetected"]?.GetValue<bool>() == true)
            limits.Add("module registry mutation");
        if (result["namespaceRawLimitReached"]?.GetValue<bool>() == true)
            limits.Add("namespace raw-entry limit");
        if (result["namespaceMutationDetected"]?.GetValue<bool>() == true)
            limits.Add("namespace mutation");
        if (result["namespaceDeadlineReached"]?.GetValue<bool>() == true)
            limits.Add("namespace time limit");
        if (result["responseTruncated"]?.GetValue<bool>() == true)
            limits.Add("response size limit");
        return limits.Count == 0 ? "bounded scan safety limit reached" : string.Join(", ", limits) + " reached";
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private sealed record VariableSnapshot(string IdentityToken, string MetadataToken, VariableRow Row);
    private sealed record ScopeSnapshot(Dictionary<string, VariableSnapshot> Items, bool IsComplete);
    private sealed record ChangeMarker(VariableChangeKind Kind, DateTime ChangedAt);
    private sealed class HandleActivityLease(MainViewModel owner, int generation) : IDisposable
    {
        private int _disposed;

        public int Generation { get; } = generation;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndHandleActivity();
        }
    }

    private sealed class HandleResponseLease(ProtocolFrame frame, HandleActivityLease activity) : IDisposable
    {
        private HandleActivityLease? _activity = activity;

        public ProtocolFrame Frame { get; } = frame;

        public void Dispose() => Interlocked.Exchange(ref _activity, null)?.Dispose();
    }

    private sealed record NavigationContext(
        VariableRow Row,
        string Path,
        int Depth,
        NavigationContext? Parent,
        IReadOnlySet<string> AncestorAddresses,
        IReadOnlyList<string> AncestorIdentityTokens)
    {
        public string RootScopeKey => Parent?.RootScopeKey ?? Row.EffectiveScopeKey;
        public string RootStableKey => Parent?.RootStableKey ?? Row.StableKey;
        public string PinKey => Parent is null ? Row.StableKey : $"{RootStableKey}|{Path}";
    }

    private void SetError(Exception exception)
    {
        ErrorMessage = exception switch
        {
            RemoteInspectionException remote => $"{remote.Code}: {remote.Message}",
            LiveAttachException live => $"{live.Code}: {live.Message}",
            _ => exception.Message,
        };
    }

    private void SetConnectionRecovery(Exception exception)
    {
        var code = exception switch
        {
            RemoteInspectionException remote => remote.Code,
            LiveAttachException live => live.Code,
            _ => null,
        };
        ConnectionRecoveryMessage = code switch
        {
            "STALE_AGENT" or "INCOMPATIBLE_AGENT" =>
                "The Python process has a different PyMonitor Agent runtime loaded. "
                + "End the Python debug session or run exit(), start Python again, select Rescan, then run Quick Attach.",
            "ACTIVE_AGENT_CONFLICT" =>
                "Another PyMonitor session is still attached to this Python process. "
                + "Detach it from the original PyMonitor window; if that is unavailable, restart Python, select Rescan, then run Quick Attach.",
            _ => "",
        };
    }

    private void UpdateCommandStates()
    {
        AttachCommand?.RaiseCanExecuteChanged();
        QuickAttachCommand?.RaiseCanExecuteChanged();
        LiveAttachCommand?.RaiseCanExecuteChanged();
        DetachCommand?.RaiseCanExecuteChanged();
        RefreshCommand?.RaiseCanExecuteChanged();
        PreviousPageCommand?.RaiseCanExecuteChanged();
        NextPageCommand?.RaiseCanExecuteChanged();
        SearchCurrentCommand?.RaiseCanExecuteChanged();
        SearchRuntimeCommand?.RaiseCanExecuteChanged();
        OpenGlobalSearchResultCommand?.RaiseCanExecuteChanged();
        NavigateBackCommand?.RaiseCanExecuteChanged();
        NavigateForwardCommand?.RaiseCanExecuteChanged();
        NavigateParentCommand?.RaiseCanExecuteChanged();
        NavigatePinnedCommand?.RaiseCanExecuteChanged();
        ResetBaselineCommand?.RaiseCanExecuteChanged();
        TogglePinCommand?.RaiseCanExecuteChanged();
        CopyObjectPathCommand?.RaiseCanExecuteChanged();
        CopyObjectTypeCommand?.RaiseCanExecuteChanged();
        CopyObjectAddressCommand?.RaiseCanExecuteChanged();
        ReloadPreviewCommand?.RaiseCanExecuteChanged();
        LoadTileCommand?.RaiseCanExecuteChanged();
        LoadHistogramCommand?.RaiseCanExecuteChanged();
        RefreshDataFrameCommand?.RaiseCanExecuteChanged();
        RefreshMatplotlibCommand?.RaiseCanExecuteChanged();
        PreviousDataFrameRowsCommand?.RaiseCanExecuteChanged();
        NextDataFrameRowsCommand?.RaiseCanExecuteChanged();
        PreviousDataFrameColumnsCommand?.RaiseCanExecuteChanged();
        NextDataFrameColumnsCommand?.RaiseCanExecuteChanged();
        StartTracingCommand?.RaiseCanExecuteChanged();
        StopTracingCommand?.RaiseCanExecuteChanged();
        TakeMemorySnapshotCommand?.RaiseCanExecuteChanged();
        CompareMemorySnapshotsCommand?.RaiseCanExecuteChanged();
        RefreshMemoryCommand?.RaiseCanExecuteChanged();
        StartExecutionMonitoringCommand?.RaiseCanExecuteChanged();
        StopExecutionMonitoringCommand?.RaiseCanExecuteChanged();
        RefreshExecutionEventsCommand?.RaiseCanExecuteChanged();
        ClearExecutionEventsCommand?.RaiseCanExecuteChanged();
        LaunchCommand?.RaiseCanExecuteChanged();
        StopCommand?.RaiseCanExecuteChanged();
        RestartCommand?.RaiseCanExecuteChanged();
    }

    private void ApplySelectedProcessPreview()
    {
        TargetPid = SelectedProcess?.Id;
        Executable = SelectedProcess?.ExecutablePath ?? "—";
        PythonVersion = SelectedProcess?.PythonVersion is Version version
            ? $"Python {version.Major}.{version.Minor}"
            : "—";
        Architecture = SelectedProcess is null ? "—" : "Detected after attach";
        RefreshProcessMemory();
    }

    private static RuntimeTreeNode PlaceholderRoot(string label, string message)
    {
        var root = new RuntimeTreeNode(label);
        root.Children.Add(new RuntimeTreeNode(message, RuntimeNodeKind.Placeholder));
        return root;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:F2} {units[unit]}";
    }

    private static string FormatNumber(double? value) => value is double number ? $"{number:G6}" : "—";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "agent")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return Environment.CurrentDirectory;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private IReadOnlyList<VariableRow> ReconcileVariableRows(IReadOnlyList<VariableRow> incoming)
    {
        var existingByKey = Variables.ToDictionary(row => row.StableKey, StringComparer.Ordinal);
        var desired = new List<VariableRow>(incoming.Count);
        foreach (var next in incoming)
        {
            if (existingByKey.TryGetValue(next.StableKey, out var existing)
                && CanUpdateVariableRowInPlace(existing, next))
            {
                existing.UpdateFrom(next);
                desired.Add(existing);
            }
            else
            {
                desired.Add(next);
            }
        }

        ReconcileVariableCollection(Variables, desired);
        return desired;
    }

    private static bool CanUpdateVariableRowInPlace(VariableRow current, VariableRow next) =>
        !current.IsRemoved
        && !next.IsRemoved
        && !string.IsNullOrEmpty(current.IdentityToken)
        && string.Equals(current.IdentityToken, next.IdentityToken, StringComparison.Ordinal);

    private static void ReconcileVariableCollection(
        ObservableCollection<VariableRow> target,
        IReadOnlyList<VariableRow> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var next = desired[index];
            if (index < target.Count && ReferenceEquals(target[index], next))
                continue;
            if (index < target.Count && string.Equals(target[index].StableKey, next.StableKey, StringComparison.Ordinal))
            {
                target[index] = next;
                continue;
            }

            var existingIndex = -1;
            for (var candidate = index + 1; candidate < target.Count; candidate++)
            {
                if (string.Equals(target[candidate].StableKey, next.StableKey, StringComparison.Ordinal))
                {
                    existingIndex = candidate;
                    break;
                }
            }
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
                if (!ReferenceEquals(target[index], next))
                    target[index] = next;
            }
            else
            {
                target.Insert(index, next);
            }
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    public async ValueTask DisposeAsync()
    {
        _session.Disconnected -= OnSessionDisconnected;
        _launcher.OutputReceived -= OnManagedOutput;
        _launcher.Exited -= OnManagedExited;
        _connectionCts?.Cancel();
        _refreshCts?.Cancel();
        _selectionCts?.Cancel();
        _detailCts?.Cancel();
        var searchCts = _runtimeSearchCts;
        _runtimeSearchCts = null;
        searchCts?.Cancel();
        CancelSearchDebounce();
        CancelClassTreeSearchDebounce();
        await ReleaseKnownHandlesBeforeDetachAsync();
        await _session.DisposeAsync();
        await _launcher.DisposeAsync();
    }
}
