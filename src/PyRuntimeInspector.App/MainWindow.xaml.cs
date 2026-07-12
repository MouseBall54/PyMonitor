using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.App.ViewModels;

namespace PyRuntimeInspector.App;

public partial class MainWindow : Window
{
    public static RoutedUICommand CopyDisplayedValueCommand { get; } = new(
        "Copy displayed value",
        nameof(CopyDisplayedValueCommand),
        typeof(MainWindow));

    public static RoutedUICommand OpenHelpCommand { get; } = new(
        "Open PyMonitor help",
        nameof(OpenHelpCommand),
        typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.F1) });

    private readonly IAppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private HelpWindow? _helpWindow;
    private bool _panning;
    private bool _applyingTheme;
    private string _theme = AppSettings.DefaultTheme;
    private Point _panOrigin;
    private double _horizontalOrigin;
    private double _verticalOrigin;

    public MainWindow()
        : this(new JsonAppSettingsService())
    {
    }

    public MainWindow(IAppSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settings = _settingsService.Load();
        InitializeComponent();
        DataContext = new MainViewModel();
        ViewModel.LaunchOutput.CollectionChanged += LaunchOutput_CollectionChanged;

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        LeftPaneColumn.Width = new GridLength(_settings.LeftPaneWidth);
        VariablePaneColumn.Width = new GridLength(_settings.RightPaneWidth);
        RestoreColumnWidths();
        ViewModel.RefreshIntervalSeconds = _settings.RefreshIntervalSeconds;
        SelectTheme(_settings.Theme);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void CopyDisplayedValue_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Parameter is string { Length: > 0 };
        e.Handled = true;
    }

    private void CopyDisplayedValue_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string value)
            ViewModel.CopyDisplayedValue(value);
        e.Handled = true;
    }

    private void RestoreColumnWidths()
    {
        RestoreColumnWidth("Variables.Name", VariableNameColumn);
        RestoreColumnWidth("Variables.Type", VariableTypeColumn);
        RestoreColumnWidth("Variables.Preview", VariablePreviewColumn);
        RestoreColumnWidth("Variables.Change", VariableChangeColumn);
        RestoreColumnWidth("Object.Name", ObjectNameColumn);
        RestoreColumnWidth("Object.Origin", ObjectOriginColumn);
        RestoreColumnWidth("Object.Type", ObjectTypeColumn);
        RestoreColumnWidth("Object.Preview", ObjectPreviewColumn);
    }

    private void RestoreColumnWidth(string key, DataGridColumn column)
    {
        if (_settings.ColumnWidths.TryGetValue(key, out var width))
            column.Width = new DataGridLength(width);
    }

    private async void RuntimeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        await ViewModel.SelectTreeNodeAsync(e.NewValue as RuntimeTreeNode);
    }

    private async void ObjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        await ViewModel.SelectObjectNodeAsync(e.NewValue as ObjectTreeNode);
    }

    private async void ObjectBreadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ObjectBreadcrumbItem item })
            await ViewModel.NavigateBreadcrumbAsync(item);
        e.Handled = true;
    }

    private void DataFrameGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (string.Equals(e.PropertyName, "__index__", StringComparison.Ordinal))
        {
            e.Column.Header = "Index";
            e.Column.Width = new DataGridLength(130);
            return;
        }

        var column = ViewModel.DataFrameColumns.FirstOrDefault(
            item => string.Equals(item.DataColumnName, e.PropertyName, StringComparison.Ordinal));
        if (column is null)
            return;
        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = column.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
            ToolTip = column.Name,
        });
        var dtype = new TextBlock
        {
            Text = column.DType,
            FontSize = 10,
            FontWeight = FontWeights.Normal,
        };
        dtype.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        header.Children.Add(dtype);
        AutomationProperties.SetName(header, $"{column.Name} column, dtype {column.DType}");
        e.Column.Header = header;
        e.Column.Width = new DataGridLength(150);
    }

    private async void ObjectTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: ObjectTreeNode node })
            return;
        e.Handled = true;
        await ViewModel.ExpandObjectNodeAsync(node);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.IsWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.SelectedWorkspaceTabIndex = 0;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            Execute(ViewModel.RefreshCommand);
            e.Handled = true;
            return;
        }

        if (e.SystemKey == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Execute(ViewModel.NavigateBackCommand);
            e.Handled = true;
        }
        else if (e.SystemKey == Key.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Execute(ViewModel.NavigateForwardCommand);
            e.Handled = true;
        }
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_helpWindow is null)
        {
            _helpWindow = new HelpWindow { Owner = this };
            _helpWindow.Closed += HelpWindow_Closed;
            _helpWindow.Show();
        }
        else
        {
            if (_helpWindow.WindowState == WindowState.Minimized)
                _helpWindow.WindowState = WindowState.Normal;
            _helpWindow.Activate();
            _helpWindow.FocusSearch();
        }

        e.Handled = true;
    }

    private void HelpWindow_Closed(object? sender, EventArgs e)
    {
        if (_helpWindow is not null)
            _helpWindow.Closed -= HelpWindow_Closed;
        _helpWindow = null;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingTheme || ThemeSelector.SelectedItem is not ComboBoxItem item || item.Content is not string theme)
            return;
        SelectTheme(theme);
    }

    private void SelectTheme(string theme)
    {
        _theme = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        _applyingTheme = true;
        ThemeSelector.SelectedIndex = _theme == "Dark" ? 1 : 0;
        _applyingTheme = false;
        ApplyTheme(_theme);
    }

    private static void ApplyTheme(string theme)
    {
        var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        SetBrush("WindowBackground", dark ? "#0F172A" : "#F5F7FB");
        SetBrush("PanelBackground", dark ? "#111827" : "#FFFFFF");
        SetBrush("RaisedBackground", dark ? "#1F2937" : "#F1F5F9");
        SetBrush("SubtleBackground", dark ? "#172033" : "#F8FAFC");
        SetBrush("BorderBrush", dark ? "#334155" : "#D8E0EA");
        SetBrush("PrimaryText", dark ? "#F1F5F9" : "#172033");
        SetBrush("SecondaryText", dark ? "#A7B3C5" : "#5F6B7A");
        SetBrush("AccentBrush", dark ? "#60A5FA" : "#2563EB");
        SetBrush("AccentHoverBrush", dark ? "#93C5FD" : "#1D4ED8");
        SetBrush("SelectionBrush", dark ? "#1E3A5F" : "#DBEAFE");
        SetSystemBrush(SystemColors.HighlightBrushKey, dark ? "#1E3A5F" : "#DBEAFE");
        SetSystemBrush(SystemColors.HighlightTextBrushKey, dark ? "#F1F5F9" : "#172033");
        SetSystemBrush(SystemColors.InactiveSelectionHighlightBrushKey, dark ? "#243247" : "#E7EFFA");
        SetSystemBrush(SystemColors.InactiveSelectionHighlightTextBrushKey, dark ? "#F1F5F9" : "#172033");
        SetBrush("HoverBrush", dark ? "#243247" : "#EFF6FF");
        SetBrush("SuccessBrush", dark ? "#4ADE80" : "#16834A");
        SetBrush("SuccessBackground", dark ? "#123523" : "#EAFBF2");
        SetBrush("WarningBrush", dark ? "#FACC15" : "#A16207");
        SetBrush("WarningBackground", dark ? "#3B3214" : "#FFF7D6");
        SetBrush("DangerBrush", dark ? "#FCA5A5" : "#B42318");
        SetBrush("DangerBackground", dark ? "#431D24" : "#FEECEC");
        SetBrush("TerminalBackground", dark ? "#020617" : "#0F172A");
        SetBrush("TerminalText", "#E2E8F0");
    }

    private static void SetBrush(string key, string color)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static void SetSystemBrush(ResourceKey key, string color)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private async void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
            return;
        var point = e.GetPosition(PreviewImage);
        var x = (int)(point.X / PreviewImage.ActualWidth * ViewModel.PreviewWidth);
        var y = (int)(point.Y / PreviewImage.ActualHeight * ViewModel.PreviewHeight);
        await ViewModel.LoadPixelAsync(x, y);
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
            return;
        var point = e.GetPosition(PreviewImage);
        ViewModel.UpdateCursor(
            (int)(point.X / PreviewImage.ActualWidth * ViewModel.PreviewWidth),
            (int)(point.Y / PreviewImage.ActualHeight * ViewModel.PreviewHeight));
    }

    private void ImageViewerScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.UpdateFitViewport(e.NewSize.Width, e.NewSize.Height);

    private void ImageViewerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ViewModel.Zoom *= e.Delta > 0 ? 1.25 : 0.8;
        e.Handled = true;
    }

    private void ImageViewerScroll_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panning = true;
        _panOrigin = e.GetPosition(ImageViewerScroll);
        _horizontalOrigin = ImageViewerScroll.HorizontalOffset;
        _verticalOrigin = ImageViewerScroll.VerticalOffset;
        ImageViewerScroll.CaptureMouse();
        e.Handled = true;
    }

    private void ImageViewerScroll_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        ImageViewerScroll.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ImageViewerScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
            return;
        var point = e.GetPosition(ImageViewerScroll);
        ImageViewerScroll.ScrollToHorizontalOffset(_horizontalOrigin - (point.X - _panOrigin.X));
        ImageViewerScroll.ScrollToVerticalOffset(_verticalOrigin - (point.Y - _panOrigin.Y));
        e.Handled = true;
    }

    private void LaunchOutput_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.LaunchOutput.Count > 0)
            LaunchOutputList.ScrollIntoView(ViewModel.LaunchOutput[^1]);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        try
        {
            _settingsService.Save(new AppSettings
            {
                Theme = _theme,
                RefreshIntervalSeconds = ViewModel.RefreshIntervalSeconds,
                WindowWidth = bounds.Width,
                WindowHeight = bounds.Height,
                IsWindowMaximized = WindowState == WindowState.Maximized,
                LeftPaneWidth = LeftPaneColumn.ActualWidth,
                RightPaneWidth = VariablePaneColumn.ActualWidth,
                ColumnWidths = new Dictionary<string, double>
                {
                    ["Variables.Name"] = VariableNameColumn.ActualWidth,
                    ["Variables.Type"] = VariableTypeColumn.ActualWidth,
                    ["Variables.Preview"] = VariablePreviewColumn.ActualWidth,
                    ["Variables.Change"] = VariableChangeColumn.ActualWidth,
                    ["Object.Name"] = ObjectNameColumn.ActualWidth,
                    ["Object.Origin"] = ObjectOriginColumn.ActualWidth,
                    ["Object.Type"] = ObjectTypeColumn.ActualWidth,
                    ["Object.Preview"] = ObjectPreviewColumn.ActualWidth,
                },
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Settings are optional; application shutdown must not be blocked by a read-only profile.
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        ViewModel.LaunchOutput.CollectionChanged -= LaunchOutput_CollectionChanged;
        await ViewModel.DisposeAsync();
    }
}
