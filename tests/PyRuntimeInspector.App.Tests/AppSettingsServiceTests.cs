using System.IO;
using PyRuntimeInspector.App.Services;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void SaveAndLoadRoundTripsAllSupportedSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "nested", "settings.json");
        var service = new JsonAppSettingsService(path);
        var expected = new AppSettings
        {
            Theme = "Dark",
            RefreshIntervalSeconds = 5,
            WindowWidth = 1440,
            WindowHeight = 860,
            IsWindowMaximized = true,
            LeftPaneWidth = 320,
            RightPaneWidth = 420,
            ColumnWidths = new Dictionary<string, double>
            {
                ["Variables.Name"] = 180,
                ["Object.Preview"] = 640,
            },
        };

        service.Save(expected);
        var actual = service.Load();

        Assert.Equal(Path.GetFullPath(path), service.SettingsPath);
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.RefreshIntervalSeconds, actual.RefreshIntervalSeconds);
        Assert.Equal(expected.WindowWidth, actual.WindowWidth);
        Assert.Equal(expected.WindowHeight, actual.WindowHeight);
        Assert.Equal(expected.IsWindowMaximized, actual.IsWindowMaximized);
        Assert.Equal(expected.LeftPaneWidth, actual.LeftPaneWidth);
        Assert.Equal(expected.RightPaneWidth, actual.RightPaneWidth);
        Assert.Equal(expected.ColumnWidths, actual.ColumnWidths);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void CorruptJsonFallsBackToSafeDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{ this is not valid JSON");
        var service = new JsonAppSettingsService(path);

        var settings = service.Load();

        Assert.Equal(AppSettings.DefaultTheme, settings.Theme);
        Assert.Equal(AppSettings.DefaultRefreshIntervalSeconds, settings.RefreshIntervalSeconds);
        Assert.Equal(AppSettings.DefaultWindowWidth, settings.WindowWidth);
        Assert.Equal(AppSettings.DefaultWindowHeight, settings.WindowHeight);
        Assert.False(settings.IsWindowMaximized);
        Assert.Equal(AppSettings.DefaultLeftPaneWidth, settings.LeftPaneWidth);
        Assert.Equal(AppSettings.DefaultRightPaneWidth, settings.RightPaneWidth);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PyMonitor.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
