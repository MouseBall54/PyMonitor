using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PyRuntimeInspector.App.Services;

public interface IAppSettingsService
{
    string SettingsPath { get; }
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class AppSettings
{
    public const string DefaultTheme = "Light";
    public const double DefaultRefreshIntervalSeconds = 1;
    public const double DefaultWindowWidth = 1500;
    public const double DefaultWindowHeight = 920;
    public const double DefaultLeftPaneWidth = 270;
    public const double DefaultRightPaneWidth = 310;

    public string Theme { get; init; } = DefaultTheme;
    public double RefreshIntervalSeconds { get; init; } = DefaultRefreshIntervalSeconds;
    public double WindowWidth { get; init; } = DefaultWindowWidth;
    public double WindowHeight { get; init; } = DefaultWindowHeight;
    public bool IsWindowMaximized { get; init; }
    public double LeftPaneWidth { get; init; } = DefaultLeftPaneWidth;
    public double RightPaneWidth { get; init; } = DefaultRightPaneWidth;
    public DateTimeOffset? LastAutomaticUpdateCheckUtc { get; init; }
    public Dictionary<string, double> ColumnWidths { get; init; } = [];

    public static AppSettings CreateDefault() => new();

    internal static AppSettings Normalize(AppSettings? settings)
    {
        if (settings is null)
            return CreateDefault();

        return new AppSettings
        {
            Theme = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? "Dark"
                : DefaultTheme,
            RefreshIntervalSeconds = FiniteAndBounded(
                settings.RefreshIntervalSeconds,
                DefaultRefreshIntervalSeconds,
                1,
                60),
            WindowWidth = FiniteAndBounded(settings.WindowWidth, DefaultWindowWidth, 960, 7680),
            WindowHeight = FiniteAndBounded(settings.WindowHeight, DefaultWindowHeight, 540, 4320),
            IsWindowMaximized = settings.IsWindowMaximized,
            LeftPaneWidth = FiniteAndBounded(settings.LeftPaneWidth, DefaultLeftPaneWidth, 160, 800),
            RightPaneWidth = FiniteAndBounded(settings.RightPaneWidth, DefaultRightPaneWidth, 300, 1200),
            LastAutomaticUpdateCheckUtc = settings.LastAutomaticUpdateCheckUtc?.ToUniversalTime(),
            ColumnWidths = NormalizeColumnWidths(settings.ColumnWidths),
        };
    }

    private static Dictionary<string, double> NormalizeColumnWidths(IReadOnlyDictionary<string, double>? widths) =>
        widths is null
            ? []
            : widths
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                    && pair.Key.Length <= 80
                    && double.IsFinite(pair.Value))
                .Take(32)
                .ToDictionary(
                    pair => pair.Key,
                    pair => Math.Clamp(pair.Value, 40, 2_000),
                    StringComparer.Ordinal);

    private static double FiniteAndBounded(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public sealed class JsonAppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public JsonAppSettingsService(string? settingsPath = null)
    {
        var selectedPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyMonitor", "settings.json")
            : settingsPath;
        SettingsPath = Path.GetFullPath(selectedPath);
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppSettings.CreateDefault();

            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions));
        }
        catch (Exception exception) when (exception is JsonException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
        {
            return AppSettings.CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The settings path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, AppSettings.Normalize(settings), SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
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
