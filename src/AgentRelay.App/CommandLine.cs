using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.App;

public static class CommandLine
{
    public static async Task<int> RunAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var command = args[0].ToLowerInvariant();
        return command switch
        {
            "doctor" => await DoctorAsync(services, args, cancellationToken),
            "quota" => await QuotaAsync(services, args, cancellationToken),
            "policy" => await PolicyAsync(services, args, cancellationToken),
            "project" => await ProjectAsync(services, args, cancellationToken),
            "handoff" => await HandoffAsync(services, args, cancellationToken),
            "codex" => await CodexAsync(services, args, cancellationToken),
            "--help" or "-h" or "help" => Help(),
            _ => throw new ArgumentException($"Unknown command: {args[0]}")
        };
    }

    private static async Task<int> QuotaAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var snapshot = await services.Quota.ReadAsync(cancellationToken);
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonSupport.Options));
        }
        else
        {
            Console.WriteLine(
                snapshot.HasPercentage
                    ? $"Prompt-credit quota: {snapshot.Detail} [{snapshot.Source}]"
                    : $"Prompt-credit quota: N/A — {snapshot.Detail}");
        }
        return snapshot.HasPercentage ? 0 : 3;
    }

    private static async Task<int> DoctorAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var report = await services.Doctor.RunAsync(cancellationToken);
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonSupport.Options));
        }
        else
        {
            foreach (var check in report.Checks)
            {
                Console.WriteLine($"{(check.Ready ? "READY" : "NOT READY")}  {check.Name}: {check.Detail}");
            }
        }
        return report.Ready ? 0 : 2;
    }

    private static async Task<int> PolicyAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        Require(args, 2, "policy get|set [off|low|medium|high]");
        if (args[1].Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            var project = Option(args, "--project");
            var policy = await services.Policy.GetAsync(
                services.Paths.CodexPolicyFile, project, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(policy, JsonSupport.Options));
            return 0;
        }
        if (args[1].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            Require(args, 3, "policy set off|low|medium|high");
            if (!Enum.TryParse<DelegationLevel>(args[2], true, out var level))
            {
                throw new ArgumentException($"Invalid delegation threshold: {args[2]}");
            }
            var policy = await services.Policy.SetLevelAsync(
                services.Paths.CodexPolicyFile, level, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(policy, JsonSupport.Options));
            return 0;
        }
        throw new ArgumentException($"Unknown policy action: {args[1]}");
    }

    private static async Task<int> ProjectAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        Require(args, 2, "project add|remove|list|trust");
        switch (args[1].ToLowerInvariant())
        {
            case "list":
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Projects.ListAsync(cancellationToken), JsonSupport.Options));
                return 0;
            case "add":
                Require(args, 3, "project add <path>");
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Projects.AddAsync(args[2], cancellationToken), JsonSupport.Options));
                return 0;
            case "remove":
                Require(args, 3, "project remove <id|path>");
                return await services.Projects.RemoveAsync(args[2], cancellationToken) ? 0 : 3;
            case "trust":
                Require(args, 3, "project trust <id|path>");
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Projects.TrustAsync(args[2], cancellationToken), JsonSupport.Options));
                return 0;
            default:
                throw new ArgumentException($"Unknown project action: {args[1]}");
        }
    }

    private static async Task<int> HandoffAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        Require(args, 2, "handoff publish|status|cancel|resume");
        var projectKey = Option(args, "--project") ??
                         throw new ArgumentException("--project <id|path> is required.");
        var project = await services.Projects.FindAsync(projectKey, cancellationToken)
            ?? throw new KeyNotFoundException($"Project is not registered: {projectKey}");

        switch (args[1].ToLowerInvariant())
        {
            case "status":
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Recovery.RecoverAsync(project, cancellationToken), JsonSupport.Options));
                return 0;
            case "cancel":
                await CancelRunnerAsync(services, project, cancellationToken);
                await services.Protocol.CancelAsync(project.Path, "Cancelled by user.", cancellationToken);
                return 0;
            case "resume":
                await services.Runtime.SetPausedAsync(
                    project, false, new SystemClock(), cancellationToken);
                return 0;
            case "publish":
                var taskPath = Option(args, "--task")
                    ?? throw new ArgumentException("--task <file> is required.");
                var title = Option(args, "--title") ?? Path.GetFileNameWithoutExtension(taskPath);
                var instructions = await File.ReadAllTextAsync(taskPath, cancellationToken);
                var gates = Options(args, "--gate");
                var missionId = Option(args, "--mission");
                var handoff = await services.Protocol.PublishAsync(
                    project.Path, new MissionRequest(title, instructions, gates, missionId), cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(handoff.Control, JsonSupport.Options));
                var result = await services.CreateRunner().RunAsync(
                    project, handoff, services.Doctor.ResolveAgyPath(), cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonSupport.Options));
                return result.State == RelayState.ReportReady ? 0 : 4;
            default:
                throw new ArgumentException($"Unknown handoff action: {args[1]}");
        }
    }

    private static async Task CancelRunnerAsync(
        RelayServices services,
        RegisteredProject project,
        CancellationToken cancellationToken)
    {
        var state = await services.Runtime.ReadAsync(project.Id, cancellationToken);
        await services.Runtime.SetPausedAsync(project, true, new SystemClock(), cancellationToken);
        if (state?.ProcessId is null || string.IsNullOrWhiteSpace(state.RunnerPath))
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(state.ProcessId.Value);
            if (!process.HasExited &&
                string.Equals(process.MainModule?.FileName, Path.GetFullPath(state.RunnerPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The durable pause is authoritative even if the process already exited.
        }
    }

    private static async Task<int> CodexAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        Require(args, 2, "codex install|repair|remove");
        switch (args[1].ToLowerInvariant())
        {
            case "install":
            case "repair":
                await services.Codex.InstallOrRepairAsync(cancellationToken);
                Console.WriteLine("Codex integration installed.");
                return 0;
            case "remove":
                await services.Codex.RemoveAsync(cancellationToken);
                Console.WriteLine("Agent Relay-owned Codex integration removed.");
                return 0;
            default:
                throw new ArgumentException($"Unknown codex action: {args[1]}");
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            Agent Relay
              doctor [--json]
              quota [--json]
              policy get [--project <path>]
              policy set off|low|medium|high
              project add|remove|trust <path-or-id>
              project list
              handoff publish --project <id|path> --task <file> [--title <text>] [--mission <id>] [--gate <command> ...]
              handoff status --project <id|path>
              handoff cancel --project <id|path>
              handoff resume --project <id|path>
              codex install|repair|remove
            """);
        return 0;
    }

    private static void Require(IReadOnlyList<string> args, int count, string usage)
    {
        if (args.Count < count)
        {
            throw new ArgumentException($"Usage: AgentRelay.exe {usage}");
        }
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static IReadOnlyList<string> Options(IReadOnlyList<string> args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(args[index + 1]);
            }
        }
        return values;
    }
}
