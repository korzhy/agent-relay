namespace AgentRelay.Windows;

public sealed class GlobalAgyLease : IDisposable
{
    public const string DefaultName = "Local\\AgentRelay-Agy-Global-v1";
    private readonly Semaphore _semaphore;
    private bool _held;

    private GlobalAgyLease(Semaphore semaphore, bool held)
    {
        _semaphore = semaphore;
        _held = held;
    }

    public bool IsHeld => _held;

    public static GlobalAgyLease? TryAcquire(string name = DefaultName)
    {
        var semaphore = new Semaphore(1, 1, name);
        try
        {
            return semaphore.WaitOne(0) ? new GlobalAgyLease(semaphore, true) : null;
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_held)
        {
            _semaphore.Release();
            _held = false;
        }
        _semaphore.Dispose();
    }
}
