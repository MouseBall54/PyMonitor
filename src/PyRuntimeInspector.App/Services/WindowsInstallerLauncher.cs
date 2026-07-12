using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace PyRuntimeInspector.App.Services;

public interface IUpdateInstallerLauncher
{
    void Launch(VerifiedUpdateInstaller installer);
}

public sealed class WindowsInstallerLauncher : IUpdateInstallerLauncher
{
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly IAuthenticodeVerifier _authenticodeVerifier;

    public WindowsInstallerLauncher()
        : this(startInfo =>
        {
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Windows Installer could not be started.");
        }, new WindowsAuthenticodeVerifier())
    {
    }

    public WindowsInstallerLauncher(
        Action<ProcessStartInfo> startProcess,
        IAuthenticodeVerifier authenticodeVerifier)
    {
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
        _authenticodeVerifier = authenticodeVerifier
            ?? throw new ArgumentNullException(nameof(authenticodeVerifier));
    }

    public void Launch(VerifiedUpdateInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        var installerPath = Path.GetFullPath(installer.Path);
        if (!string.Equals(Path.GetExtension(installerPath), ".msi", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(installerPath))
            throw new FileNotFoundException("The verified PyMonitor MSI is no longer available.", installerPath);

        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(installer.Sha256);
        }
        catch (FormatException exception)
        {
            throw new ApplicationUpdateException(
                "INVALID_VERIFIED_INSTALLER",
                "The verified installer SHA-256 is invalid.",
                exception);
        }
        if (expectedHash.Length != SHA256.HashSizeInBytes)
            throw new ApplicationUpdateException(
                "INVALID_VERIFIED_INSTALLER",
                "The verified installer SHA-256 is invalid.");

        using var installerStream = new FileStream(
            installerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var actualHash = SHA256.HashData(installerStream);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            throw new ApplicationUpdateException(
                "VERIFIED_INSTALLER_CHANGED",
                "The verified installer changed before Windows Installer could start.");
        _authenticodeVerifier.VerifyTrusted(installerPath);

        var systemDirectory = Environment.SystemDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(systemDirectory, "msiexec.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = systemDirectory,
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(installerPath);
        _startProcess(startInfo);
    }
}
