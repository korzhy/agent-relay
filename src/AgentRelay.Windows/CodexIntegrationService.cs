using System.Text;
using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed record CodexIntegrationOwnership(
    int SchemaVersion,
    bool AgentsFileCreated,
    string? SkillBackupDirectory,
    bool PolicyCreated,
    string? PolicyBackupPath,
    string InstalledPolicyHash,
    DateTimeOffset UpdatedAt);

public sealed class CodexIntegrationService
{
    private const string OwnershipMarker = "Owned by Agent Relay. Safe to remove only through Agent Relay.";
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files;
    private readonly IClock _clock;
    private readonly string _skillSource;

    public CodexIntegrationService(
        AppPaths paths,
        AtomicFileStore files,
        string skillSource,
        IClock? clock = null)
    {
        _paths = paths;
        _files = files;
        _skillSource = skillSource;
        _clock = clock ?? new SystemClock();
    }

    public async Task InstallOrRepairAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_skillSource) ||
            !File.Exists(Path.Combine(_skillSource, "SKILL.md")))
        {
            throw new DirectoryNotFoundException($"Bundled skill is missing: {_skillSource}");
        }

        Directory.CreateDirectory(_paths.CodexDirectory);
        Directory.CreateDirectory(_paths.IntegrationDirectory);
        var existingOwnership = await _files.ReadJsonAsync<CodexIntegrationOwnership>(
            _paths.IntegrationManifest, cancellationToken).ConfigureAwait(false);
        var stamp = _clock.UtcNow.ToString("yyyyMMddHHmmssfff");

        var agentsCreated = existingOwnership?.AgentsFileCreated ?? !File.Exists(_paths.CodexAgentsFile);
        var agentsText = File.Exists(_paths.CodexAgentsFile)
            ? await File.ReadAllTextAsync(_paths.CodexAgentsFile, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var mergedAgents = ManagedBlockEditor.Upsert(agentsText, ManagedAgentsBlock());
        if (!string.Equals(Normalize(agentsText), Normalize(mergedAgents), StringComparison.Ordinal))
        {
            await _files.WriteTextAsync(
                _paths.CodexAgentsFile, mergedAgents, createBackup: File.Exists(_paths.CodexAgentsFile),
                cancellationToken).ConfigureAwait(false);
        }

        var skillBackup = existingOwnership?.SkillBackupDirectory;
        var markerPath = Path.Combine(_paths.CodexSkillDirectory, ".agent-relay-owned");
        if (Directory.Exists(_paths.CodexSkillDirectory) && !File.Exists(markerPath) &&
            string.IsNullOrWhiteSpace(skillBackup))
        {
            skillBackup = Path.Combine(_paths.IntegrationDirectory, $"skill-backup-{stamp}");
            Directory.Move(_paths.CodexSkillDirectory, skillBackup);
        }
        if (Directory.Exists(_paths.CodexSkillDirectory))
        {
            DeleteDirectoryContents(_paths.CodexSkillDirectory);
        }
        else
        {
            Directory.CreateDirectory(_paths.CodexSkillDirectory);
        }
        CopyDirectory(_skillSource, _paths.CodexSkillDirectory);
        await File.WriteAllTextAsync(markerPath, OwnershipMarker, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var policyCreated = existingOwnership?.PolicyCreated ?? !File.Exists(_paths.CodexPolicyFile);
        var policyBackup = existingOwnership?.PolicyBackupPath;
        DelegationPolicy policy;
        if (File.Exists(_paths.CodexPolicyFile))
        {
            if (string.IsNullOrWhiteSpace(policyBackup) && existingOwnership is null)
            {
                policyBackup = Path.Combine(_paths.IntegrationDirectory, $"policy-backup-{stamp}.json");
                File.Copy(_paths.CodexPolicyFile, policyBackup);
            }
            policy = JsonSerializer.Deserialize<DelegationPolicy>(
                         await File.ReadAllTextAsync(_paths.CodexPolicyFile, cancellationToken).ConfigureAwait(false),
                         JsonSupport.Options)
                     ?? DelegationPolicy.CreateDefault(_clock);
            var normalizedPolicy = policy with
            {
                SchemaVersion = AgentRelayConstants.PolicySchemaVersion,
                PreferredExecutor = new ExecutorPreference(),
            };
            normalizedPolicy = normalizedPolicy.Level == DelegationLevel.Off
                ? normalizedPolicy with { Enabled = false }
                : normalizedPolicy with { Enabled = true };
            if (normalizedPolicy != policy)
            {
                normalizedPolicy = normalizedPolicy with { UpdatedAt = _clock.UtcNow };
            }
            policy = normalizedPolicy;
        }
        else
        {
            policy = DelegationPolicy.CreateDefault(_clock);
        }
        policy.Validate();
        var desiredPolicy = JsonSerializer.Serialize(policy, JsonSupport.Options) + Environment.NewLine;
        var policyNeedsWrite = !File.Exists(_paths.CodexPolicyFile) ||
                               !string.Equals(
                                   await AtomicFileStore.Sha256Async(_paths.CodexPolicyFile, cancellationToken)
                                       .ConfigureAwait(false),
                                   AtomicFileStore.Sha256Text(desiredPolicy),
                                   StringComparison.OrdinalIgnoreCase);
        if (policyNeedsWrite)
        {
            await _files.WriteTextAsync(
                _paths.CodexPolicyFile, desiredPolicy, createBackup: File.Exists(_paths.CodexPolicyFile),
                cancellationToken).ConfigureAwait(false);
        }
        var policyHash = await AtomicFileStore.Sha256Async(_paths.CodexPolicyFile, cancellationToken)
            .ConfigureAwait(false);

        var updatedAt = existingOwnership is not null &&
                        existingOwnership.AgentsFileCreated == agentsCreated &&
                        string.Equals(existingOwnership.SkillBackupDirectory, skillBackup, StringComparison.Ordinal) &&
                        existingOwnership.PolicyCreated == policyCreated &&
                        string.Equals(existingOwnership.PolicyBackupPath, policyBackup, StringComparison.Ordinal) &&
                        string.Equals(existingOwnership.InstalledPolicyHash, policyHash,
                            StringComparison.OrdinalIgnoreCase)
            ? existingOwnership.UpdatedAt
            : _clock.UtcNow;
        var ownership = new CodexIntegrationOwnership(
            1,
            agentsCreated,
            skillBackup,
            policyCreated,
            policyBackup,
            policyHash,
            updatedAt);
        if (existingOwnership != ownership)
        {
            await _files.WriteJsonAsync(
                _paths.IntegrationManifest, ownership, createBackup: File.Exists(_paths.IntegrationManifest),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        var ownership = await _files.ReadJsonAsync<CodexIntegrationOwnership>(
            _paths.IntegrationManifest, cancellationToken).ConfigureAwait(false);

        if (File.Exists(_paths.CodexAgentsFile))
        {
            var current = await File.ReadAllTextAsync(_paths.CodexAgentsFile, cancellationToken)
                .ConfigureAwait(false);
            var cleaned = ManagedBlockEditor.Remove(current);
            if (ownership?.AgentsFileCreated == true && string.IsNullOrWhiteSpace(cleaned))
            {
                File.Delete(_paths.CodexAgentsFile);
            }
            else if (!string.Equals(current, cleaned, StringComparison.Ordinal))
            {
                await _files.WriteTextAsync(_paths.CodexAgentsFile, cleaned, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var skillMarker = Path.Combine(_paths.CodexSkillDirectory, ".agent-relay-owned");
        if (Directory.Exists(_paths.CodexSkillDirectory) && File.Exists(skillMarker))
        {
            Directory.Delete(_paths.CodexSkillDirectory, recursive: true);
            if (!string.IsNullOrWhiteSpace(ownership?.SkillBackupDirectory) &&
                Directory.Exists(ownership.SkillBackupDirectory))
            {
                Directory.Move(ownership.SkillBackupDirectory, _paths.CodexSkillDirectory);
            }
        }

        if (ownership is not null && File.Exists(_paths.CodexPolicyFile))
        {
            var currentHash = await AtomicFileStore.Sha256Async(_paths.CodexPolicyFile, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(currentHash, ownership.InstalledPolicyHash, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(ownership.PolicyBackupPath) &&
                    File.Exists(ownership.PolicyBackupPath))
                {
                    File.Copy(ownership.PolicyBackupPath, _paths.CodexPolicyFile, overwrite: true);
                }
                else if (ownership.PolicyCreated)
                {
                    File.Delete(_paths.CodexPolicyFile);
                }
            }
        }

        if (File.Exists(_paths.IntegrationManifest))
        {
            File.Delete(_paths.IntegrationManifest);
        }
    }

    private static string ManagedAgentsBlock()
        => $"""
            {AgentRelayConstants.ManagedBlockStart}
            ## Agent Relay: Sol reasoning and external delegation

            - Treat the Codex UI-selected reasoning effort as the preferred starting point, not a hard floor or ceiling.
            - Sol may move freely between `high` and `xhigh` according to uncertainty, risk, and reasoning value, including lowering `xhigh` when it is excessive.
            - Use `medium` conservatively for bounded or mechanical work only when `high` clearly adds no material value.
            - Allowed efforts are `medium`, `high`, and `xhigh`. Do not select `low`, `max`, or `ultra` unless the user explicitly changes this rule.
            - Effort changes never relax validation, security, or final-integration responsibilities.
            - Resolve external delegation through `$HOME\.codex\external-agent-delegation.json`.
            - Delegation threshold and Flash model effort are separate settings. The exact executor is `Antigravity / gemini-3.6-flash-high`.
            - External agents never authorize architecture, security acceptance, final readiness, production, deploy, secrets, or irreversible actions.
            {AgentRelayConstants.ManagedBlockEnd}
            """;

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void DeleteDirectoryContents(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            Directory.Delete(child, recursive: true);
        }
    }

    private static string Normalize(string text)
        => text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
}

public static class ManagedBlockEditor
{
    public static string Upsert(string source, string block)
    {
        var without = Remove(source).TrimEnd();
        return string.IsNullOrWhiteSpace(without)
            ? block.Trim() + Environment.NewLine
            : without + Environment.NewLine + Environment.NewLine + block.Trim() + Environment.NewLine;
    }

    public static string Remove(string source)
    {
        var start = source.IndexOf(AgentRelayConstants.ManagedBlockStart, StringComparison.Ordinal);
        if (start < 0)
        {
            return source;
        }
        var end = source.IndexOf(AgentRelayConstants.ManagedBlockEnd, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException("Agent Relay managed AGENTS block is missing its end marker.");
        }
        end += AgentRelayConstants.ManagedBlockEnd.Length;
        return (source[..start] + source[end..]).Trim() + Environment.NewLine;
    }
}
