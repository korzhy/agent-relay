using System.Diagnostics;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed class RuntimeRecoveryService
{
    private readonly RuntimeStore _runtime;
    private readonly IClock _clock;

    public RuntimeRecoveryService(RuntimeStore runtime, IClock? clock = null)
    {
        _runtime = runtime;
        _clock = clock ?? new SystemClock();
    }

    public async Task<ProjectRuntimeState> RecoverAsync(
        RegisteredProject project,
        CancellationToken cancellationToken = default)
    {
        var current = await _runtime.ReadAsync(project.Id, cancellationToken).ConfigureAwait(false)
            ?? RuntimeStore.NewReady(project, _clock.UtcNow);
        if (File.Exists(_runtime.PausePath(project.Id)))
        {
            current = current with
            {
                State = RelayState.Paused,
                ProcessId = null,
                UpdatedAt = _clock.UtcNow,
                Detail = "Dispatch pause is armed."
            };
            await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
            return current;
        }

        if (current.State is RelayState.Running or RelayState.Waiting && !ProcessMatches(current))
        {
            current = current with
            {
                State = RelayState.Stalled,
                ProcessId = null,
                UpdatedAt = _clock.UtcNow,
                Detail = "Recovered an interrupted runner without a valid report."
            };
            await _runtime.WriteAsync(current, cancellationToken).ConfigureAwait(false);
        }
        return current;
    }

    private static bool ProcessMatches(ProjectRuntimeState state)
    {
        if (state.ProcessId is null || string.IsNullOrWhiteSpace(state.RunnerPath))
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(state.ProcessId.Value);
            return !process.HasExited &&
                   string.Equals(
                       process.MainModule?.FileName,
                       Path.GetFullPath(state.RunnerPath),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
