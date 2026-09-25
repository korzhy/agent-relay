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
            "account" => await AccountAsync(services, args, cancellationToken),
            "policy" => await PolicyAsync(services, args, cancellationToken),
            "project" => await ProjectAsync(services, args, cancellationToken),
            "activity" => await ActivityAsync(services, args, cancellationToken),
            "experience" => await ExperienceAsync(services, args, cancellationToken),
            "handoff" => await HandoffAsync(services, args, cancellationToken),
            "codex" => await CodexAsync(services, args, cancellationToken),
            "update" => await UpdateAsync(services, args, cancellationToken),
            "--help" or "-h" or "help" => Help(),
            _ => throw new ArgumentException($"Unknown command: {args[0]}")
        };
    }

    private static async Task<int> ExperienceAsync(RelayServices services,
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        Require(args, 2, "experience recall|record --project <id|path>");
        var projectKey = Option(args, "--project") ?? throw new ArgumentException("--project is required.");
        var project = await services.Projects.FindAsync(projectKey, cancellationToken);
        var store = new ExperienceStore(services.Paths, services.Files);
        if (args[1].Equals("recall", StringComparison.OrdinalIgnoreCase))
        {
            DelegationTaskKind? kind = null;
            if (Option(args, "--kind") is { } kindText)
                kind = Enum.TryParse<DelegationTaskKind>(kindText, true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed : throw new ArgumentException("--kind must be mechanical, implementation or investigation.");
            var summary = project is null
                ? new ExperienceSummary(0, 0, 0, 0, 0, 0, 0, [])
                : await store.RecallAsync(project.Id, kind, Option(args, "--model"), cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(summary, JsonSupport.Options));
            return 0;
        }
        if (!args[1].Equals("record", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown experience action.");
        if (project is null) throw new KeyNotFoundException("Project is not registered.");
        var inputPath = Option(args, "--file") ?? throw new ArgumentException("--file <review.json> is required.");
        if (new FileInfo(inputPath).Length > 65536) throw new InvalidDataException("Review input exceeds 64 KiB.");
        var input = await services.Files.ReadJsonAsync<ExperienceInput>(inputPath, cancellationToken)
            ?? throw new InvalidDataException("Review input is empty.");
        var entry = await store.RecordAsync(project, input, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(entry, JsonSupport.Options));
        return 0;
    }

    private static async Task<int> AccountAsync(
        RelayServices services,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        Require(args, 2, "account list|add|activate|refresh|remove|settings");
        var agyPath = services.Doctor.ResolveAgyPath();
        switch (args[1].ToLowerInvariant())
        {
            case "list":
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Accounts.ListAsync(cancellationToken), JsonSupport.Options));
                return 0;
            case "add":
                var label = Option(args, "--label") ??
                            throw new ArgumentException("--label <label> is required.");
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Accounts.AddAsync(
                        label, agyPath,
                        args.Contains("--activate", StringComparer.OrdinalIgnoreCase), cancellationToken),
                    JsonSupport.Options));
                return 0;
            case "activate":
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Accounts.ActivateAsync(
                        Option(args, "--id") ?? throw new ArgumentException("--id <id> is required."),
                        agyPath, cancellationToken), JsonSupport.Options));
                return 0;
            case "refresh":
                var all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
                var id = Option(args, "--id");
                if (!all && id is null)
                {
                    throw new ArgumentException("account refresh requires --id <id> or --all.");
                }
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Accounts.RefreshAsync(agyPath, id, all, cancellationToken),
                    JsonSupport.Options));
                return 0;
            case "remove":
                await services.Accounts.RemoveAsync(
                    Option(args, "--id") ?? throw new ArgumentException("--id <id> is required."),
                    cancellationToken);
                Console.WriteLine("{\"status\":\"removed\"}");
                return 0;
            case "settings":
                var value = Option(args, "--rotation-threshold");
                if (value is null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        await services.Accounts.GetSettingsAsync(cancellationToken), JsonSupport.Options));
                    return 0;
                }
                if (!int.TryParse(value, out var threshold))
                {
                    throw new ArgumentException("--rotation-threshold must be an integer from 1 to 50.");
                }
                Console.WriteLine(JsonSerializer.Serialize(
                    await services.Accounts.SetThresholdAsync(threshold, cancellationToken),
                    JsonSupport.Options));
                return 0;
            default:
                throw new ArgumentException($"Unknown account action: {args[1]}");
        }
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
                    throw new ArgumentException($"Invalid Codex activity phase: {phaseText}");
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
        var registry = await services.Accounts.ListAsync(cancellationToken);
        var snapshot = registry.Accounts.FirstOrDefault(account => account.Id == registry.ActiveAccountId);
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonSupport.Options));
        }
        else
        {
            Console.WriteLine(
                snapshot?.Quota is not null
                    ? $"Gemini quota: {snapshot.Quota.RemainingPercent}% [{snapshot.Label}]"
                    : "Gemini quota: N/A — no active managed account with a checked quota.");
        }
        return snapshot?.Quota is not null ? 0 : 3;
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
            {
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

                await services.Recovery.RecoverAsync(project, cancellationToken);

                var taskPath = Option(args, "--task")
                    ?? throw new ArgumentException("--task <file> is required.");
                var title = Option(args, "--title") ?? Path.GetFileNameWithoutExtension(taskPath);
                var instructions = await File.ReadAllTextAsync(taskPath, cancellationToken);
                var gates = Options(args, "--gate");
                var timeoutText = Option(args, "--timeout-minutes") ?? "30";
                if (!int.TryParse(timeoutText, out var timeoutMinutes) || timeoutMinutes is < 1 or > 120)
                    throw new ArgumentException("--timeout-minutes must be an integer from 1 to 120 (default 30 per attempt).");
                var runnerOptions = RunnerOptions.Default with { HardTimeout = TimeSpan.FromMinutes(timeoutMinutes) };
                var missionId = Option(args, "--mission");
                GlobalAgyLease? acquiredLease;
                try
                {
                    acquiredLease = GlobalAgyLease.TryAcquire();
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            status = exception is UnauthorizedAccessException
                                ? "localStateAccessDenied" : "localStateUnavailable",
                            detail = $"Cannot access the Agent Relay runner lock for this Windows identity: " +
                                     exception.Message
                        }, JsonSupport.Options));
                    return 10;
                }
                using var runnerLease = acquiredLease;
                if (runnerLease is null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            status = "runnerBusy",
                            detail = "Another Agent Relay runner owns agy; no handoff was created."
                        }, JsonSupport.Options));
                    return 11;
                }

                var agyPath = services.Doctor.ResolveAgyPath();
                var attemptedAccounts = new HashSet<string>(StringComparer.Ordinal);
                var account = await services.Accounts.PreflightAsync(
                    agyPath, attemptedAccounts, cancellationToken);
                if (account.Status != "ready" || account.Account is null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(account, JsonSupport.Options));
                    return account.Status == "accountRequired" ? 9 : 10;
                }
                await services.Activity.SetAsync(
                    project,
                    SolActivityPhase.Delegating,
                    $"Codex передаёт Gemini executor ограниченную задачу: {title}.",
                    missionId,
                    cancellationToken: cancellationToken);
                RunnerResult result;
                PublishedHandoff handoff;
                var currentInstructions = instructions;
                do
                {
                    attemptedAccounts.Add(account.Account.Id);
                    var modelSelection = await services.Models.ResolveAsync(agyPath, cancellationToken);
                    await services.Runtime.AppendLogAsync(
                        new ActionLogEntry(
                            DateTimeOffset.UtcNow,
                            project.Id,
                            "model-resolved",
                            $"account={account.Account.Id} model={modelSelection.Executor.Model} " +
                            $"source={modelSelection.Source}; {modelSelection.Detail}"),
                        cancellationToken);
                    handoff = await services.Protocol.PublishAsync(
                        project.Path,
                        new MissionRequest(title, currentInstructions, gates, missionId),
                        modelSelection.Executor,
                        cancellationToken);
                    missionId = handoff.Control.MissionId;
                    try
                    {
                        await services.Activity.SetAsync(
                            project,
                            SolActivityPhase.Delegating,
                            $"Codex передал Gemini executor ограниченную задачу: {title}.",
                            handoff.Control.MissionId,
                            handoff.Control.HandoffId,
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                       JsonException)
                    {
                        // A presentation failure after publication must not strand this attempt.
                    }
                    Console.WriteLine(JsonSerializer.Serialize(handoff.Control, JsonSupport.Options));
                    result = await services.CreateRunner(runnerOptions).RunAsync(
                        project, handoff, agyPath, cancellationToken, runnerLease);
                    if (result.State != RelayState.QuotaExhausted)
                    {
                        break;
                    }

                    await services.Protocol.CancelAsync(
                        handoff,
                        "Runner reported a confirmed quota/rate-limit failure; rotating managed account.",
                        cancellationToken);
                    account = await services.Accounts.PreflightAsync(
                        agyPath, attemptedAccounts, cancellationToken);
                    if (account.Status != "ready" || account.Account is null)
                    {
                        var preflightSummary = account.Status == "noEligibleAccount"
                            ? "no unused eligible managed account remains"
                            : $"account preflight stopped with status {account.Status}";
                        result = result with
                        {
                            Detail = $"QuotaExhausted: {preflightSummary}. " +
                                     account.Detail
                        };
                        break;
                    }
                    currentInstructions = instructions + Environment.NewLine + Environment.NewLine +
                        "This is a quota-retry revision. Inspect and preserve existing working-tree changes " +
                        "from the interrupted attempt, verify incomplete work, and continue safely.";
                } while (true);
                await services.Activity.SetAsync(
                    project,
                    result.State == RelayState.ReportReady
                        ? SolActivityPhase.Reviewing
                        : SolActivityPhase.Blocked,
                    result.State == RelayState.ReportReady
                        ? "Отчёт Gemini executor получен; Codex должен независимо проверить результат."
                        : result.Detail,
                    handoff.Control.MissionId,
                    handoff.Control.HandoffId,
                    cancellationToken: cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonSupport.Options));
                return result.State == RelayState.ReportReady ? 0 : 4;
            }
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
              account list
              account add --label <label> [--activate]
              account activate --id <id>
              account refresh (--id <id>|--all)
              account remove --id <id>
              account settings [--rotation-threshold <1..50>]
              policy get [--project <path>]
              policy set off|low|medium|high
              project add|remove|trust <path-or-id>
              project list
              activity get|clear --project <id|path>
              experience recall --project <id|path> [--kind mechanical|implementation|investigation] [--model <exact-model>]
              experience record --project <id|path> --file <review.json>  (controller-reviewed outcome; local only)
              activity set --project <id|path> --phase <phase> --summary <text> [--mission <id>] [--handoff <id>]
              handoff publish --project <id|path> --task <file> [--title <text>] [--mission <id>] [--gate <command> ...] [--timeout-minutes <1..120; default 30 per attempt>]
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
