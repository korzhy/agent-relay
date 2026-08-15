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
        Assert.Contains(AgentRelayConstants.Model, executor.Detail, StringComparison.Ordinal);
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
            DoctorService.AgyModelCatalog.ContainsExactModel(output, AgentRelayConstants.Model));

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
