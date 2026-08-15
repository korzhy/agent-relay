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
        var executor = report.Checks.Single(check => check.Name == "Flash executor");
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
    public void ModelCatalog_SelectsNewestNumericFlashHigh()
    {
        const string output = """
            gemini-3.6-flash-high Gemini 3.6 Flash (High)
            gemini-3.10-flash-high Gemini 3.10 Flash (High)
            gemini-3.7-flash-medium Gemini 3.7 Flash (Medium)
            gemini-3.7-flash-high Gemini 3.7 Flash (High)
            claude-sonnet-4-6 Claude Sonnet
            """;

        Assert.Equal("gemini-3.10-flash-high", AgyModelCatalog.SelectLatestFlashHigh(output));
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

        var cached = await service.ResolveAsync(Path.Combine(local, "missing-agy.exe"));
        Assert.Equal(ModelSelectionSource.Cache, cached.Source);
        Assert.Equal(discovered.Executor, cached.Executor);
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
