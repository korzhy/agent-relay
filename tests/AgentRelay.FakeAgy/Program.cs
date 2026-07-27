using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AgentRelay.Core;

namespace AgentRelay.FakeAgy;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("models"))
        {
            Console.WriteLine(AgentRelayConstants.Model);
            return 0;
        }

        string? taskPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--print" && i + 1 < args.Length)
            {
                var prompt = args[i + 1];
                var match = Regex.Match(prompt, @"\.agent-relay[/\\]tasks[/\\][^\s""]+");
                if (match.Success)
                {
                    taskPath = Path.Combine(Directory.GetCurrentDirectory(), match.Value);
                }
            }
        }

        if (taskPath == null || !File.Exists(taskPath))
        {
            var tasksDir = Path.Combine(Directory.GetCurrentDirectory(), ".agent-relay", "tasks");
            if (Directory.Exists(tasksDir))
            {
                taskPath = Directory.GetFiles(tasksDir, "*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }

        if (taskPath == null || !File.Exists(taskPath))
        {
            Console.Error.WriteLine("FakeAgy error: Task payload not found.");
            return 1;
        }

        var json = File.ReadAllText(taskPath);
        var task = JsonSerializer.Deserialize<TaskPayload>(json, JsonSupport.Options);
        if (task == null)
        {
            Console.Error.WriteLine("FakeAgy error: Failed to parse task payload.");
            return 1;
        }

        const string modePrefix = "fake-mode:";
        var mode = task.Instructions.StartsWith(modePrefix, StringComparison.OrdinalIgnoreCase)
            ? task.Instructions[modePrefix.Length..].Trim()
            : "pass";
        if (mode.Equals("crash", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("FakeAgy error: Process crashed unexpectedly.");
            return 1;
        }
        if (mode.Equals("quota", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("quota exhausted: rate limit exceeded for gemini-3.6-flash-high");
            return 1;
        }
        if (mode.Equals("stall", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("FakeAgy: entering infinite stall...");
            Thread.Sleep(TimeSpan.FromHours(1));
            return 0;
        }

        var reportPath = WorkspaceSafety.ResolveRelative(Directory.GetCurrentDirectory(), task.RequiredReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        if (mode.Equals("missing_report", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("FakeAgy: exiting 0 without creating report.");
            return 0;
        }

        if (mode.Equals("invalid_report", StringComparison.OrdinalIgnoreCase))
        {
            var invalidReport = new ReportPayload(
                task.ProtocolVersion,
                task.HandoffId,
                task.MissionId,
                task.Revision,
                task.RunAttemptId,
                task.Executor,
                DateTimeOffset.UtcNow,
                ReportClaim.Pass,
                new[] { "test.txt" },
                new[] { new ExecutedCommand("dotnet test", 0) },
                null,
                Array.Empty<string>(),
                new ProhibitedActionConfirmation(false, true, true, true, true, true, true), // Invalid confirmation
                "Invalid report payload."
            );
            var invalidJson = JsonSerializer.Serialize(invalidReport, JsonSupport.Options) + Environment.NewLine;
            File.WriteAllText(reportPath, invalidJson);
            Console.WriteLine("FakeAgy: written invalid report to " + reportPath);
            return 0;
        }

        // Pass mode (default)
        var passReport = new ReportPayload(
            task.ProtocolVersion,
            task.HandoffId,
            task.MissionId,
            task.Revision,
            task.RunAttemptId,
            task.Executor,
            DateTimeOffset.UtcNow,
            ReportClaim.Pass,
            new[] { "test.txt" },
            new[] { new ExecutedCommand("dotnet test", 0) },
            null,
            Array.Empty<string>(),
            new ProhibitedActionConfirmation(true, true, true, true, true, true, true),
            "Fake agy execution succeeded."
        );
        var passJson = JsonSerializer.Serialize(passReport, JsonSupport.Options) + Environment.NewLine;
        File.WriteAllText(reportPath, passJson);
        Console.WriteLine("FakeAgy: written PASS report to " + reportPath);
        return 0;
    }
}
