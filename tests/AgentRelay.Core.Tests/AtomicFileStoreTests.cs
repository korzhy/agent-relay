using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class AtomicFileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AtomicFileStore _store;

    public AtomicFileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRelayCoreTests_Atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new AtomicFileStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WriteTextAsync_CreatesDirectoryAndWritesFile()
    {
        var path = Path.Combine(_tempDir, "sub", "test.txt");
        await _store.WriteTextAsync(path, "Hello World\nLine 2");

        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("Hello World\nLine 2", content);
    }

    [Fact]
    public async Task WriteTextAsync_CreatesBackup_WhenFileExistsAndBackupRequested()
    {
        var path = Path.Combine(_tempDir, "backup_test.txt");
        await File.WriteAllTextAsync(path, "Original Content");

        await _store.WriteTextAsync(path, "New Content", createBackup: true);

        Assert.Equal("New Content", await File.ReadAllTextAsync(path));
        var bakFiles = Directory.GetFiles(_tempDir, "backup_test.txt.*.bak");
        Assert.Single(bakFiles);
        Assert.Equal("Original Content", await File.ReadAllTextAsync(bakFiles[0]));
    }

    [Fact]
    public void Sha256Text_NormalizesNewlinesAndComputesHash()
    {
        var textLf = "hello\nworld";
        var textCrlf = "hello\r\nworld";

        var hash1 = AtomicFileStore.Sha256Text(textLf);
        var hash2 = AtomicFileStore.Sha256Text(textCrlf);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public async Task Sha256Async_ComputesExactFileHash()
    {
        var path = Path.Combine(_tempDir, "hash.txt");
        var content = "test file hash";
        await File.WriteAllTextAsync(path, content);

        var fileHash = await AtomicFileStore.Sha256Async(path);
        var textHash = AtomicFileStore.Sha256Text(content);

        Assert.Equal(textHash, fileHash);
    }

    [Fact]
    public async Task WriteImmutableTextAsync_InitialWriteReturnsTrue_IdenticalWriteReturnsFalse_DifferentWriteThrows()
    {
        var path = Path.Combine(_tempDir, "immutable.txt");
        var content1 = "Immutable Content 1";
        var content2 = "Immutable Content 2";

        var result1 = await _store.WriteImmutableTextAsync(path, content1);
        Assert.True(result1);
        Assert.True(File.Exists(path));

        var result2 = await _store.WriteImmutableTextAsync(path, content1);
        Assert.False(result2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.WriteImmutableTextAsync(path, content2));
    }

    [Fact]
    public async Task WriteImmutableJsonAsync_InitialWriteReturnsTrue_IdenticalWriteReturnsFalse_DifferentWriteThrows()
    {
        var path = Path.Combine(_tempDir, "immutable.json");
        var data1 = new ExecutorIdentity("Antigravity", "gemini-3.6-flash-high");
        var data2 = new ExecutorIdentity("Antigravity", "other-model");

        var result1 = await _store.WriteImmutableJsonAsync(path, data1);
        Assert.True(result1);

        var result2 = await _store.WriteImmutableJsonAsync(path, data1);
        Assert.False(result2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.WriteImmutableJsonAsync(path, data2));
    }
}
