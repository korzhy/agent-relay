using AgentRelay.Windows;

namespace AgentRelay.IntegrationTests;

public sealed class StableFileGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AgentRelayStableFile_" + Guid.NewGuid().ToString("N"));

    public StableFileGateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task WaitAsync_MissingFile_ReturnsMissingWithNoObservations()
    {
        var result = await new StableFileGate(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(50))
            .WaitAsync(Path.Combine(_root, "missing.json"));

        Assert.Equal(StableFileStatus.Missing, result.Status);
        Assert.Equal(0, result.Observations);
        Assert.Null(result.Hash);
    }

    [Fact]
    public async Task WaitAsync_DelayedFile_WaitsForTwoMatchingHashes()
    {
        var path = Path.Combine(_root, "delayed.json");
        var writer = Task.Run(async () =>
        {
            await Task.Delay(40);
            await File.WriteAllTextAsync(path, "{\"status\":\"ready\"}");
        });

        var result = await new StableFileGate(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(250))
            .WaitAsync(path);
        await writer;

        Assert.Equal(StableFileStatus.Stable, result.Status);
        Assert.True(result.Observations >= 2);
        Assert.NotNull(result.Hash);
    }

    [Fact]
    public async Task WaitAsync_LockedFile_ReturnsUnreadableWithSafeDiagnostic()
    {
        var path = Path.Combine(_root, "locked.json");
        await File.WriteAllTextAsync(path, "{}");
        await using var locked = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await new StableFileGate(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(60))
            .WaitAsync(path);

        Assert.Equal(StableFileStatus.Unreadable, result.Status);
        Assert.Contains("IOException", result.LastError, StringComparison.Ordinal);
    }
}
