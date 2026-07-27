using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed record ActionLogEntry(
    DateTimeOffset Timestamp,
    string ProjectId,
    string Action,
    string Detail,
    int? ExitCode = null);

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

    public Task<ProjectRuntimeState?> ReadAsync(
        string projectId,
        CancellationToken cancellationToken = default)
        => _files.ReadJsonAsync<ProjectRuntimeState>(StatePath(projectId), cancellationToken);

    public Task WriteAsync(ProjectRuntimeState state, CancellationToken cancellationToken = default)
        => _files.WriteJsonAsync(StatePath(state.ProjectId), state, false, cancellationToken);

    public async Task AppendLogAsync(ActionLogEntry entry, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(ProjectLogDirectory(entry.ProjectId), "actions.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(entry, JsonSupport.Options) + Environment.NewLine;
        await _logLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _logLock.Release();
        }
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
