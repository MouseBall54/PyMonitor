using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.App.ViewModels;

namespace PyRuntimeInspector.App;

public partial class HelpWindow : Window
{
    public static RoutedUICommand FocusSearchCommand { get; } = new(
        "Focus help search",
        nameof(FocusSearchCommand),
        typeof(HelpWindow),
        new InputGestureCollection
        {
            new KeyGesture(Key.F1),
            new KeyGesture(Key.F, ModifierKeys.Control),
        });

    private readonly HelpViewModel _viewModel;
    private readonly AppUpdateManager? _updateManager;
    private readonly Action<GitHubUpdateRelease>? _releaseFound;
    private readonly Func<Window, GitHubUpdateRelease, Task>? _installUpdate;

    public HelpWindow()
        : this(null, null, null)
    {
    }

    public HelpWindow(
        AppUpdateManager? updateManager,
        Action<GitHubUpdateRelease>? releaseFound,
        Func<Window, GitHubUpdateRelease, Task>? installUpdate)
    {
        _updateManager = updateManager;
        _releaseFound = releaseFound;
        _installUpdate = installUpdate;
        InitializeComponent();
        _viewModel = new HelpViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    public void FocusSearch()
    {
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => FocusSearch();

    private void Window_Closed(object? sender, EventArgs e) =>
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HelpViewModel.ResultSummary))
            return;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            RaiseResultSummaryLiveRegionChanged);
    }

    private void RaiseResultSummaryLiveRegionChanged()
    {
        if (!IsLoaded)
            return;

        var peer = UIElementAutomationPeer.FromElement(ResultSummaryText)
            ?? UIElementAutomationPeer.CreatePeerForElement(ResultSummaryText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void FocusSearch_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        FocusSearch();
        e.Handled = true;
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (HelpResultsList.SelectedItem is not null)
        {
            HelpResultsList.ScrollIntoView(HelpResultsList.SelectedItem);
            HelpResultsList.Focus();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ((HelpViewModel)DataContext).ClearSearch();
        FocusSearch();
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow(_updateManager, _releaseFound, _installUpdate) { Owner = this }.ShowDialog();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Close();
            e.Handled = true;
            return;
        }

    }
}
