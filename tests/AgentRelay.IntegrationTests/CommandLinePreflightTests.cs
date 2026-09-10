using AgentRelay.App;
using AgentRelay.Core;
using AgentRelay.Windows;

namespace AgentRelay.IntegrationTests;

public sealed class CommandLinePreflightTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "AgentRelayCliPreflight_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    [Fact]
    public async Task Publish_OffThenUntrusted_NeverCreatesRepositoryTransport()
    {
        var home = Path.Combine(_root, "home");
        var local = Path.Combine(_root, "local");
        var workspace = Path.Combine(_root, "workspace");
        var skill = Path.Combine(_root, "skill");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(skill);
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), "---\nname: test\n---\n");
        var task = Path.Combine(_root, "task.md");
        await File.WriteAllTextAsync(task, "fake-mode:pass");
        var services = RelayServices.Create(
            new AppPaths(home, local),
            skill,
            new NoOpClipboard(),
            new FixedClock(DateTimeOffset.Parse("2026-07-28T08:00:00Z")));

        await services.Policy.SetLevelAsync(services.Paths.CodexPolicyFile, DelegationLevel.Off);
        Directory.CreateDirectory(Path.Combine(workspace, ".codex"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".codex", "external-agent-delegation.json"),
            """
            {
              "schemaVersion": 1,
              "enabled": true,
              "level": "high"
            }
            """);
        var offExit = await CommandLine.RunAsync(
            services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--no-trust-prompt"]);

        Assert.Equal(6, offExit);
        Assert.Empty(await services.Projects.ListAsync());
        Assert.False(Directory.Exists(Path.Combine(workspace, AgentRelayConstants.TransportDirectory)));

        await services.Policy.SetLevelAsync(services.Paths.CodexPolicyFile, DelegationLevel.High);
        var trustExit = await CommandLine.RunAsync(
            services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--no-trust-prompt"]);

        Assert.Equal(5, trustExit);
        var registered = Assert.Single(await services.Projects.ListAsync());
        Assert.Null(registered.TrustedAt);
        Assert.False(Directory.Exists(Path.Combine(workspace, AgentRelayConstants.TransportDirectory)));
    }

    [Fact]
    public async Task Publish_HighTrusted_ExecutesFakeWithoutGuiAndMovesSolToReviewing()
    {
        var home = Path.Combine(_root, "home-high");
        var local = Path.Combine(_root, "local-high");
        var workspace = Path.Combine(_root, "workspace-high");
        var skill = Path.Combine(_root, "skill-high");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(skill);
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), "---\nname: test\n---\n");
        var agyDirectory = Path.Combine(local, "agy", "bin");
        Directory.CreateDirectory(agyDirectory);
        foreach (var file in Directory.GetFiles(GetFakeAgyDirectory()))
        {
            File.Copy(file, Path.Combine(agyDirectory, Path.GetFileName(file)));
        }
        var task = Path.Combine(_root, "task-high.md");
        await File.WriteAllTextAsync(task, "fake-mode:pass");
        var clipboard = new RecordingClipboard();
        var credentials = new FakeCredentialStore();
        var services = RelayServices.Create(
            new AppPaths(home, local),
            skill,
            clipboard,
            new FixedClock(DateTimeOffset.Parse("2026-07-28T08:00:00Z")),
            credentialStore: credentials,
            agyAccountClient: new FakeAgyAccountClient(credentials));
        await services.Policy.SetLevelAsync(services.Paths.CodexPolicyFile, DelegationLevel.High);
        await services.Accounts.AddAsync("Test Pro", services.Doctor.ResolveAgyPath(), true);
        var project = await services.Projects.AddAsync(workspace);
        await services.Projects.TrustAsync(project.Id);

        var exit = await CommandLine.RunAsync(
            services,
            [
                "handoff", "publish", "--project", workspace, "--task", task,
                "--title", "Fake autonomous hand-off", "--gate", "gate-1", "--no-trust-prompt"
            ]);

        Assert.Equal(0, exit);
        var state = await services.Runtime.ReadAsync(project.Id);
        Assert.Equal(RelayState.ReportReady, state?.State);
        Assert.Equal(SolActivityPhase.Reviewing, (await services.Activity.GetAsync(project.Id))?.Phase);
        Assert.Equal(1, clipboard.WriteCount);
        Assert.True(Directory.Exists(Path.Combine(workspace, AgentRelayConstants.TransportDirectory)));
    }

    [Fact]
    public async Task Publish_WithoutManagedAccount_ReturnsAccountRequiredWithoutHandoff()
    {
        var home = Path.Combine(_root, "home-account-required");
        var local = Path.Combine(_root, "local-account-required");
        var workspace = Path.Combine(_root, "workspace-account-required");
        var skill = Path.Combine(_root, "skill-account-required");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(skill);
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), "---\nname: test\n---\n");
        var task = Path.Combine(_root, "task-account-required.md");
        await File.WriteAllTextAsync(task, "fake-mode:pass");
        var credentials = new FakeCredentialStore();
        var services = RelayServices.Create(
            new AppPaths(home, local), skill, new NoOpClipboard(),
            new FixedClock(DateTimeOffset.Parse("2026-09-06T08:00:00Z")),
            credentialStore: credentials,
            agyAccountClient: new FakeAgyAccountClient(credentials));
        await services.Policy.SetLevelAsync(services.Paths.CodexPolicyFile, DelegationLevel.High);
        var project = await services.Projects.AddAsync(workspace);
        await services.Projects.TrustAsync(project.Id);

        var exit = await CommandLine.RunAsync(services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--no-trust-prompt"]);

        Assert.Equal(9, exit);
        Assert.False(Directory.Exists(Path.Combine(workspace, AgentRelayConstants.TransportDirectory)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("invalid")]
    public async Task InvalidTimeoutFailsBeforeAccountPreflightAndTransport(string timeout)
    {
        var workspace = Path.Combine(_root, "timeout-repo");
        Directory.CreateDirectory(workspace);
        var paths = new AppPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        var services = RelayServices.Create(paths, Path.Combine(_root, "skill"), new NoOpClipboard());
        await services.Policy.SetLevelAsync(paths.CodexPolicyFile, DelegationLevel.Medium);
        var project = await services.Projects.AddAsync(workspace);
        await services.Projects.TrustAsync(project.Id);
        var task = Path.Combine(_root, "task.md");
        await File.WriteAllTextAsync(task, "instructions");
        await Assert.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--timeout-minutes", timeout]));
        Assert.False(Directory.Exists(Path.Combine(workspace, ".agent-relay")));
    }

    [Fact]
    public async Task RecallUnknownProjectIsReadOnlyAndDoesNotRegisterOrTrust()
    {
        var workspace = Path.Combine(_root, "unknown");
        Directory.CreateDirectory(workspace);
        var paths = new AppPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        var services = RelayServices.Create(paths, Path.Combine(_root, "skill"), new NoOpClipboard());
        Assert.Equal(0, await CommandLine.RunAsync(services,
            ["experience", "recall", "--project", workspace, "--kind", "implementation"]));
        Assert.Empty(await services.Projects.ListAsync());
        Assert.False(Directory.Exists(Path.Combine(workspace, ".agent-relay")));
    }

    [Fact]
    public async Task Publish_WhenGlobalRunnerBusy_ReturnsElevenWithoutHandoff()
    {
        var home = Path.Combine(_root, "home-busy");
        var local = Path.Combine(_root, "local-busy");
        var workspace = Path.Combine(_root, "workspace-busy");
        var skill = Path.Combine(_root, "skill-busy");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(skill);
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), "---\nname: test\n---\n");
        var task = Path.Combine(_root, "task-busy.md");
        await File.WriteAllTextAsync(task, "fake-mode:pass");
        var credentials = new FakeCredentialStore();
        var services = RelayServices.Create(
            new AppPaths(home, local), skill, new NoOpClipboard(),
            new FixedClock(DateTimeOffset.Parse("2026-09-06T08:00:00Z")),
            credentialStore: credentials,
            agyAccountClient: new FakeAgyAccountClient(credentials));
        await services.Policy.SetLevelAsync(services.Paths.CodexPolicyFile, DelegationLevel.High);
        await services.Accounts.AddAsync("Pro", services.Doctor.ResolveAgyPath(), true);
        var project = await services.Projects.AddAsync(workspace);
        await services.Projects.TrustAsync(project.Id);
        using var lease = GlobalAgyLease.TryAcquire();
        Assert.NotNull(lease);

        var exit = await CommandLine.RunAsync(services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--no-trust-prompt"]);

        Assert.Equal(11, exit);
        Assert.False(Directory.Exists(Path.Combine(workspace, AgentRelayConstants.TransportDirectory)));
    }

    [Fact]
    public async Task CancelThenPublish_IsRejectedUntilResumeWithoutCreatingOrphanHandoff()
    {
        var home = Path.Combine(_root, "home-pause");
        var local = Path.Combine(_root, "local-pause");
        var workspace = Path.Combine(_root, "workspace-pause");
        var skill = Path.Combine(_root, "skill-pause");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(skill);
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), "---\nname: test\n---\n");
        var agyDirectory = Path.Combine(local, "agy", "bin");
        Directory.CreateDirectory(agyDirectory);
        foreach (var file in Directory.GetFiles(GetFakeAgyDirectory()))
        {
            File.Copy(file, Path.Combine(agyDirectory, Path.GetFileName(file)));
        }
        var task = Path.Combine(_root, "task-pause.md");
        await File.WriteAllTextAsync(task, "fake-mode:pass");
        var credentials = new FakeCredentialStore();
        var services = RelayServices.Create(
            new AppPaths(home, local),
            skill,
            new NoOpClipboard(),
            new FixedClock(DateTimeOffset.Parse("2026-07-28T08:00:00Z")),
            credentialStore: credentials,
            agyAccountClient: new FakeAgyAccountClient(credentials));
        await services.Policy.SetLevelAsync(services.Paths.CodexPolicyFile, DelegationLevel.High);
        await services.Accounts.AddAsync("Test Pro", services.Doctor.ResolveAgyPath(), true);
        var project = await services.Projects.AddAsync(workspace);
        project = await services.Projects.TrustAsync(project.Id);
        var oldHandoff = await services.Protocol.PublishAsync(
            workspace, new MissionRequest("Old stalled handoff", "Instructions", ["gate-1"]));
        await services.Runtime.WriteAsync(new ProjectRuntimeState(
            1,
            project.Id,
            RelayState.Stalled,
            oldHandoff.Control.HandoffId,
            oldHandoff.Control.MissionId,
            oldHandoff.Control.Revision,
            oldHandoff.Control.RunAttemptId,
            null,
            DateTimeOffset.Parse("2026-07-28T08:00:00Z"),
            "Old runner stalled.",
            oldHandoff.ControlHash,
            null));

        Assert.Equal(0, await CommandLine.RunAsync(
            services, ["handoff", "cancel", "--project", workspace]));
        var controlPath = Path.Combine(
            workspace, AgentRelayConstants.TransportDirectory, "control.json");
        var controlBeforeRejectedPublish = await services.Files.ReadJsonAsync<ControlEnvelope>(controlPath);

        var pausedExit = await CommandLine.RunAsync(
            services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--no-trust-prompt"]);

        Assert.Equal(7, pausedExit);
        var controlAfterRejectedPublish = await services.Files.ReadJsonAsync<ControlEnvelope>(controlPath);
        Assert.Equal(controlBeforeRejectedPublish, controlAfterRejectedPublish);
        Assert.True(services.Runtime.IsPaused(project.Id));

        Assert.Equal(0, await CommandLine.RunAsync(
            services, ["handoff", "resume", "--project", workspace]));
        var resumed = await services.Runtime.ReadAsync(project.Id);
        Assert.Equal(RelayState.Ready, resumed?.State);
        Assert.Null(resumed?.HandoffId);

        var replacementExit = await CommandLine.RunAsync(
            services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--no-trust-prompt"]);
        Assert.Equal(0, replacementExit);
        Assert.Equal(RelayState.ReportReady, (await services.Runtime.ReadAsync(project.Id))?.State);
    }

    [Fact]
    public async Task QuotaFailure_RotatesAccountAndPublishesNewMissionRevision()
    {
        var home = Path.Combine(_root, "home-retry");
        var local = Path.Combine(_root, "local-retry");
        var workspace = Path.Combine(_root, "workspace-retry");
        var skill = Path.Combine(_root, "skill-retry");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(skill);
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), "---\nname: test\n---\n");
        var agyDirectory = Path.Combine(local, "agy", "bin");
        Directory.CreateDirectory(agyDirectory);
        foreach (var file in Directory.GetFiles(GetFakeAgyDirectory()))
        {
            File.Copy(file, Path.Combine(agyDirectory, Path.GetFileName(file)));
        }
        var task = Path.Combine(_root, "task-retry.md");
        await File.WriteAllTextAsync(task, "fake-mode:quota_then_pass");
        var credentials = new FakeCredentialStore();
        var agy = new FakeAgyAccountClient(credentials);
        var services = RelayServices.Create(
            new AppPaths(home, local), skill, new NoOpClipboard(),
            new FixedClock(DateTimeOffset.Parse("2026-09-06T08:00:00Z")),
            credentialStore: credentials, agyAccountClient: agy);
        await services.Policy.SetLevelAsync(services.Paths.CodexPolicyFile, DelegationLevel.High);
        await services.Accounts.AddAsync("First", services.Doctor.ResolveAgyPath(), true);
        await services.Accounts.AddAsync("Second", services.Doctor.ResolveAgyPath(), false);
        var project = await services.Projects.AddAsync(workspace);
        await services.Projects.TrustAsync(project.Id);

        var exit = await CommandLine.RunAsync(services,
            ["handoff", "publish", "--project", workspace, "--task", task, "--no-trust-prompt"]);

        Assert.Equal(0, exit);
        var control = await services.Files.ReadJsonAsync<ControlEnvelope>(Path.Combine(
            workspace, AgentRelayConstants.TransportDirectory, "control.json"));
        Assert.Equal(2, control?.Revision);
        Assert.NotNull(control?.ParentHandoffId);
        Assert.Equal(RelayState.ReportReady, (await services.Runtime.ReadAsync(project.Id))?.State);
        Assert.All((await services.Accounts.ListAsync()).Accounts,
            account => Assert.NotNull(account.LastUsedAt));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class NoOpClipboard : IClipboardWriter
    {
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingClipboard : IClipboardWriter
    {
        public int WriteCount { get; private set; }

        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public byte[]? Read(string target) => _values.TryGetValue(target, out var value) ? value.ToArray() : null;
        public void Write(string target, byte[] credential) => _values[target] = credential.ToArray();
        public void Delete(string target) => _values.Remove(target);
    }

    private sealed class FakeAgyAccountClient(FakeCredentialStore credentials) : IAgyAccountClient
    {
        private int _counter;

        public Task AuthorizeAsync(string agyPath, CancellationToken cancellationToken = default)
        {
            _counter++;
            credentials.Write(AccountManager.AgyCredentialTarget, System.Text.Encoding.UTF8.GetBytes(
                $"{{\"token\":{{\"access_token\":\"access-{_counter}\",\"refresh_token\":\"refresh-{_counter}\"}}," +
                $"\"email\":\"test-{_counter}@example.com\"}}"));
            return Task.CompletedTask;
        }

        public Task<AgyAccountInspection> InspectAsync(
            string agyPath, byte[] credential, CancellationToken cancellationToken = default)
        {
            using var document = System.Text.Json.JsonDocument.Parse(credential);
            return Task.FromResult(new AgyAccountInspection(
                new GeminiQuotaSnapshot(100, 100, DateTimeOffset.Parse("2026-07-28T08:00:00Z")),
                document.RootElement.GetProperty("email").GetString(),
                "PRO", AccountEligibility.Eligible, null));
        }
    }

    private static string GetFakeAgyDirectory()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(root) && !File.Exists(Path.Combine(root, "AgentRelay.sln")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        var release = Path.Combine(
            root, "tests", "AgentRelay.FakeAgy", "bin", "Release", "net8.0");
        return File.Exists(Path.Combine(release, "agy.exe"))
            ? release
            : throw new FileNotFoundException(
                "Fake agy.exe was not built.", Path.Combine(release, "agy.exe"));
    }
}
