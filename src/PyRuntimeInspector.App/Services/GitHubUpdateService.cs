using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PyRuntimeInspector.App.Infrastructure;

namespace PyRuntimeInspector.App.Services;

public sealed class ApplicationUpdateException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record UpdateDownloadLimits(
    int ReleaseMetadataBytes = 1024 * 1024,
    int ChecksumBytes = 16 * 1024,
    long InstallerBytes = 512L * 1024 * 1024);

public sealed record GitHubReleaseAsset(string Name, Uri DownloadUri, long SizeBytes);

public sealed record GitHubUpdateRelease(
    SemanticVersion Version,
    string TagName,
    string Name,
    Uri ReleasePageUri,
    DateTimeOffset? PublishedAt,
    GitHubReleaseAsset Installer,
    GitHubReleaseAsset Checksum);

public sealed record UpdateCheckResult(
    SemanticVersion CurrentVersion,
    GitHubUpdateRelease LatestRelease)
{
    public bool IsUpdateAvailable => LatestRelease.Version.CompareTo(CurrentVersion) > 0;
}

public sealed record VerifiedUpdateInstaller(string Path, string Sha256);

public interface IGitHubUpdateService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    Task<VerifiedUpdateInstaller> DownloadAndVerifyInstallerAsync(
        GitHubUpdateRelease release,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubUpdateService : IGitHubUpdateService
{
    private const string RepositoryMetadataKey = "GitHubRepository";
    private static readonly Regex ChecksumPattern = new(
        @"\A(?<hash>[0-9a-fA-F]{64})[ \t]+(?<name>[^\r\n]+)\z",
        RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly string _repositoryOwner;
    private readonly string _repositoryName;
    private readonly SemanticVersion _currentVersion;
    private readonly IAuthenticodeVerifier _authenticodeVerifier;
    private readonly UpdateDownloadLimits _limits;

    public GitHubUpdateService(HttpClient httpClient)
        : this(
            httpClient,
            ReadRepositorySlug(typeof(GitHubUpdateService).Assembly),
            ReadCurrentVersion(typeof(GitHubUpdateService).Assembly))
    {
    }

    public GitHubUpdateService(HttpClient httpClient, string repositorySlug)
        : this(
            httpClient,
            repositorySlug,
            ReadCurrentVersion(typeof(GitHubUpdateService).Assembly))
    {
    }

    public GitHubUpdateService(
        HttpClient httpClient,
        string repositorySlug,
        string currentVersion,
        IAuthenticodeVerifier? authenticodeVerifier = null,
        UpdateDownloadLimits? limits = null)
        : this(
            httpClient,
            repositorySlug,
            SemanticVersion.Parse(currentVersion),
            authenticodeVerifier,
            limits)
    {
    }

    public GitHubUpdateService(
        HttpClient httpClient,
        string repositorySlug,
        SemanticVersion currentVersion,
        IAuthenticodeVerifier? authenticodeVerifier = null,
        UpdateDownloadLimits? limits = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        (_repositoryOwner, _repositoryName) = ParseRepositorySlug(repositorySlug);
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _authenticodeVerifier = authenticodeVerifier ?? new WindowsAuthenticodeVerifier();
        _limits = limits ?? new UpdateDownloadLimits();
        if (_limits.ReleaseMetadataBytes <= 0
            || _limits.ChecksumBytes <= 0
            || _limits.InstallerBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "All update download limits must be positive.");
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(_repositoryOwner)}/{Uri.EscapeDataString(_repositoryName)}/releases/latest");
        var metadata = await DownloadBytesAsync(
            endpoint,
            expectedSize: null,
            _limits.ReleaseMetadataBytes,
            "RELEASE_METADATA_TOO_LARGE",
            cancellationToken);

        GitHubReleaseResponse response;
        try
        {
            response = JsonSerializer.Deserialize<GitHubReleaseResponse>(metadata)
                ?? throw new JsonException("The response was empty.");
        }
        catch (JsonException exception)
        {
            throw new ApplicationUpdateException(
                "INVALID_RELEASE_RESPONSE",
                "GitHub returned invalid release metadata.",
                exception);
        }

        if (response.Draft || response.Prerelease)
            throw new ApplicationUpdateException("UNSTABLE_RELEASE", "GitHub latest returned a draft or prerelease.");

        var tag = response.TagName?.Trim();
        var versionText = tag is { Length: > 1 } && tag[0] is 'v' or 'V' ? tag[1..] : tag;
        if (!SemanticVersion.TryParse(versionText, out var releaseVersion) || releaseVersion.IsPrerelease)
            throw new ApplicationUpdateException("INVALID_RELEASE_VERSION", "The latest stable release tag is not semantic versioned.");

        var expectedInstallerName = GetExpectedInstallerName(releaseVersion);
        var expectedChecksumName = expectedInstallerName + ".sha256";
        var installer = SelectExactAsset(response.Assets, expectedInstallerName);
        var checksum = SelectExactAsset(response.Assets, expectedChecksumName);
        var releasePageUri = ParseGitHubUri(response.HtmlUrl, "release page");

        return new UpdateCheckResult(
            _currentVersion,
            new GitHubUpdateRelease(
                releaseVersion,
                tag!,
                string.IsNullOrWhiteSpace(response.Name) ? tag! : response.Name.Trim(),
                releasePageUri,
                response.PublishedAt,
                installer,
                checksum));
    }

    public async Task<VerifiedUpdateInstaller> DownloadAndVerifyInstallerAsync(
        GitHubUpdateRelease release,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Version.IsPrerelease || release.Version.CompareTo(_currentVersion) <= 0)
            throw new ApplicationUpdateException("UPDATE_NOT_NEWER", "Only a newer stable release can be downloaded.");

        var expectedInstallerName = GetExpectedInstallerName(release.Version);
        if (!string.Equals(release.Installer.Name, expectedInstallerName, StringComparison.Ordinal)
            || !string.Equals(release.Checksum.Name, expectedInstallerName + ".sha256", StringComparison.Ordinal))
            throw new ApplicationUpdateException("INVALID_RELEASE_ASSETS", "The selected release assets do not match the expected win-x64 MSI.");
        _ = ParseGitHubAssetUri(release.Installer.DownloadUri.AbsoluteUri, expectedInstallerName);
        _ = ParseGitHubAssetUri(release.Checksum.DownloadUri.AbsoluteUri, expectedInstallerName + ".sha256");

        var checksumBytes = await DownloadBytesAsync(
            release.Checksum.DownloadUri,
            release.Checksum.SizeBytes,
            _limits.ChecksumBytes,
            "CHECKSUM_TOO_LARGE",
            cancellationToken);
        var expectedHash = ParseChecksum(checksumBytes, expectedInstallerName);

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destinationPath = Path.Combine(destinationRoot, expectedInstallerName);
        var temporaryPath = Path.Combine(destinationRoot, $".{expectedInstallerName}.{Guid.NewGuid():N}.download");
        try
        {
            await DownloadFileAsync(
                release.Installer,
                temporaryPath,
                _limits.InstallerBytes,
                cancellationToken);

            byte[] actualHash;
            await using (var installer = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = await SHA256.HashDataAsync(installer, cancellationToken);
            }

            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new ApplicationUpdateException("CHECKSUM_MISMATCH", "The downloaded MSI does not match its SHA-256 sidecar.");

            _authenticodeVerifier.VerifyTrusted(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new VerifiedUpdateInstaller(destinationPath, Convert.ToHexString(actualHash).ToLowerInvariant());
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task DownloadFileAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (asset.SizeBytes <= 0)
            throw new ApplicationUpdateException("INVALID_ASSET_SIZE", $"GitHub reported an invalid size for {asset.Name}.");
        if (asset.SizeBytes > maximumBytes)
            throw new ApplicationUpdateException("INSTALLER_TOO_LARGE", $"{asset.Name} exceeds the allowed download size.");

        using var response = await SendAsync(asset.DownloadUri, cancellationToken);
        ValidateContentLength(response, asset.SizeBytes, maximumBytes, "INSTALLER_TOO_LARGE");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var copied = await CopyBoundedAsync(source, destination, maximumBytes, "INSTALLER_TOO_LARGE", cancellationToken);
        if (copied != asset.SizeBytes)
            throw new ApplicationUpdateException("DOWNLOAD_SIZE_MISMATCH", $"{asset.Name} did not match the size reported by GitHub.");
    }

    private async Task<byte[]> DownloadBytesAsync(
        Uri uri,
        long? expectedSize,
        long maximumBytes,
        string tooLargeCode,
        CancellationToken cancellationToken)
    {
        if (expectedSize is <= 0)
            throw new ApplicationUpdateException("INVALID_ASSET_SIZE", "GitHub reported an invalid asset size.");
        if (expectedSize > maximumBytes)
            throw new ApplicationUpdateException(tooLargeCode, "The update response exceeds the allowed download size.");

        using var response = await SendAsync(uri, cancellationToken);
        ValidateContentLength(response, expectedSize, maximumBytes, tooLargeCode);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var copied = await CopyBoundedAsync(source, destination, maximumBytes, tooLargeCode, cancellationToken);
        if (expectedSize is not null && copied != expectedSize.Value)
            throw new ApplicationUpdateException("DOWNLOAD_SIZE_MISMATCH", "The update response size did not match GitHub metadata.");
        return destination.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", $"PyMonitor/{_currentVersion}");
        if (string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;

        var statusCode = response.StatusCode;
        response.Dispose();
        throw new ApplicationUpdateException(
            "UPDATE_HTTP_ERROR",
            $"GitHub update request failed with HTTP {(int)statusCode} ({statusCode}).");
    }

    private GitHubReleaseAsset SelectExactAsset(IReadOnlyList<GitHubAssetResponse>? assets, string expectedName)
    {
        var matches = assets?
            .Where(asset => string.Equals(asset.Name, expectedName, StringComparison.Ordinal))
            .ToArray() ?? [];
        if (matches.Length != 1)
            throw new ApplicationUpdateException(
                "RELEASE_ASSET_MISSING",
                $"The latest release must contain exactly one {expectedName} asset.");

        var match = matches[0];
        if (match.Size <= 0)
            throw new ApplicationUpdateException("INVALID_ASSET_SIZE", $"GitHub reported an invalid size for {expectedName}.");
        return new GitHubReleaseAsset(
            expectedName,
            ParseGitHubAssetUri(match.BrowserDownloadUrl, expectedName),
            match.Size);
    }

    private Uri ParseGitHubAssetUri(string? value, string expectedName)
    {
        var uri = ParseGitHubUri(value, expectedName);
        var releaseDownloadPrefix = $"/{_repositoryOwner}/{_repositoryName}/releases/download/";
        var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        if (!uri.AbsolutePath.StartsWith(releaseDownloadPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fileName, expectedName, StringComparison.Ordinal))
            throw new ApplicationUpdateException("INVALID_RELEASE_RESPONSE", $"GitHub returned an invalid {expectedName} URL.");
        return uri;
    }

    private Uri ParseGitHubUri(string? value, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new ApplicationUpdateException("INVALID_RELEASE_RESPONSE", $"GitHub returned an invalid {description} URL.");

        var expectedPathPrefix = $"/{_repositoryOwner}/{_repositoryName}/";
        if (!uri.AbsolutePath.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ApplicationUpdateException("INVALID_RELEASE_RESPONSE", $"The {description} URL belongs to another repository.");
        return uri;
    }

    private static byte[] ParseChecksum(byte[] contents, string expectedInstallerName)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(contents)
                .TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new ApplicationUpdateException("INVALID_CHECKSUM", "The SHA-256 sidecar is not valid UTF-8.", exception);
        }

        var match = ChecksumPattern.Match(text);
        if (!match.Success
            || !string.Equals(match.Groups["name"].Value, expectedInstallerName, StringComparison.Ordinal))
            throw new ApplicationUpdateException("INVALID_CHECKSUM", "The SHA-256 sidecar has an invalid hash or filename.");

        return Convert.FromHexString(match.Groups["hash"].Value);
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        string tooLargeCode,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return total;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new ApplicationUpdateException(tooLargeCode, "The update download exceeded its allowed size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateContentLength(
        HttpResponseMessage response,
        long? expectedSize,
        long maximumBytes,
        string tooLargeCode)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > maximumBytes)
            throw new ApplicationUpdateException(tooLargeCode, "The update response exceeds the allowed download size.");
        if (contentLength is not null && expectedSize is not null && contentLength != expectedSize)
            throw new ApplicationUpdateException("DOWNLOAD_SIZE_MISMATCH", "The HTTP content length did not match GitHub metadata.");
    }

    private static string GetExpectedInstallerName(SemanticVersion version) =>
        $"PyMonitor-{version}-win-x64.msi";

    private static (string Owner, string Name) ParseRepositorySlug(string repositorySlug)
    {
        if (string.IsNullOrWhiteSpace(repositorySlug)
            || !string.Equals(repositorySlug, repositorySlug.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("GitHub repository must use the owner/name format.", nameof(repositorySlug));

        var parts = repositorySlug.Split('/');
        if (parts.Length != 2
            || parts.Any(part => part.Length is 0 or > 100
                || part is "." or ".."
                || !part.All(IsRepositoryCharacter)))
            throw new ArgumentException("GitHub repository must use the owner/name format.", nameof(repositorySlug));
        return (parts[0], parts[1]);
    }

    private static bool IsRepositoryCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '-' or '_' or '.';

    private static string ReadRepositorySlug(Assembly assembly)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => string.Equals(attribute.Key, RepositoryMetadataKey, StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .SingleOrDefault();
        if (string.IsNullOrWhiteSpace(value))
            throw new ApplicationUpdateException(
                "REPOSITORY_NOT_CONFIGURED",
                $"Assembly metadata '{RepositoryMetadataKey}' is not configured.");
        return value;
    }

    private static string ReadCurrentVersion(Assembly assembly)
    {
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];
        if (string.IsNullOrWhiteSpace(value))
            throw new ApplicationUpdateException("VERSION_NOT_CONFIGURED", "The application version is unavailable.");
        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubAssetResponse>? Assets { get; init; }
    }

    private sealed class GitHubAssetResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}
