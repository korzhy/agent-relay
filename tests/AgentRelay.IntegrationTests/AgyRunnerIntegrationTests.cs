using System;
using System.Diagnostics;
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
        _fastOptions = new RunnerOptions(
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(2),
            TimeSpan.FromMilliseconds(10))
        {
            ReportPollInterval = TimeSpan.FromMilliseconds(10),
            ReportStabilityTimeout = TimeSpan.FromMilliseconds(150)
        };
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
        Assert.Contains("status=missing", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("report-observation", result.Failure?.Stage);
        Assert.Equal(0, result.Failure?.ExitCode);
        Assert.Equal(handoff.Control.RunAttemptId, result.Failure?.RunAttemptId);
        Assert.True(File.Exists(result.FailurePath));
        Assert.Equal(result.FailurePath, (await _runtimeStore.ReadAsync(registered.Id))?.FailurePath);
        Assert.True(File.Exists(result.Failure?.StdoutLogPath));
        Assert.True(File.Exists(result.Failure?.StderrLogPath));

        var replacement = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Retry", "fake-mode:pass", ["gate1"], handoff.Control.MissionId));
        Assert.Equal(2, replacement.Control.Revision);
        Assert.Equal(handoff.Control.HandoffId, replacement.Control.ParentHandoffId);
        Assert.True(File.Exists(result.FailurePath));
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
        Assert.Equal("process-exit", result.Failure?.Stage);
        Assert.Equal(1, result.Failure?.ExitCode);
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
    public async Task RunAsync_HardTimeoutStopsAttemptBeforeLongStallLimit()
    {
        var projectPath = Path.Combine(_tempDir, "proj_hard_timeout");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Budget", "fake-mode:stall", ["gate1"]));
        var runner = new AgyRunner(_protocol, _runtimeStore, options: new RunnerOptions(
            TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(10)));
        var result = await runner.RunAsync(registered, handoff, GetFakeAgyPath());
        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Contains("hard timeout", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ReviewPromptPath);
    }

    [Fact]
    public async Task RunAsync_NetworkFailure_IsClassifiedAsTransport()
    {
        var projectPath = Path.Combine(_tempDir, "proj_network_error");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Network fault", "fake-mode:network_error", ["gate1"]));

        var result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
            .RunAsync(registered, handoff, GetFakeAgyPath());

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("transport", result.Failure?.Stage);
        Assert.Contains("transport failed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Failure?.StderrLogPath);
        Assert.Contains("AGY_ERROR", await File.ReadAllTextAsync(result.Failure!.StderrLogPath!));
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task RunAsync_LockedLog_RecordsTerminalFailureAndStopsProcess(string stream)
    {
        var projectPath = Path.Combine(_tempDir, $"proj_locked_{stream}");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Log capture fault", "fake-mode:stall", ["gate1"]));
        var logDirectory = _runtimeStore.ProjectLogDirectory(registered.Id);
        Directory.CreateDirectory(logDirectory);
        var lockedPath = Path.Combine(logDirectory, $"{handoff.Control.RunAttemptId}.{stream}.log");
        await using var locked = new FileStream(
            lockedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var runner = new AgyRunner(_protocol, _runtimeStore, options: new RunnerOptions(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));
        var runTask = runner.RunAsync(registered, handoff, GetFakeAgyPath());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        ProjectRuntimeState? running;
        do
        {
            await Task.Delay(10);
            running = await _runtimeStore.ReadAsync(registered.Id);
        } while (running?.ProcessId is null && DateTimeOffset.UtcNow < deadline);

        var processId = running?.ProcessId;
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(7));

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.True(result.Failure?.Stage == "log-capture", result.Detail);
        Assert.True(File.Exists(result.FailurePath));
        Assert.Equal(RelayState.Stalled, (await _runtimeStore.ReadAsync(registered.Id))?.State);
        if (processId is not null)
        {
            try
            {
                using var process = Process.GetProcessById(processId.Value);
                Assert.True(process.HasExited);
            }
            catch (ArgumentException)
            {
                // The OS has already reaped the exact child process.
            }
        }
    }

    [Fact]
    public async Task RunAsync_LogDirectoryUnavailable_RecordsFailureBeforeStartingProcess()
    {
        var projectPath = Path.Combine(_tempDir, "proj_log_directory_unavailable");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Setup fault", "fake-mode:pass", ["gate1"]));
        var logDirectory = _runtimeStore.ProjectLogDirectory(registered.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(logDirectory)!);
        await File.WriteAllTextAsync(logDirectory, "A file blocks creation of the log directory.");

        var result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
            .RunAsync(registered, handoff, GetFakeAgyPath());

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Equal("runner-setup", result.Failure?.Stage);
        Assert.Null(result.ExitCode);
        var local = await _runtimeStore.ReadAsync(registered.Id);
        Assert.Equal(RelayState.Stalled, local?.State);
        Assert.Null(local?.ProcessId);
        Assert.Equal(handoff.Control.RunAttemptId, local?.RunAttemptId);
    }

    [Fact]
    public async Task RunAsync_FailurePointerUnavailable_KeepsLocalTerminalStateAndRecovers()
    {
        var projectPath = Path.Combine(_tempDir, "proj_locked_failure_pointer");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Missing report", "fake-mode:missing_report", ["gate1"]));
        var failurePointer = Path.Combine(projectPath, AgentRelayConstants.TransportDirectory,
            "failure.json");
        RunnerResult result;
        await using (var locked = new FileStream(
                         failurePointer, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
                .RunAsync(registered, handoff, GetFakeAgyPath());
            Assert.Equal(RelayState.Stalled, result.State);
            Assert.Null(result.FailurePath);
            var local = await _runtimeStore.ReadAsync(registered.Id);
            Assert.Equal(RelayState.Stalled, local?.State);
            Assert.Equal("report-observation", local?.FailureStage);
            Assert.Contains("Failure record unavailable", local?.Detail);
        }

        var recovered = await new RuntimeRecoveryService(_runtimeStore, protocol: _protocol)
            .RecoverAsync(registered);
        var failure = await _files.ReadJsonAsync<FailureEnvelope>(failurePointer);
        Assert.Equal("report-observation", failure?.Stage);
        Assert.Equal(handoff.Control.RunAttemptId, failure?.RunAttemptId);
        Assert.True(File.Exists(recovered.FailurePath));
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
    public async Task RunAsync_QuotaTextAcrossOutputChunks_IsDetected()
    {
        var projectPath = Path.Combine(_tempDir, "proj_quota_chunks");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Chunked quota", "fake-mode:quota_chunk_boundary", ["gate1"]));
        var runner = new AgyRunner(_protocol, _runtimeStore, options: _fastOptions);

        var result = await runner.RunAsync(registered, handoff, GetFakeAgyPath());

        Assert.Equal(RelayState.QuotaExhausted, result.State);
        Assert.Equal("quota", result.Failure?.Stage);
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
    public async Task RunAsync_ExecutableDisappearsAfterPublication_RecordsTerminalFailure()
    {
        var projectPath = Path.Combine(_tempDir, "proj_executable_missing");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Missing executable", "Instructions", ["gate1"]));

        var result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
            .RunAsync(registered, handoff, Path.Combine(_tempDir, "missing-agy.exe"));

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Equal("process-start", result.Failure?.Stage);
        Assert.NotNull(await _protocol.PublishAsync(projectPath,
            new MissionRequest("Replacement", "Instructions", ["gate1"])));
    }

    [Fact]
    public async Task RunAsync_TamperedPublishedControl_RecordsLocalTerminalOutcome()
    {
        var projectPath = Path.Combine(_tempDir, "proj_tampered_dispatch");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Dispatch integrity", "fake-mode:pass", ["gate1"]));
        await File.AppendAllTextAsync(handoff.ControlPath, "\n");

        var result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
            .RunAsync(registered, handoff, GetFakeAgyPath());

        Assert.Equal(RelayState.Stalled, result.State);
        Assert.Null(result.FailurePath);
        var local = await _runtimeStore.ReadAsync(registered.Id);
        Assert.Equal("dispatch-validation", local?.FailureStage);
        Assert.Null(local?.ProcessId);
        Assert.Equal(handoff.Control.RunAttemptId, local?.RunAttemptId);
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

    [Fact]
    public async Task RuntimeRecovery_DoesNotMistakeReusedPidForOriginalRunner()
    {
        var projectPath = Path.Combine(_tempDir, "proj_reused_pid");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        using var currentProcess = Process.GetCurrentProcess();
        await _runtimeStore.WriteAsync(new ProjectRuntimeState(
            1, registered.Id, RelayState.Running, "h1", "m1", 1, "r1",
            currentProcess.Id, DateTimeOffset.UtcNow, "Old runner.", "hash", null,
            currentProcess.MainModule!.FileName,
            ProcessStartedAt: DateTimeOffset.UtcNow.AddDays(-1)));

        var recovered = await new RuntimeRecoveryService(_runtimeStore).RecoverAsync(registered);

        Assert.Equal(RelayState.Stalled, recovered.State);
        Assert.Null(recovered.ProcessId);
        Assert.False(currentProcess.HasExited);
    }

    [Fact]
    public async Task RuntimeRecovery_RecordsDeadRunnerAndAllowsReplacement()
    {
        var projectPath = Path.Combine(_tempDir, "proj_recovery_replacement");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Interrupted", "Instructions", ["gate1"]));
        await _runtimeStore.WriteAsync(new ProjectRuntimeState(
            1, registered.Id, RelayState.Running,
            handoff.Control.HandoffId, handoff.Control.MissionId,
            handoff.Control.Revision, handoff.Control.RunAttemptId,
            999999, DateTimeOffset.UtcNow, "Running...", handoff.ControlHash, null,
            GetFakeAgyPath()));

        var recovery = new RuntimeRecoveryService(_runtimeStore, protocol: _protocol);
        using (var activeRunnerLease = GlobalAgyLease.TryAcquire())
        {
            Assert.NotNull(activeRunnerLease);
            Assert.Equal(RelayState.Running, (await recovery.RecoverAsync(registered)).State);
        }
        var recovered = await recovery.RecoverAsync(registered);
        var failurePath = Path.Combine(projectPath, AgentRelayConstants.TransportDirectory, "failure.json");
        var failure = await _files.ReadJsonAsync<FailureEnvelope>(failurePath);

        Assert.Equal(RelayState.Stalled, recovered.State);
        Assert.Equal("runtime-recovery", failure?.Stage);
        Assert.Null(failure?.ExitCode);
        Assert.True(File.Exists(recovered.FailurePath));
        var replacement = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Replacement", "Instructions", ["gate1"]));
        Assert.Equal(handoff.Control.HandoffId, replacement.Control.ParentHandoffId);
    }

    [Fact]
    public async Task RuntimeRecovery_RecognizesAcceptedReportAfterRunnerCrash()
    {
        var projectPath = Path.Combine(_tempDir, "proj_accepted_report_recovery");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Accepted report", "fake-mode:pass", ["gate1"]));
        var result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
            .RunAsync(registered, handoff, GetFakeAgyPath());
        Assert.Equal(RelayState.ReportReady, result.State);
        await _runtimeStore.WriteAsync(new ProjectRuntimeState(
            1, registered.Id, RelayState.Running,
            handoff.Control.HandoffId, handoff.Control.MissionId,
            handoff.Control.Revision, handoff.Control.RunAttemptId,
            999999, DateTimeOffset.UtcNow, "Interrupted before runtime completion.",
            handoff.ControlHash, null, GetFakeAgyPath()));

        var recovered = await new RuntimeRecoveryService(_runtimeStore, protocol: _protocol)
            .RecoverAsync(registered);

        Assert.Equal(RelayState.ReportReady, recovered.State);
        Assert.Null(recovered.ProcessId);
        Assert.Equal(result.ReviewPromptPath, recovered.ReviewPromptPath);
        Assert.Null(recovered.FailurePath);
        Assert.False(File.Exists(Path.Combine(projectPath,
            AgentRelayConstants.TransportDirectory, "failure.json")));
    }

    [Fact]
    public async Task RuntimeRecovery_RejectsTamperedAcceptedReport()
    {
        var projectPath = Path.Combine(_tempDir, "proj_tampered_report_recovery");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Tampered report", "fake-mode:pass", ["gate1"]));
        var result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
            .RunAsync(registered, handoff, GetFakeAgyPath());
        Assert.Equal(RelayState.ReportReady, result.State);
        await File.AppendAllTextAsync(handoff.ExpectedReportPath, "\n");
        await _runtimeStore.WriteAsync(new ProjectRuntimeState(
            1, registered.Id, RelayState.Running,
            handoff.Control.HandoffId, handoff.Control.MissionId,
            handoff.Control.Revision, handoff.Control.RunAttemptId,
            999999, DateTimeOffset.UtcNow, "Interrupted before runtime completion.",
            handoff.ControlHash, null, GetFakeAgyPath()));

        var recovered = await new RuntimeRecoveryService(_runtimeStore, protocol: _protocol)
            .RecoverAsync(registered);

        Assert.Equal(RelayState.Stalled, recovered.State);
        Assert.Equal("runtime-recovery", recovered.FailureStage);
        Assert.True(File.Exists(recovered.FailurePath));
    }

    [Fact]
    public async Task RuntimeRecovery_PreservesRecordedFailureStageAfterRunnerCrash()
    {
        var projectPath = Path.Combine(_tempDir, "proj_recorded_failure_recovery");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Missing report", "fake-mode:missing_report", ["gate1"]));
        var result = await new AgyRunner(_protocol, _runtimeStore, options: _fastOptions)
            .RunAsync(registered, handoff, GetFakeAgyPath());
        Assert.Equal("report-observation", result.Failure?.Stage);
        await _runtimeStore.WriteAsync(new ProjectRuntimeState(
            1, registered.Id, RelayState.Running,
            handoff.Control.HandoffId, handoff.Control.MissionId,
            handoff.Control.Revision, handoff.Control.RunAttemptId,
            999999, DateTimeOffset.UtcNow, "Interrupted before runtime completion.",
            handoff.ControlHash, null, GetFakeAgyPath()));

        var recovered = await new RuntimeRecoveryService(_runtimeStore, protocol: _protocol)
            .RecoverAsync(registered);

        Assert.Equal(RelayState.Stalled, recovered.State);
        Assert.Equal("report-observation", recovered.FailureStage);
        Assert.Equal(result.FailurePath, recovered.FailurePath);
    }

    [Fact]
    public async Task RuntimeRecovery_RepairsLegacyMissingReportState()
    {
        var projectPath = Path.Combine(_tempDir, "proj_legacy_stalled");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Old attempt", "Instructions", ["gate1"]));
        await _runtimeStore.WriteAsync(new ProjectRuntimeState(
            1, registered.Id, RelayState.Stalled,
            handoff.Control.HandoffId, handoff.Control.MissionId,
            handoff.Control.Revision, handoff.Control.RunAttemptId,
            null, DateTimeOffset.UtcNow,
            "Runner exited but report validation failed: Runner exited without a stable report payload " +
            "after debounce/hash validation (status=missing, observations=0).",
            handoff.ControlHash, null));

        var recovered = await new RuntimeRecoveryService(_runtimeStore, protocol: _protocol)
            .RecoverAsync(registered);

        Assert.Equal(RelayState.Stalled, recovered.State);
        Assert.True(File.Exists(recovered.FailurePath));
        var failure = await _files.ReadJsonAsync<FailureEnvelope>(recovered.FailurePath!);
        Assert.Equal("legacy-terminal-recovery", failure?.Stage);
        Assert.NotNull(await _protocol.PublishAsync(projectPath,
            new MissionRequest("Replacement", "Instructions", ["gate1"])));
    }

    [Fact]
    public async Task RuntimeRecovery_DoesNotReleaseUnknownStalledState()
    {
        var projectPath = Path.Combine(_tempDir, "proj_unknown_stalled");
        Directory.CreateDirectory(projectPath);
        var registered = await _registry.AddAsync(projectPath);
        registered = await _registry.TrustAsync(registered.Id);
        var handoff = await _protocol.PublishAsync(projectPath,
            new MissionRequest("Active attempt", "Instructions", ["gate1"]));
        await _runtimeStore.WriteAsync(new ProjectRuntimeState(
            1, registered.Id, RelayState.Stalled,
            handoff.Control.HandoffId, handoff.Control.MissionId,
            handoff.Control.Revision, handoff.Control.RunAttemptId,
            null, DateTimeOffset.UtcNow,
            "runnerBusy: another Agent Relay runner owns agy.",
            handoff.ControlHash, null));

        var recovered = await new RuntimeRecoveryService(_runtimeStore, protocol: _protocol)
            .RecoverAsync(registered);

        Assert.Null(recovered.FailurePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _protocol.PublishAsync(
            projectPath, new MissionRequest("Must block", "Instructions", ["gate1"])));
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
