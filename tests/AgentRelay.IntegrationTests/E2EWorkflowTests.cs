using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using AgentRelay.Core;
using AgentRelay.Windows;
using Xunit;

namespace AgentRelay.IntegrationTests;

public sealed class E2EWorkflowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _appPaths;
    private readonly AtomicFileStore _files;
    private readonly ProtocolService _protocol;
    private readonly RuntimeStore _runtimeStore;
    private readonly ProjectRegistry _registry;

    public E2EWorkflowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRelayIntegration_E2E_" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(_tempDir, "Home");
        var local = Path.Combine(_tempDir, "LocalAppData");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);

        _appPaths = new AppPaths(home, local);
        _files = new AtomicFileStore();
        _protocol = new ProtocolService(_files);
        _runtimeStore = new RuntimeStore(_appPaths, _files);
        _registry = new ProjectRegistry(_files, _appPaths.ProjectsFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CompleteE2EWorkflow_GitRepo_Delegation_Execution_ReviewPrompt()
    {
        // 1. Initialize temporary Git repository
        var repoDir = Path.Combine(_tempDir, "e2e-repo");
        Directory.CreateDirectory(repoDir);
        RunGitCommand(repoDir, "init");
        RunGitCommand(repoDir, "config user.name \"Test User\"");
        RunGitCommand(repoDir, "config user.email \"test@example.com\"");

        var sampleFile = Path.Combine(repoDir, "README.md");
        await File.WriteAllTextAsync(sampleFile, "# Test Project\n");
        RunGitCommand(repoDir, "add README.md");
        RunGitCommand(repoDir, "commit -m \"Initial commit\"");

        var gitStatusBeforeReg = RunGitCommand(repoDir, "status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(gitStatusBeforeReg));

        // 2. Register project - causes no repository changes
        var registered = await _registry.AddAsync(repoDir);
        registered = await _registry.TrustAsync(registered.Id);

        var gitStatusAfterReg = RunGitCommand(repoDir, "status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(gitStatusAfterReg));

        // 3. First delegation - creates only .agent-relay directory
        var request = new MissionRequest("E2E Mission", "fake-mode:pass", new[] { "gate-1" });
        var handoff = await _protocol.PublishAsync(repoDir, request);

        var rootEntries = Directory.GetFileSystemEntries(repoDir)
            .Select(Path.GetFileName)
            .Where(name => name != ".git" && name != "README.md")
            .ToArray();

        Assert.Single(rootEntries);
        Assert.Equal(".agent-relay", rootEntries[0]);

        // 4. Run dispatch with fake agy.exe
        var fastOptions = new RunnerOptions(TimeSpan.FromMinutes(15), TimeSpan.FromHours(2), TimeSpan.FromMilliseconds(10));
        var runner = new AgyRunner(_protocol, _runtimeStore, options: fastOptions);
        var agyPath = GetFakeAgyPath();

        var result = await runner.RunAsync(registered, handoff, agyPath);

        Assert.Equal(RelayState.ReportReady, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.ReviewPromptPath);
        Assert.True(File.Exists(result.ReviewPromptPath));

        // 5. Verify review prompt and review.json content
        var promptText = await File.ReadAllTextAsync(result.ReviewPromptPath);

        Assert.Contains($"handoffId: {handoff.Control.HandoffId}", promptText);
        Assert.Contains($"missionId: {handoff.Control.MissionId}", promptText);
        Assert.Contains($"runAttemptId: {handoff.Control.RunAttemptId}", promptText);

        Assert.Contains($"controlSha256: {handoff.ControlHash}", promptText);
        Assert.Contains($"taskSha256: {handoff.TaskHash}", promptText);

        var reportPath = WorkspaceSafety.ResolveRelative(repoDir, handoff.Control.RequiredReportPath);
        var reportHash = await AtomicFileStore.Sha256Async(reportPath);
        Assert.Contains($"reportSha256: {reportHash}", promptText);

        var reviewJsonPath = Path.Combine(repoDir, ".agent-relay", "review.json");
        Assert.True(File.Exists(reviewJsonPath));
        var reviewEnvelope = await _files.ReadJsonAsync<ReviewEnvelope>(reviewJsonPath);
        Assert.NotNull(reviewEnvelope);
        Assert.False(string.IsNullOrWhiteSpace(reviewEnvelope.ReviewAttemptId));
        Assert.Contains($"reviewAttemptId: {reviewEnvelope.ReviewAttemptId}", promptText);

        var immutableEnvelopeFullPath = Path.Combine(repoDir, reviewEnvelope.ReportEnvelope.Path);
        Assert.True(File.Exists(immutableEnvelopeFullPath));
        var expectedReportEnvelopeHash = await AtomicFileStore.Sha256Async(immutableEnvelopeFullPath);
        Assert.Equal(expectedReportEnvelopeHash, reviewEnvelope.ReportEnvelope.Sha256);

        var promptFullPath = Path.Combine(repoDir, reviewEnvelope.Prompt.Path);
        Assert.True(File.Exists(promptFullPath));
        var expectedPromptHash = await AtomicFileStore.Sha256Async(promptFullPath);
        Assert.Equal(expectedPromptHash, reviewEnvelope.Prompt.Sha256);
    }

    private static string RunGitCommand(string workingDir, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {arguments} failed ({proc.ExitCode}): {stderr}");
        }
        return stdout;
    }

    private static string GetFakeAgyPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var baseAgy = Path.Combine(baseDir, "agy.exe");
        if (File.Exists(baseAgy)) return baseAgy;

        var solutionRoot = GetSolutionRoot();
        var releasePath = Path.Combine(solutionRoot, "tests", "AgentRelay.FakeAgy", "bin", "Release", "net8.0", "agy.exe");
        if (File.Exists(releasePath)) return releasePath;

        var debugPath = Path.Combine(solutionRoot, "tests", "AgentRelay.FakeAgy", "bin", "Debug", "net8.0", "agy.exe");
        if (File.Exists(debugPath)) return debugPath;

        throw new FileNotFoundException("Fake agy.exe not found.");
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
