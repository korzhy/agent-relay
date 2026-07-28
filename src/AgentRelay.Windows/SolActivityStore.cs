using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed class SolActivityStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files;
    private readonly IClock _clock;

    public SolActivityStore(AppPaths paths, AtomicFileStore files, IClock? clock = null)
    {
        _paths = paths;
        _files = files;
        _clock = clock ?? new SystemClock();
    }

    public string PathFor(string projectId)
        => Path.Combine(_paths.RuntimeDirectory, projectId, "activity.json");

    public async Task<SolActivity?> GetAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var activity = await _files.ReadJsonAsync<SolActivity>(PathFor(projectId), cancellationToken)
            .ConfigureAwait(false);
        activity?.Validate();
        return activity;
    }

    public async Task<SolActivity> SetAsync(
        RegisteredProject project,
        SolActivityPhase phase,
        string summary,
        string? missionId = null,
        string? handoffId = null,
        string source = SolActivity.CodexSource,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Activity summary is required.", nameof(summary));
        }

        var now = _clock.UtcNow;
        var activity = new SolActivity(
            SolActivity.CurrentSchemaVersion,
            project.Id,
            phase,
            summary.Trim(),
            NormalizeGuid(missionId, nameof(missionId)),
            NormalizeGuid(handoffId, nameof(handoffId)),
            now,
            now + DefaultLifetime,
            source);
        activity.Validate();
        await _files.WriteJsonAsync(PathFor(project.Id), activity, false, cancellationToken)
            .ConfigureAwait(false);
        return activity;
    }

    public Task ClearAsync(string projectId)
    {
        var path = PathFor(projectId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private static string? NormalizeGuid(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!Guid.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"{parameterName} must be a GUID.", parameterName);
        }
        return parsed.ToString("N");
    }
}
