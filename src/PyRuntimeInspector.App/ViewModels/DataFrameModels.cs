namespace PyRuntimeInspector.App.ViewModels;

public sealed record DataFrameColumnInfo(int Position, string Name, string DType)
{
    public string Display => $"{Name}  ·  {DType}";
    public string DataColumnName => $"Column_{Position}";
}

public enum DataFramePaneState
{
    NoSelection,
    Loading,
    Ready,
    Empty,
    Error,
}
