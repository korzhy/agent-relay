using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentRelay.Core;
using AgentRelay.Windows;

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
            "activity" => await ActivityAsync(services, args, cancellationToken),
            "handoff" => await HandoffAsync(services, args, cancellationToken),
            "codex" => await CodexAsync(services, args, cancellationToken),
            "update" => await UpdateAsync(services, args, cancellationToken),
            "--help" or "-h" or "help" => Help(),
            _ => throw new ArgumentException($"Unknown command: {args[0]}")
        };
    }

    private static async Task<int> ActivityAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        Require(args, 2, "activity get|set|clear --project <id|path>");
        var projectKey = Option(args, "--project") ??
                         throw new ArgumentException("--project <id|path> is required.");
        var action = args[1].ToLowerInvariant();
        var project = await services.Projects.FindAsync(projectKey, cancellationToken);
        if (project is null && action == "set" && Directory.Exists(projectKey))
        {
            project = await services.Projects.AddAsync(projectKey, cancellationToken);
        }
        if (project is null)
        {
            throw new KeyNotFoundException($"Project is not registered: {projectKey}");
        }

        switch (action)
        {
            case "get":
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Activity.GetAsync(project.Id, cancellationToken), JsonSupport.Options));
                return 0;
            case "clear":
                await services.Activity.ClearAsync(project.Id);
                return 0;
            case "set":
                var phaseText = Option(args, "--phase") ??
                                throw new ArgumentException("--phase <phase> is required.");
                if (!Enum.TryParse<SolActivityPhase>(phaseText, true, out var phase))
                {
                    throw new ArgumentException($"Invalid Sol activity phase: {phaseText}");
                }
                var summary = Option(args, "--summary") ??
                              throw new ArgumentException("--summary <text> is required.");
                var activity = await services.Activity.SetAsync(
                    project,
                    phase,
                    summary,
                    Option(args, "--mission"),
                    Option(args, "--handoff"),
                    SolActivity.CodexSource,
                    cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(activity, JsonSupport.Options));
                return 0;
            default:
                throw new ArgumentException($"Unknown activity action: {args[1]}");
        }
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
        var project = await services.Projects.FindAsync(projectKey, cancellationToken);

        switch (args[1].ToLowerInvariant())
        {
            case "status":
                if (project is null)
                {
                    throw new KeyNotFoundException($"Project is not registered: {projectKey}");
                }
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Recovery.RecoverAsync(project, cancellationToken), JsonSupport.Options));
                return 0;
            case "cancel":
                if (project is null)
                {
                    throw new KeyNotFoundException($"Project is not registered: {projectKey}");
                }
                await CancelRunnerAsync(services, project, cancellationToken);
                var cancelled = await services.Protocol.CancelAsync(
                    project.Path, "Cancelled by user.", cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        status = "paused",
                        cancelled.HandoffId,
                        cancelled.MissionId,
                        cancelled.Revision,
                        detail = "The active handoff is cancelled and future dispatch is blocked until resume."
                    },
                    JsonSupport.Options));
                return 0;
            case "resume":
                if (project is null)
                {
                    throw new KeyNotFoundException($"Project is not registered: {projectKey}");
                }
                await services.Runtime.SetPausedAsync(
                    project, false, new SystemClock(), cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        status = "ready",
                        detail = "Future dispatch is enabled. Cancelled or interrupted handoffs are not restarted; publish a new handoff."
                    },
                    JsonSupport.Options));
                return 0;
            case "publish":
                var workspace = project?.Path ?? WorkspaceSafety.Validate(projectKey);
                var policy = await services.Policy.GetAsync(
                    services.Paths.CodexPolicyFile, cancellationToken: cancellationToken);
                if (policy.Level == DelegationLevel.Off)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            status = "delegationOff",
                            detail = "External delegation threshold is OFF."
                        },
                        JsonSupport.Options));
                    return 6;
                }

                var updateState = await services.Updates.GetStateAsync(cancellationToken);
                if (updateState is
                    {
                        Status: UpdateStatus.Installing
                    } &&
                    DateTimeOffset.UtcNow - updateState.CheckedAt <
                    UpdateService.InstallationReservationLifetime)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            status = "updateInstalling",
                            detail = "Hand-off is blocked while a verified Agent Relay update is installing."
                        },
                        JsonSupport.Options));
                    return 8;
                }

                project ??= await services.Projects.AddAsync(workspace, cancellationToken);
                if (project.TrustedAt is null)
                {
                    var accepted = !args.Contains("--no-trust-prompt", StringComparer.OrdinalIgnoreCase) &&
                                   RequestWorkspaceTrust(project.Path);
                    if (!accepted)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                status = "trustRequired",
                                projectId = project.Id,
                                workspace = project.Path,
                                detail = "No repository transport was created and agy.exe was not started."
                            },
                            JsonSupport.Options));
                        return 5;
                    }
                    project = await services.Projects.TrustAsync(project.Id, cancellationToken);
                }

                if (services.Runtime.IsPaused(project.Id))
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            status = "dispatchPaused",
                            projectId = project.Id,
                            detail = "Durable dispatch pause is armed. Run handoff resume, then publish again; no handoff was created."
                        },
                        JsonSupport.Options));
                    return 7;
                }

                var taskPath = Option(args, "--task")
                    ?? throw new ArgumentException("--task <file> is required.");
                var title = Option(args, "--title") ?? Path.GetFileNameWithoutExtension(taskPath);
                var instructions = await File.ReadAllTextAsync(taskPath, cancellationToken);
                var gates = Options(args, "--gate");
                var missionId = Option(args, "--mission");
                await services.Activity.SetAsync(
                    project,
                    SolActivityPhase.Delegating,
                    $"Sol передаёт Gemini executor ограниченную задачу: {title}.",
                    missionId,
                    cancellationToken: cancellationToken);
                var modelSelection = await services.Models.ResolveAsync(
                    services.Doctor.ResolveAgyPath(), cancellationToken);
                await services.Runtime.AppendLogAsync(
                    new ActionLogEntry(
                        DateTimeOffset.UtcNow,
                        project.Id,
                        "model-resolved",
                        $"model={modelSelection.Executor.Model} source={modelSelection.Source}; " +
                        modelSelection.Detail),
                    cancellationToken);
                var handoff = await services.Protocol.PublishAsync(
                    project.Path,
                    new MissionRequest(title, instructions, gates, missionId),
                    modelSelection.Executor,
                    cancellationToken);
                await services.Activity.SetAsync(
                    project,
                    SolActivityPhase.Delegating,
                    $"Sol передал Gemini executor ограниченную задачу: {title}.",
                    handoff.Control.MissionId,
                    handoff.Control.HandoffId,
                    cancellationToken: cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(handoff.Control, JsonSupport.Options));
                var result = await services.CreateRunner().RunAsync(
                    project, handoff, services.Doctor.ResolveAgyPath(), cancellationToken);
                await services.Activity.SetAsync(
                    project,
                    result.State == RelayState.ReportReady
                        ? SolActivityPhase.Reviewing
                        : SolActivityPhase.Blocked,
                    result.State == RelayState.ReportReady
                        ? "Отчёт Gemini executor получен; Sol должен независимо проверить результат."
                        : result.Detail,
                    handoff.Control.MissionId,
                    handoff.Control.HandoffId,
                    cancellationToken: cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonSupport.Options));
                return result.State == RelayState.ReportReady ? 0 : 4;
            default:
                throw new ArgumentException($"Unknown handoff action: {args[1]}");
        }
    }

    private static bool RequestWorkspaceTrust(string workspace)
        => System.Windows.MessageBox.Show(
               $"Разрешить Agent Relay запускать Gemini executor с правом редактирования только в этой папке?\n\n" +
               $"{workspace}\n\n" +
               "Будет использована самая поздно обнаруженная доступная Gemini High; exact model будет " +
               "зафиксирована в handoff перед запуском с accept-edits. " +
               "Это однократное доверие конкретному workspace и не меняет глобальный порог делегирования.",
               "Agent Relay — доверие workspace",
               System.Windows.MessageBoxButton.YesNo,
               System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

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

    private static async Task<int> UpdateAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        Require(args, 2, "update status|check|set|apply");
        switch (args[1].ToLowerInvariant())
        {
            case "status":
                Console.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        currentVersion = services.Updates.CurrentVersion,
                        settings = await services.Updates.GetSettingsAsync(cancellationToken),
                        state = await services.Updates.GetStateAsync(cancellationToken)
                    },
                    JsonSupport.Options));
                return 0;
            case "set":
                Require(args, 3, "update set on|off");
                var enabled = args[2].ToLowerInvariant() switch
                {
                    "on" => true,
                    "off" => false,
                    _ => throw new ArgumentException("Usage: AgentRelay.exe update set on|off")
                };
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Updates.SetEnabledAsync(enabled, cancellationToken),
                    JsonSupport.Options));
                return 0;
            case "check":
                var checkedState = await services.Updates.CheckAsync(true, cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(checkedState, JsonSupport.Options));
                return checkedState.Status == UpdateStatus.Failed ? 4 : 0;
            case "apply":
                var state = await services.Updates.CheckAsync(false, cancellationToken);
                if (state.Status is not (UpdateStatus.Staged or UpdateStatus.Deferred))
                {
                    Console.WriteLine(JsonSerializer.Serialize(state, JsonSupport.Options));
                    return state.Status == UpdateStatus.Failed ? 4 : 3;
                }
                if (await HasActiveRunnerAsync(services, cancellationToken))
                {
                    var deferred = await services.Updates.MarkDeferredAsync(
                        state,
                        "Обновление отложено до завершения активного Gemini runner.",
                        cancellationToken);
                    Console.WriteLine(JsonSerializer.Serialize(deferred, JsonSupport.Options));
                    return 7;
                }
                var executable = Environment.ProcessPath
                                 ?? throw new InvalidOperationException("Current executable path is unavailable.");
                if (!services.Updates.IsInstalledBuild(executable))
                {
                    throw new InvalidOperationException(
                        "Automatic install is allowed only from the per-user Agent Relay installation.");
                }
                await services.Updates.LaunchInstallerAsync(state, cancellationToken);
                Console.WriteLine("Verified Agent Relay update installer started.");
                return 0;
            default:
                throw new ArgumentException($"Unknown update action: {args[1]}");
        }
    }

    private static async Task<bool> HasActiveRunnerAsync(
        RelayServices services,
        CancellationToken cancellationToken)
    {
        foreach (var project in await services.Projects.ListAsync(cancellationToken))
        {
            var state = await services.Recovery.RecoverAsync(project, cancellationToken);
            if (state.State is RelayState.Running or RelayState.Waiting)
            {
                return true;
            }
        }
        return false;
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
              activity get|clear --project <id|path>
              activity set --project <id|path> --phase <phase> --summary <text> [--mission <id>] [--handoff <id>]
              handoff publish --project <id|path> --task <file> [--title <text>] [--mission <id>] [--gate <command> ...]
              handoff status --project <id|path>
              handoff cancel --project <id|path>  (cancel active handoff and pause future dispatch)
              handoff resume --project <id|path>  (enable future dispatch; never replay a handoff)
              codex install|repair|remove
              update status
              update check
              update set on|off
              update apply
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
