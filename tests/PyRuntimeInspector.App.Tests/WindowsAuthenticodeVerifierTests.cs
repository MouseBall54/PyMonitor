using System.IO;
using PyRuntimeInspector.App.Services;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class WindowsAuthenticodeVerifierTests
{
    [Fact]
    public void WindowsTrustProviderRejectsAnUnsignedFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"pymonitor-unsigned-{Guid.NewGuid():N}.msi");
        File.WriteAllText(path, "not a signed installer");
        try
        {
            var verifier = new WindowsAuthenticodeVerifier();

            var exception = Assert.Throws<ApplicationUpdateException>(() => verifier.VerifyTrusted(path));

            Assert.Equal("AUTHENTICODE_INVALID", exception.Code);
            Assert.Contains("0x", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingInstallerIsRejectedBeforeCallingWindowsTrustProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pymonitor-missing-{Guid.NewGuid():N}.msi");

        var verifier = new WindowsAuthenticodeVerifier();

        Assert.Throws<FileNotFoundException>(() => verifier.VerifyTrusted(path));
    }
}
