using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRelay.Core;
using AgentRelay.Windows;
using Xunit;

namespace AgentRelay.IntegrationTests;

public sealed class CodexIntegrationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _appPaths;
    private readonly AtomicFileStore _files;
    private readonly string _skillSource;
    private readonly CodexIntegrationService _service;

    public CodexIntegrationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRelayIntegration_Codex_" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(_tempDir, "Home");
        var local = Path.Combine(_tempDir, "LocalAppData");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);

        _appPaths = new AppPaths(home, local);
        _files = new AtomicFileStore();

        var solutionRoot = GetSolutionRoot();
        _skillSource = Path.Combine(solutionRoot, "src", "AgentRelay.App", "Assets", "external-agent-delegation");

        _service = new CodexIntegrationService(_appPaths, _files, _skillSource);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task InstallOrRepairAsync_InstallsIntegration_WithDefaultMediumPolicyAndModel()
    {
        await _service.InstallOrRepairAsync();

        Assert.True(File.Exists(_appPaths.CodexAgentsFile));
        Assert.True(File.Exists(_appPaths.CodexPolicyFile));
        Assert.True(File.Exists(Path.Combine(_appPaths.CodexSkillDirectory, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_appPaths.CodexSkillDirectory, ".agent-relay-owned")));
        Assert.True(File.Exists(_appPaths.IntegrationManifest));

        var policyText = await File.ReadAllTextAsync(_appPaths.CodexPolicyFile);
        var policy = JsonSerializer.Deserialize<DelegationPolicy>(policyText, JsonSupport.Options);

        Assert.NotNull(policy);
        Assert.True(policy.Enabled);
        Assert.Equal(DelegationLevel.Medium, policy.Level);
        Assert.Equal(AgentRelayConstants.Provider, policy.PreferredExecutor.Provider);
        Assert.Equal(AgentRelayConstants.Model, policy.PreferredExecutor.Model);

        var agentsText = await File.ReadAllTextAsync(_appPaths.CodexAgentsFile);
        var skillText = await File.ReadAllTextAsync(
            Path.Combine(_appPaths.CodexSkillDirectory, "SKILL.md"));
        Assert.Contains("%LOCALAPPDATA%\\Programs\\AgentRelay\\AgentRelay.exe", agentsText);
        Assert.Contains("the Relay GUI does not need to be running", agentsText);
        Assert.Contains("never grant `project trust`", agentsText);
        Assert.Contains("activity set --project", skillText);
        Assert.Contains("handoff publish --project", skillText);
        Assert.Contains("Never invoke `project trust`", skillText);
    }

    [Fact]
    public async Task InstallOrRepairAsync_IsIdempotent_AndPreservesForeignAgentsContent()
    {
        Directory.CreateDirectory(_appPaths.CodexDirectory);
        var foreignContent = "# Foreign User Header\n\nSome custom agent instructions here.\n";
        await File.WriteAllTextAsync(_appPaths.CodexAgentsFile, foreignContent);

        // First install
        await _service.InstallOrRepairAsync();
        var contentAfterFirst = await File.ReadAllTextAsync(_appPaths.CodexAgentsFile);

        Assert.Contains("# Foreign User Header", contentAfterFirst);
        Assert.Contains(AgentRelayConstants.ManagedBlockStart, contentAfterFirst);

        // Second install (repair / idempotent)
        await _service.InstallOrRepairAsync();
        var contentAfterSecond = await File.ReadAllTextAsync(_appPaths.CodexAgentsFile);

        Assert.Equal(contentAfterFirst, contentAfterSecond);
    }

    [Fact]
    public async Task InstallOrRepairAsync_CreatesBackups_WhenUnmanagedFilesExist()
    {
        Directory.CreateDirectory(_appPaths.CodexSkillDirectory);
        await File.WriteAllTextAsync(Path.Combine(_appPaths.CodexSkillDirectory, "unmanaged.txt"), "old skill");

        Directory.CreateDirectory(_appPaths.CodexDirectory);
        await File.WriteAllTextAsync(_appPaths.CodexPolicyFile, "{\"schemaVersion\":1}");

        await _service.InstallOrRepairAsync();

        var ownership = await _files.ReadJsonAsync<CodexIntegrationOwnership>(_appPaths.IntegrationManifest);
        Assert.NotNull(ownership);
        Assert.NotNull(ownership.SkillBackupDirectory);
        Assert.True(Directory.Exists(ownership.SkillBackupDirectory));
        Assert.True(File.Exists(Path.Combine(ownership.SkillBackupDirectory, "unmanaged.txt")));

        Assert.NotNull(ownership.PolicyBackupPath);
        Assert.True(File.Exists(ownership.PolicyBackupPath));
    }

    [Fact]
    public async Task RemoveAsync_SafelyRemovesIntegration_AndRestoresBackups()
    {
        Directory.CreateDirectory(_appPaths.CodexSkillDirectory);
        await File.WriteAllTextAsync(Path.Combine(_appPaths.CodexSkillDirectory, "unmanaged.txt"), "old skill");

        await _service.InstallOrRepairAsync();
        await _service.RemoveAsync();

        Assert.False(File.Exists(Path.Combine(_appPaths.CodexSkillDirectory, ".agent-relay-owned")));
        Assert.True(File.Exists(Path.Combine(_appPaths.CodexSkillDirectory, "unmanaged.txt")));
        Assert.False(File.Exists(_appPaths.IntegrationManifest));
    }

    [Fact]
    public async Task RemoveAsync_PreservesForeignAgentsAndRestoresForeignPolicy()
    {
        Directory.CreateDirectory(_appPaths.CodexDirectory);
        var foreignAgents = "# Foreign rules\n\nKeep this text.\n";
        await File.WriteAllTextAsync(_appPaths.CodexAgentsFile, foreignAgents);
        var foreignPolicy = """
            {
              "schemaVersion": 1,
              "enabled": true,
              "level": "high",
              "preferredExecutor": {
                "provider": "Antigravity",
                "model": "gemini-3.6-flash-high"
              },
              "updatedAt": "2026-01-01T00:00:00Z"
            }
            """;
        await File.WriteAllTextAsync(_appPaths.CodexPolicyFile, foreignPolicy);

        await _service.InstallOrRepairAsync();
        await _service.RemoveAsync();

        Assert.Equal(foreignAgents, await File.ReadAllTextAsync(_appPaths.CodexAgentsFile));
        Assert.Equal(foreignPolicy, await File.ReadAllTextAsync(_appPaths.CodexPolicyFile));
    }

    private static string GetSolutionRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "AgentRelay.sln")))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return dir ?? throw new DirectoryNotFoundException("AgentRelay.sln not found");
    }
}
