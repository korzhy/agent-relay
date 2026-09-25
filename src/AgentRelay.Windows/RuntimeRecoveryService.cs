using System.Diagnostics;
using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed class RuntimeRecoveryService
{
    private readonly RuntimeStore _runtime;
    private readonly ProtocolService? _protocol;
    private readonly IClock _clock;

    public RuntimeRecoveryService(
        RuntimeStore runtime,
        IClock? clock = null,
        ProtocolService? protocol = null)
    {
        _runtime = runtime;
        _protocol = protocol;
        _clock = clock ?? new SystemClock();
    }

    public async Task<ProjectRuntimeState> RecoverAsync(
        RegisteredProject project,
        CancellationToken cancellationToken = default)
    {
        var current = await _runtime.ReadAsync(project.Id, cancellationToken).ConfigureAwait(false)
            ?? RuntimeStore.NewReady(project, _clock.UtcNow);
        if (_runtime.IsPaused(project.Id))
        {
            current = current with
            {
                State = RelayState.Paused,
                ProcessId = null,
                UpdatedAt = _clock.UtcNow,
                Detail = "Dispatch pause is armed."
            };
            await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
            return current;
        }

        if (current.State is RelayState.Stalled or RelayState.QuotaExhausted &&
            current.FailurePath is null &&
            (current.FailureStage is not null || IsKnownTerminalDetail(current.Detail)) &&
            _protocol is not null)
        {
            using var lease = TryAcquireRecoveryLease();
            if (lease is not null)
            {
                var (failurePath, failureError) = await TryRecordFailureAsync(
                    project, current, current.State,
                    current.FailureStage ?? "legacy-terminal-recovery",
                    current.Detail!, cancellationToken).ConfigureAwait(false);
                if (failurePath is not null)
                {
                    current = current with { FailurePath = failurePath, UpdatedAt = _clock.UtcNow };
                    await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
                }
                else if (failureError is not null &&
                         !current.Detail!.Contains("Failure record unavailable:", StringComparison.Ordinal))
                {
                    current = current with
                    {
                        Detail = $"{current.Detail} Failure record unavailable: {failureError}",
                        UpdatedAt = _clock.UtcNow
                    };
                    await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var processMatch = current.State is RelayState.Running or RelayState.Waiting
            ? ProcessMatches(current)
            : null;
        if (current.State is RelayState.Running or RelayState.Waiting && processMatch is null)
        {
            return current with
            {
                Detail = "Runner process identity could not be verified; outcome is not yet terminal. " +
                         "Retry status after OS process inspection becomes available."
            };
        }
        if (current.State is RelayState.Running or RelayState.Waiting && processMatch == false)
        {
            using var lease = TryAcquireRecoveryLease();
            if (lease is null)
            {
                return current;
            }
            if (_protocol is not null && current.HandoffId is not null &&
                current.Revision is not null && current.RunAttemptId is not null &&
                current.LastControlHash is not null)
            {
                ReportEnvelope? accepted = null;
                try
                {
                    accepted = await _protocol.ReadAcceptedReportAsync(
                        project.Path, current.HandoffId, current.Revision.Value,
                        current.RunAttemptId, current.LastControlHash,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                   InvalidOperationException or JsonException or
                                                   UnauthorizedAccessException)
                {
                    // An unreadable or invalid pointer is not evidence of an accepted report.
                }
                if (accepted is not null)
                {
                    current = current with
                    {
                        State = RelayState.ReportReady,
                        ProcessId = null,
                        UpdatedAt = _clock.UtcNow,
                        Detail = "Recovered a validated report after interrupted runner completion.",
                        ReviewPromptPath = WorkspaceSafety.ResolveRelative(
                            project.Path, accepted.ReviewPromptPath),
                        FailurePath = null,
                        FailureStage = null
                    };
                    await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
                    return current;
                }
                FailureEnvelope? recordedFailure = null;
                try
                {
                    recordedFailure = await _protocol.ReadRecordedFailureAsync(
                        project.Path, current.HandoffId, current.Revision.Value,
                        current.RunAttemptId, current.LastControlHash,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                   InvalidOperationException or JsonException or
                                                   UnauthorizedAccessException)
                {
                    // A damaged immutable record is not a usable terminal outcome.
                }
                if (recordedFailure is not null)
                {
                    var (restoredPath, restorationError) = await TryRecordFailureAsync(
                        project, current, recordedFailure.State, recordedFailure.Stage,
                        recordedFailure.Detail, cancellationToken).ConfigureAwait(false);
                    current = current with
                    {
                        State = recordedFailure.State,
                        ProcessId = null,
                        UpdatedAt = _clock.UtcNow,
                        Detail = restoredPath is null
                            ? $"{recordedFailure.Detail} Failure record unavailable: {restorationError}"
                            : recordedFailure.Detail,
                        FailurePath = restoredPath,
                        FailureStage = recordedFailure.Stage
                    };
                    await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
                    return current;
                }
            }
            var detail = "Recovered an interrupted runner without a valid report.";
            var (failurePath, failureError) = await TryRecordFailureAsync(
                project, current, RelayState.Stalled, "runtime-recovery", detail,
                cancellationToken).ConfigureAwait(false);
            if (failurePath is null)
                detail += $" Failure record unavailable: {failureError ?? "handoff identity is incomplete"}";
            current = current with
            {
                State = RelayState.Stalled,
                ProcessId = null,
                UpdatedAt = _clock.UtcNow,
                Detail = detail,
                FailurePath = failurePath,
                FailureStage = "runtime-recovery"
            };
            await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
            try
            {
                await _runtime.AppendLogAsync(
                    new ActionLogEntry(_clock.UtcNow, project.Id, "recovered", detail),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The recovered runtime state is already durable.
            }
        }
        return current;
    }

    private async Task<(string? Path, string? Error)> TryRecordFailureAsync(
        RegisteredProject project,
        ProjectRuntimeState state,
        RelayState failureState,
        string stage,
        string detail,
        CancellationToken cancellationToken)
    {
        if (_protocol is null || state.HandoffId is null || state.MissionId is null ||
            state.Revision is null || state.RunAttemptId is null || state.LastControlHash is null)
            return (null, "handoff identity is incomplete");

        var logDirectory = _runtime.ProjectLogDirectory(project.Id);
        var stdoutPath = Path.Combine(logDirectory, $"{state.RunAttemptId}.stdout.log");
        var stderrPath = Path.Combine(logDirectory, $"{state.RunAttemptId}.stderr.log");
        try
        {
            await _protocol.RecordFailureAsync(
                project.Path,
                new FailureEnvelope(
                    AgentRelayConstants.ProtocolVersion,
                    state.HandoffId,
                    state.MissionId,
                    state.Revision.Value,
                    state.RunAttemptId,
                    state.LastControlHash,
                    _clock.UtcNow,
                    failureState,
                    stage,
                    null,
                    detail,
                    File.Exists(stdoutPath) ? stdoutPath : null,
                    File.Exists(stderrPath) ? stderrPath : null),
                cancellationToken).ConfigureAwait(false);
            return (Path.Combine(project.Path, AgentRelayConstants.TransportDirectory,
                "reports", $"{state.HandoffId}-r{state.Revision}-{state.RunAttemptId}.failure.json"), null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or
                JsonException or UnauthorizedAccessException)
        {
            return (null, exception.Message);
        }
    }

    private static bool IsKnownTerminalDetail(string? detail)
        => detail is not null &&
           (detail.StartsWith("Runner exited", StringComparison.Ordinal) ||
            detail.StartsWith("agy.exe exited", StringComparison.Ordinal) ||
            detail.StartsWith("agy.exe failed to start", StringComparison.Ordinal) ||
            detail.StartsWith("Quota exhaustion confirmed", StringComparison.Ordinal) ||
            detail.StartsWith("Runner hard timeout", StringComparison.Ordinal) ||
            detail.StartsWith("Runner stalled with no filesystem", StringComparison.Ordinal) ||
            detail.StartsWith("Recovered an interrupted runner", StringComparison.Ordinal));

    private static GlobalAgyLease? TryAcquireRecoveryLease()
    {
        try
        {
            return GlobalAgyLease.TryAcquire();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool? ProcessMatches(ProjectRuntimeState state)
    {
        if (state.ProcessId is null || string.IsNullOrWhiteSpace(state.RunnerPath))
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(state.ProcessId.Value);
            return !process.HasExited &&
                   string.Equals(
                       process.MainModule?.FileName,
                       Path.GetFullPath(state.RunnerPath),
                       StringComparison.OrdinalIgnoreCase) &&
                   (state.ProcessStartedAt is null ||
                    Math.Abs((process.StartTime.ToUniversalTime() -
                              state.ProcessStartedAt.Value.UtcDateTime).TotalSeconds) < 1);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
