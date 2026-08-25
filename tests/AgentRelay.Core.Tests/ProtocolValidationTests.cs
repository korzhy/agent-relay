using System;
using System.IO;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class ProtocolValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ExecutorIdentity _validExecutor;
    private readonly string _handoffId;
    private readonly string _missionId;
    private readonly string _runAttemptId;

    public ProtocolValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRelayCoreTests_Protocol_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _validExecutor = new ExecutorIdentity(AgentRelayConstants.Provider, AgentRelayConstants.FallbackModel);
        _handoffId = Guid.NewGuid().ToString("N");
        _missionId = Guid.NewGuid().ToString("N");
        _runAttemptId = Guid.NewGuid().ToString("N");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ValidateControl_ValidEnvelope_Passes()
    {
        var taskRel = ".agent-relay/tasks/t1.json";
        var taskPath = Path.Combine(_tempDir, taskRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
        File.WriteAllText(taskPath, "{}");

        var control = new ControlEnvelope(
            AgentRelayConstants.ProtocolVersion,
            _handoffId,
            _missionId,
            1,
            null,
            _runAttemptId,
            "assigned",
            _validExecutor,
            DateTimeOffset.UtcNow,
            new PayloadReference(taskRel, new string('a', 64)),
            ".agent-relay/reports/r1.json"
        );

        ProtocolService.ValidateControl(control, _tempDir);
    }

    [Fact]
    public async Task PublishCancelAndCorrection_PreservesImmutableTaskAndIncrementsRevision()
    {
        var files = new AtomicFileStore();
        var service = new ProtocolService(files);
        var missionId = Guid.NewGuid().ToString("N");
        var first = await service.PublishAsync(
            _tempDir, new MissionRequest("First", "Do work", ["dotnet test"], missionId));
        var originalTask = await File.ReadAllBytesAsync(first.TaskPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync(
                _tempDir, new MissionRequest("Duplicate", "Must block", [], missionId)));

        await service.CancelAsync(_tempDir, "Correction required.");
        var second = await service.PublishAsync(
            _tempDir, new MissionRequest("Correction", "Fix exact failure", ["dotnet test"], missionId));

        Assert.Equal(1, first.Control.Revision);
        Assert.Equal(2, second.Control.Revision);
        Assert.Equal(first.Control.HandoffId, second.Control.ParentHandoffId);
        Assert.NotEqual(first.Control.HandoffId, second.Control.HandoffId);
        Assert.NotEqual(first.Control.RunAttemptId, second.Control.RunAttemptId);
        Assert.Equal(originalTask, await File.ReadAllBytesAsync(first.TaskPath));
    }

    [Fact]
    public async Task PublishAsync_PinsResolvedExecutorInImmutablePayloads()
    {
        var files = new AtomicFileStore();
        var service = new ProtocolService(files);
        var executor = new ExecutorIdentity(AgentRelayConstants.Provider, "gemini-3.5-pro-high");

        var handoff = await service.PublishAsync(
            _tempDir, new MissionRequest("Latest", "Do work", ["dotnet test"]), executor);
        var task = await files.ReadJsonAsync<TaskPayload>(handoff.TaskPath);

        Assert.Equal(executor, handoff.Control.Executor);
        Assert.Equal(executor, task?.Executor);
    }

    [Fact]
    public async Task PublishAsync_StaleCancelPointerDoesNotMakeActiveHandoffTerminal()
    {
        var files = new AtomicFileStore();
        var service = new ProtocolService(files);
        var first = await service.PublishAsync(
            _tempDir, new MissionRequest("First", "Do work", ["dotnet test"]));
        var staleCancel = await service.CancelAsync(_tempDir, "First is cancelled.");
        var second = await service.PublishAsync(
            _tempDir, new MissionRequest("Second", "Do more work", ["dotnet test"]));
        var cancelPath = Path.Combine(
            _tempDir, AgentRelayConstants.TransportDirectory, "cancel.json");
        await files.WriteJsonAsync(cancelPath, staleCancel, false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync(
                _tempDir, new MissionRequest("Must block", "Do not publish", [])));

        Assert.Contains(second.Control.HandoffId, exception.Message, StringComparison.Ordinal);
        Assert.NotEqual(first.Control.HandoffId, second.Control.HandoffId);
    }

    [Fact]
    public async Task ValidateForDispatchAsync_RejectsImmutableTaskTampering()
    {
        var files = new AtomicFileStore();
        var service = new ProtocolService(files);
        var handoff = await service.PublishAsync(
            _tempDir, new MissionRequest("Tamper", "Do work", ["dotnet test"]));

        await File.AppendAllTextAsync(handoff.TaskPath, "tampered");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ValidateForDispatchAsync(handoff));
        Assert.Contains("hash mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateForDispatchAsync_RejectsCancelledHandoff()
    {
        var files = new AtomicFileStore();
        var service = new ProtocolService(files);
        var handoff = await service.PublishAsync(
            _tempDir, new MissionRequest("Cancelled", "Do not run", ["dotnet test"]));
        await service.CancelAsync(_tempDir, "Cancelled by user.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ValidateForDispatchAsync(handoff));

        Assert.Contains("cancelled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateControl_InvalidProtocolVersion_Throws()
    {
        var control = new ControlEnvelope(
            99, _handoffId, _missionId, 1, null, _runAttemptId, "assigned", _validExecutor,
            DateTimeOffset.UtcNow, new PayloadReference(".agent-relay/tasks/t1.json", "hash"),
            ".agent-relay/reports/r1.json"
        );

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateControl(control, _tempDir));
    }

    [Fact]
    public void ValidateControl_MissingTaskPayload_Throws()
    {
        var control = new ControlEnvelope(
            AgentRelayConstants.ProtocolVersion, _handoffId, _missionId, 1, null, _runAttemptId, "assigned",
            _validExecutor, DateTimeOffset.UtcNow,
            new PayloadReference(".agent-relay/tasks/nonexistent.json", "hash"),
            ".agent-relay/reports/r1.json"
        );

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateControl(control, _tempDir));
    }

    [Fact]
    public void ValidateControl_ReportPathEscapesReportsDirectory_Throws()
    {
        var taskRel = ".agent-relay/tasks/t1.json";
        var taskPath = Path.Combine(_tempDir, taskRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
        File.WriteAllText(taskPath, "{}");

        var control = new ControlEnvelope(
            AgentRelayConstants.ProtocolVersion, _handoffId, _missionId, 1, null, _runAttemptId, "assigned",
            _validExecutor, DateTimeOffset.UtcNow,
            new PayloadReference(taskRel, "hash"),
            ".agent-relay/invalid-location.json"
        );

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateControl(control, _tempDir));
    }

    [Fact]
    public void ValidateReport_ValidPassReport_Passes()
    {
        var control = CreateValidControl();
        var report = CreateValidReport(control, ReportClaim.Pass);

        ProtocolService.ValidateReport(report, control);
    }

    [Fact]
    public void ValidateReport_HandoffIdMismatch_Throws()
    {
        var control = CreateValidControl();
        var report = CreateValidReport(control, ReportClaim.Pass) with { HandoffId = "mismatched" };

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateReport(report, control));
    }

    [Fact]
    public void ValidateReport_ProhibitedActionNotConfirmed_Throws()
    {
        var control = CreateValidControl();
        var report = CreateValidReport(control, ReportClaim.Pass) with
        {
            ProhibitedActions = new ProhibitedActionConfirmation(false, true, true, true, true, true, true)
        };

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateReport(report, control));
    }

    [Fact]
    public void ValidateReport_PassClaimWithNoCommands_Throws()
    {
        var control = CreateValidControl();
        var report = CreateValidReport(control, ReportClaim.Pass) with
        {
            Commands = Array.Empty<ExecutedCommand>()
        };

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateReport(report, control));
    }

    [Fact]
    public void ValidateReport_PassClaimWithUnavailableDependency_Throws()
    {
        var control = CreateValidControl();
        var report = CreateValidReport(control, ReportClaim.Pass) with
        {
            UnavailableDependencies = new[] { "missing-dep" }
        };

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateReport(report, control));
    }

    [Fact]
    public void ValidateReport_NonPassClaimWithoutFirstFailureOrUnavailableDep_Throws()
    {
        var control = CreateValidControl();
        var report = CreateValidReport(control, ReportClaim.Fail) with
        {
            FirstFailure = null,
            UnavailableDependencies = Array.Empty<string>()
        };

        Assert.Throws<InvalidDataException>(() => ProtocolService.ValidateReport(report, control));
    }

    private ControlEnvelope CreateValidControl() => new(
        AgentRelayConstants.ProtocolVersion, _handoffId, _missionId, 1, null, _runAttemptId, "assigned",
        _validExecutor, DateTimeOffset.UtcNow,
        new PayloadReference(".agent-relay/tasks/t1.json", "hash"),
        ".agent-relay/reports/r1.json"
    );

    private ReportPayload CreateValidReport(ControlEnvelope control, ReportClaim claim) => new(
        control.ProtocolVersion, control.HandoffId, control.MissionId, control.Revision,
        control.RunAttemptId, control.Executor, DateTimeOffset.UtcNow, claim,
        new[] { "file.cs" }, new[] { new ExecutedCommand("dotnet test", 0) },
        claim == ReportClaim.Pass ? null : "Failed step", Array.Empty<string>(),
        new ProhibitedActionConfirmation(true, true, true, true, true, true, true),
        "Summary"
    );
}
