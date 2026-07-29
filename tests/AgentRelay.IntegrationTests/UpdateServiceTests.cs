using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRelay.Core;
using AgentRelay.Windows;

namespace AgentRelay.IntegrationTests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "AgentRelayUpdateTests_" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files = new();
    private readonly FixedClock _clock =
        new(DateTimeOffset.Parse("2026-07-29T08:00:00Z"));

    public UpdateServiceTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        Directory.CreateDirectory(_paths.HomeDirectory);
        Directory.CreateDirectory(_paths.LocalAppDataDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    [Fact]
    public async Task Settings_DefaultEnabled_AndSetPersistsAtomically()
    {
        var handler = new FakeHttpHandler();
        var service = CreateService(handler);

        var defaults = await service.GetSettingsAsync();
        var disabled = await service.SetEnabledAsync(false);
        var reloaded = await service.GetSettingsAsync();
        Assert.Equal(UpdateStatus.Disabled, (await service.GetStateAsync())?.Status);
        var enabledAgain = await service.SetEnabledAsync(true);

        Assert.True(defaults.Enabled);
        Assert.False(disabled.Enabled);
        Assert.Equal(disabled, reloaded);
        Assert.True(enabledAgain.Enabled);
        Assert.Null(await service.GetStateAsync());
        Assert.True(File.Exists(_paths.UpdateSettingsFile));
    }

    [Fact]
    public async Task CheckAsync_NewerStableRelease_StagesExactVerifiedInstaller()
    {
        var release = CreateRelease();
        var handler = CreateSuccessfulHandler(release);
        var service = CreateService(handler, currentVersion: "0.2.0+old");

        var state = await service.CheckAsync(force: true);

        Assert.Equal(UpdateStatus.Staged, state.Status);
        Assert.Equal("0.3.0", state.LatestVersion);
        Assert.NotNull(state.InstallerPath);
        Assert.True(File.Exists(state.InstallerPath));
        Assert.Equal(release.InstallerSha256, state.InstallerSha256);
        Assert.Equal(release.InstallerBytes.Length, new FileInfo(state.InstallerPath).Length);
        Assert.Equal(
            release.InstallerSha256,
            await AtomicFileStore.Sha256Async(state.InstallerPath));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task CheckAsync_StagedState_IsReusedWithoutNetwork()
    {
        var release = CreateRelease();
        var handler = CreateSuccessfulHandler(release);
        var service = CreateService(handler);
        var first = await service.CheckAsync(force: true);
        var requestsAfterFirst = handler.RequestCount;

        var second = await service.CheckAsync();

        Assert.Equal(first, second);
        Assert.Equal(requestsAfterFirst, handler.RequestCount);
    }

    [Fact]
    public async Task CheckAsync_AfterUpgrade_DoesNotReapplyOldStagedInstaller()
    {
        var release = CreateRelease();
        var handler = CreateSuccessfulHandler(release);
        var oldService = CreateService(handler, currentVersion: "0.2.0");
        Assert.Equal(UpdateStatus.Staged, (await oldService.CheckAsync(true)).Status);
        var requestsBeforeRestart = handler.RequestCount;

        var upgradedService = CreateService(handler, currentVersion: "0.3.0+new");
        var state = await upgradedService.CheckAsync();

        Assert.Equal(UpdateStatus.Current, state.Status);
        Assert.Equal("0.3.0", state.CurrentVersion);
        Assert.True(handler.RequestCount > requestsBeforeRestart);
    }

    [Fact]
    public async Task CheckAsync_MaliciousAssetHost_FailsClosedWithoutPackage()
    {
        var release = CreateRelease(installerUrl: "https://example.test/AgentRelaySetup-x64.exe");
        var handler = CreateSuccessfulHandler(release);
        var service = CreateService(handler);

        var state = await service.CheckAsync(force: true);

        Assert.Equal(UpdateStatus.Failed, state.Status);
        Assert.Contains("Untrusted release asset URL", state.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_paths.UpdatePackagesDirectory));
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_IsThrottledLocally()
    {
        var handler = new FakeHttpHandler();
        var service = CreateService(handler);
        var first = await service.CheckAsync(force: true);
        var requestsAfterFailure = handler.RequestCount;

        var second = await service.CheckAsync();

        Assert.Equal(UpdateStatus.Failed, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(requestsAfterFailure, handler.RequestCount);
    }

    [Fact]
    public async Task CheckAsync_CorruptInstaller_FailsClosedAndDeletesPartial()
    {
        var release = CreateRelease();
        var handler = CreateSuccessfulHandler(
            release,
            installerOverride: Enumerable.Repeat((byte)0x7F, release.InstallerBytes.Length).ToArray());
        var service = CreateService(handler);

        var state = await service.CheckAsync(force: true);

        Assert.Equal(UpdateStatus.Failed, state.Status);
        Assert.Contains("SHA-256 mismatch", state.Detail, StringComparison.Ordinal);
        Assert.Empty(
            Directory.Exists(_paths.UpdatePackagesDirectory)
                ? Directory.GetFiles(_paths.UpdatePackagesDirectory, "*.partial", SearchOption.AllDirectories)
                : []);
    }

    [Fact]
    public async Task DisabledCheck_DoesNotContactGitHub()
    {
        var handler = new FakeHttpHandler();
        var service = CreateService(handler);
        await service.SetEnabledAsync(false);

        var state = await service.CheckAsync(force: true);

        Assert.Equal(UpdateStatus.Disabled, state.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LaunchInstallerAsync_RechecksHashAndUsesSilentPerUserArguments()
    {
        var release = CreateRelease();
        var handler = CreateSuccessfulHandler(release);
        var launcher = new RecordingLauncher();
        var service = CreateService(handler, launcher: launcher);
        var state = await service.CheckAsync(force: true);

        await service.LaunchInstallerAsync(state);

        Assert.Equal(state.InstallerPath, launcher.InstallerPath);
        Assert.Contains("/VERYSILENT", launcher.Arguments);
        Assert.Contains("/AUTOUPDATE=1", launcher.Arguments);
        Assert.DoesNotContain("/SILENT", launcher.Arguments);
        Assert.Equal(UpdateStatus.Installing, (await service.GetStateAsync())?.Status);
    }

    [Fact]
    public async Task LaunchInstallerAsync_ModifiedStagedInstaller_IsRejected()
    {
        var release = CreateRelease();
        var handler = CreateSuccessfulHandler(release);
        var launcher = new RecordingLauncher();
        var service = CreateService(handler, launcher: launcher);
        var state = await service.CheckAsync(force: true);
        await File.AppendAllTextAsync(state.InstallerPath!, "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.LaunchInstallerAsync(state));
        Assert.Null(launcher.InstallerPath);
    }

    [Fact]
    public async Task LaunchInstallerAsync_StartFailure_IsPersistedAndCanRetry()
    {
        var release = CreateRelease();
        var handler = CreateSuccessfulHandler(release);
        var launcher = new ThrowingLauncher();
        var service = CreateService(handler, launcher: launcher);
        var state = await service.CheckAsync(force: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LaunchInstallerAsync(state));

        var failed = await service.GetStateAsync();
        Assert.Equal(UpdateStatus.Failed, failed?.Status);
        Assert.Contains("installer", failed?.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private UpdateService CreateService(
        FakeHttpHandler handler,
        string currentVersion = "0.2.0",
        IUpdateInstallerLauncher? launcher = null)
        => new(
            _paths,
            _files,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) },
            currentVersion,
            launcher,
            _clock);

    private static FakeHttpHandler CreateSuccessfulHandler(
        ReleaseFixture release,
        byte[]? installerOverride = null)
    {
        var handler = new FakeHttpHandler();
        handler.AddJson(UpdateService.LatestReleaseApi, release.ReleaseJson);
        handler.AddBytes(new Uri(release.ChecksumUrl), release.ChecksumBytes);
        handler.AddBytes(new Uri(release.InstallerUrl), installerOverride ?? release.InstallerBytes);
        return handler;
    }

    private static ReleaseFixture CreateRelease(
        string installerUrl =
            "https://github.com/korzhy/agent-relay/releases/download/v0.3.0/AgentRelaySetup-x64.exe")
    {
        var installer = new byte[1024 * 1024];
        for (var index = 0; index < installer.Length; index++)
        {
            installer[index] = (byte)(index % 251);
        }
        var installerHash = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
        var checksumUrl =
            "https://github.com/korzhy/agent-relay/releases/download/v0.3.0/" +
            "AgentRelaySetup-x64.exe.sha256";
        var checksum = Encoding.ASCII.GetBytes(
            $"{installerHash}  AgentRelaySetup-x64.exe{Environment.NewLine}");
        var checksumHash = Convert.ToHexString(SHA256.HashData(checksum)).ToLowerInvariant();
        var releaseJson = JsonSerializer.Serialize(
            new
            {
                tag_name = "v0.3.0",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = UpdateService.InstallerAssetName,
                        browser_download_url = installerUrl,
                        size = installer.Length,
                        digest = $"sha256:{installerHash}"
                    },
                    new
                    {
                        name = UpdateService.ChecksumAssetName,
                        browser_download_url = checksumUrl,
                        size = checksum.Length,
                        digest = $"sha256:{checksumHash}"
                    }
                }
            },
            JsonSupport.Options);
        return new ReleaseFixture(
            installer,
            installerHash,
            installerUrl,
            checksum,
            checksumUrl,
            releaseJson);
    }

    private sealed record ReleaseFixture(
        byte[] InstallerBytes,
        string InstallerSha256,
        string InstallerUrl,
        byte[] ChecksumBytes,
        string ChecksumUrl,
        string ReleaseJson);

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Func<HttpResponseMessage>> _responses = new();

        public int RequestCount { get; private set; }

        public void AddJson(Uri uri, string json)
            => _responses[uri] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        public void AddBytes(Uri uri, byte[] bytes)
            => _responses[uri] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri is not null &&
                _responses.TryGetValue(request.RequestUri, out var response))
            {
                var result = response();
                result.RequestMessage = request;
                return Task.FromResult(result);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request
            });
        }
    }

    private sealed class RecordingLauncher : IUpdateInstallerLauncher
    {
        public string? InstallerPath { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public void Launch(string installerPath, IReadOnlyList<string> arguments)
        {
            InstallerPath = installerPath;
            Arguments = arguments.ToArray();
        }
    }

    private sealed class ThrowingLauncher : IUpdateInstallerLauncher
    {
        public void Launch(string installerPath, IReadOnlyList<string> arguments)
            => throw new InvalidOperationException("simulated start failure");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
