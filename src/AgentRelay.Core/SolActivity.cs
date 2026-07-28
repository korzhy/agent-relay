namespace AgentRelay.Core;

public enum SolActivityPhase
{
    Evaluating,
    Delegating,
    WaitingForFlash,
    Working,
    Reviewing,
    Integrating,
    Completed,
    Blocked
}

public sealed record SolActivity(
    int SchemaVersion,
    string ProjectId,
    SolActivityPhase Phase,
    string Summary,
    string? MissionId,
    string? HandoffId,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    string Source)
{
    public const int CurrentSchemaVersion = 1;
    public const string CodexSource = "Codex / Sol";

    public bool IsFresh(DateTimeOffset now)
        => now <= ExpiresAt;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported activity schemaVersion: {SchemaVersion}");
        }
        if (string.IsNullOrWhiteSpace(ProjectId) ||
            string.IsNullOrWhiteSpace(Summary) ||
            string.IsNullOrWhiteSpace(Source))
        {
            throw new InvalidDataException("Activity projectId, summary, and source are required.");
        }
        if (UpdatedAt.Offset != TimeSpan.Zero ||
            ExpiresAt.Offset != TimeSpan.Zero ||
            ExpiresAt <= UpdatedAt)
        {
            throw new InvalidDataException("Activity timestamps must be UTC and expiresAt must follow updatedAt.");
        }
        if (MissionId is not null && !Guid.TryParse(MissionId, out _))
        {
            throw new InvalidDataException("Activity missionId must be a GUID.");
        }
        if (HandoffId is not null && !Guid.TryParse(HandoffId, out _))
        {
            throw new InvalidDataException("Activity handoffId must be a GUID.");
        }
    }
}

public sealed record MissionCandidate(
    string ProjectId,
    RelayState State,
    DateTimeOffset UpdatedAt);

public static class MissionSelector
{
    public static MissionCandidate? Select(IEnumerable<MissionCandidate> candidates)
        => candidates
            .OrderBy(candidate => Priority(candidate.State))
            .ThenByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefault();

    private static int Priority(RelayState state)
        => state switch
        {
            RelayState.Running or RelayState.Waiting => 0,
            RelayState.ReportReady or RelayState.Stalled or RelayState.QuotaExhausted or RelayState.Paused => 1,
            _ => 2
        };
}
