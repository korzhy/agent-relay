using System;
using System.IO;
using System.Threading.Tasks;
using AgentRelay.Core;
using AgentRelay.Windows;
using Xunit;

namespace AgentRelay.IntegrationTests;

public sealed class AgyRunnerIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _appPaths;
    private readonly AtomicFileStore _files;
    private readonly ProtocolService _protocol;
    private readonly RuntimeStore _runtimeStore;
    private readonly ProjectRegistry _registry;
    private readonly RunnerOptions _fastOptions;

    public AgyRunnerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRelayIntegration_Runner_" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(_tempDir, "Home");
        var local = Path.Combine(_tempDir, "LocalAppData");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);

        _appPaths = new AppPaths(home, local);
        _files = new AtomicFileStore();
        _protocol = new ProtocolService(_files);
        _runtimeStore = new RuntimeStore(_appPaths, _files);
        _registry = new ProjectRegistry(_files, _appPaths.ProjectsFile);
        _fastOptions = new RunnerOptions(TimeSpan.FromMinutes(15), TimeSpan.FromHours(2), TimeSpan.FromMilliseconds(10));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task RunAsync_ValidPassReport_ReturnsReportReady()
    {
        var projectPath = Path.Combine(_tempDir, "proj_pass");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var request = new MissionRequest("Test Mission", "fake-mode:pass", new[] { "gate1" });
        var handoff = await _protocol.PublishAsync(projectPath, request);

        var activityStore = new SolActivityStore(_appPaths, _files);
        var clipboard = new RecordingClipboard();
        var delivery = new ReviewPromptDeliveryService(_appPaths, _files, clipboard);
        var runner = new AgyRunner(
            _protocol,
            _runtimeStore,
            options: _fastOptions,
            activity: activityStore,
            delivery: delivery);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.ReportReady, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.ReviewPromptPath);
        Assert.True(File.Exists(result.ReviewPromptPath));
        Assert.Equal(1, clipboard.WriteCount);
        Assert.Contains("reviewAttemptId:", clipboard.LastText);
        Assert.True((await delivery.GetAsync(registered.Id))?.Succeeded);
        Assert.Equal(SolActivityPhase.WaitingForFlash, (await activityStore.GetAsync(registered.Id))?.Phase);
    }

    [Fact]
    public async Task RunAsync_InvalidReport_ReturnsStalled()
    {
        var projectPath = Path.Combine(_tempDir, "proj_invalid");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var request = new MissionRequest("Test Mission", "fake-mode:invalid_report", new[] { "gate1" });
        var handoff = await _protocol.PublishAsync(projectPath, request);

        var runner = new AgyRunner(_protocol, _runtimeStore, options: _fastOptions);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("report validation failed", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_MissingReport_ReturnsStalled()
    {
        var projectPath = Path.Combine(_tempDir, "proj_missing_report");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var request = new MissionRequest("Test Mission", "fake-mode:missing_report", new[] { "gate1" });
        var handoff = await _protocol.PublishAsync(projectPath, request);

        var runner = new AgyRunner(_protocol, _runtimeStore, options: _fastOptions);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("report validation failed", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_NonzeroCrash_ReturnsStalled()
    {
        var projectPath = Path.Combine(_tempDir, "proj_crash");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var request = new MissionRequest("Test Mission", "fake-mode:crash", new[] { "gate1" });
        var handoff = await _protocol.PublishAsync(projectPath, request);

        var runner = new AgyRunner(_protocol, _runtimeStore, options: _fastOptions);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_StallTimeout_ReturnsStalledAndKillsProcess()
    {
        var projectPath = Path.Combine(_tempDir, "proj_stall");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var request = new MissionRequest("Test Mission", "fake-mode:stall", new[] { "gate1" });
        var handoff = await _protocol.PublishAsync(projectPath, request);

        var customOptions = new RunnerOptions(
            StallTimeout: TimeSpan.FromMilliseconds(500),
            HardTimeout: TimeSpan.FromSeconds(5),
            MonitorInterval: TimeSpan.FromMilliseconds(10)
        );
        var runner = new AgyRunner(_protocol, _runtimeStore, options: customOptions);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Contains("stalled", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_QuotaExhaustion_ReturnsQuotaExhausted()
    {
        var projectPath = Path.Combine(_tempDir, "proj_quota");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var request = new MissionRequest("Test Mission", "fake-mode:quota", new[] { "gate1" });
        var handoff = await _protocol.PublishAsync(projectPath, request);

        var runner = new AgyRunner(_protocol, _runtimeStore, options: _fastOptions);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.QuotaExhausted, result.State);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Quota exhaustion confirmed", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_PauseBeforeDispatch_CancelsHandoffAndResumeAllowsReplacement()
    {
        var projectPath = Path.Combine(_tempDir, "proj_pause");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var request = new MissionRequest("Test Mission", "Instructions", new[] { "gate1" });
        var handoff = await _protocol.PublishAsync(projectPath, request);

        await _runtimeStore.SetPausedAsync(registered, true, new SystemClock());

        var runner = new AgyRunner(_protocol, _runtimeStore, options: _fastOptions);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.Paused, result.State);
        Assert.Null(result.ExitCode);
        var cancelPath = Path.Combine(
            projectPath, AgentRelayConstants.TransportDirectory, "cancel.json");
        var cancel = await _files.ReadJsonAsync<CancelEnvelope>(cancelPath);
        Assert.Equal(handoff.Control.HandoffId, cancel?.HandoffId);

        await _runtimeStore.SetPausedAsync(registered, false, new SystemClock());
        var ready = await _runtimeStore.ReadAsync(registered.Id);
        Assert.Equal(RelayState.Ready, ready?.State);
        Assert.Null(ready?.HandoffId);

        var replacement = await _protocol.PublishAsync(
            projectPath, new MissionRequest("Replacement", "Instructions", ["gate1"]));
        Assert.NotEqual(handoff.Control.HandoffId, replacement.Control.HandoffId);
    }

    [Fact]
    public async Task RunAsync_PauseDuringExecution_ArmsPauseAndStopsExactRunner()
    {
        var projectPath = Path.Combine(_tempDir, "proj_active_pause");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(
            projectPath, new MissionRequest("Pause mission", "fake-mode:stall", ["gate1"]));
        var runner = new AgyRunner(
            _protocol,
            _runtimeStore,
            options: new RunnerOptions(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(20)));

        var runTask = runner.RunAsync(registered, handoff, GetFakeAgyPath());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        ProjectRuntimeState? running;
        do
        {
            await Task.Delay(25);
            running = await _runtimeStore.ReadAsync(registered.Id);
        } while (running?.State != RelayState.Running && DateTimeOffset.UtcNow < deadline);

        Assert.NotNull(running);
        Assert.Equal(RelayState.Running, running.State);
        Assert.NotNull(running.ProcessId);
        await _runtimeStore.SetPausedAsync(registered, true, new SystemClock());

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RelayState.Paused, result.State);
        Assert.Contains("interrupted run", result.Detail, StringComparison.OrdinalIgnoreCase);
        var cancel = await _files.ReadJsonAsync<CancelEnvelope>(Path.Combine(
            projectPath, AgentRelayConstants.TransportDirectory, "cancel.json"));
        Assert.Equal(handoff.Control.HandoffId, cancel?.HandoffId);
    }

    [Fact]
    public async Task RunAsync_InvalidExecutable_ReturnsStalledInsteadOfLeavingRuntimeUnassigned()
    {
        var projectPath = Path.Combine(_tempDir, "proj_invalid_executable");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(
            projectPath, new MissionRequest("Invalid executable", "Instructions", ["gate1"]));
        var invalidExecutable = Path.Combine(_tempDir, "not-an-executable.exe");
        await File.WriteAllTextAsync(invalidExecutable, "not a Windows executable");
        var runner = new AgyRunner(_protocol, _runtimeStore, options: _fastOptions);

        var result = await runner.RunAsync(registered, handoff, invalidExecutable);

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Contains("failed to start", result.Detail, StringComparison.OrdinalIgnoreCase);
        var runtime = await _runtimeStore.ReadAsync(registered.Id);
        Assert.Equal(RelayState.Stalled, runtime?.State);
        Assert.Equal(handoff.Control.HandoffId, runtime?.HandoffId);
    }

    [Fact]
    public async Task RuntimeRecovery_RecoversInterruptedStateToStalled()
    {
        var projectPath = Path.Combine(_tempDir, "proj_recovery");
        Directory.CreateDirectory(projectPath);

        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);

        var fakeState = new ProjectRuntimeState(
            1, registered.Id, RelayState.Running, "h1", "m1", 1, "r1", 999999, // Nonexistent PID
            DateTimeOffset.UtcNow, "Running...", "hash", null
        );
        await _runtimeStore.WriteAsync(fakeState);

        var recovery = new RuntimeRecoveryService(_runtimeStore);
        var recovered = await recovery.RecoverAsync(registered);

        Assert.Equal(RelayState.Stalled, recovered.State);
        Assert.Null(recovered.ProcessId);
        Assert.Contains("Recovered an interrupted runner", recovered.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFakeAgyPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var baseAgy = Path.Combine(baseDir, "agy.exe");
        if (File.Exists(baseAgy)) return baseAgy;

        var solutionRoot = GetSolutionRoot();
        var releasePath = Path.Combine(solutionRoot, "tests", "AgentRelay.FakeAgy", "bin", "Release", "net8.0", "agy.exe");
        if (File.Exists(releasePath)) return releasePath;

        var debugPath = Path.Combine(solutionRoot, "tests", "AgentRelay.FakeAgy", "bin", "Debug", "net8.0", "agy.exe");
        if (File.Exists(debugPath)) return debugPath;

        throw new FileNotFoundException("Fake agy.exe not found.");
    }

    private static string GetSolutionRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "AgentRelay.sln")))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return dir ?? throw new DirectoryNotFoundException("AgentRelay.sln not found");
    }

    private sealed class RecordingClipboard : IClipboardWriter
    {
        public int WriteCount { get; private set; }
        public string LastText { get; private set; } = string.Empty;

        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            LastText = text;
            return Task.CompletedTask;
        }
    }
}
