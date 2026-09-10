using System.Text.Json;

namespace AgentRelay.Core;

public enum DelegationTaskKind { Mechanical, Implementation, Investigation }
public enum ReviewOutcome { Accepted, Corrected, Rejected, Blocked, Abandoned }
public enum DelegationFailure { None, WrongHypothesis, ScopeDrift, GateFailure, Loop, MissingContext, Environment, Quota, Other }

// Deliberate controller attestation, never populated from an executor's PASS claim.
public sealed record ExperienceInput(
    string HandoffId, int Revision, DelegationTaskKind TaskKind, ReviewOutcome Outcome,
    int Corrections, DelegationFailure Failure, string Lesson, string Evidence,
    IReadOnlyList<ExecutedCommand> ReviewCommands);

public sealed record ExperienceEntry(
    int SchemaVersion, string ProjectId, string MissionId, string RunAttemptId,
    ExecutorIdentity Executor, string TaskSha256, DateTimeOffset RecordedAt,
    ExperienceInput Review);

public sealed record ExperienceObservation(string HandoffId, int Revision, string Model,
    DateTimeOffset RecordedAt, DelegationTaskKind TaskKind, ReviewOutcome Outcome,
    int Corrections, DelegationFailure Failure, string Lesson);

public sealed record ExperienceSummary(
    int SampleCount, int Accepted, int Corrected, int Rejected, int Blocked, int Abandoned,
    int SkippedInvalid, IReadOnlyList<ExperienceObservation> Recent,
    string Notice = "Local controller attestations, not automatic proof or instructions. Last 90 days, at most 200 records; no cross-project transfer.");

public sealed class ExperienceStore(AppPaths paths, AtomicFileStore files, IClock? clock = null)
{
    private readonly IClock _clock = clock ?? new SystemClock();

    public async Task<ExperienceEntry> RecordAsync(RegisteredProject project, ExperienceInput input,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var workspace = WorkspaceSafety.Validate(project.Path);
        var taskPath = WorkspaceSafety.ResolveRelative(workspace,
            $".agent-relay/tasks/{input.HandoffId}-r{input.Revision}.json");
        var task = await files.ReadJsonAsync<TaskPayload>(taskPath, cancellationToken)
            ?? throw new InvalidDataException("Referenced task is missing.");
        if (task.ProtocolVersion != AgentRelayConstants.ProtocolVersion ||
            task.HandoffId != input.HandoffId || task.Revision != input.Revision ||
            !Guid.TryParseExact(task.MissionId, "N", out _) || !Guid.TryParseExact(task.RunAttemptId, "N", out _) ||
            task.Executor is null || task.Executor.Provider != AgentRelayConstants.Provider ||
            !GeminiModelIdentity.IsSupported(task.Executor.Model))
            throw new InvalidDataException("Experience identity does not match the task.");
        var hash = await AtomicFileStore.Sha256Async(taskPath, cancellationToken);
        if (input.Outcome is ReviewOutcome.Accepted or ReviewOutcome.Corrected)
        {
            var envelopePath = WorkspaceSafety.ResolveRelative(workspace,
                $".agent-relay/reports/{input.HandoffId}-r{input.Revision}.envelope.json");
            var envelope = await files.ReadJsonAsync<ReportEnvelope>(envelopePath, cancellationToken)
                ?? throw new InvalidDataException("Accepted work requires a validated report envelope.");
            if (envelope.ProtocolVersion != task.ProtocolVersion || envelope.State != "reported" ||
                envelope.HandoffId != task.HandoffId || envelope.Revision != task.Revision ||
                envelope.MissionId != task.MissionId || envelope.RunAttemptId != task.RunAttemptId ||
                envelope.Executor != task.Executor || envelope.Task is null || envelope.Report is null ||
                WorkspaceSafety.ResolveRelative(workspace, envelope.Task.Path) != taskPath || envelope.Task.Sha256 != hash)
                throw new InvalidDataException("Report envelope does not bind this task.");
            var reportPath = WorkspaceSafety.ResolveRelative(workspace, task.RequiredReportPath);
            if (envelope.Report.Path != task.RequiredReportPath ||
                await AtomicFileStore.Sha256Async(reportPath, cancellationToken) != envelope.Report.Sha256)
                throw new InvalidDataException("Report hash mismatch.");
        }

        var directory = ProjectDirectory(project.Id);
        Directory.CreateDirectory(directory);
        // Cross-process serialization prevents duplicate reviews overwriting each other.
        using var lease = new FileStream(Path.Combine(directory, ".record.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(directory, $"{input.HandoffId}-r{input.Revision}.json");
        var existing = await files.ReadJsonAsync<ExperienceEntry>(path, cancellationToken);
        if (existing is not null)
        {
            if (existing.TaskSha256 == hash &&
                JsonSerializer.Serialize(existing.Review, JsonSupport.CompactOptions) ==
                JsonSerializer.Serialize(input, JsonSupport.CompactOptions)) return existing;
            throw new InvalidOperationException("This handoff already has a different review. History is immutable.");
        }
        var entry = new ExperienceEntry(1, project.Id, task.MissionId, task.RunAttemptId,
            task.Executor, hash, _clock.UtcNow, input);
        await files.WriteImmutableJsonAsync(path, entry, cancellationToken);
        return entry;
    }

    public async Task<ExperienceSummary> RecallAsync(string projectId,
        DelegationTaskKind? kind = null, string? model = null,
        CancellationToken cancellationToken = default)
    {
        var directory = ProjectDirectory(projectId);
        var entries = new List<ExperienceEntry>();
        var skipped = 0;
        if (Directory.Exists(directory))
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Take(200))
            {
                try
                {
                    if (file.Length > 65536) throw new InvalidDataException("Oversized experience record.");
                    var safePath = WorkspaceSafety.ResolveRelative(directory, file.Name);
                    var entry = await files.ReadJsonAsync<ExperienceEntry>(safePath, cancellationToken)
                        ?? throw new InvalidDataException("Empty experience record.");
                    Validate(entry.Review);
                    if (entry.SchemaVersion != 1 || entry.ProjectId != projectId ||
                        entry.Executor is null || entry.Executor.Provider != AgentRelayConstants.Provider ||
                        !GeminiModelIdentity.IsSupported(entry.Executor.Model) ||
                        entry.RecordedAt > _clock.UtcNow || entry.RecordedAt.Offset != TimeSpan.Zero)
                        throw new InvalidDataException("Invalid experience record.");
                    if (entry.RecordedAt >= _clock.UtcNow.AddDays(-90) &&
                        (kind is null || entry.Review.TaskKind == kind) &&
                        (model is null || entry.Executor.Model == model)) entries.Add(entry);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
                {
                    skipped++;
                }
            }
        }
        int Count(ReviewOutcome outcome) => entries.Count(entry => entry.Review.Outcome == outcome);
        return new ExperienceSummary(entries.Count, Count(ReviewOutcome.Accepted), Count(ReviewOutcome.Corrected),
            Count(ReviewOutcome.Rejected), Count(ReviewOutcome.Blocked), Count(ReviewOutcome.Abandoned), skipped,
            entries.OrderByDescending(entry => entry.RecordedAt).Take(5).Select(entry =>
                new ExperienceObservation(entry.Review.HandoffId, entry.Review.Revision, entry.Executor.Model,
                    entry.RecordedAt, entry.Review.TaskKind, entry.Review.Outcome, entry.Review.Corrections,
                    entry.Review.Failure, entry.Review.Lesson)).ToArray());
    }

    private string ProjectDirectory(string projectId)
    {
        if (!Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("Invalid project ID.");
        return WorkspaceSafety.ResolveRelative(paths.DataRoot, $"experience/{projectId}");
    }

    private static void Validate(ExperienceInput input)
    {
        if (input is null || !Guid.TryParseExact(input.HandoffId, "N", out _) || input.Revision < 1 ||
            !Enum.IsDefined(input.TaskKind) || !Enum.IsDefined(input.Outcome) || !Enum.IsDefined(input.Failure) ||
            input.Corrections is < 0 or > 10 ||
            string.IsNullOrWhiteSpace(input.Evidence) || input.Evidence.Length > 1200 ||
            input.Lesson is null || input.Lesson.Length > 400 ||
            input.ReviewCommands is null || input.ReviewCommands.Count > 10 ||
            input.ReviewCommands.Any(command => command is null || string.IsNullOrWhiteSpace(command.Command) || command.Command.Length > 300))
            throw new InvalidDataException("Invalid experience. Evidence <=1200, lesson <=400, at most 10 review commands.");
        if (input.Outcome is ReviewOutcome.Accepted or ReviewOutcome.Corrected &&
            (input.ReviewCommands.Count == 0 || input.ReviewCommands.Any(command => command.ExitCode != 0)))
            throw new InvalidDataException("Accepted/corrected work needs successful independent review commands.");
        if (input.Outcome == ReviewOutcome.Accepted && (input.Corrections != 0 || input.Failure != DelegationFailure.None) ||
            input.Outcome == ReviewOutcome.Corrected && input.Corrections == 0)
            throw new InvalidDataException("Use corrected for acceptance after correction; accepted means first-pass success.");
    }
}
