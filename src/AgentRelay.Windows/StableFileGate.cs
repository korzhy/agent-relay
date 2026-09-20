using System.Diagnostics;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public enum StableFileStatus
{
    Stable,
    Missing,
    Unstable,
    Unreadable
}

public sealed record StableFileResult(
    StableFileStatus Status,
    string? Hash,
    int Observations,
    string? LastError)
{
    public bool IsStable => Status == StableFileStatus.Stable;

    public string Diagnostic =>
        $"status={Status.ToString().ToLowerInvariant()}, observations={Observations}" +
        (string.IsNullOrWhiteSpace(LastError) ? string.Empty : $", lastError={LastError}");
}

public sealed class StableFileGate
{
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;

    public StableFileGate(TimeSpan? pollInterval = null, TimeSpan? timeout = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(150);
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        if (_timeout < _pollInterval) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<StableFileResult> WaitAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        string? previous = null;
        string? lastError = null;
        var observations = 0;
        var sawFile = false;
        while (Stopwatch.GetElapsedTime(started) < _timeout)
        {
            if (!File.Exists(path))
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            sawFile = true;
            string current;
            try
            {
                current = await AtomicFileStore.Sha256Async(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastError = $"{exception.GetType().Name}: {exception.Message}";
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            observations++;
            lastError = null;
            if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
            {
                return new StableFileResult(StableFileStatus.Stable, current, observations, null);
            }
            previous = current;
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        var status = !sawFile
            ? StableFileStatus.Missing
            : lastError is not null
                ? StableFileStatus.Unreadable
                : StableFileStatus.Unstable;
        return new StableFileResult(status, previous, observations, lastError);
    }
}
