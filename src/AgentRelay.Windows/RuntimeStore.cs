using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed record ActionLogEntry(
    DateTimeOffset Timestamp,
    string ProjectId,
    string Action,
    string Detail,
    int? ExitCode = null);

public sealed record ActionLogReadResult(
    IReadOnlyList<ActionLogEntry> Entries,
    int InvalidRecordCount,
    bool HasIncompleteTail);

public sealed class RuntimeStore
{
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files;
    private readonly SemaphoreSlim _logLock = new(1, 1);

    public RuntimeStore(AppPaths paths, AtomicFileStore files)
    {
        _paths = paths;
        _files = files;
    }

    public string StatePath(string projectId)
        => Path.Combine(_paths.RuntimeDirectory, projectId, "state.json");

    public string PausePath(string projectId)
        => Path.Combine(_paths.RuntimeDirectory, projectId, "paused.flag");

    public string ProjectLogDirectory(string projectId)
        => Path.Combine(_paths.LogsDirectory, projectId);

    public string ActionLogPath(string projectId)
        => Path.Combine(ProjectLogDirectory(projectId), "actions.jsonl");

    public Task<ProjectRuntimeState?> ReadAsync(
        string projectId,
        CancellationToken cancellationToken = default)
        => _files.ReadJsonAsync<ProjectRuntimeState>(StatePath(projectId), cancellationToken);

    public Task WriteAsync(ProjectRuntimeState state, CancellationToken cancellationToken = default)
        => _files.WriteJsonAsync(StatePath(state.ProjectId), state, false, cancellationToken);

    public async Task AppendLogAsync(ActionLogEntry entry, CancellationToken cancellationToken = default)
    {
        var path = ActionLogPath(entry.ProjectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(entry, JsonSupport.CompactOptions) + Environment.NewLine;
        await _logLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var separator = string.Empty;
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                await using var tail = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                tail.Seek(-1, SeekOrigin.End);
                if (tail.ReadByte() is not (byte)'\n')
                {
                    separator = Environment.NewLine;
                }
            }
            await File.AppendAllTextAsync(path, separator + line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _logLock.Release();
        }
    }

    public async Task<ActionLogReadResult> ReadLogAsync(
        string projectId,
        int maximumEntries = 50,
        CancellationToken cancellationToken = default)
    {
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        var path = ActionLogPath(projectId);
        if (!File.Exists(path))
        {
            return new ActionLogReadResult([], 0, false);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = new List<ActionLogEntry>();
        var invalid = 0;
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (start < 0)
            {
                if (character == '{')
                {
                    start = index;
                    depth = 1;
                    inString = false;
                    escaped = false;
                }
                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ActionLogEntry>(
                        text[start..(index + 1)], JsonSupport.Options);
                    if (entry is null)
                    {
                        invalid++;
                    }
                    else
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    invalid++;
                }
                start = -1;
            }
        }

        return new ActionLogReadResult(
            entries.TakeLast(maximumEntries).ToArray(),
            invalid,
            start >= 0);
    }

    public async Task SetPausedAsync(
        RegisteredProject project,
        bool paused,
        IClock clock,
        CancellationToken cancellationToken = default)
    {
        var pausePath = PausePath(project.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(pausePath)!);
        if (paused)
        {
            await File.WriteAllTextAsync(
                pausePath, clock.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(pausePath))
        {
            File.Delete(pausePath);
        }

        var current = await ReadAsync(project.Id, cancellationToken).ConfigureAwait(false)
            ?? NewReady(project, clock.UtcNow);
        var next = current with
        {
            State = paused ? RelayState.Paused : RelayState.Ready,
            ProcessId = null,
            UpdatedAt = clock.UtcNow,
            Detail = paused ? "Dispatch pause is armed." : "Dispatch is enabled."
        };
        await WriteAsync(next, cancellationToken).ConfigureAwait(false);
        await AppendLogAsync(new ActionLogEntry(
            clock.UtcNow, project.Id, paused ? "pause" : "resume", next.Detail), cancellationToken)
            .ConfigureAwait(false);
    }

    public static ProjectRuntimeState NewReady(RegisteredProject project, DateTimeOffset now)
        => new(
            1, project.Id, RelayState.Ready, null, null, null, null, null,
            now, "Ready for a trusted hand-off.", null, null);
}
