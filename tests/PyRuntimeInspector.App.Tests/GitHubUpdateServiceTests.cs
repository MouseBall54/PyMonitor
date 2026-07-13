using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PyRuntimeInspector.App.Services;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Fact]
    public async Task CheckSelectsExactWinX64MsiAssetsAndUsesSemanticVersionComparison()
    {
        using var fixture = CreateFixture(new FixtureOptions
        {
            Tag = "v26.10.0",
            AdditionalAssets =
            [
                new AssetDefinition("PyMonitor-26.10.0-win-x64.zip", 200),
                new AssetDefinition("PyMonitor-26.10.0-win-x86.msi", 100),
                new AssetDefinition("PyMonitor-26.10.0-win-x86.msi.sha256", 100),
            ],
        });

        var result = await fixture.Service.CheckForUpdateAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("26.10.0", result.LatestRelease.Version.ToString());
        Assert.Equal("PyMonitor-26.10.0-win-x64.msi", result.LatestRelease.Installer.Name);
        Assert.Equal("PyMonitor-26.10.0-win-x64.msi.sha256", result.LatestRelease.Checksum.Name);
        Assert.Equal(fixture.InstallerBytes.Length, result.LatestRelease.Installer.SizeBytes);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(
            "https://api.github.com/repos/example-owner/PyMonitor/releases/latest",
            request.Uri.AbsoluteUri);
        Assert.Equal("PyMonitor/26.7.12", request.UserAgent);
        Assert.Contains("application/vnd.github+json", request.Accept, StringComparison.Ordinal);
        Assert.Equal("2022-11-28", request.ApiVersion);
    }

    [Theory]
    [InlineData("v26.7.12")]
    [InlineData("v26.7.10")]
    public async Task CheckReportsNoUpdateWhenLatestStableVersionIsNotNewer(string tag)
    {
        using var fixture = CreateFixture(new FixtureOptions { Tag = tag });

        var result = await fixture.Service.CheckForUpdateAsync();

        Assert.False(result.IsUpdateAvailable);
    }

    [Theory]
    [InlineData("v26.7.13", true, false, "UNSTABLE_RELEASE")]
    [InlineData("v26.7.13", false, true, "UNSTABLE_RELEASE")]
    [InlineData("v26.7.13-rc.1", false, false, "INVALID_RELEASE_VERSION")]
    public async Task CheckRejectsDraftPrereleaseAndSemanticallyUnstableReleases(
        string tag,
        bool draft,
        bool prerelease,
        string expectedCode)
    {
        using var fixture = CreateFixture(new FixtureOptions
        {
            Tag = tag,
            Draft = draft,
            Prerelease = prerelease,
        });

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.CheckForUpdateAsync());

        Assert.Equal(expectedCode, exception.Code);
    }

    [Theory]
    [InlineData("missing-installer")]
    [InlineData("wrong-installer-case")]
    [InlineData("missing-checksum")]
    [InlineData("duplicate-installer")]
    public async Task CheckRequiresExactlyNamedMsiAndSidecarAssets(string scenario)
    {
        using var fixture = CreateFixture(new FixtureOptions
        {
            AssetFactory = (installerName, checksumName, installerSize, checksumSize) => scenario switch
            {
                "missing-installer" => [new AssetDefinition(checksumName, checksumSize)],
                "wrong-installer-case" =>
                [
                    new AssetDefinition(installerName.ToUpperInvariant(), installerSize),
                    new AssetDefinition(checksumName, checksumSize),
                ],
                "missing-checksum" => [new AssetDefinition(installerName, installerSize)],
                "duplicate-installer" =>
                [
                    new AssetDefinition(installerName, installerSize),
                    new AssetDefinition(installerName, installerSize),
                    new AssetDefinition(checksumName, checksumSize),
                ],
                _ => throw new InvalidOperationException(),
            },
        });

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.CheckForUpdateAsync());

        Assert.Equal("RELEASE_ASSET_MISSING", exception.Code);
    }

    [Fact]
    public async Task DownloadVerifiesChecksumThenAuthenticodeBeforePublishingInstaller()
    {
        using var directory = new TemporaryDirectory();
        var verifier = new RecordingAuthenticodeVerifier();
        using var fixture = CreateFixture(new FixtureOptions { AuthenticodeVerifier = verifier });
        var release = (await fixture.Service.CheckForUpdateAsync()).LatestRelease;

        var verified = await fixture.Service.DownloadAndVerifyInstallerAsync(release, directory.Path);

        Assert.Equal(Path.Combine(directory.Path, fixture.InstallerName), verified.Path);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(fixture.InstallerBytes)).ToLowerInvariant(),
            verified.Sha256);
        Assert.Equal(fixture.InstallerBytes, File.ReadAllBytes(verified.Path));
        Assert.Equal(fixture.InstallerBytes, Assert.Single(verifier.VerifiedContents));
        Assert.Equal(
            new[] { fixture.ChecksumName, fixture.InstallerName },
            fixture.Handler.Requests.Skip(1).Select(request => Path.GetFileName(request.Uri.AbsolutePath)));
        Assert.Single(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task ChecksumMismatchDeletesPartialDownloadAndSkipsAuthenticode()
    {
        using var directory = new TemporaryDirectory();
        var verifier = new RecordingAuthenticodeVerifier();
        using var fixture = CreateFixture(new FixtureOptions
        {
            ChecksumText = new string('0', 64) + "  {installer}",
            AuthenticodeVerifier = verifier,
        });
        var release = (await fixture.Service.CheckForUpdateAsync()).LatestRelease;

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.DownloadAndVerifyInstallerAsync(release, directory.Path));

        Assert.Equal("CHECKSUM_MISMATCH", exception.Code);
        Assert.Empty(verifier.VerifiedContents);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task SidecarMustNameTheExactInstaller()
    {
        using var directory = new TemporaryDirectory();
        using var fixture = CreateFixture(new FixtureOptions
        {
            ChecksumText = "{hash}  PyMonitor-other-win-x64.msi",
        });
        var release = (await fixture.Service.CheckForUpdateAsync()).LatestRelease;

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.DownloadAndVerifyInstallerAsync(release, directory.Path));

        Assert.Equal("INVALID_CHECKSUM", exception.Code);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task StreamingInstallerCannotExceedTheBoundWhenContentLengthIsMissing()
    {
        using var directory = new TemporaryDirectory();
        using var fixture = CreateFixture(new FixtureOptions
        {
            InstallerBytes = Encoding.ASCII.GetBytes("twelve-bytes"),
            InstallerReportedSize = 8,
            UnknownInstallerContentLength = true,
            Limits = new UpdateDownloadLimits(1024 * 1024, 16 * 1024, 10),
        });
        var release = (await fixture.Service.CheckForUpdateAsync()).LatestRelease;

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.DownloadAndVerifyInstallerAsync(release, directory.Path));

        Assert.Equal("INSTALLER_TOO_LARGE", exception.Code);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task ReleaseMetadataCannotExceedTheBoundWhenContentLengthIsMissing()
    {
        using var fixture = CreateFixture(new FixtureOptions
        {
            UnknownMetadataContentLength = true,
            Limits = new UpdateDownloadLimits(100, 16 * 1024, 512 * 1024),
        });

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.CheckForUpdateAsync());

        Assert.Equal("RELEASE_METADATA_TOO_LARGE", exception.Code);
    }

    [Fact]
    public async Task ChecksumAssetCannotExceedItsBound()
    {
        using var directory = new TemporaryDirectory();
        using var fixture = CreateFixture(new FixtureOptions
        {
            ChecksumReportedSize = 200,
            Limits = new UpdateDownloadLimits(1024 * 1024, 100, 512 * 1024),
        });
        var release = (await fixture.Service.CheckForUpdateAsync()).LatestRelease;

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.DownloadAndVerifyInstallerAsync(release, directory.Path));

        Assert.Equal("CHECKSUM_TOO_LARGE", exception.Code);
        Assert.Single(fixture.Handler.Requests);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task AuthenticodeFailureDeletesPartialDownloadWithoutReplacingExistingInstaller()
    {
        using var directory = new TemporaryDirectory();
        var existingPath = Path.Combine(directory.Path, "PyMonitor-26.7.13-win-x64.msi");
        var existingContents = Encoding.ASCII.GetBytes("existing verified installer");
        File.WriteAllBytes(existingPath, existingContents);
        var verifier = new RecordingAuthenticodeVerifier(fail: true);
        using var fixture = CreateFixture(new FixtureOptions { AuthenticodeVerifier = verifier });
        var release = (await fixture.Service.CheckForUpdateAsync()).LatestRelease;

        var exception = await Assert.ThrowsAsync<ApplicationUpdateException>(
            () => fixture.Service.DownloadAndVerifyInstallerAsync(release, directory.Path));

        Assert.Equal("AUTHENTICODE_INVALID", exception.Code);
        Assert.Single(verifier.VerifiedContents);
        Assert.Equal(existingContents, File.ReadAllBytes(existingPath));
        Assert.Single(Directory.EnumerateFiles(directory.Path));
    }

    private static UpdateFixture CreateFixture(FixtureOptions? options = null) => new(options ?? new FixtureOptions());

    private sealed class UpdateFixture : IDisposable
    {
        private const string Repository = "example-owner/PyMonitor";
        private readonly HttpClient _httpClient;

        public UpdateFixture(FixtureOptions options)
        {
            var version = options.Tag.Length > 1 && options.Tag[0] is 'v' or 'V'
                ? options.Tag[1..]
                : options.Tag;
            InstallerName = $"PyMonitor-{version}-win-x64.msi";
            ChecksumName = InstallerName + ".sha256";
            InstallerBytes = options.InstallerBytes ?? Encoding.ASCII.GetBytes("test MSI payload");
            var actualHash = Convert.ToHexString(SHA256.HashData(InstallerBytes)).ToLowerInvariant();
            var checksumText = (options.ChecksumText ?? "{hash}  {installer}")
                .Replace("{hash}", actualHash, StringComparison.Ordinal)
                .Replace("{installer}", InstallerName, StringComparison.Ordinal);
            var checksumBytes = Encoding.UTF8.GetBytes(checksumText + "\n");
            var installerSize = options.InstallerReportedSize ?? InstallerBytes.LongLength;
            var checksumSize = options.ChecksumReportedSize ?? checksumBytes.LongLength;
            var defaultAssets = new[]
            {
                new AssetDefinition(InstallerName, installerSize),
                new AssetDefinition(ChecksumName, checksumSize),
            };
            var assets = options.AssetFactory?.Invoke(
                    InstallerName,
                    ChecksumName,
                    installerSize,
                    checksumSize)
                ?? defaultAssets.Concat(options.AdditionalAssets ?? []).ToArray();

            var releaseJson = CreateReleaseJson(options, assets);
            Handler = new StubHttpMessageHandler(request =>
            {
                if (string.Equals(request.RequestUri!.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
                {
                    HttpContent content = options.UnknownMetadataContentLength
                        ? new UnknownLengthContent(Encoding.UTF8.GetBytes(releaseJson))
                        : new StringContent(releaseJson, Encoding.UTF8, "application/json");
                    return Response(content);
                }

                var name = Uri.UnescapeDataString(Path.GetFileName(request.RequestUri.AbsolutePath));
                if (string.Equals(name, ChecksumName, StringComparison.Ordinal))
                    return Response(new ByteArrayContent(checksumBytes));
                if (string.Equals(name, InstallerName, StringComparison.Ordinal))
                {
                    HttpContent content = options.UnknownInstallerContentLength
                        ? new UnknownLengthContent(InstallerBytes)
                        : new ByteArrayContent(InstallerBytes);
                    return Response(content);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            _httpClient = new HttpClient(Handler);
            Service = new GitHubUpdateService(
                _httpClient,
                Repository,
                options.CurrentVersion,
                options.AuthenticodeVerifier ?? new RecordingAuthenticodeVerifier(),
                options.Limits);
        }

        public GitHubUpdateService Service { get; }
        public StubHttpMessageHandler Handler { get; }
        public string InstallerName { get; }
        public string ChecksumName { get; }
        public byte[] InstallerBytes { get; }

        public void Dispose() => _httpClient.Dispose();

        private static string CreateReleaseJson(FixtureOptions options, IEnumerable<AssetDefinition> assets)
        {
            var assetNodes = assets.Select(asset => (JsonNode)new JsonObject
            {
                ["name"] = asset.Name,
                ["size"] = asset.Size,
                ["browser_download_url"] =
                    $"https://github.com/example-owner/PyMonitor/releases/download/{options.Tag}/{asset.Name}",
            }).ToArray();
            return new JsonObject
            {
                ["tag_name"] = options.Tag,
                ["name"] = $"PyMonitor {options.Tag}",
                ["html_url"] = $"https://github.com/example-owner/PyMonitor/releases/tag/{options.Tag}",
                ["draft"] = options.Draft,
                ["prerelease"] = options.Prerelease,
                ["published_at"] = "2026-07-12T10:00:00Z",
                ["assets"] = new JsonArray(assetNodes),
            }.ToJsonString();
        }

        private static HttpResponseMessage Response(HttpContent content) => new(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private sealed class FixtureOptions
    {
        public string Tag { get; init; } = "v26.7.13";
        public string CurrentVersion { get; init; } = "26.7.12";
        public bool Draft { get; init; }
        public bool Prerelease { get; init; }
        public byte[]? InstallerBytes { get; init; }
        public long? InstallerReportedSize { get; init; }
        public long? ChecksumReportedSize { get; init; }
        public string? ChecksumText { get; init; }
        public bool UnknownMetadataContentLength { get; init; }
        public bool UnknownInstallerContentLength { get; init; }
        public IReadOnlyList<AssetDefinition>? AdditionalAssets { get; init; }
        public Func<string, string, long, long, IReadOnlyList<AssetDefinition>>? AssetFactory { get; init; }
        public IAuthenticodeVerifier? AuthenticodeVerifier { get; init; }
        public UpdateDownloadLimits? Limits { get; init; }
    }

    private sealed record AssetDefinition(string Name, long Size);

    private sealed class RecordingAuthenticodeVerifier(bool fail = false) : IAuthenticodeVerifier
    {
        public List<byte[]> VerifiedContents { get; } = [];

        public void VerifyTrusted(string filePath)
        {
            VerifiedContents.Add(File.ReadAllBytes(filePath));
            if (fail)
                throw new ApplicationUpdateException("AUTHENTICODE_INVALID", "Test signature failure.");
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public ConcurrentQueue<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(new RequestSnapshot(
                request.RequestUri!,
                request.Headers.UserAgent.ToString(),
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var values)
                    ? Assert.Single(values)
                    : null));
            return Task.FromResult(responder(request));
        }
    }

    private sealed record RequestSnapshot(Uri Uri, string UserAgent, string Accept, string? ApiVersion);

    private sealed class UnknownLengthContent(byte[] contents) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(contents).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
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
