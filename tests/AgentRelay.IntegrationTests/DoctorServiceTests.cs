using AgentRelay.Core;
using AgentRelay.Windows;

namespace AgentRelay.IntegrationTests;

public sealed class DoctorServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "AgentRelayIntegration_Doctor_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_DetectsCodexAgyAndExactModel_FailsClosedOnMissingIntegration()
    {
        var home = Path.Combine(_tempDirectory, "home");
        var local = Path.Combine(_tempDirectory, "local");
        Directory.CreateDirectory(home);
        var codexPath = Path.Combine(local, "Programs", "Codex", "Codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
        await File.WriteAllTextAsync(codexPath, "doctor fixture");

        var fakeOutput = GetFakeAgyOutput();
        var agyDirectory = Path.Combine(local, "agy", "bin");
        Directory.CreateDirectory(agyDirectory);
        foreach (var source in Directory.EnumerateFiles(fakeOutput))
        {
            File.Copy(source, Path.Combine(agyDirectory, Path.GetFileName(source)));
        }

        var report = await new DoctorService(new AppPaths(home, local)).RunAsync();

        Assert.False(report.Ready);
        Assert.Equal(codexPath, report.CodexPath);
        Assert.Equal(Path.Combine(agyDirectory, "agy.exe"), report.AgyPath);
        Assert.True(report.Checks.Single(check => check.Name == "Codex App").Ready);
        Assert.True(report.Checks.Single(check => check.Name == "Antigravity CLI").Ready);
        var executor = report.Checks.Single(check => check.Name == "Gemini executor");
        Assert.True(executor.Ready);
        Assert.Contains("gemini-3.7-flash-high", executor.Detail, StringComparison.Ordinal);
        Assert.False(report.Checks.Single(check => check.Name == "Codex integration").Ready);
    }

    [Theory]
    [InlineData("gemini-3.6-flash-high", true)]
    [InlineData("gemini-3.6-flash-high\tGemini 3.6 Flash (High)", true)]
    [InlineData("gemini-3.6-flash-high Gemini 3.6 Flash (High)", true)]
    [InlineData("gemini-3.6-flash-high-malicious\tLookalike", false)]
    [InlineData("prefix-gemini-3.6-flash-high", false)]
    [InlineData("Gemini-3.6-Flash-High", false)]
    public void ModelCatalog_RequiresExactFirstColumn(string output, bool expected)
        => Assert.Equal(
            expected,
            AgyModelCatalog.ContainsExactModel(output, AgentRelayConstants.FallbackModel));

    [Fact]
    public void ModelCatalog_InitialBaselineUsesNumericVersionAsTieBreaker()
    {
        const string output = """
            gemini-3.6-flash-high Gemini 3.6 Flash (High)
            gemini-3.10-flash-high Gemini 3.10 Flash (High)
            gemini-3.7-flash-medium Gemini 3.7 Flash (Medium)
            gemini-3.7-flash-high Gemini 3.7 Flash (High)
            gemini-3.1-pro-high Gemini 3.1 Pro (High)
            claude-sonnet-4-6 Claude Sonnet
            """;

        var models = AgyModelCatalog.ParseModels(output);
        var discovery = AgyModelCatalog.RecordObservedModels(
            null, models, DateTimeOffset.Parse("2026-08-25T10:00:00Z"), out var changed);

        Assert.True(changed);
        Assert.Equal(
            "gemini-3.10-flash-high",
            AgyModelCatalog.SelectMostRecentlyObservedHigh(models, discovery));
    }

    [Fact]
    public void ModelCatalog_LaterObservedProWinsDespiteLowerNumericVersion()
    {
        var baseline = new[] { "gemini-3.7-flash-high", "gemini-3.1-pro-high" };
        var initial = AgyModelCatalog.RecordObservedModels(
            null,
            baseline,
            DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
            out _);
        var septemberCatalog = baseline.Append("gemini-3.5-pro-high").ToArray();
        var updated = AgyModelCatalog.RecordObservedModels(
            initial,
            septemberCatalog,
            DateTimeOffset.Parse("2026-09-15T10:00:00Z"),
            out var changed);

        Assert.True(changed);
        Assert.Equal(
            "gemini-3.5-pro-high",
            AgyModelCatalog.SelectMostRecentlyObservedHigh(septemberCatalog, updated));
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-15T10:00:00Z"),
            updated.Models.Single(entry => entry.Model == "gemini-3.5-pro-high").FirstSeenAt);

        var unchanged = AgyModelCatalog.RecordObservedModels(
            updated,
            septemberCatalog,
            DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
            out var changedAgain);
        Assert.False(changedAgain);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-15T10:00:00Z"),
            unchanged.Models.Single(entry => entry.Model == "gemini-3.5-pro-high").FirstSeenAt);
    }

    [Theory]
    [InlineData("gemini-3.7-flash-high", true)]
    [InlineData("gemini-3.5-pro-high", true)]
    [InlineData("gemini-3.5-pro-medium", false)]
    [InlineData("gemini-3.5-pro-high-malicious", false)]
    [InlineData("claude-4.5-sonnet-high", false)]
    public void GeminiModelIdentity_AcceptsOnlyExactGeminiHigh(string model, bool expected)
    {
        Assert.Equal(expected, GeminiModelIdentity.IsSupported(model));
    }

    [Fact]
    public async Task ModelSelection_ResolvesLatestAndFallsBackToVerifiedCache()
    {
        var home = Path.Combine(_tempDirectory, "selection-home");
        var local = Path.Combine(_tempDirectory, "selection-local");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        var paths = new AppPaths(home, local);
        var service = new AgyModelSelectionService(paths, new AtomicFileStore());
        var agyPath = Path.Combine(GetFakeAgyOutput(), "agy.exe");

        var discovered = await service.ResolveAsync(agyPath);
        Assert.Equal(ModelSelectionSource.Catalog, discovered.Source);
        Assert.Equal("gemini-3.7-flash-high", discovered.Executor.Model);
        Assert.True(File.Exists(paths.ModelSelectionFile));
        Assert.True(File.Exists(paths.ModelDiscoveryFile));

        var cached = await service.ResolveAsync(Path.Combine(local, "missing-agy.exe"));
        Assert.Equal(ModelSelectionSource.Cache, cached.Source);
        Assert.Equal(discovered.Executor, cached.Executor);
    }

    [Fact]
    public async Task ModelSelection_CorruptDiscoveryFailsSafeToVerifiedCacheWithoutOverwrite()
    {
        var home = Path.Combine(_tempDirectory, "corrupt-home");
        var local = Path.Combine(_tempDirectory, "corrupt-local");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        var paths = new AppPaths(home, local);
        var service = new AgyModelSelectionService(paths, new AtomicFileStore());
        var agyPath = Path.Combine(GetFakeAgyOutput(), "agy.exe");
        var discovered = await service.ResolveAsync(agyPath);
        const string corrupt = "{ not valid json";
        await File.WriteAllTextAsync(paths.ModelDiscoveryFile, corrupt);

        var result = await service.ResolveAsync(agyPath);

        Assert.Equal(ModelSelectionSource.Cache, result.Source);
        Assert.Equal(discovered.Executor, result.Executor);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(paths.ModelDiscoveryFile));
    }

    [Fact]
    public async Task ModelSelection_UsesBuiltInFallbackWithoutCatalogOrCache()
    {
        var home = Path.Combine(_tempDirectory, "fallback-home");
        var local = Path.Combine(_tempDirectory, "fallback-local");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        var service = new AgyModelSelectionService(
            new AppPaths(home, local), new AtomicFileStore());

        var result = await service.ResolveAsync(Path.Combine(local, "missing-agy.exe"));

        Assert.Equal(ModelSelectionSource.BuiltInFallback, result.Source);
        Assert.Equal(AgentRelayConstants.FallbackModel, result.Executor.Model);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Windows may briefly retain the executable mapping; the OS temp cleaner can reclaim it.
            }
            catch (UnauthorizedAccessException)
            {
                // Same recovery behavior as above.
            }
        }
    }

    private static string GetFakeAgyOutput()
    {
        var solutionRoot = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(solutionRoot, "AgentRelay.sln")))
        {
            solutionRoot = Directory.GetParent(solutionRoot)?.FullName
                ?? throw new DirectoryNotFoundException("AgentRelay.sln not found.");
        }

        var output = Path.Combine(
            solutionRoot, "tests", "AgentRelay.FakeAgy", "bin", "Release", "net8.0");
        if (!File.Exists(Path.Combine(output, "agy.exe")))
        {
            throw new FileNotFoundException("Fake agy.exe was not built.");
        }
        return output;
    }
}
