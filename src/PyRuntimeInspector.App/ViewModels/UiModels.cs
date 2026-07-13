using System.Collections.ObjectModel;
using System.IO;
using PyRuntimeInspector.App.Infrastructure;
using PyRuntimeInspector.App.Services;

namespace PyRuntimeInspector.App.ViewModels;

public enum RuntimeNodeKind
{
    Root,
    Thread,
    Frame,
    Scope,
    Module,
    GcObjects,
    Placeholder,
}

public sealed class RuntimeTreeNode(string label, RuntimeNodeKind kind = RuntimeNodeKind.Root)
{
    public string Label { get; } = label;
    public RuntimeNodeKind Kind { get; } = kind;
    public string? FrameHandle { get; init; }
    public string? ScopeType { get; init; }
    public string? ModuleName { get; init; }
    public ObservableCollection<RuntimeTreeNode> Children { get; } = [];
}

public sealed class VariableRow : ObservableObject
{
    private string _handleId = "";
    private string _typeName = "";
    private string _moduleName = "";
    private string _qualifiedTypeName = "";
    private string _safePreview = "";
    private string _address = "";
    private long _shallowSize;
    private long? _payloadSize;
    private string _shape = "";
    private string _dtype = "";
    private bool _expandable;
    private string? _adapterKind;
    private string _changeToken = "";
    private string _identityToken = "";
    private string _metadataToken = "";
    private VariableChangeKind _changeKind;
    private DateTime? _changedAt;
    private bool _isRemoved;
    private bool _isPinned;

    public required string Name { get; init; }
    public required string Scope { get; init; }
    public string StableScopeKey { get; init; } = "";
    public required string HandleId { get => _handleId; init => _handleId = value; }
    public required string TypeName { get => _typeName; init => _typeName = value; }
    public required string ModuleName { get => _moduleName; init => _moduleName = value; }
    public required string QualifiedTypeName { get => _qualifiedTypeName; init => _qualifiedTypeName = value; }
    public required string SafePreview { get => _safePreview; init => _safePreview = value; }
    public required string Address { get => _address; init => _address = value; }
    public required long ShallowSize { get => _shallowSize; init => _shallowSize = value; }
    public long? PayloadSize { get => _payloadSize; init => _payloadSize = value; }
    public string Shape { get => _shape; init => _shape = value; }
    public string DType { get => _dtype; init => _dtype = value; }
    public bool Expandable { get => _expandable; init => _expandable = value; }
    public string? AdapterKind { get => _adapterKind; init => _adapterKind = value; }
    public required string ChangeToken { get => _changeToken; init => _changeToken = value; }
    public string IdentityToken { get => _identityToken; init => _identityToken = value; }
    public string MetadataToken { get => _metadataToken; init => _metadataToken = value; }
    public VariableChangeKind ChangeKind { get => _changeKind; init => _changeKind = value; }
    public DateTime? ChangedAt { get => _changedAt; init => _changedAt = value; }
    public bool IsRemoved { get => _isRemoved; init => _isRemoved = value; }
    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }
    public bool Changed => ChangeKind != VariableChangeKind.Unchanged;
    public string ChangeLabel => ChangeKind switch
    {
        VariableChangeKind.Added => "Added",
        VariableChangeKind.Removed => "Removed",
        VariableChangeKind.Rebound => "Rebound",
        VariableChangeKind.MetadataChanged => "Updated",
        _ => "Unchanged",
    };
    public string ChangeGlyph => ChangeKind switch
    {
        VariableChangeKind.Added => "+",
        VariableChangeKind.Removed => "−",
        VariableChangeKind.Rebound => "↻",
        VariableChangeKind.MetadataChanged => "Δ",
        _ => "",
    };
    public string ChangeDisplay => Changed ? $"{ChangeGlyph} {ChangeLabel}" : ChangeLabel;
    public string ChangedAtText => ChangedAt is DateTime value ? value.ToString("HH:mm:ss") : "—";
    public string ChangeTimeDisplay => Changed ? $"{ChangedAtText} · since refresh" : "";
    public string ChangeDetail => Changed
        ? $"Changed since previous refresh at {ChangedAtText}"
        : "No change since previous refresh";
    public string EffectiveScopeKey => string.IsNullOrEmpty(StableScopeKey) ? Scope : StableScopeKey;
    public string StableKey => $"{EffectiveScopeKey}:{Name}";

    public void UpdateFrom(VariableRow source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(StableKey, source.StableKey, StringComparison.Ordinal))
            throw new ArgumentException("Only rows with the same stable key can be updated in place.", nameof(source));

        SetProperty(ref _handleId, source.HandleId, nameof(HandleId));
        SetProperty(ref _typeName, source.TypeName, nameof(TypeName));
        SetProperty(ref _moduleName, source.ModuleName, nameof(ModuleName));
        SetProperty(ref _qualifiedTypeName, source.QualifiedTypeName, nameof(QualifiedTypeName));
        SetProperty(ref _safePreview, source.SafePreview, nameof(SafePreview));
        SetProperty(ref _address, source.Address, nameof(Address));
        SetProperty(ref _shallowSize, source.ShallowSize, nameof(ShallowSize));
        SetProperty(ref _payloadSize, source.PayloadSize, nameof(PayloadSize));
        SetProperty(ref _shape, source.Shape, nameof(Shape));
        SetProperty(ref _dtype, source.DType, nameof(DType));
        SetProperty(ref _expandable, source.Expandable, nameof(Expandable));
        SetProperty(ref _adapterKind, source.AdapterKind, nameof(AdapterKind));
        SetProperty(ref _changeToken, source.ChangeToken, nameof(ChangeToken));
        SetProperty(ref _identityToken, source.IdentityToken, nameof(IdentityToken));
        SetProperty(ref _metadataToken, source.MetadataToken, nameof(MetadataToken));
        var changeKindChanged = SetProperty(ref _changeKind, source.ChangeKind, nameof(ChangeKind));
        var changedAtChanged = SetProperty(ref _changedAt, source.ChangedAt, nameof(ChangedAt));
        SetProperty(ref _isRemoved, source.IsRemoved, nameof(IsRemoved));
        IsPinned = source.IsPinned;

        if (changeKindChanged)
        {
            OnPropertyChanged(nameof(Changed));
            OnPropertyChanged(nameof(ChangeLabel));
            OnPropertyChanged(nameof(ChangeGlyph));
            OnPropertyChanged(nameof(ChangeDisplay));
        }
        if (changeKindChanged || changedAtChanged)
        {
            OnPropertyChanged(nameof(ChangedAtText));
            OnPropertyChanged(nameof(ChangeTimeDisplay));
            OnPropertyChanged(nameof(ChangeDetail));
        }
    }
}

public sealed class GlobalSearchResultRow
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    public required string ObjectPath { get; init; }
    public required string MatchFields { get; init; }
    public required string SourceKind { get; init; }
    public string? ModuleName { get; init; }
    public string? FrameHandle { get; init; }
    public string? ScopeType { get; init; }
    public string? RootName { get; init; }
    public required int Depth { get; init; }
    public VariableRow? Value { get; init; }
    public string TypeName => Value?.TypeName ?? "—";
    public string SafePreview => Value?.SafePreview ?? "";
    public bool CanOpen => Value is not null || SourceKind is "module" or "frame";
}

public enum VariableChangeKind
{
    Unchanged,
    Added,
    Removed,
    Rebound,
    MetadataChanged,
}

public enum InspectorPaneState
{
    NoSelection,
    Loading,
    Ready,
    Empty,
    Expired,
    Error,
}

public enum MatplotlibPaneState
{
    NoSelection,
    Loading,
    Ready,
    Unavailable,
    Error,
}

public enum ObjectNodeKind
{
    Object,
    Group,
    LoadMore,
    Status,
}

public sealed record ObjectBreadcrumbItem(string Label, string Path, int Depth, bool IsCurrent)
{
    public bool IsRoot => Depth == 0;
    public string ToolTip => IsCurrent ? $"Current object: {Path}" : $"Go directly to {Path}";
    public string AccessibilityName => IsCurrent
        ? $"Current object {Label}, level {Depth}"
        : $"Navigate to ancestor {Label}, level {Depth}";
}

public sealed class ObjectTreeNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isLoading;
    private bool _isLoaded;
    private bool _isSearchVisible = true;
    private bool _isSearchMatch;
    private bool _isSearchAncestor;

    public required string Label { get; init; }
    public string Origin { get; init; } = "";
    public string Path { get; init; } = "";
    public int Depth { get; init; }
    public ObjectNodeKind Kind { get; init; } = ObjectNodeKind.Object;
    public VariableRow? Value { get; init; }
    public ObjectTreeNode? Parent { get; init; }
    public bool IsCycle { get; init; }
    public bool IsExpired { get; init; }
    public int Offset { get; init; }
    public int TotalChildren { get; set; }
    public int LoadedChildren { get; set; }
    public ObservableCollection<ObjectTreeNode> Children { get; } = [];
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public bool IsLoaded { get => _isLoaded; set => SetProperty(ref _isLoaded, value); }
    public bool IsSearchVisible { get => _isSearchVisible; set => SetProperty(ref _isSearchVisible, value); }
    public bool IsSearchMatch { get => _isSearchMatch; set => SetProperty(ref _isSearchMatch, value); }
    public bool IsSearchAncestor { get => _isSearchAncestor; set => SetProperty(ref _isSearchAncestor, value); }
    public bool CanNavigate => Kind == ObjectNodeKind.Object && Value is not null && !IsCycle && !IsExpired;
    public string TypeName => Value?.TypeName ?? "";
    public string Preview => Value?.SafePreview ?? "";
    public string Address => Value?.Address ?? "";
    public string LevelLabel => $"L{Depth}";
    public string ParentContext => Kind switch
    {
        ObjectNodeKind.Object when Parent is null => "Object root",
        ObjectNodeKind.Object => $"Object parent: {Parent!.Label}",
        ObjectNodeKind.Group => $"Section of: {Parent?.Label ?? "current object"}",
        ObjectNodeKind.LoadMore => $"More children of: {Parent?.Label ?? "current object"}",
        _ => $"Status for: {Parent?.Label ?? "current object"}",
    };
    public string AccessibilityName => Kind == ObjectNodeKind.Object
        ? $"{Label}, {TypeName}, object level {Depth}, {ParentContext}"
        : $"{Label}, {Kind}, {ParentContext}";
    public string HierarchyHelpText => string.IsNullOrWhiteSpace(Path)
        ? AccessibilityName
        : $"{AccessibilityName}. Path: {Path}";
}

public sealed class ClassTreeNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSearchVisible = true;
    private bool _isSearchMatch;
    private bool _isSearchAncestor;

    public required string Label { get; init; }
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    public string DeclaredBy { get; init; } = "";
    public string Source { get; init; } = "";
    public int Depth { get; internal set; }
    public ClassTreeNode? Parent { get; internal set; }
    public ObservableCollection<ClassTreeNode> Children { get; } = [];
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsSearchVisible { get => _isSearchVisible; set => SetProperty(ref _isSearchVisible, value); }
    public bool IsSearchMatch { get => _isSearchMatch; set => SetProperty(ref _isSearchMatch, value); }
    public bool IsSearchAncestor { get => _isSearchAncestor; set => SetProperty(ref _isSearchAncestor, value); }
    public string LevelLabel => $"L{Depth}";
    public string ParentContext => Parent is null ? "Class tree root" : $"Tree parent: {Parent.Label}";
    public string HierarchyPath => Parent is null ? Label : $"{Parent.HierarchyPath} > {Label}";
    public string AccessibilityName => $"{Label}, {Kind}, tree level {Depth}, {ParentContext}";
    public string HierarchyHelpText =>
        $"{AccessibilityName}. Hierarchy: {HierarchyPath}"
        + (string.IsNullOrWhiteSpace(DeclaredBy) ? "" : $". Declared by: {DeclaredBy}")
        + (string.IsNullOrWhiteSpace(Detail) ? "" : $". Detail: {Detail}")
        + (string.IsNullOrWhiteSpace(Source) ? "" : $". Source: {Source}");
}

public sealed record ObjectChildRow(
    string Name,
    string Origin,
    string Type,
    string Preview,
    string Address,
    string HandleId = "",
    bool Expandable = false,
    string Path = "");

public sealed record ClassMemberRow(
    string Name,
    string Kind,
    string DeclaredBy,
    string Signature,
    bool Inherited = false,
    string Source = "");

public sealed record PinnedObjectRow(string StableKey, string Name, string Path, string TypeName, string Preview, string Address)
{
    public string Display => $"{Name}  ·  {TypeName}";
}
public sealed record MemorySnapshotRow(string SnapshotId, string Label, DateTime CreatedAt, long TraceCount, long TotalBytes)
{
    public string Display => $"{CreatedAt:HH:mm:ss}  {Label}  ({TotalBytes:N0} B)";
}

public sealed record MemoryStatisticRow(
    string Filename,
    int LineNumber,
    long SizeBytes,
    long Count,
    long? SizeDiffBytes,
    long? CountDiff)
{
    public string Location => LineNumber > 0 ? $"{Filename}:{LineNumber}" : Filename;
}

public sealed record MemorySampleRow(
    DateTime Timestamp,
    long? WorkingSetBytes,
    long? PrivateBytes,
    long? VirtualBytes,
    long? PythonCurrentBytes,
    long? PythonPeakBytes);

public sealed record HistogramBinRow(int Index, double Start, double End, long Count);

public sealed record ExecutionEventRow(
    long Sequence,
    DateTime Timestamp,
    long ThreadId,
    string EventName,
    string FunctionName,
    string Filename,
    int LineNumber,
    int? InstructionOffset,
    string? Detail)
{
    public string Location => $"{Path.GetFileName(Filename)}:{LineNumber}";
}

public sealed class EnvironmentVariableRow : ObservableObject
{
    private string _name = "NAME";
    private string _value = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

public sealed record LaunchOutputLine(DateTime Timestamp, ProcessOutputKind Kind, string Text)
{
    public string Prefix => Kind == ProcessOutputKind.StandardError ? "stderr" : "stdout";
    public string Display => $"{Timestamp:HH:mm:ss.fff}  [{Prefix}]  {Text}";
}
