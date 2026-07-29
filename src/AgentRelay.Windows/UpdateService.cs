using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public interface IUpdateInstallerLauncher
{
    void Launch(string installerPath, IReadOnlyList<string> arguments);
}

public sealed class ProcessUpdateInstallerLauncher : IUpdateInstallerLauncher
{
    public void Launch(string installerPath, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)
                               ?? throw new InvalidOperationException("Installer has no parent directory.")
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("The Agent Relay update installer did not start.");
        }
    }
}

public sealed class UpdateService
{
    public const string Repository = "korzhy/agent-relay";
    public const string InstallerAssetName = "AgentRelaySetup-x64.exe";
    public const string ChecksumAssetName = "AgentRelaySetup-x64.exe.sha256";
    public static readonly Uri LatestReleaseApi =
        new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly TimeSpan AutoApplyInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FailedCheckRetryInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan InstallationReservationLifetime = TimeSpan.FromMinutes(15);
    private const long MinimumInstallerBytes = 1 * 1024 * 1024;
    private const long MaximumInstallerBytes = 200 * 1024 * 1024;
    private const int MaximumChecksumBytes = 4096;
    private const int MaximumReleaseDocumentBytes = 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files;
    private readonly HttpClient _http;
    private readonly IUpdateInstallerLauncher _launcher;
    private readonly IClock _clock;
    private readonly ReleaseVersion _currentVersion;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public UpdateService(
        AppPaths paths,
        AtomicFileStore files,
        HttpClient http,
        string currentVersion,
        IUpdateInstallerLauncher? launcher = null,
        IClock? clock = null)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out _currentVersion))
        {
            throw new ArgumentException($"Invalid current version: {currentVersion}", nameof(currentVersion));
        }
        _paths = paths;
        _files = files;
        _http = http;
        _launcher = launcher ?? new ProcessUpdateInstallerLauncher();
        _clock = clock ?? new SystemClock();
    }

    public string CurrentVersion => _currentVersion.ToString();

    public async Task<UpdateSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await _files.ReadJsonAsync<UpdateSettings>(
            _paths.UpdateSettingsFile, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            settings = UpdateSettings.CreateDefault(_clock);
            await _files.WriteJsonAsync(
                _paths.UpdateSettingsFile, settings, false, cancellationToken).ConfigureAwait(false);
        }
        settings.Validate();
        return settings;
    }

    public async Task<UpdateSettings> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var current = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var updated = current with { Enabled = enabled, UpdatedAt = _clock.UtcNow };
        updated.Validate();
        await _files.WriteJsonAsync(
            _paths.UpdateSettingsFile, updated, false, cancellationToken).ConfigureAwait(false);
        if (enabled && !current.Enabled)
        {
            if (File.Exists(_paths.UpdateStateFile))
            {
                File.Delete(_paths.UpdateStateFile);
            }
        }
        else if (!enabled)
        {
            var existing = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            await WriteStateAsync(
                new UpdateState(
                    UpdateState.CurrentSchemaVersion,
                    UpdateStatus.Disabled,
                    CurrentVersion,
                    existing?.LatestVersion,
                    _clock.UtcNow,
                    "Автообновление выключено."),
                cancellationToken).ConfigureAwait(false);
        }
        return updated;
    }

    public async Task<UpdateState?> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await _files.ReadJsonAsync<UpdateState>(
            _paths.UpdateStateFile, cancellationToken).ConfigureAwait(false);
        state?.Validate();
        return state;
    }

    public async Task<UpdateState> CheckAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            var existing = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Enabled)
            {
                return await WriteStateAsync(
                    new UpdateState(
                        UpdateState.CurrentSchemaVersion,
                        UpdateStatus.Disabled,
                        CurrentVersion,
                        existing?.LatestVersion,
                        _clock.UtcNow,
                        "Автообновление выключено."),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!force && existing is not null)
            {
                var existingTargetsNewerVersion =
                    ReleaseVersion.TryParse(existing.LatestVersion, out var existingLatest) &&
                    existingLatest.CompareTo(_currentVersion) > 0;
                if (existing.Status is UpdateStatus.Staged or UpdateStatus.Deferred &&
                    existingTargetsNewerVersion &&
                    await IsValidStagedStateAsync(existing, cancellationToken).ConfigureAwait(false))
                {
                    return existing;
                }
                var retryAfter = existing.Status switch
                {
                    UpdateStatus.Failed => FailedCheckRetryInterval,
                    UpdateStatus.Installing => InstallationReservationLifetime,
                    _ => TimeSpan.FromHours(settings.CheckIntervalHours)
                };
                if (string.Equals(existing.CurrentVersion, CurrentVersion, StringComparison.Ordinal) &&
                    _clock.UtcNow - existing.CheckedAt < retryAfter)
                {
                    return existing;
                }
            }

            try
            {
                return await CheckRemoteAndStageAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidDataException or
                    JsonException or UnauthorizedAccessException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                return await WriteStateAsync(
                    new UpdateState(
                        UpdateState.CurrentSchemaVersion,
                        UpdateStatus.Failed,
                        CurrentVersion,
                        existing?.LatestVersion,
                        _clock.UtcNow,
                        $"Проверка обновления не удалась: {exception.Message}"),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<UpdateState> MarkDeferredAsync(
        UpdateState staged,
        string detail,
        CancellationToken cancellationToken = default)
    {
        if (staged.Status is not (UpdateStatus.Staged or UpdateStatus.Deferred) ||
            string.IsNullOrWhiteSpace(staged.InstallerPath) ||
            string.IsNullOrWhiteSpace(staged.InstallerSha256))
        {
            throw new InvalidOperationException("Only a staged update can be deferred.");
        }
        return await WriteStateAsync(
            staged with
            {
                Status = UpdateStatus.Deferred,
                CheckedAt = _clock.UtcNow,
                Detail = detail
            },
            cancellationToken).ConfigureAwait(false);
    }

    public bool IsInstalledBuild(string executablePath)
        => string.Equals(
            Path.GetFullPath(executablePath),
            Path.GetFullPath(_paths.InstalledExecutable),
            StringComparison.OrdinalIgnoreCase);

    public async Task LaunchInstallerAsync(
        UpdateState state,
        CancellationToken cancellationToken = default)
    {
        if (state.Status is not (UpdateStatus.Staged or UpdateStatus.Deferred) ||
            !await IsValidStagedStateAsync(state, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The staged Agent Relay update is missing or has an invalid hash.");
        }
        if (!ReleaseVersion.TryParse(state.LatestVersion, out var latest) ||
            latest.CompareTo(_currentVersion) <= 0)
        {
            throw new InvalidDataException("Update version must be newer than the installed version.");
        }

        var installing = await WriteStateAsync(
            state with
            {
                Status = UpdateStatus.Installing,
                CheckedAt = _clock.UtcNow,
                Detail = $"Запущена установка Agent Relay {state.LatestVersion}."
            },
            cancellationToken).ConfigureAwait(false);
        try
        {
            _launcher.Launch(
                installing.InstallerPath!,
                [
                    "/VERYSILENT",
                    "/SUPPRESSMSGBOXES",
                    "/NORESTART",
                    "/CLOSEAPPLICATIONS",
                    "/AUTOUPDATE=1"
                ]);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await WriteStateAsync(
                installing with
                {
                    Status = UpdateStatus.Failed,
                    CheckedAt = _clock.UtcNow,
                    Detail = $"Не удалось запустить installer: {exception.Message}"
                },
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<UpdateState> CheckRemoteAndStageAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(LatestReleaseApi);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long releaseLength &&
            releaseLength > MaximumReleaseDocumentBytes)
        {
            throw new InvalidDataException("GitHub release document exceeds the size limit.");
        }
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var releaseBytes = await ReadLimitedAsync(
            responseStream, MaximumReleaseDocumentBytes, cancellationToken).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubRelease>(releaseBytes, JsonSupport.Options)
                      ?? throw new InvalidDataException("GitHub returned an empty release document.");
        if (release.Draft || release.Prerelease || release.Assets is null)
        {
            throw new InvalidDataException("GitHub latest release is not stable.");
        }
        if (!ReleaseVersion.TryParse(release.TagName, out var latest) ||
            !string.Equals(release.TagName, $"v{latest}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid stable release tag: {release.TagName}");
        }
        if (latest.CompareTo(_currentVersion) <= 0)
        {
            CleanupOldPackages(_currentVersion);
            return await WriteStateAsync(
                new UpdateState(
                    UpdateState.CurrentSchemaVersion,
                    UpdateStatus.Current,
                    CurrentVersion,
                    latest.ToString(),
                    _clock.UtcNow,
                    "Установлена актуальная стабильная версия."),
                cancellationToken).ConfigureAwait(false);
        }

        var installer = RequireAsset(release.Assets, InstallerAssetName, latest);
        var checksum = RequireAsset(release.Assets, ChecksumAssetName, latest);
        if (installer.Size is < MinimumInstallerBytes or > MaximumInstallerBytes)
        {
            throw new InvalidDataException($"Unexpected installer size: {installer.Size} bytes.");
        }

        var checksumBytes = await DownloadSmallAssetAsync(
            checksum, MaximumChecksumBytes, cancellationToken).ConfigureAwait(false);
        var expectedHash = ParseChecksum(checksumBytes);
        ValidateAssetDigest(installer, expectedHash);

        var versionDirectory = Path.Combine(_paths.UpdatePackagesDirectory, latest.ToString());
        Directory.CreateDirectory(versionDirectory);
        var installerPath = Path.Combine(versionDirectory, InstallerAssetName);
        if (!await FileMatchesAsync(
                installerPath, expectedHash, installer.Size, cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }
            await DownloadInstallerAsync(
                installer, installerPath, expectedHash, cancellationToken).ConfigureAwait(false);
        }
        CleanupOldPackages(latest);

        return await WriteStateAsync(
            new UpdateState(
                UpdateState.CurrentSchemaVersion,
                UpdateStatus.Staged,
                CurrentVersion,
                latest.ToString(),
                _clock.UtcNow,
                $"Agent Relay {latest} загружен и проверен по SHA-256.",
                installerPath,
                expectedHash),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> DownloadSmallAssetAsync(
        GitHubAsset asset,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(new Uri(asset.BrowserDownloadUrl));
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateFinalDownloadUri(response.RequestMessage?.RequestUri);
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new InvalidDataException($"Update metadata exceeds {maximumBytes} bytes.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var bytes = await ReadLimitedAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
        ValidateDownloadedDigest(asset, bytes);
        return bytes;
    }

    private async Task DownloadInstallerAsync(
        GitHubAsset asset,
        string destination,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var partial = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            using var request = CreateRequest(new Uri(asset.BrowserDownloadUrl));
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateFinalDownloadUri(response.RequestMessage?.RequestUri);
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength != asset.Size)
            {
                throw new InvalidDataException(
                    $"Installer length mismatch: release={asset.Size}, response={contentLength}.");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var output = new FileStream(
                             partial,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaximumInstallerBytes || total > asset.Size)
                    {
                        throw new InvalidDataException("Installer download exceeded the declared size.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (total != asset.Size)
                {
                    throw new InvalidDataException(
                        $"Installer length mismatch: release={asset.Size}, downloaded={total}.");
                }
            }

            var actualHash = await AtomicFileStore.Sha256Async(partial, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Installer SHA-256 mismatch: expected {expectedHash}, actual {actualHash}.");
            }
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private async Task<bool> IsValidStagedStateAsync(
        UpdateState state,
        CancellationToken cancellationToken)
    {
        if (state.InstallerPath is null || state.InstallerSha256 is null ||
            !IsPackagePath(state.InstallerPath))
        {
            return false;
        }
        return await FileMatchesAsync(
            state.InstallerPath, state.InstallerSha256, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> FileMatchesAsync(
        string path,
        string expectedHash,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) ||
            (expectedLength is not null && new FileInfo(path).Length != expectedLength.Value))
        {
            return false;
        }
        var actual = await AtomicFileStore.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPackagePath(string path)
    {
        var root = Path.GetFullPath(_paths.UpdatePackagesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileName(candidate), InstallerAssetName, StringComparison.Ordinal);
    }

    private void CleanupOldPackages(ReleaseVersion keep)
    {
        if (!Directory.Exists(_paths.UpdatePackagesDirectory))
        {
            return;
        }
        var root = Path.GetFullPath(_paths.UpdatePackagesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(_paths.UpdatePackagesDirectory))
        {
            var candidate = Path.GetFullPath(directory);
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(candidate), keep.ToString(), StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                Directory.Delete(candidate, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A just-finished installer may still hold its package. Retry on the next check.
            }
        }
    }

    private async Task<UpdateState> WriteStateAsync(
        UpdateState state,
        CancellationToken cancellationToken)
    {
        state.Validate();
        await _files.WriteJsonAsync(
            _paths.UpdateStateFile, state, false, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"AgentRelay/{CurrentVersion}");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static GitHubAsset RequireAsset(
        IReadOnlyList<GitHubAsset> assets,
        string name,
        ReleaseVersion version)
    {
        var matches = assets.Where(asset => string.Equals(asset.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"Release must contain exactly one {name} asset.");
        }
        var asset = matches[0];
        var uri = new Uri(asset.BrowserDownloadUrl);
        var expectedPath = $"/{Repository}/releases/download/v{version}/{name}";
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Untrusted release asset URL: {uri}");
        }
        return asset;
    }

    private static string ParseChecksum(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes).Trim();
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            parts[0].Length != 64 ||
            !parts[0].All(Uri.IsHexDigit) ||
            !string.Equals(parts[1].TrimStart('*'), InstallerAssetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Release checksum file has an invalid format.");
        }
        return parts[0].ToLowerInvariant();
    }

    private static void ValidateAssetDigest(GitHubAsset asset, string expectedHash)
    {
        if (!TryReadSha256Digest(asset.Digest, out var digest) ||
            !string.Equals(digest, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"GitHub digest is missing or invalid for {asset.Name}.");
        }
    }

    private static void ValidateDownloadedDigest(GitHubAsset asset, byte[] bytes)
    {
        if (!TryReadSha256Digest(asset.Digest, out var expected))
        {
            throw new InvalidDataException($"GitHub digest is missing or invalid for {asset.Name}.");
        }
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Downloaded digest mismatch for {asset.Name}.");
        }
    }

    private static bool TryReadSha256Digest(string? value, out string digest)
    {
        digest = string.Empty;
        const string prefix = "sha256:";
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var candidate = value[prefix.Length..];
        if (candidate.Length != 64 || !candidate.All(Uri.IsHexDigit))
        {
            return false;
        }
        digest = candidate.ToLowerInvariant();
        return true;
    }

    private static void ValidateFinalDownloadUri(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
            (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Host, "release-assets.githubusercontent.com",
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Release download redirected to an untrusted host: {uri}");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"Update metadata exceeds {maximumBytes} bytes.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        bool Draft,
        bool Prerelease,
        IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        string Name,
        [property: JsonPropertyName("browser_download_url")]
        string BrowserDownloadUrl,
        long Size,
        string? Digest);
}
