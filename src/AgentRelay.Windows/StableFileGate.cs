using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed class StableFileGate
{
    private readonly TimeSpan _debounce;
    private readonly int _attempts;

    public StableFileGate(TimeSpan? debounce = null, int attempts = 4)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        _attempts = Math.Max(2, attempts);
    }

    public async Task<string?> WaitAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string? previous = null;
        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            if (!File.Exists(path))
            {
                await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
                continue;
            }

            string current;
            try
            {
                current = await AtomicFileStore.Sha256Async(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
            previous = current;
            await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}
