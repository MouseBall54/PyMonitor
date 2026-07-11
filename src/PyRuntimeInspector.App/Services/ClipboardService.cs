using System.Windows;

namespace PyRuntimeInspector.App.Services;

public interface IClipboardService
{
    void SetText(string text);
}

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}
