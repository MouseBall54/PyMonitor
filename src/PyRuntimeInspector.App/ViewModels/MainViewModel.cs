using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly SynchronizationContext? _uiContext;
    private readonly Dictionary<string, string> _changeTokens = [];
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _selectionCts;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _refreshCts;
    private RuntimeTreeNode? _currentScope;
    private long _selectionGeneration;
    private long _detailGeneration;
    private int _pageOffset;
    private int _pageTotal;
    private const int PageSize = 200;
    private double _fitViewportWidth;
    private double _fitViewportHeight;
    private bool _fitMode = true;

    private ProcessItem? _selectedProcess;
    private VariableRow? _selectedVariable;
    private string _portText;
    private string _token;
    private string _status = "Disconnected";
    private string _errorMessage = "";
    private bool _isConnected;
    private bool _isBusy;
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
    private string _searchText = "";
    private string _pageLabel = "0 items";
    private string _lastLatency = "—";
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

    public MainViewModel() : this(new InspectorSession(), new ProcessDiscovery(), new ManagedPythonLauncher(), new LiveAttachService())
    {
    }

    public MainViewModel(
        IInspectorSession session,
        IProcessDiscovery processDiscovery,
        IManagedPythonLauncher? launcher = null,
        ILiveAttachService? liveAttachService = null)
    {
        _session = session;
        _processDiscovery = processDiscovery;
        _launcher = launcher ?? new ManagedPythonLauncher();
        _liveAttachService = liveAttachService ?? new LiveAttachService();
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
        LiveAttachCommand = new AsyncCommand(LiveAttachAsync, () => !IsConnected && !IsBusy && SelectedProcess?.ExecutablePath is not null);
        DetachCommand = new AsyncCommand(DetachAsync, () => IsConnected || IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected);
        PreviousPageCommand = new AsyncCommand(PreviousPageAsync, () => IsConnected && _pageOffset > 0);
        NextPageCommand = new AsyncCommand(NextPageAsync, () => IsConnected && _pageOffset + PageSize < _pageTotal);
        ReloadPreviewCommand = new AsyncCommand(ReloadArrayPreviewAsync, () => IsConnected && _arrayHandle is not null);
        LoadTileCommand = new AsyncCommand(LoadArrayTileAsync, () => IsConnected && _arrayHandle is not null);
        LoadHistogramCommand = new AsyncCommand(LoadHistogramAsync, () => IsConnected && _arrayHandle is not null);
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
    public ObservableCollection<ObjectChildRow> ObjectChildren { get; } = [];
    public ObservableCollection<ClassMemberRow> ClassMembers { get; } = [];
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

    public AsyncCommand AttachCommand { get; }
    public AsyncCommand LiveAttachCommand { get; }
    public AsyncCommand DetachCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand ReloadPreviewCommand { get; }
    public AsyncCommand LoadTileCommand { get; }
    public AsyncCommand LoadHistogramCommand { get; }
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
                UpdateCommandStates();
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
    public double RefreshIntervalSeconds { get => _refreshIntervalSeconds; set => SetProperty(ref _refreshIntervalSeconds, Math.Max(1, value)); }
    public string Breadcrumb { get => _breadcrumb; private set => SetProperty(ref _breadcrumb, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }
    public string PageLabel { get => _pageLabel; private set => SetProperty(ref _pageLabel, value); }
    public string LastLatency { get => _lastLatency; private set => SetProperty(ref _lastLatency, value); }

    public VariableRow? SelectedVariable
    {
        get => _selectedVariable;
        set
        {
            if (SetProperty(ref _selectedVariable, value) && value is not null)
                _ = LoadDetailsAsync(value);
        }
    }

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
        if (node?.Kind == RuntimeNodeKind.Scope)
            await LoadScopeAsync(node, resetPage: true);
        else if (node?.Kind == RuntimeNodeKind.Frame)
        {
            var locals = node.Children.FirstOrDefault(child => child.ScopeType == "locals");
            if (locals is not null)
                await LoadScopeAsync(locals, resetPage: true);
        }
    }

    public async Task LoadScopeAsync(RuntimeTreeNode node, bool resetPage = false)
    {
        if (!IsConnected || node.FrameHandle is null || node.ScopeType is null)
            return;
        if (resetPage)
            _pageOffset = 0;
        _currentScope = node;
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts?.Token ?? CancellationToken.None);
        var generation = Interlocked.Increment(ref _selectionGeneration);
        var token = _selectionCts.Token;
        Breadcrumb = $"Threads / {node.Label}";
        try
        {
            var frame = await RequestAsync("scopes.list", new JsonObject
            {
                ["frameHandle"] = node.FrameHandle,
                ["scopeType"] = node.ScopeType,
                ["offset"] = _pageOffset,
                ["pageSize"] = PageSize,
            }, token);
            if (generation != _selectionGeneration || token.IsCancellationRequested)
                return;
            ApplyScopeResult(frame.Header["result"]!.AsObject(), node);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
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
                await _session.DetachAsync();
                await _launcher.StopAsync();
                throw new InvalidOperationException($"Connected PID {actualPid} does not match launched PID {handle.ProcessId}.");
            }
            ApplyRuntime(runtime);
            IsConnected = true;
            Status = "Connected (managed launch)";
            await LoadRuntimeTreeAsync(_connectionCts.Token);
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
                await _session.DetachAsync();
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
            await _session.DetachAsync();
        await _launcher.StopAsync();
        ResetConnection("Managed target stopped");
    }

    private async Task RestartManagedAsync()
    {
        if (IsBusy)
            return;
        if (_session.IsConnected)
            await _session.DetachAsync();
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
            Status = "Connected";
            await LoadRuntimeTreeAsync(_connectionCts.Token);
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
            ErrorMessage = "The selected process executable is unavailable. Try running PyRuntime Inspector with sufficient permission.";
            return;
        }

        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!TryGetConnectionSettings(out var port))
            return;

        ErrorMessage = "";
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

            Status = "Live attach scheduled; waiting for the target safe point";
            var runtime = await attachTask;
            ApplyRuntime(runtime);
            IsConnected = true;
            Status = "Connected (live attach)";
            await LoadRuntimeTreeAsync(_connectionCts.Token);
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
        _connectionCts?.Cancel();
        _refreshCts?.Cancel();
        try
        {
            await _session.DetachAsync();
        }
        finally
        {
            ResetConnection("Disconnected");
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

    private async Task LoadRuntimeTreeAsync(CancellationToken token)
    {
        RefreshProcessMemory();
        var threadsFrame = await RequestAsync("threads.list", cancellationToken: token);
        var framesFrame = await RequestAsync("frames.list", cancellationToken: token);
        var threads = threadsFrame.Header["result"]!["items"]!.AsArray();
        var frames = framesFrame.Header["result"]!["items"]!.AsArray();
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
        Replace(RuntimeRoots, [
            processRoot,
            interpreterRoot,
            threadsRoot,
            PlaceholderRoot("Modules", "Module namespace browsing is scheduled after the Phase 1 shell."),
            PlaceholderRoot("Classes", "Select a variable to inspect its class."),
            PlaceholderRoot("GC-tracked objects", "Heap scanning is intentionally not enabled."),
        ]);
        await RefreshMemoryAsync(token);
        await RefreshExecutionStatusAsync(token);
        if (ExecutionMonitoringActive)
            await LoadExecutionEventsAsync(token);
    }

    private void ApplyRuntime(JsonObject runtime)
    {
        TargetPid = runtime["pid"]!.GetValue<int>();
        PythonVersion = runtime["version"]!.GetValue<string>().Split('\n')[0];
        Architecture = runtime["processArchitecture"]?.GetValue<string>() ?? "—";
        Executable = runtime["executable"]?.GetValue<string>() ?? "—";
        RefreshProcessMemory();
    }

    private void ApplyScopeResult(JsonObject result, RuntimeTreeNode node)
    {
        _pageTotal = result["total"]!.GetValue<int>();
        var rows = new List<VariableRow>();
        foreach (var item in result["items"]!.AsArray())
        {
            var name = item!["name"]!.GetValue<string>();
            var value = item["value"]!.AsObject();
            var changeToken = value["changeToken"]?.GetValue<string>() ?? "";
            var changeKey = $"{node.FrameHandle}:{node.ScopeType}:{name}";
            var changed = _changeTokens.TryGetValue(changeKey, out var previous) && previous != changeToken;
            _changeTokens[changeKey] = changeToken;
            var shape = value["shape"] is JsonArray shapeArray
                ? "(" + string.Join(", ", shapeArray.Select(part => part!.GetValue<int>())) + ")"
                : "";
            rows.Add(new VariableRow
            {
                Name = name,
                Scope = node.ScopeType!,
                HandleId = value["handleId"]!.GetValue<string>(),
                TypeName = value["typeName"]?.GetValue<string>() ?? "?",
                ModuleName = value["moduleName"]?.GetValue<string>() ?? "?",
                QualifiedTypeName = value["qualifiedTypeName"]?.GetValue<string>() ?? "?",
                SafePreview = value["safePreview"]?.GetValue<string>() ?? "",
                Address = value["addressHex"]?.GetValue<string>() ?? "",
                ShallowSize = value["shallowSizeBytes"]?.GetValue<long>() ?? 0,
                PayloadSize = value["payloadSizeBytes"]?.GetValue<long>(),
                Shape = shape,
                DType = value["dtype"]?.GetValue<string>() ?? "",
                Expandable = value["expandable"]?.GetValue<bool>() ?? false,
                AdapterKind = value["adapterKind"]?.GetValue<string>(),
                ChangeToken = changeToken,
                Changed = changed,
            });
        }
        Replace(Variables, rows);
        ApplyFilter();
        var first = _pageTotal == 0 ? 0 : _pageOffset + 1;
        var last = Math.Min(_pageOffset + PageSize, _pageTotal);
        PageLabel = $"{first}–{last} of {_pageTotal}";
        UpdateCommandStates();
    }

    private async Task LoadDetailsAsync(VariableRow row)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts?.Token ?? CancellationToken.None);
        var token = _detailCts.Token;
        var generation = Interlocked.Increment(ref _detailGeneration);
        SelectedType = row.TypeName;
        SelectedModule = row.ModuleName;
        SelectedQualifiedName = row.QualifiedTypeName;
        SelectedAddress = row.Address;
        SelectedShallowSize = FormatBytes(row.ShallowSize);
        SelectedPayloadSize = row.PayloadSize is long payload ? FormatBytes(payload) : "—";
        _arrayHandle = row.AdapterKind == "numpy.ndarray" ? row.HandleId : null;
        UpdateCommandStates();
        ObjectChildren.Clear();
        ClassMembers.Clear();
        try
        {
            var children = await RequestAsync("objects.listChildren", new JsonObject
            {
                ["handleId"] = row.HandleId,
                ["offset"] = 0,
                ["pageSize"] = 200,
            }, token);
            if (generation != _detailGeneration || token.IsCancellationRequested)
                return;
            var childRows = children.Header["result"]!["items"]!.AsArray().Select(item =>
            {
                var value = item!["value"]!.AsObject();
                return new ObjectChildRow(
                    item["name"]!.GetValue<string>(),
                    item["origin"]!.GetValue<string>(),
                    value["typeName"]?.GetValue<string>() ?? "?",
                    value["safePreview"]?.GetValue<string>() ?? "",
                    value["addressHex"]?.GetValue<string>() ?? "");
            }).ToArray();
            Replace(ObjectChildren, childRows);

            var classFrame = await RequestAsync("classes.describe", new JsonObject { ["handleId"] = row.HandleId }, token);
            if (generation != _detailGeneration || token.IsCancellationRequested)
                return;
            var description = classFrame.Header["result"]!.AsObject();
            Replace(ClassMembers, description["members"]!.AsArray().Select(member => new ClassMemberRow(
                member!["name"]!.GetValue<string>(),
                member["kind"]!.GetValue<string>(),
                member["declaredBy"]!.GetValue<string>(),
                member["signature"]?.GetValue<string>() ?? "unavailable")));

            if (_arrayHandle is not null)
                await LoadArrayDescriptionAndPreviewAsync(generation, token);
            else
                ClearArrayDetails();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async Task LoadArrayDescriptionAndPreviewAsync(long generation, CancellationToken token)
    {
        var descriptionFrame = await RequestAsync("arrays.describe", new JsonObject { ["handleId"] = _arrayHandle }, token);
        if (generation != _detailGeneration || token.IsCancellationRequested)
            return;
        var description = descriptionFrame.Header["result"]!.AsObject();
        _arrayDimensions = description["shape"]!.AsArray().Select(item => item!.GetValue<int>()).ToArray();
        ArrayShape = "(" + string.Join(", ", _arrayDimensions) + ")";
        ArrayDType = description["dtype"]!.GetValue<string>();
        ArrayStrides = "(" + string.Join(", ", description["strides"]!.AsArray().Select(item => item!.GetValue<int>())) + ")";
        ArrayDataAddress = description["dataAddressHex"]!.GetValue<string>();
        ArrayOwner = description["ownsData"]!.GetValue<bool>() ? "Owns data" : "View / shared owner";
        var guess = description["layoutGuess"]!.GetValue<string>();
        ArrayLayout = guess switch { "GRAY" => "GRAY", "CHW" => "CHW", "volume" => "VOLUME", _ => "HWC" };
        LayoutConfidence = description["layoutConfidence"]!.GetValue<string>();
        SliceAxis = 0;
        SliceIndex = ArrayLayout == "VOLUME" && _arrayDimensions.Length == 3 ? _arrayDimensions[0] / 2 : 0;
        await LoadArrayPreviewAsync(token);
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
            var parameters = BuildArrayViewParameters();
            parameters["x"] = TileX;
            parameters["y"] = TileY;
            parameters["width"] = TileWidth;
            parameters["height"] = TileHeight;
            var frame = await RequestAsync("arrays.tile", parameters, _detailCts?.Token ?? _connectionCts?.Token ?? CancellationToken.None);
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

    private async Task LoadArrayPreviewAsync(CancellationToken token)
    {
        var parameters = BuildArrayViewParameters();
        parameters["maxWidth"] = 1024;
        parameters["maxHeight"] = 1024;
        var frame = await RequestAsync("arrays.preview", parameters, token);
        ApplyArrayBitmap(frame);
        if (_fitMode)
            ApplyFitZoom();
    }

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
                if (_currentScope is not null)
                    await LoadScopeAsync(_currentScope);
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
        var filtered = string.IsNullOrEmpty(query)
            ? Variables
            : Variables.Where(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.SafePreview.Contains(query, StringComparison.OrdinalIgnoreCase));
        Replace(FilteredVariables, filtered);
    }

    private void CopyEnvironment()
    {
        var text = $"$env:PY_INSPECTOR_HOST='127.0.0.1'\r\n$env:PY_INSPECTOR_PORT='{PortText}'\r\n$env:PY_INSPECTOR_TOKEN='{Token}'\r\n$env:PYTHONPATH='{FindRepositoryRoot()}\\agent'";
        Clipboard.SetText(text);
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
        _selectionCts?.Cancel();
        _detailCts?.Cancel();
        _refreshCts?.Cancel();
        IsConnected = false;
        IsBusy = false;
        Status = status;
        RuntimeRoots.Clear();
        Variables.Clear();
        FilteredVariables.Clear();
        ObjectChildren.Clear();
        ClassMembers.Clear();
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
        PythonVersion = Architecture = Executable = "—";
        PrivateBytes = WorkingSet = VirtualSize = PeakWorkingSet = "—";
        _lastProcessMemory = null;
        SelectedType = SelectedModule = SelectedQualifiedName = SelectedAddress = "—";
        SelectedShallowSize = SelectedPayloadSize = "—";
        SelectedVariable = null;
        _currentScope = null;
        _arrayHandle = null;
        UpdateCommandStates();
        ClearArrayDetails();
    }

    private void ClearArrayDetails()
    {
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

    private void SetError(Exception exception)
    {
        ErrorMessage = exception switch
        {
            RemoteInspectionException remote => $"{remote.Code}: {remote.Message}",
            LiveAttachException live => $"{live.Code}: {live.Message}",
            _ => exception.Message,
        };
    }

    private void UpdateCommandStates()
    {
        AttachCommand?.RaiseCanExecuteChanged();
        LiveAttachCommand?.RaiseCanExecuteChanged();
        DetachCommand?.RaiseCanExecuteChanged();
        RefreshCommand?.RaiseCanExecuteChanged();
        PreviousPageCommand?.RaiseCanExecuteChanged();
        NextPageCommand?.RaiseCanExecuteChanged();
        ReloadPreviewCommand?.RaiseCanExecuteChanged();
        LoadTileCommand?.RaiseCanExecuteChanged();
        LoadHistogramCommand?.RaiseCanExecuteChanged();
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

    public async ValueTask DisposeAsync()
    {
        _session.Disconnected -= OnSessionDisconnected;
        _launcher.OutputReceived -= OnManagedOutput;
        _launcher.Exited -= OnManagedExited;
        _connectionCts?.Cancel();
        _refreshCts?.Cancel();
        _selectionCts?.Cancel();
        _detailCts?.Cancel();
        await _session.DisposeAsync();
        await _launcher.DisposeAsync();
    }
}
