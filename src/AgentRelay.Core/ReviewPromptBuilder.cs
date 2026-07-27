namespace AgentRelay.Core;

public static class ReviewPromptBuilder
{
    public static string Build(ReportEnvelope envelope, ReportPayload report)
        => $"""
            Review the completed Agent Relay external hand-off in the currently open Codex task.

            Treat the implementer report as a claim, not evidence. Validate hashes first, run the first
            deterministic gate, then independently review semantics. Codex retains architecture,
            security, and final-readiness authority. Do not launch codex.exe from this prompt.

            Exact identity:
            - protocolVersion: {envelope.ProtocolVersion}
            - handoffId: {envelope.HandoffId}
            - missionId: {envelope.MissionId}
            - revision: {envelope.Revision}
            - runAttemptId: {envelope.RunAttemptId}
            - reviewAttemptId: {envelope.ReviewAttemptId}
            - executor: {envelope.Executor.Provider} / {envelope.Executor.Model}
            - control: {envelope.Control.Path}
            - controlSha256: {envelope.Control.Sha256}
            - task: {envelope.Task.Path}
            - taskSha256: {envelope.Task.Sha256}
            - report: {envelope.Report.Path}
            - reportSha256: {envelope.Report.Sha256}
            - implementerClaim: {report.Claim.ToString().ToUpperInvariant()}

            Stop at the first actionable machine failure. If evidence is unavailable, record
            UNVERIFIED or BLOCKED rather than accepting PASS. Never deploy, push, access production,
            handle secrets, or perform irreversible actions without separate user authorization.
            """;
}
