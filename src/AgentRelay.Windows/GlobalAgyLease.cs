using System.Security.Cryptography;
using System.Text;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed class GlobalAgyLease : IDisposable
{
    public const string DefaultName = "Local\\AgentRelay-Agy-Global-v1";
    private readonly FileStream _stream;
    private bool _held = true;

    private GlobalAgyLease(FileStream stream)
    {
        _stream = stream;
    }

    public bool IsHeld => _held;

    public static GlobalAgyLease? TryAcquire(string name = DefaultName)
    {
        var runtimeDirectory = AppPaths.FromEnvironment().RuntimeDirectory;
        Directory.CreateDirectory(runtimeDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        var path = Path.Combine(runtimeDirectory, $"agy-{hash[..24]}.lock");
        try
        {
            return new GlobalAgyLease(new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        _stream.Dispose();
    }
}
