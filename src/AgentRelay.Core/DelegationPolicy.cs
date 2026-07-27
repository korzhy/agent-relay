namespace AgentRelay.Core;

public enum DelegationLevel
{
    Off,
    Low,
    Medium,
    High
}

public sealed record ExecutorPreference(
    string Provider = AgentRelayConstants.Provider,
    string Model = AgentRelayConstants.Model);

public sealed record DelegationPolicy(
    int SchemaVersion,
    bool Enabled,
    DelegationLevel Level,
    ExecutorPreference PreferredExecutor,
    DateTimeOffset UpdatedAt)
{
    public static DelegationPolicy CreateDefault(IClock? clock = null)
        => new(
            AgentRelayConstants.PolicySchemaVersion,
            true,
            DelegationLevel.Medium,
            new ExecutorPreference(),
            (clock ?? new SystemClock()).UtcNow);

    public DelegationPolicy WithLevel(DelegationLevel level, IClock clock)
        => this with
        {
            Enabled = level != DelegationLevel.Off,
            Level = level,
            UpdatedAt = clock.UtcNow
        };

    public void Validate()
    {
        if (SchemaVersion != AgentRelayConstants.PolicySchemaVersion)
        {
            throw new InvalidDataException($"Unsupported policy schemaVersion: {SchemaVersion}");
        }

        if (Enabled == (Level == DelegationLevel.Off))
        {
            throw new InvalidDataException("Policy enabled and level fields disagree.");
        }

        if (!string.Equals(PreferredExecutor.Provider, AgentRelayConstants.Provider, StringComparison.Ordinal) ||
            !string.Equals(PreferredExecutor.Model, AgentRelayConstants.Model, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Executor must be exactly {AgentRelayConstants.Provider} / {AgentRelayConstants.Model}.");
        }
    }
}

public sealed class PolicyService
{
    private readonly AtomicFileStore _files;
    private readonly IClock _clock;

    public PolicyService(AtomicFileStore files, IClock? clock = null)
    {
        _files = files;
        _clock = clock ?? new SystemClock();
    }

    public async Task<DelegationPolicy> GetAsync(
        string globalPath,
        string? projectRoot = null,
        CancellationToken cancellationToken = default)
    {
        var global = await _files.ReadJsonAsync<DelegationPolicy>(globalPath, cancellationToken)
            .ConfigureAwait(false) ?? DelegationPolicy.CreateDefault(_clock);
        global.Validate();

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return Normalize(global);
        }

        var overridePath = Path.Combine(Path.GetFullPath(projectRoot), ".codex", "external-agent-delegation.json");
        var project = await _files.ReadJsonAsync<ProjectPolicyOverride>(overridePath, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            return Normalize(global);
        }

        if (project.SchemaVersion is not null and not AgentRelayConstants.PolicySchemaVersion)
        {
            throw new InvalidDataException($"Unsupported project policy schemaVersion: {project.SchemaVersion}");
        }

        var merged = global with
        {
            Enabled = project.Enabled ?? (project.Level is null ? global.Enabled : project.Level != DelegationLevel.Off),
            Level = project.Level ?? global.Level,
            PreferredExecutor = project.PreferredExecutor ?? global.PreferredExecutor
        };
        merged = Normalize(merged);
        merged.Validate();
        return merged;
    }

    public async Task<DelegationPolicy> SetLevelAsync(
        string globalPath,
        DelegationLevel level,
        CancellationToken cancellationToken = default)
    {
        var current = await _files.ReadJsonAsync<DelegationPolicy>(globalPath, cancellationToken)
            .ConfigureAwait(false) ?? DelegationPolicy.CreateDefault(_clock);
        var updated = current.WithLevel(level, _clock) with
        {
            SchemaVersion = AgentRelayConstants.PolicySchemaVersion,
            PreferredExecutor = new ExecutorPreference()
        };
        updated.Validate();
        await _files.WriteJsonAsync(globalPath, updated, createBackup: File.Exists(globalPath), cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    private static DelegationPolicy Normalize(DelegationPolicy policy)
    {
        if (!policy.Enabled || policy.Level == DelegationLevel.Off)
        {
            return policy with { Enabled = false, Level = DelegationLevel.Off };
        }

        return policy;
    }

    private sealed record ProjectPolicyOverride(
        int? SchemaVersion,
        bool? Enabled,
        DelegationLevel? Level,
        ExecutorPreference? PreferredExecutor);
}
