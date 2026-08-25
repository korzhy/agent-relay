using System;
using System.IO;
using System.Threading.Tasks;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class PolicyServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AtomicFileStore _files;
    private readonly PolicyService _service;

    public PolicyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRelayCoreTests_Policy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _files = new AtomicFileStore();
        _service = new PolicyService(_files);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task GetAsync_ReturnsDefaultPolicy_WhenGlobalFileDoesNotExist()
    {
        var globalPath = Path.Combine(_tempDir, "global-policy.json");
        var policy = await _service.GetAsync(globalPath);

        Assert.NotNull(policy);
        Assert.Equal(AgentRelayConstants.PolicySchemaVersion, policy.SchemaVersion);
        Assert.True(policy.Enabled);
        Assert.Equal(DelegationLevel.Medium, policy.Level);
        Assert.Equal(AgentRelayConstants.Provider, policy.PreferredExecutor.Provider);
        Assert.Equal(AgentRelayConstants.ModelSelector, policy.PreferredExecutor.Model);
    }

    [Fact]
    public async Task GetAsync_AppliesProjectOverride_WithPrecedence()
    {
        var globalPath = Path.Combine(_tempDir, "global-policy.json");
        await _service.SetLevelAsync(globalPath, DelegationLevel.High);

        var projectRoot = Path.Combine(_tempDir, "my-project");
        var codexDir = Path.Combine(projectRoot, ".codex");
        Directory.CreateDirectory(codexDir);

        var overridePath = Path.Combine(codexDir, "external-agent-delegation.json");
        var overrideJson = """
        {
            "schemaVersion": 1,
            "level": "Low"
        }
        """;
        await File.WriteAllTextAsync(overridePath, overrideJson);

        var policy = await _service.GetAsync(globalPath, projectRoot);
        Assert.True(policy.Enabled);
        Assert.Equal(DelegationLevel.Low, policy.Level);
    }

    [Fact]
    public async Task GetAsync_NormalizesOffLevelToDisabled()
    {
        var globalPath = Path.Combine(_tempDir, "global-policy.json");
        await _service.SetLevelAsync(globalPath, DelegationLevel.Off);

        var policy = await _service.GetAsync(globalPath);
        Assert.False(policy.Enabled);
        Assert.Equal(DelegationLevel.Off, policy.Level);
    }

    [Fact]
    public async Task SetLevelAsync_UpdatesAndPersistsPolicy()
    {
        var globalPath = Path.Combine(_tempDir, "global-policy.json");
        var updated = await _service.SetLevelAsync(globalPath, DelegationLevel.High);

        Assert.True(updated.Enabled);
        Assert.Equal(DelegationLevel.High, updated.Level);

        var loaded = await _service.GetAsync(globalPath);
        Assert.Equal(DelegationLevel.High, loaded.Level);
    }

    [Fact]
    public void Validate_ThrowsInvalidDataException_OnSchemaMismatch()
    {
        var policy = new DelegationPolicy(
            SchemaVersion: 99,
            Enabled: true,
            Level: DelegationLevel.Medium,
            PreferredExecutor: new ExecutorPreference(),
            UpdatedAt: DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => policy.Validate());
    }

    [Fact]
    public void Validate_ThrowsInvalidDataException_OnEnabledLevelMismatch()
    {
        var policy = new DelegationPolicy(
            SchemaVersion: AgentRelayConstants.PolicySchemaVersion,
            Enabled: true,
            Level: DelegationLevel.Off,
            PreferredExecutor: new ExecutorPreference(),
            UpdatedAt: DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => policy.Validate());
    }

    [Fact]
    public void Validate_ThrowsInvalidDataException_OnExecutorMismatch()
    {
        var policy = new DelegationPolicy(
            SchemaVersion: AgentRelayConstants.PolicySchemaVersion,
            Enabled: true,
            Level: DelegationLevel.Medium,
            PreferredExecutor: new ExecutorPreference("CustomProvider", "custom-model"),
            UpdatedAt: DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => policy.Validate());
    }

    [Fact]
    public async Task GetAsync_NormalizesLegacyFlashSelectorToCurrentSelector()
    {
        var globalPath = Path.Combine(_tempDir, "legacy-policy.json");
        var legacy = DelegationPolicy.CreateDefault() with
        {
            PreferredExecutor = new ExecutorPreference(
                AgentRelayConstants.Provider,
                AgentRelayConstants.LegacyFlashModelSelector)
        };
        await _files.WriteJsonAsync(globalPath, legacy);

        var loaded = await _service.GetAsync(globalPath);

        Assert.Equal(AgentRelayConstants.ModelSelector, loaded.PreferredExecutor.Model);
    }
}
