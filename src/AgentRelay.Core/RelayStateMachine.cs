namespace AgentRelay.Core;

public sealed record ProjectRuntimeState(
    int SchemaVersion,
    string ProjectId,
    RelayState State,
    string? HandoffId,
    string? MissionId,
    int? Revision,
    string? RunAttemptId,
    int? ProcessId,
    DateTimeOffset UpdatedAt,
    string? Detail,
    string? LastControlHash,
    string? ReviewPromptPath,
    string? RunnerPath = null);

public static class RelayStateMachine
{
    private static readonly IReadOnlyDictionary<RelayState, RelayState[]> Allowed =
        new Dictionary<RelayState, RelayState[]>
        {
            [RelayState.Ready] = [RelayState.Running, RelayState.Paused],
            [RelayState.Running] =
                [RelayState.Waiting, RelayState.Stalled, RelayState.QuotaExhausted, RelayState.ReportReady,
                 RelayState.Paused],
            [RelayState.Waiting] =
                [RelayState.Running, RelayState.Stalled, RelayState.QuotaExhausted, RelayState.ReportReady,
                 RelayState.Paused],
            [RelayState.Stalled] = [RelayState.Running, RelayState.Paused, RelayState.Ready],
            [RelayState.QuotaExhausted] = [RelayState.Running, RelayState.Paused, RelayState.Ready],
            [RelayState.ReportReady] = [RelayState.Ready, RelayState.Running, RelayState.Paused],
            [RelayState.Paused] = [RelayState.Ready, RelayState.Running, RelayState.Stalled]
        };

    public static ProjectRuntimeState Transition(
        ProjectRuntimeState current,
        RelayState next,
        DateTimeOffset now,
        string? detail = null)
    {
        if (current.State != next && !Allowed[current.State].Contains(next))
        {
            throw new InvalidOperationException($"Invalid relay transition {current.State} -> {next}.");
        }

        return current with { State = next, UpdatedAt = now, Detail = detail };
    }
}

public sealed class DuplicateHashGuard
{
    private string? _lastHash;

    public bool TryAccept(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) ||
            string.Equals(hash, _lastHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _lastHash = hash;
        return true;
    }
}
