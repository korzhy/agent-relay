using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentRelay.Core;

public sealed class AtomicFileStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task WriteTextAsync(
        string path,
        string content,
        bool createBackup = false,
        CancellationToken cancellationToken = default)
    {
        var bytes = new UTF8Encoding(false).GetBytes(NormalizeNewline(content));
        await WriteBytesAsync(path, bytes, createBackup, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteJsonAsync<T>(
        string path,
        T value,
        bool createBackup = false,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonSupport.Options) + Environment.NewLine;
        return WriteTextAsync(path, json, createBackup, cancellationToken);
    }

    public async Task<bool> WriteImmutableJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonSupport.Options) + Environment.NewLine;
        var bytes = new UTF8Encoding(false).GetBytes(NormalizeNewline(json));
        return await WriteImmutableBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WriteImmutableTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var bytes = new UTF8Encoding(false).GetBytes(NormalizeNewline(content));
        return await WriteImmutableBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WriteImmutableBytesAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var pathLock = _pathLocks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(fullPath))
            {
                var existing = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
                {
                    return false;
                }

                throw new InvalidOperationException($"Immutable payload already exists: {path}");
            }

            await WriteBytesCoreAsync(fullPath, bytes, false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            pathLock.Release();
        }
    }

    public async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonSupport.Options, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Text(string content)
    {
        var bytes = new UTF8Encoding(false).GetBytes(NormalizeNewline(content));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private async Task WriteBytesAsync(
        string path,
        byte[] bytes,
        bool createBackup,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var pathLock = _pathLocks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteBytesCoreAsync(fullPath, bytes, createBackup, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pathLock.Release();
        }
    }

    private static async Task WriteBytesCoreAsync(
        string fullPath,
        byte[] bytes,
        bool createBackup,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Path has no parent directory: {fullPath}");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = createBackup && File.Exists(fullPath)
            ? fullPath + $".{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak"
            : null;

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeNewline(string value)
        => value.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
}
