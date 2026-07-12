using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.App.ViewModels;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

[Collection("WPF UI")]
public sealed class MainWindowSmokeTests
{
    [Fact]
    public async Task MainWindowAtMinimumViewportRendersWithoutBindingErrorsAndVirtualizesVariables()
    {
        using var settingsDirectory = new TemporaryDirectory();
        var testSettingsPath = Path.Combine(settingsDirectory.Path, "settings.json");
        var userSettingsPath = new JsonAppSettingsService().SettingsPath;
        var userSettingsBefore = CaptureFile(userSettingsPath);
        Assert.False(string.Equals(testSettingsPath, userSettingsPath, StringComparison.OrdinalIgnoreCase));

        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            App? application = null;
            MainWindow? window = null;
            var bindingSource = PresentationTraceSources.DataBindingSource;
            var originalBindingTraceLevel = bindingSource.Switch.Level;
            using var bindingFailures = new BindingFailureTraceListener();
            try
            {
                bindingSource.Switch.Level = SourceLevels.Warning;
                bindingSource.Listeners.Add(bindingFailures);

                application = new App();
                application.InitializeComponent();
                window = new MainWindow(new JsonAppSettingsService(testSettingsPath))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 0,
                    Top = 0,
                    Width = 960,
                    Height = 540,
                };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.InRange(window.ActualWidth, 950, 970);
                Assert.InRange(window.ActualHeight, 530, 550);
                var primaryActions = Descendants<Button>(window)
                    .Where(button => button.Content is string text
                        && text is "Quick Attach" or "Refresh" or "Detach" or "About")
                    .ToDictionary(button => (string)button.Content);
                Assert.Equal(4, primaryActions.Count);
                foreach (var (name, button) in primaryActions)
                {
                    Assert.True(button.IsVisible, $"{name} is not visible at the minimum supported viewport.");
                    var bounds = button.TransformToAncestor(window).TransformBounds(
                        new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Assert.True(bounds.Left >= 0 && bounds.Top >= 0
                        && bounds.Right <= window.ActualWidth
                        && bounds.Bottom <= window.ActualHeight,
                        $"{name} is clipped at the minimum supported viewport: {bounds}.");
                }

                var objectNavigationButtons = Descendants<Button>(window)
                    .Where(button => AutomationProperties.GetName(button) is
                        "Back in object history" or "Forward in object history" or "Go to parent object")
                    .ToArray();
                Assert.Equal(3, objectNavigationButtons.Length);
                Assert.All(objectNavigationButtons, button =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(button.ToolTip?.ToString()));
                    Assert.Equal(button.ToolTip?.ToString(), AutomationProperties.GetHelpText(button));
                });
                var ancestry = Descendants<ItemsControl>(window)
                    .Single(control => AutomationProperties.GetName(control) == "Object ancestry breadcrumb");
                Assert.NotNull(ancestry.ItemTemplate);
                Assert.Equal("History 0 / 0", Descendants<TextBlock>(window)
                    .Single(text => AutomationProperties.GetName(text) == "History 0 / 0").Text);
                Assert.Equal("No level", Descendants<TextBlock>(window)
                    .Single(text => AutomationProperties.GetName(text) == "No level").Text);

                var dataFrameTab = LogicalDescendants<TabItem>(window)
                    .Single(tab => string.Equals(tab.Header?.ToString(), "DataFrame", StringComparison.Ordinal));
                Assert.Equal("Pandas DataFrame preview", AutomationProperties.GetName(dataFrameTab));
                var dataFrameGrid = LogicalDescendants<DataGrid>(dataFrameTab)
                    .Single(grid => AutomationProperties.GetName(grid) == "DataFrame preview table");
                Assert.True(dataFrameGrid.AutoGenerateColumns);
                Assert.True(dataFrameGrid.EnableRowVirtualization);
                Assert.True(dataFrameGrid.EnableColumnVirtualization);
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(dataFrameGrid));
                var dataFrameActions = LogicalDescendants<Button>(dataFrameTab)
                    .Select(AutomationProperties.GetName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.Ordinal);
                Assert.True(new[]
                {
                    "Refresh DataFrame preview",
                    "Previous DataFrame rows",
                    "Next DataFrame rows",
                    "Previous DataFrame columns",
                    "Next DataFrame columns",
                }.All(dataFrameActions.Contains));
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                viewModel.DataFrameColumns.Add(new DataFrameColumnInfo(0, "customer", "string"));
                using var dataFrameTable = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
                dataFrameTable.Columns.Add("__index__", typeof(string));
                dataFrameTable.Columns.Add("Column_0", typeof(string));
                dataFrameTable.Rows.Add("row-1", "Ada");
                dataFrameTab.Visibility = Visibility.Visible;
                dataFrameTab.IsEnabled = true;
                dataFrameTab.IsSelected = true;
                dataFrameGrid.ItemsSource = dataFrameTable.DefaultView;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal(2, dataFrameGrid.Columns.Count);
                Assert.Equal("Index", dataFrameGrid.Columns[0].Header);
                var customerHeader = Assert.IsType<StackPanel>(dataFrameGrid.Columns[1].Header);
                Assert.Equal("customer column, dtype string", AutomationProperties.GetName(customerHeader));

                AssertVariablesGridIsVirtualized(window);
                Assert.True(bindingFailures.Messages.Count == 0,
                    $"WPF reported data-binding warnings/errors:{Environment.NewLine}{string.Join(Environment.NewLine, bindingFailures.Messages)}");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    window?.Close();
                    application?.Shutdown();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                bindingSource.Listeners.Remove(bindingFailures);
                bindingSource.Switch.Level = originalBindingTraceLevel;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.TrySetResult(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(exception);
        thread.Join(TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(testSettingsPath), "Closing the smoke-test window should save only its isolated settings file.");
        Assert.Equal(userSettingsBefore, CaptureFile(userSettingsPath));
    }

    private static void AssertVariablesGridIsVirtualized(MainWindow window)
    {
        const int itemCount = 5_000;
        var variablesGrid = Descendants<DataGrid>(window)
            .Single(grid => AutomationProperties.GetName(grid) == "Variables");
        var variables = Enumerable.Range(0, itemCount)
            .Select(index => new VariableRow
            {
                Name = $"variable_{index}",
                Scope = "Local",
                HandleId = $"handle-{index}",
                TypeName = "int",
                ModuleName = "builtins",
                QualifiedTypeName = "builtins.int",
                SafePreview = index.ToString(),
                Address = $"0x{index:x}",
                ShallowSize = 28,
                ChangeToken = index.ToString(),
            })
            .ToArray();

        variablesGrid.ItemsSource = variables;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();

        Assert.True(variablesGrid.EnableRowVirtualization);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(variablesGrid));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(variablesGrid));
        Assert.True(ScrollViewer.GetCanContentScroll(variablesGrid));
        Assert.True(variablesGrid.IsVisible && variablesGrid.ActualHeight > 0,
            "The Variables grid must be visible and measured at the minimum viewport.");

        var initiallyRealized = CountRealizedRows(variablesGrid, itemCount);
        AssertRealizedRowsAreBounded(initiallyRealized, itemCount, "initial viewport");
        Assert.Null(variablesGrid.ItemContainerGenerator.ContainerFromIndex(itemCount - 1));
    }

    private static int CountRealizedRows(DataGrid grid, int itemCount) =>
        Enumerable.Range(0, itemCount)
            .Count(index => grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow);

    private static void AssertRealizedRowsAreBounded(int realizedRows, int itemCount, string phase)
    {
        Assert.True(realizedRows > 0, $"The Variables grid did not realize any rows for the {phase}.");
        Assert.True(realizedRows * 20 < itemCount,
            $"Row virtualization was ineffective for the {phase}: {realizedRows} of {itemCount} rows were realized.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject)
                continue;
            if (dependencyObject is T match)
                yield return match;
            foreach (var descendant in LogicalDescendants<T>(dependencyObject))
                yield return descendant;
        }
    }

    private static SettingsFileSnapshot CaptureFile(string path)
    {
        if (!File.Exists(path))
            return default;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new SettingsFileSnapshot(
            Exists: true,
            stream.Length,
            File.GetLastWriteTimeUtc(path),
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class BindingFailureTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();
        public override bool IsThreadSafe => true;

        public override void Write(string? message) => Record(message);
        public override void WriteLine(string? message) => Record(message);

        private void Record(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _messages.Enqueue(message.Trim());
        }
    }

    private readonly record struct SettingsFileSnapshot(
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc,
        string? Sha256);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PyMonitor.Tests",
                Guid.NewGuid().ToString("N"));
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
