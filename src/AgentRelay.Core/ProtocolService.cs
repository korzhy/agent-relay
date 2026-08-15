using System.Text.Json;

namespace AgentRelay.Core;

public sealed class ProtocolService
{
    private static readonly string[] ProhibitedActions =
    [
        "Do not decide or accept architecture.",
        "Do not accept security or final readiness.",
        "Do not deploy, push, publish, or touch production.",
        "Do not access, request, expose, or rotate secrets.",
        "Do not perform irreversible actions."
    ];

    private readonly AtomicFileStore _files;
    private readonly IClock _clock;

    public ProtocolService(AtomicFileStore files, IClock? clock = null)
    {
        _files = files;
        _clock = clock ?? new SystemClock();
    }

    public async Task<PublishedHandoff> PublishAsync(
        string workspaceRoot,
        MissionRequest request,
        CancellationToken cancellationToken = default)
        => await PublishAsync(
            workspaceRoot,
            request,
            new ExecutorIdentity(AgentRelayConstants.Provider, AgentRelayConstants.FallbackModel),
            cancellationToken).ConfigureAwait(false);

    public async Task<PublishedHandoff> PublishAsync(
        string workspaceRoot,
        MissionRequest request,
        ExecutorIdentity executor,
        CancellationToken cancellationToken = default)
    {
        ValidateExecutor(executor);
        var workspace = WorkspaceSafety.Validate(workspaceRoot);
        var transport = Path.Combine(workspace, AgentRelayConstants.TransportDirectory);
        var currentControlPath = Path.Combine(transport, "control.json");
        var currentReportPath = Path.Combine(transport, "report.json");
        var currentCancelPath = Path.Combine(transport, "cancel.json");
        var previous = await _files.ReadJsonAsync<ControlEnvelope>(currentControlPath, cancellationToken)
            .ConfigureAwait(false);

        if (previous is not null && !await IsTerminalAsync(
                previous, currentReportPath, currentCancelPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Project already has active handoff {previous.HandoffId} revision {previous.Revision}.");
        }

        var missionId = Guid.NewGuid().ToString("N");
        if (!string.IsNullOrWhiteSpace(request.MissionId))
        {
            if (!Guid.TryParse(request.MissionId, out var parsedMissionId))
            {
                throw new InvalidDataException("missionId must be a globally unique GUID.");
            }
            missionId = parsedMissionId.ToString("N");
        }
        var revision = previous is not null &&
                       string.Equals(previous.MissionId, missionId, StringComparison.Ordinal)
            ? checked(previous.Revision + 1)
            : 1;
        var handoffId = Guid.NewGuid().ToString("N");
        var runAttemptId = Guid.NewGuid().ToString("N");
        var tasksDirectory = Path.Combine(transport, "tasks");
        var reportsDirectory = Path.Combine(transport, "reports");
        var reviewsDirectory = Path.Combine(transport, "reviews");
        Directory.CreateDirectory(tasksDirectory);
        Directory.CreateDirectory(reportsDirectory);
        Directory.CreateDirectory(reviewsDirectory);

        var taskRelative = Relative(workspace, Path.Combine(tasksDirectory, $"{handoffId}-r{revision}.json"));
        var expectedReportRelative = Relative(
            workspace, Path.Combine(reportsDirectory, $"{handoffId}-r{revision}-{runAttemptId}.json"));
        var taskPath = WorkspaceSafety.ResolveRelative(workspace, taskRelative);
        var now = _clock.UtcNow;
        var task = new TaskPayload(
            AgentRelayConstants.ProtocolVersion,
            handoffId,
            missionId!,
            revision,
            runAttemptId,
            executor,
            now,
            request.Title,
            request.Instructions,
            request.DeterministicGates,
            ProhibitedActions,
            expectedReportRelative);
        await _files.WriteImmutableJsonAsync(taskPath, task, cancellationToken).ConfigureAwait(false);
        var taskHash = await AtomicFileStore.Sha256Async(taskPath, cancellationToken).ConfigureAwait(false);

        var control = new ControlEnvelope(
            AgentRelayConstants.ProtocolVersion,
            handoffId,
            missionId!,
            revision,
            previous?.HandoffId,
            runAttemptId,
            "assigned",
            executor,
            now,
            new PayloadReference(taskRelative, taskHash),
            expectedReportRelative);
        ValidateControl(control, workspace);
        await _files.WriteJsonAsync(currentControlPath, control, false, cancellationToken).ConfigureAwait(false);
        var controlHash = await AtomicFileStore.Sha256Async(currentControlPath, cancellationToken).ConfigureAwait(false);

        if (File.Exists(currentReportPath))
        {
            File.Move(currentReportPath, Path.Combine(
                reportsDirectory, $"previous-report-pointer-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json"));
        }
        if (File.Exists(currentCancelPath))
        {
            File.Move(currentCancelPath, Path.Combine(
                transport, $"previous-cancel-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json"));
        }

        return new PublishedHandoff(
            workspace,
            currentControlPath,
            controlHash,
            taskPath,
            taskHash,
            WorkspaceSafety.ResolveRelative(workspace, expectedReportRelative),
            control);
    }

    public async Task<ReportEnvelope> AcceptReportAsync(
        PublishedHandoff handoff,
        CancellationToken cancellationToken = default)
    {
        await ValidateForDispatchAsync(handoff, cancellationToken).ConfigureAwait(false);

        var report = await _files.ReadJsonAsync<ReportPayload>(handoff.ExpectedReportPath, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Runner exited without a report payload.");
        ValidateReport(report, handoff.Control);
        var reportHash = await AtomicFileStore.Sha256Async(handoff.ExpectedReportPath, cancellationToken)
            .ConfigureAwait(false);
        var transport = Path.Combine(handoff.WorkspaceRoot, AgentRelayConstants.TransportDirectory);
        var reviewPromptRelative = Relative(
            handoff.WorkspaceRoot,
            Path.Combine(transport, "reviews", $"{handoff.Control.HandoffId}-r{handoff.Control.Revision}-prompt.txt"));
        var reviewPromptPath = WorkspaceSafety.ResolveRelative(handoff.WorkspaceRoot, reviewPromptRelative);
        var reportRelative = Relative(handoff.WorkspaceRoot, handoff.ExpectedReportPath);
        var reviewAttemptId = Guid.NewGuid().ToString("N");

        var envelope = new ReportEnvelope(
            AgentRelayConstants.ProtocolVersion,
            handoff.Control.HandoffId,
            handoff.Control.MissionId,
            handoff.Control.Revision,
            handoff.Control.RunAttemptId,
            "reported",
            handoff.Control.Executor,
            _clock.UtcNow,
            new PayloadReference(
                Relative(handoff.WorkspaceRoot, handoff.ControlPath), handoff.ControlHash),
            new PayloadReference(
                Relative(handoff.WorkspaceRoot, handoff.TaskPath), handoff.TaskHash),
            new PayloadReference(reportRelative, reportHash),
            reviewPromptRelative,
            reviewAttemptId);

        var prompt = ReviewPromptBuilder.Build(envelope, report);
        await _files.WriteImmutableTextAsync(reviewPromptPath, prompt, cancellationToken).ConfigureAwait(false);
        var promptHash = await AtomicFileStore.Sha256Async(reviewPromptPath, cancellationToken)
            .ConfigureAwait(false);
        var immutableEnvelopePath = Path.Combine(
            transport, "reports", $"{handoff.Control.HandoffId}-r{handoff.Control.Revision}.envelope.json");
        await _files.WriteImmutableJsonAsync(immutableEnvelopePath, envelope, cancellationToken).ConfigureAwait(false);
        var reportEnvelopeHash = await AtomicFileStore.Sha256Async(immutableEnvelopePath, cancellationToken)
            .ConfigureAwait(false);
        var reviewEnvelope = new ReviewEnvelope(
            AgentRelayConstants.ProtocolVersion,
            handoff.Control.HandoffId,
            handoff.Control.MissionId,
            handoff.Control.Revision,
            reviewAttemptId,
            "awaiting-codex",
            handoff.Control.Executor,
            "Codex / Sol (UI-selected effort)",
            _clock.UtcNow,
            new PayloadReference(Relative(handoff.WorkspaceRoot, immutableEnvelopePath), reportEnvelopeHash),
            new PayloadReference(reviewPromptRelative, promptHash));
        var immutableReviewEnvelopePath = Path.Combine(
            transport, "reviews", $"{handoff.Control.HandoffId}-r{handoff.Control.Revision}.envelope.json");
        await _files.WriteImmutableJsonAsync(
            immutableReviewEnvelopePath, reviewEnvelope, cancellationToken).ConfigureAwait(false);
        await _files.WriteJsonAsync(
            Path.Combine(transport, "review.json"), reviewEnvelope, false, cancellationToken)
            .ConfigureAwait(false);
        await _files.WriteJsonAsync(Path.Combine(transport, "report.json"), envelope, false, cancellationToken)
            .ConfigureAwait(false);
        return envelope;
    }

    public async Task ValidateForDispatchAsync(
        PublishedHandoff handoff,
        CancellationToken cancellationToken = default)
    {
        ValidateControl(handoff.Control, handoff.WorkspaceRoot);
        var currentControlHash = await AtomicFileStore.Sha256Async(handoff.ControlPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentControlHash, handoff.ControlHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Control envelope changed after publication.");
        }

        var currentTaskHash = await AtomicFileStore.Sha256Async(handoff.TaskPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentTaskHash, handoff.TaskHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentTaskHash, handoff.Control.Task.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Immutable task payload hash mismatch.");
        }

        var task = await _files.ReadJsonAsync<TaskPayload>(handoff.TaskPath, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Task payload is invalid.");
        ValidateTask(task, handoff.Control);
    }

    public async Task<CancelEnvelope> CancelAsync(
        string workspaceRoot,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var workspace = WorkspaceSafety.Validate(workspaceRoot);
        var transport = Path.Combine(workspace, AgentRelayConstants.TransportDirectory);
        var control = await _files.ReadJsonAsync<ControlEnvelope>(
            Path.Combine(transport, "control.json"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No handoff exists.");
        var cancel = new CancelEnvelope(
            AgentRelayConstants.ProtocolVersion,
            control.HandoffId,
            control.MissionId,
            control.Revision,
            _clock.UtcNow,
            reason);
        await _files.WriteJsonAsync(Path.Combine(transport, "cancel.json"), cancel, false, cancellationToken)
            .ConfigureAwait(false);
        return cancel;
    }

    public static void ValidateControl(ControlEnvelope control, string workspaceRoot)
    {
        if (control.ProtocolVersion != AgentRelayConstants.ProtocolVersion ||
            control.Revision < 1 ||
            !Guid.TryParse(control.HandoffId, out _) ||
            !Guid.TryParse(control.MissionId, out _) ||
            !Guid.TryParse(control.RunAttemptId, out _) ||
            control.ParentHandoffId is not null && !Guid.TryParse(control.ParentHandoffId, out _) ||
            !string.Equals(control.State, "assigned", StringComparison.Ordinal) ||
            control.CreatedAt.Offset != TimeSpan.Zero ||
            !IsSha256(control.Task.Sha256))
        {
            throw new InvalidDataException("Control envelope identity or state is invalid.");
        }

        ValidateExecutor(control.Executor);
        var taskPath = WorkspaceSafety.ResolveRelative(workspaceRoot, control.Task.Path);
        if (!File.Exists(taskPath))
        {
            throw new InvalidDataException($"Task payload is missing: {control.Task.Path}");
        }
        var taskRoot = Path.GetFullPath(Path.Combine(
            workspaceRoot, AgentRelayConstants.TransportDirectory, "tasks")) + Path.DirectorySeparatorChar;
        if (!taskPath.StartsWith(taskRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Task payload must remain under .agent-relay/tasks.");
        }

        var reportPath = WorkspaceSafety.ResolveRelative(workspaceRoot, control.RequiredReportPath);
        var reportRoot = Path.GetFullPath(Path.Combine(
            workspaceRoot, AgentRelayConstants.TransportDirectory, "reports")) + Path.DirectorySeparatorChar;
        if (!reportPath.StartsWith(reportRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Report payload must remain under .agent-relay/reports.");
        }
    }

    public static void ValidateReport(ReportPayload report, ControlEnvelope control)
    {
        if (report.ProtocolVersion != AgentRelayConstants.ProtocolVersion ||
            !string.Equals(report.HandoffId, control.HandoffId, StringComparison.Ordinal) ||
            !string.Equals(report.MissionId, control.MissionId, StringComparison.Ordinal) ||
            report.Revision != control.Revision ||
            !string.Equals(report.RunAttemptId, control.RunAttemptId, StringComparison.Ordinal) ||
            report.CreatedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Report identity does not match the active handoff.");
        }

        ValidateExecutor(report.Executor);
        if (report.Executor != control.Executor)
        {
            throw new InvalidDataException("Report executor does not match the active handoff.");
        }
        if (report.ChangedFiles is null || report.Commands is null ||
            report.UnavailableDependencies is null || report.ProhibitedActions is null)
        {
            throw new InvalidDataException("Report is missing required truth fields.");
        }

        var confirmations = report.ProhibitedActions;
        if (!confirmations.NoArchitectureAcceptance ||
            !confirmations.NoSecurityAcceptance ||
            !confirmations.NoFinalReadinessAcceptance ||
            !confirmations.NoDeployOrPush ||
            !confirmations.NoProductionAccess ||
            !confirmations.NoSecretsAccess ||
            !confirmations.NoIrreversibleActions)
        {
            throw new InvalidDataException("Report does not confirm all prohibited-action boundaries.");
        }

        if (report.Claim == ReportClaim.Pass &&
            (report.Commands.Count == 0 || report.UnavailableDependencies.Count > 0))
        {
            throw new InvalidDataException("PASS requires executable commands and no unavailable dependencies.");
        }

        if (report.Claim != ReportClaim.Pass && string.IsNullOrWhiteSpace(report.FirstFailure) &&
            report.UnavailableDependencies.Count == 0)
        {
            throw new InvalidDataException("Non-PASS report requires a first failure or unavailable dependency.");
        }
    }

    private static void ValidateTask(TaskPayload task, ControlEnvelope control)
    {
        if (task.ProtocolVersion != AgentRelayConstants.ProtocolVersion ||
            !string.Equals(task.HandoffId, control.HandoffId, StringComparison.Ordinal) ||
            !string.Equals(task.MissionId, control.MissionId, StringComparison.Ordinal) ||
            task.Revision != control.Revision ||
            !string.Equals(task.RunAttemptId, control.RunAttemptId, StringComparison.Ordinal) ||
            !string.Equals(task.RequiredReportPath, control.RequiredReportPath, StringComparison.Ordinal) ||
            task.CreatedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Task identity does not match the active control envelope.");
        }
        ValidateExecutor(task.Executor);
        if (task.Executor != control.Executor)
        {
            throw new InvalidDataException("Task executor does not match the active handoff.");
        }
    }

    private static async Task<bool> IsTerminalAsync(
        ControlEnvelope control,
        string reportPointer,
        string cancelPointer,
        CancellationToken cancellationToken)
    {
        if (File.Exists(cancelPointer))
        {
            return true;
        }

        if (!File.Exists(reportPointer))
        {
            return false;
        }

        await using var stream = File.OpenRead(reportPointer);
        var report = await JsonSerializer.DeserializeAsync<ReportEnvelope>(
            stream, JsonSupport.Options, cancellationToken).ConfigureAwait(false);
        return report is not null &&
               string.Equals(report.HandoffId, control.HandoffId, StringComparison.Ordinal) &&
               report.Revision == control.Revision;
    }

    private static void ValidateExecutor(ExecutorIdentity executor)
    {
        if (!string.Equals(executor.Provider, AgentRelayConstants.Provider, StringComparison.Ordinal) ||
            !FlashModelIdentity.IsSupported(executor.Model))
        {
            throw new InvalidDataException(
                $"Executor must be {AgentRelayConstants.Provider} / gemini-<version>-flash-high.");
        }
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(
            character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
