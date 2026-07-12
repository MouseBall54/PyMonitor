using System.IO;
using System.Net.Http;

namespace PyRuntimeInspector.App.Services;

public sealed class AppUpdateManager : IDisposable
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    private readonly IGitHubUpdateService? _service;
    private readonly HttpClient? _ownedHttpClient;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _disposed;

    public AppUpdateManager(IGitHubUpdateService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    private AppUpdateManager(string disabledReason)
    {
        DisabledReason = disabledReason;
    }

    private AppUpdateManager(IGitHubUpdateService service, HttpClient ownedHttpClient)
        : this(service)
    {
        _ownedHttpClient = ownedHttpClient;
    }

    public bool IsConfigured => _service is not null;
    public string? DisabledReason { get; }
    public UpdateCheckResult? LastResult { get; private set; }
    public GitHubUpdateRelease? AvailableRelease =>
        LastResult is { IsUpdateAvailable: true } result ? result.LatestRelease : null;

    public static AppUpdateManager CreateDefault()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        try
        {
            return new AppUpdateManager(new GitHubUpdateService(httpClient), httpClient);
        }
        catch (Exception exception) when (exception is ApplicationUpdateException
                                               or ArgumentException
                                               or FormatException)
        {
            httpClient.Dispose();
            return new AppUpdateManager(
                "Update checks are available in builds published by the PyMonitor GitHub release workflow.");
        }
    }

    public static bool IsAutomaticCheckDue(
        DateTimeOffset? lastCheckUtc,
        DateTimeOffset nowUtc)
    {
        if (lastCheckUtc is null)
            return true;

        var last = lastCheckUtc.Value.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        return last > now || now - last >= AutomaticCheckInterval;
    }

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_service is null)
            return null;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _checkGate.WaitAsync(linkedCancellation.Token);
        try
        {
            LastResult = await _service.CheckForUpdateAsync(linkedCancellation.Token);
            return LastResult;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public async Task<VerifiedUpdateInstaller> DownloadAndVerifyInstallerAsync(
        GitHubUpdateRelease release,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_service is null)
            throw new ApplicationUpdateException("REPOSITORY_NOT_CONFIGURED", DisabledReason ?? "Updates are not configured.");

        var destination = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PyMonitor",
            "Updates",
            release.Version.ToString());
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        return await _service.DownloadAndVerifyInstallerAsync(release, destination, linkedCancellation.Token);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _ownedHttpClient?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
