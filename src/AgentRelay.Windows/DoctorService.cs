using System.Diagnostics;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed record DoctorCheck(string Name, bool Ready, string Detail);

public sealed record DoctorReport(
    bool Ready,
    DateTimeOffset CheckedAt,
    string CodexPath,
    string AgyPath,
    IReadOnlyList<DoctorCheck> Checks);

public sealed class DoctorService
{
    private readonly AppPaths _paths;
    private readonly IClock _clock;

    public DoctorService(AppPaths paths, IClock? clock = null)
    {
        _paths = paths;
        _clock = clock ?? new SystemClock();
    }

    public async Task<DoctorReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var codexPath = FindCodexApp();
        var agyPath = ResolveAgyPath();
        var checks = new List<DoctorCheck>
        {
            new("Codex App", codexPath is not null,
                codexPath ?? "Codex App executable was not found."),
            new("Antigravity CLI", File.Exists(agyPath),
                File.Exists(agyPath) ? agyPath : $"Missing: {agyPath}")
        };

        if (File.Exists(agyPath))
        {
            var modelCheck = await CheckExactModelAsync(agyPath, cancellationToken).ConfigureAwait(false);
            checks.Add(modelCheck);
        }
        else
        {
            checks.Add(new DoctorCheck(
                "Flash executor",
                false,
                $"Cannot verify exact model {AgentRelayConstants.Model} without agy.exe."));
        }

        checks.Add(CheckCodexIntegration());
        return new DoctorReport(
            checks.All(item => item.Ready),
            _clock.UtcNow,
            codexPath ?? string.Empty,
            agyPath,
            checks);
    }

    public string ResolveAgyPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("AGENTRELAY_AGY_PATH");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(_paths.LocalAppDataDirectory, "agy", "bin", "agy.exe")
            : Path.GetFullPath(overridePath);
    }

    private string? FindCodexApp()
    {
        var overridePath = Environment.GetEnvironmentVariable("AGENTRELAY_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var candidates = new[]
        {
            Path.Combine(_paths.LocalAppDataDirectory, "Programs", "Codex", "Codex.exe"),
            Path.Combine(_paths.LocalAppDataDirectory, "Codex", "Codex.exe")
        };
        var direct = candidates.FirstOrDefault(File.Exists);
        if (direct is not null)
        {
            return direct;
        }

        try
        {
            foreach (var process in Process.GetProcessesByName("Codex"))
            {
                using (process)
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // App package process metadata can be inaccessible; package discovery below remains available.
        }

        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            return Directory.EnumerateFiles(
                    windowsApps, "codex.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains(
                    $"{Path.DirectorySeparatorChar}OpenAI.Codex_", StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<DoctorCheck> CheckExactModelAsync(
        string agyPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var start = new ProcessStartInfo(agyPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("models");
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("agy models did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var exact = process.ExitCode == 0 && AgyModelCatalog.ContainsExactModel(
                stdout,
                AgentRelayConstants.Model);
            return new DoctorCheck(
                "Flash executor",
                exact,
                exact
                    ? $"{AgentRelayConstants.Provider} / {AgentRelayConstants.Model}"
                    : $"Exact model {AgentRelayConstants.Model} is unavailable. Exit {process.ExitCode}: {stderr.Trim()}");
        }
        catch (OperationCanceledException)
        {
            return new DoctorCheck("Flash executor", false, "agy models timed out.");
        }
        catch (Exception exception)
        {
            return new DoctorCheck("Flash executor", false, exception.Message);
        }
    }

    public static class AgyModelCatalog
    {
        public static bool ContainsExactModel(string output, string exactModel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exactModel);

            foreach (var line in output.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.AsSpan().IndexOfAny(' ', '\t');
                var model = separator < 0 ? line.AsSpan() : line.AsSpan(0, separator);
                if (model.Equals(exactModel, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private DoctorCheck CheckCodexIntegration()
    {
        var agentsReady = File.Exists(_paths.CodexAgentsFile) &&
                          File.ReadAllText(_paths.CodexAgentsFile).Contains(
                              AgentRelayConstants.ManagedBlockStart, StringComparison.Ordinal);
        var skillReady = File.Exists(Path.Combine(_paths.CodexSkillDirectory, ".agent-relay-owned"));
        var policyReady = false;
        try
        {
            var policy = File.Exists(_paths.CodexPolicyFile)
                ? System.Text.Json.JsonSerializer.Deserialize<DelegationPolicy>(
                    File.ReadAllText(_paths.CodexPolicyFile), JsonSupport.Options)
                : null;
            policy?.Validate();
            policyReady = policy is not null;
        }
        catch (Exception)
        {
            policyReady = false;
        }

        return new DoctorCheck(
            "Codex integration",
            agentsReady && skillReady && policyReady,
            agentsReady && skillReady && policyReady
                ? "Managed AGENTS block, policy, and external-agent-delegation skill are installed."
                : "Run `AgentRelay.exe codex install` or use Repair in the dashboard.");
    }
}
