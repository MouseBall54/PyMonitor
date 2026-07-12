using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using PyRuntimeInspector.App.Services;

namespace PyRuntimeInspector.App;

public partial class AboutWindow : Window
{
    private readonly AppUpdateManager? _updateManager;
    private readonly Action<GitHubUpdateRelease>? _releaseFound;
    private readonly Func<Window, GitHubUpdateRelease, Task>? _installUpdate;
    private GitHubUpdateRelease? _availableRelease;

    public AboutWindow()
        : this(null, null, null)
    {
    }

    public AboutWindow(
        AppUpdateManager? updateManager,
        Action<GitHubUpdateRelease>? releaseFound,
        Func<Window, GitHubUpdateRelease, Task>? installUpdate)
    {
        _updateManager = updateManager;
        _releaseFound = releaseFound;
        _installUpdate = installUpdate;
        InitializeComponent();

        if (_updateManager is not { IsConfigured: true })
        {
            CheckForUpdatesButton.IsEnabled = false;
            SetUpdateStatus(_updateManager?.DisabledReason
                ?? "Update checks are available in official GitHub release builds.");
            return;
        }

        if (_updateManager.AvailableRelease is { } release)
            ShowAvailableRelease(release);
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateManager is not { IsConfigured: true })
            return;

        CheckForUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        SetUpdateStatus("Checking the latest stable GitHub release…");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var result = await _updateManager.CheckForUpdateAsync(timeout.Token);
            if (result is null)
            {
                SetUpdateStatus("Update checks are not configured for this build.");
            }
            else if (result.IsUpdateAvailable)
            {
                ShowAvailableRelease(result.LatestRelease);
                _releaseFound?.Invoke(result.LatestRelease);
            }
            else
            {
                _availableRelease = null;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                SetUpdateStatus($"PyMonitor {result.CurrentVersion} is up to date.");
            }
        }
        catch (Exception exception) when (exception is ApplicationUpdateException
                                               or HttpRequestException
                                               or OperationCanceledException
                                               or IOException)
        {
            SetUpdateStatus($"Could not check for updates: {exception.Message}");
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is null || _installUpdate is null)
            return;

        CheckForUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        SetUpdateStatus($"Preparing PyMonitor {_availableRelease.Version}…");
        try
        {
            await _installUpdate(this, _availableRelease);
        }
        finally
        {
            if (IsLoaded)
            {
                CheckForUpdatesButton.IsEnabled = true;
                InstallUpdateButton.IsEnabled = true;
                ShowAvailableRelease(_availableRelease);
            }
        }
    }

    private void ShowAvailableRelease(GitHubUpdateRelease release)
    {
        _availableRelease = release;
        SetUpdateStatus($"PyMonitor {release.Version} is available from GitHub Releases.");
        InstallUpdateButton.Visibility = Visibility.Visible;
    }

    private void SetUpdateStatus(string status)
    {
        UpdateStatusText.Text = status;
        AutomationProperties.SetName(UpdateStatusText, status);
        if (!IsLoaded)
            return;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            RaiseUpdateStatusLiveRegionChanged);
    }

    private void RaiseUpdateStatusLiveRegionChanged()
    {
        if (!IsLoaded)
            return;
        var peer = UIElementAutomationPeer.FromElement(UpdateStatusText)
            ?? UIElementAutomationPeer.CreatePeerForElement(UpdateStatusText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
