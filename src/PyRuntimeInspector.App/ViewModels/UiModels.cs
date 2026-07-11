using System.Collections.ObjectModel;
using PyRuntimeInspector.App.Infrastructure;
using PyRuntimeInspector.App.Services;

namespace PyRuntimeInspector.App.ViewModels;

public enum RuntimeNodeKind
{
    Root,
    Thread,
    Frame,
    Scope,
    Placeholder,
}

public sealed class RuntimeTreeNode(string label, RuntimeNodeKind kind = RuntimeNodeKind.Root)
{
    public string Label { get; } = label;
    public RuntimeNodeKind Kind { get; } = kind;
    public string? FrameHandle { get; init; }
    public string? ScopeType { get; init; }
    public ObservableCollection<RuntimeTreeNode> Children { get; } = [];
}

public sealed class VariableRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Scope { get; init; }
    public required string HandleId { get; init; }
    public required string TypeName { get; init; }
    public required string ModuleName { get; init; }
    public required string QualifiedTypeName { get; init; }
    public required string SafePreview { get; init; }
    public required string Address { get; init; }
    public required long ShallowSize { get; init; }
    public long? PayloadSize { get; init; }
    public string Shape { get; init; } = "";
    public string DType { get; init; } = "";
    public bool Expandable { get; init; }
    public string? AdapterKind { get; init; }
    public required string ChangeToken { get; init; }
    public bool Changed { get; init; }
}

public sealed record ObjectChildRow(string Name, string Origin, string Type, string Preview, string Address);
public sealed record ClassMemberRow(string Name, string Kind, string DeclaredBy, string Signature);

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
