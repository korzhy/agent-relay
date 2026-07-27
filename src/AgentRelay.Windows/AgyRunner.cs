using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed record RunnerOptions(
    TimeSpan StallTimeout,
    TimeSpan HardTimeout,
    TimeSpan MonitorInterval)
{
    public static RunnerOptions Default { get; } =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(2), TimeSpan.FromMilliseconds(250));
}

public sealed record RunnerResult(
    RelayState State,
    int? ExitCode,
    string Detail,
    string? ReviewPromptPath);

public sealed class AgyRunner
{
    private static readonly string[] QuotaPatterns =
    [
        "quota exhausted",
        "resource_exhausted",
        "rate limit exceeded",
        "insufficient quota",
        "quota has been exhausted"
    ];

    private readonly ProtocolService _protocol;
    private readonly RuntimeStore _runtime;
    private readonly IClock _clock;
    private readonly RunnerOptions _options;

    public AgyRunner(
        ProtocolService protocol,
        RuntimeStore runtime,
        IClock? clock = null,
        RunnerOptions? options = null)
    {
        _protocol = protocol;
        _runtime = runtime;
        _clock = clock ?? new SystemClock();
        _options = options ?? RunnerOptions.Default;
    }

    public async Task<RunnerResult> RunAsync(
        RegisteredProject project,
        PublishedHandoff handoff,
        string agyExecutable,
        CancellationToken cancellationToken = default)
    {
        if (project.TrustedAt is null)
        {
            throw new InvalidOperationException(
                "Dispatch is blocked until one-time trust consent is recorded for this workspace.");
        }
        if (!string.Equals(
                WorkspaceSafety.Validate(project.Path),
                WorkspaceSafety.Validate(handoff.WorkspaceRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Registered project and handoff workspace do not match.");
        }
        if (!File.Exists(agyExecutable))
        {
            throw new FileNotFoundException("agy.exe was not found.", agyExecutable);
        }
        if (File.Exists(_runtime.PausePath(project.Id)))
        {
            return await FinishAsync(
                project, handoff, RelayState.Paused, null, "Dispatch pause is armed.", null, cancellationToken)
                .ConfigureAwait(false);
        }
        await _protocol.ValidateForDispatchAsync(handoff, cancellationToken).ConfigureAwait(false);

        return await Task.Factory.StartNew(
                () => RunWithMutex(project, handoff, agyExecutable, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .ConfigureAwait(false);
    }

    private RunnerResult RunWithMutex(
        RegisteredProject project,
        PublishedHandoff handoff,
        string agyExecutable,
        CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, MutexName(project.Path));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex)
            {
                throw new InvalidOperationException("Another runner already owns this project.");
            }

            return ExecuteOwnedAsync(
                project, handoff, agyExecutable, cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private async Task<RunnerResult> ExecuteOwnedAsync(
        RegisteredProject project,
        PublishedHandoff handoff,
        string agyExecutable,
        CancellationToken cancellationToken)
    {
        var logDirectory = _runtime.ProjectLogDirectory(project.Id);
        Directory.CreateDirectory(logDirectory);
        var stdoutPath = Path.Combine(logDirectory, $"{handoff.Control.RunAttemptId}.stdout.log");
        var stderrPath = Path.Combine(logDirectory, $"{handoff.Control.RunAttemptId}.stderr.log");
        var prompt = BuildRunnerPrompt(handoff);
        var start = new ProcessStartInfo(agyExecutable)
        {
            WorkingDirectory = project.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(AgentRelayConstants.Model);
        start.ArgumentList.Add("--mode");
        start.ArgumentList.Add("accept-edits");
        start.ArgumentList.Add("--dangerously-skip-permissions");
        start.ArgumentList.Add("--print");
        start.ArgumentList.Add(prompt);
        start.ArgumentList.Add("--print-timeout");
        start.ArgumentList.Add("2h");

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var lastActivityTimestamp = Stopwatch.GetTimestamp();
        void RecordActivity() => Interlocked.Exchange(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
        using var watcher = new FileSystemWatcher(project.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        FileSystemEventHandler onActivity = (_, args) =>
        {
            if (!args.FullPath.Contains(
                    $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                RecordActivity();
            }
        };
        RenamedEventHandler onRenamed = (_, _) => RecordActivity();
        watcher.Changed += onActivity;
        watcher.Created += onActivity;
        watcher.Deleted += onActivity;
        watcher.Renamed += onRenamed;
        watcher.EnableRaisingEvents = true;

        if (!process.Start())
        {
            return await FinishAsync(
                project, handoff, RelayState.Stalled, null, "agy.exe failed to start.", null,
                cancellationToken).ConfigureAwait(false);
        }

        var running = new ProjectRuntimeState(
            1,
            project.Id,
            RelayState.Running,
            handoff.Control.HandoffId,
            handoff.Control.MissionId,
            handoff.Control.Revision,
            handoff.Control.RunAttemptId,
            process.Id,
            _clock.UtcNow,
            $"Running {AgentRelayConstants.Provider} / {AgentRelayConstants.Model}.",
            handoff.ControlHash,
            null,
            Path.GetFullPath(agyExecutable));
        await _runtime.WriteAsync(running, cancellationToken).ConfigureAwait(false);
        await _runtime.AppendLogAsync(new ActionLogEntry(
            _clock.UtcNow, project.Id, "dispatch",
            $"handoff={handoff.Control.HandoffId} revision={handoff.Control.Revision} model={AgentRelayConstants.Model}"),
            cancellationToken).ConfigureAwait(false);

        var stdoutTask = CaptureAsync(process.StandardOutput, stdoutPath, RecordActivity);
        var stderrTask = CaptureAsync(process.StandardError, stderrPath, RecordActivity);
        var startedTimestamp = Stopwatch.GetTimestamp();
        var exitTask = process.WaitForExitAsync(cancellationToken);
        string? forcedReason = null;
        RelayState? forcedState = null;

        while (!exitTask.IsCompleted)
        {
            await Task.Delay(_options.MonitorInterval, cancellationToken).ConfigureAwait(false);
            if (File.Exists(_runtime.PausePath(project.Id)))
            {
                forcedReason = "Dispatch was paused; interrupted run has no completion claim.";
                forcedState = RelayState.Paused;
                KillTree(process);
                break;
            }
            if (Stopwatch.GetElapsedTime(startedTimestamp) > _options.HardTimeout)
            {
                forcedReason = "Runner hard timeout elapsed without a valid report.";
                forcedState = RelayState.Stalled;
                KillTree(process);
                break;
            }
            if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivityTimestamp)) > _options.StallTimeout)
            {
                forcedReason = "Runner stalled with no filesystem or process-output activity.";
                forcedState = RelayState.Stalled;
                KillTree(process);
                break;
            }
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Process already exited during termination.
        }
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var exitCode = process.HasExited ? process.ExitCode : (int?)null;

        if (forcedState is not null)
        {
            return await FinishAsync(
                project, handoff, forcedState.Value, exitCode, forcedReason!, null, cancellationToken)
                .ConfigureAwait(false);
        }

        var combined = (await ReadIfExistsAsync(stdoutPath, cancellationToken).ConfigureAwait(false)) + "\n" +
                       (await ReadIfExistsAsync(stderrPath, cancellationToken).ConfigureAwait(false));
        if (exitCode != 0)
        {
            var quota = QuotaPatterns.Any(
                pattern => combined.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            var state = quota ? RelayState.QuotaExhausted : RelayState.Stalled;
            var detail = quota
                ? "Quota exhaustion confirmed by actual runner output."
                : $"agy.exe exited with code {exitCode}; no valid completion was accepted.";
            return await FinishAsync(
                project, handoff, state, exitCode, detail, null, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var stableReportHash = await new StableFileGate().WaitAsync(
                handoff.ExpectedReportPath, cancellationToken).ConfigureAwait(false);
            if (stableReportHash is null)
            {
                throw new InvalidDataException(
                    "Runner exited without a stable report payload after debounce/hash validation.");
            }
            var reportEnvelope = await _protocol.AcceptReportAsync(handoff, cancellationToken)
                .ConfigureAwait(false);
            var reviewPromptPath = WorkspaceSafety.ResolveRelative(
                project.Path, reportEnvelope.ReviewPromptPath);
            return await FinishAsync(
                project, handoff, RelayState.ReportReady, exitCode,
                "Validated report is ready for independent Codex review.",
                reviewPromptPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or IOException)
        {
            return await FinishAsync(
                project, handoff, RelayState.Stalled, exitCode,
                $"Runner exited but report validation failed: {exception.Message}",
                null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RunnerResult> FinishAsync(
        RegisteredProject project,
        PublishedHandoff handoff,
        RelayState state,
        int? exitCode,
        string detail,
        string? reviewPromptPath,
        CancellationToken cancellationToken)
    {
        var runtime = new ProjectRuntimeState(
            1,
            project.Id,
            state,
            handoff.Control.HandoffId,
            handoff.Control.MissionId,
            handoff.Control.Revision,
            handoff.Control.RunAttemptId,
            null,
            _clock.UtcNow,
            detail,
            handoff.ControlHash,
            reviewPromptPath,
            null);
        await _runtime.WriteAsync(runtime, cancellationToken).ConfigureAwait(false);
        await _runtime.AppendLogAsync(new ActionLogEntry(
            _clock.UtcNow, project.Id, "complete", detail, exitCode), cancellationToken)
            .ConfigureAwait(false);
        return new RunnerResult(state, exitCode, detail, reviewPromptPath);
    }

    private static async Task CaptureAsync(
        StreamReader reader,
        string path,
        Action onActivity)
    {
        await using var writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onActivity();
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadIfExistsAsync(string path, CancellationToken cancellationToken)
        => File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static string MutexName(string workspace)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspace).ToUpperInvariant()));
        return $"Local\\AgentRelay-{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }

    private static string BuildRunnerPrompt(PublishedHandoff handoff)
        => $"""
            You are the external implementation executor for one Agent Relay hand-off.
            Read and follow the immutable task payload at:
            {handoff.Control.Task.Path}

            Exact immutable identity:
            - protocolVersion: {handoff.Control.ProtocolVersion}
            - handoffId: {handoff.Control.HandoffId}
            - missionId: {handoff.Control.MissionId}
            - revision: {handoff.Control.Revision}
            - runAttemptId: {handoff.Control.RunAttemptId}
            - taskSha256: {handoff.Control.Task.Sha256}
            - executor: {AgentRelayConstants.Provider} / {AgentRelayConstants.Model}

            Execute the task now in the current workspace. Write a single JSON report payload to:
            {handoff.Control.RequiredReportPath}

            The report must match Agent Relay protocol v1 and include:
            protocolVersion, handoffId, missionId, revision, runAttemptId, executor,
            createdAt, claim (pass|fail|blocked|unverified), changedFiles,
            commands (each with command and exitCode), firstFailure, unavailableDependencies,
            prohibitedActions with all seven confirmations true, and summary.

            PASS is forbidden when a required dependency or executable proof is unavailable.
            A crash or response text is not a report. Do not edit task/control payloads.
            Never accept architecture, security, or final readiness; never deploy/push,
            access production/secrets, or perform irreversible actions.
            """;
}
