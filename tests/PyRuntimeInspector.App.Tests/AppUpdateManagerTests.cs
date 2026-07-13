using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using PyRuntimeInspector.App.Infrastructure;
using PyRuntimeInspector.App.Services;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class AppUpdateManagerTests
{
    [Fact]
    public void AutomaticCheckIsDueAfterTwentyFourHoursAndAfterClockMovesBackward()
    {
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

        Assert.True(AppUpdateManager.IsAutomaticCheckDue(null, now));
        Assert.False(AppUpdateManager.IsAutomaticCheckDue(now.AddHours(-23).AddMinutes(-59), now));
        Assert.True(AppUpdateManager.IsAutomaticCheckDue(now.AddHours(-24), now));
        Assert.True(AppUpdateManager.IsAutomaticCheckDue(now.AddMinutes(1), now));
    }

    [Fact]
    public async Task CheckAndDownloadUseOneSharedServiceAndVersionDirectory()
    {
        var release = CreateRelease("26.7.13");
        var service = new RecordingUpdateService(release);
        using var manager = new AppUpdateManager(service);

        var result = await manager.CheckForUpdateAsync();
        var installer = await manager.DownloadAndVerifyInstallerAsync(release);

        Assert.NotNull(result);
        Assert.True(result.IsUpdateAvailable);
        Assert.Same(release, manager.AvailableRelease);
        Assert.Equal(1, service.CheckCount);
        Assert.Equal(1, service.DownloadCount);
        Assert.EndsWith(
            Path.Combine("PyMonitor", "Updates", "26.7.13"),
            service.DestinationDirectory,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("verified", installer.Sha256);
    }

    [Fact]
    public void InstallerLauncherUsesElevationAndArgumentListForVerifiedMsi()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "PyMonitor update 26.7.13.msi");
        File.WriteAllBytes(path, [0]);
        ProcessStartInfo? captured = null;
        var launcher = new WindowsInstallerLauncher(
            startInfo => captured = startInfo,
            new AcceptingAuthenticodeVerifier());
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        launcher.Launch(new VerifiedUpdateInstaller(path, sha256));

        Assert.NotNull(captured);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), captured.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
        Assert.Equal(Environment.SystemDirectory, captured.WorkingDirectory);
        Assert.Equal(["/i", Path.GetFullPath(path)], captured.ArgumentList);
    }

    [Fact]
    public void InstallerLauncherRejectsMissingOrNonMsiFiles()
    {
        var launcher = new WindowsInstallerLauncher(_ => { }, new AcceptingAuthenticodeVerifier());

        Assert.Throws<FileNotFoundException>(() =>
            launcher.Launch(new VerifiedUpdateInstaller("missing.msi", "hash")));
        Assert.Throws<FileNotFoundException>(() =>
            launcher.Launch(new VerifiedUpdateInstaller(typeof(AppUpdateManagerTests).Assembly.Location, "hash")));
    }

    [Fact]
    public void InstallerLauncherRejectsAFileChangedAfterDownloadVerification()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "PyMonitor-26.7.13-win-x64.msi");
        File.WriteAllText(path, "tampered");
        var launcher = new WindowsInstallerLauncher(_ => { }, new AcceptingAuthenticodeVerifier());

        var exception = Assert.Throws<ApplicationUpdateException>(() =>
            launcher.Launch(new VerifiedUpdateInstaller(path, new string('0', 64))));

        Assert.Equal("VERIFIED_INSTALLER_CHANGED", exception.Code);
    }

    private static GitHubUpdateRelease CreateRelease(string version)
    {
        var semanticVersion = SemanticVersion.Parse(version);
        var installerName = $"PyMonitor-{version}-win-x64.msi";
        return new GitHubUpdateRelease(
            semanticVersion,
            $"v{version}",
            $"PyMonitor v{version}",
            new Uri($"https://github.com/example/PyMonitor/releases/tag/v{version}"),
            DateTimeOffset.UtcNow,
            new GitHubReleaseAsset(
                installerName,
                new Uri($"https://github.com/example/PyMonitor/releases/download/v{version}/{installerName}"),
                1),
            new GitHubReleaseAsset(
                installerName + ".sha256",
                new Uri($"https://github.com/example/PyMonitor/releases/download/v{version}/{installerName}.sha256"),
                1));
    }

    private sealed class RecordingUpdateService(GitHubUpdateRelease release) : IGitHubUpdateService
    {
        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }
        public string? DestinationDirectory { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Task.FromResult(new UpdateCheckResult(SemanticVersion.Parse("26.7.12"), release));
        }

        public Task<VerifiedUpdateInstaller> DownloadAndVerifyInstallerAsync(
            GitHubUpdateRelease selectedRelease,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            DestinationDirectory = destinationDirectory;
            return Task.FromResult(new VerifiedUpdateInstaller("installer.msi", "verified"));
        }
    }

    private sealed class AcceptingAuthenticodeVerifier : IAuthenticodeVerifier
    {
        public void VerifyTrusted(string filePath)
        {
        }
    }

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
