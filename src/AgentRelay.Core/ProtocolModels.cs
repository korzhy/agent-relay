namespace AgentRelay.Core;

public enum ReportClaim
{
    Pass,
    Fail,
    Blocked,
    Unverified
}

public enum RelayState
{
    Ready,
    Running,
    Waiting,
    Stalled,
    QuotaExhausted,
    ReportReady,
    Paused
}

public sealed record PayloadReference(string Path, string Sha256);

public sealed record ExecutorIdentity(string Provider, string Model);

public sealed record TaskPayload(
    int ProtocolVersion,
    string HandoffId,
    string MissionId,
    int Revision,
    string RunAttemptId,
    ExecutorIdentity Executor,
    DateTimeOffset CreatedAt,
    string Title,
    string Instructions,
    IReadOnlyList<string> DeterministicGates,
    IReadOnlyList<string> ProhibitedActions,
    string RequiredReportPath);

public sealed record ControlEnvelope(
    int ProtocolVersion,
    string HandoffId,
    string MissionId,
    int Revision,
    string? ParentHandoffId,
    string RunAttemptId,
    string State,
    ExecutorIdentity Executor,
    DateTimeOffset CreatedAt,
    PayloadReference Task,
    string RequiredReportPath);

public sealed record ExecutedCommand(string Command, int ExitCode);

public sealed record ProhibitedActionConfirmation(
    bool NoArchitectureAcceptance,
    bool NoSecurityAcceptance,
    bool NoFinalReadinessAcceptance,
    bool NoDeployOrPush,
    bool NoProductionAccess,
    bool NoSecretsAccess,
    bool NoIrreversibleActions);

public sealed record ReportPayload(
    int ProtocolVersion,
    string HandoffId,
    string MissionId,
    int Revision,
    string RunAttemptId,
    ExecutorIdentity Executor,
    DateTimeOffset CreatedAt,
    ReportClaim Claim,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ExecutedCommand> Commands,
    string? FirstFailure,
    IReadOnlyList<string> UnavailableDependencies,
    ProhibitedActionConfirmation ProhibitedActions,
    string Summary);

public sealed record ReportEnvelope(
    int ProtocolVersion,
    string HandoffId,
    string MissionId,
    int Revision,
    string RunAttemptId,
    string State,
    ExecutorIdentity Executor,
    DateTimeOffset CreatedAt,
    PayloadReference Control,
    PayloadReference Task,
    PayloadReference Report,
    string ReviewPromptPath,
    string? ReviewAttemptId = null);

public sealed record ReviewEnvelope(
    int ProtocolVersion,
    string HandoffId,
    string MissionId,
    int Revision,
    string ReviewAttemptId,
    string State,
    ExecutorIdentity ImplementedBy,
    string Reviewer,
    DateTimeOffset CreatedAt,
    PayloadReference ReportEnvelope,
    PayloadReference Prompt);

public sealed record CancelEnvelope(
    int ProtocolVersion,
    string HandoffId,
    string MissionId,
    int Revision,
    DateTimeOffset CreatedAt,
    string Reason);

public sealed record MissionRequest(
    string Title,
    string Instructions,
    IReadOnlyList<string> DeterministicGates,
    string? MissionId = null);

public sealed record PublishedHandoff(
    string WorkspaceRoot,
    string ControlPath,
    string ControlHash,
    string TaskPath,
    string TaskHash,
    string ExpectedReportPath,
    ControlEnvelope Control);
