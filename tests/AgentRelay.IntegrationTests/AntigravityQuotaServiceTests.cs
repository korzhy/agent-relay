using AgentRelay.Core;
using AgentRelay.Windows;

namespace AgentRelay.IntegrationTests;

public sealed class AntigravityQuotaServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "AgentRelayIntegration_Quota_" + Guid.NewGuid().ToString("N"));

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    public AntigravityQuotaServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task ReadAsync_FreshQuotaRecord_ReturnsFreshSnapshotWithDocumentedSource()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":80,\"used_percentage\":20,\"available\":800,\"monthly\":1000}}";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:05:00.000Z")
        };

        var service = new AntigravityQuotaService(_tempDirectory, clock);
        var snapshot = await service.ReadAsync();

        Assert.Equal(QuotaFreshness.Fresh, snapshot.Freshness);
        Assert.Equal(80, snapshot.RemainingPercentage);
        Assert.Equal(20, snapshot.UsedPercentage);
        Assert.Equal(800, snapshot.AvailablePromptCredits);
        Assert.Equal(1000, snapshot.MonthlyPromptCredits);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T10:00:00.000Z"), snapshot.ObservedAt);
        Assert.Equal(AntigravityQuotaService.SourceName, snapshot.Source);
        Assert.True(snapshot.HasPercentage);
    }

    [Fact]
    public async Task ReadAsync_ValidOldTimestamp_ReturnsStaleSnapshot()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T09:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":80,\"used_percentage\":20,\"available\":800,\"monthly\":1000}}";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:00:00.000Z")
        };

        var service = new AntigravityQuotaService(_tempDirectory, clock);
        var snapshot = await service.ReadAsync();

        Assert.Equal(QuotaFreshness.Stale, snapshot.Freshness);
        Assert.Equal(80, snapshot.RemainingPercentage);
        Assert.Equal(20, snapshot.UsedPercentage);
        Assert.Equal(AntigravityQuotaService.SourceName, snapshot.Source);
    }

    [Fact]
    public async Task ReadAsync_MultipleRecordsInOneFile_SelectsLastCompleteRecord()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T08:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":50,\"used_percentage\":50,\"available\":500,\"monthly\":1000}}\n" +
                         "Quota update received: {\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":90,\"used_percentage\":10,\"available\":900,\"monthly\":1000}}";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:02:00.000Z")
        };

        var service = new AntigravityQuotaService(_tempDirectory, clock);
        var snapshot = await service.ReadAsync();

        Assert.Equal(QuotaFreshness.Fresh, snapshot.Freshness);
        Assert.Equal(90, snapshot.RemainingPercentage);
        Assert.Equal(10, snapshot.UsedPercentage);
        Assert.Equal(900, snapshot.AvailablePromptCredits);
        Assert.Equal(1000, snapshot.MonthlyPromptCredits);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T10:00:00.000Z"), snapshot.ObservedAt);
    }

    [Fact]
    public async Task ReadAsync_IncompleteTrailingRecord_FallsBackToLastCompleteRecord()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":75,\"used_percentage\":25}}\n" +
                         "Quota update received: {\"timestamp\":\"2026-07-27T10:01:00.000Z\",\"prompt_credits\":";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:02:00.000Z")
        };

        var snapshot = await new AntigravityQuotaService(_tempDirectory, clock).ReadAsync();

        Assert.Equal(QuotaFreshness.Fresh, snapshot.Freshness);
        Assert.Equal(75, snapshot.RemainingPercentage);
        Assert.Equal(25, snapshot.UsedPercentage);
    }

    [Fact]
    public async Task ReadAsync_InvalidTrailingJson_FallsBackToLastValidRecord()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":60,\"used_percentage\":40}}\n" +
                         "Quota update received: {not-json}";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:02:00.000Z")
        };

        var snapshot = await new AntigravityQuotaService(_tempDirectory, clock).ReadAsync();

        Assert.Equal(QuotaFreshness.Fresh, snapshot.Freshness);
        Assert.Equal(60, snapshot.RemainingPercentage);
    }

    [Fact]
    public async Task ReadAsync_MissingLogDirectory_ReturnsUnavailableWithNoPercentage()
    {
        var missingDir = Path.Combine(_tempDirectory, "non_existent_dir_" + Guid.NewGuid().ToString("N"));

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:00:00.000Z")
        };

        var service = new AntigravityQuotaService(missingDir, clock);
        var snapshot = await service.ReadAsync();

        Assert.Equal(QuotaFreshness.Unavailable, snapshot.Freshness);
        Assert.Null(snapshot.RemainingPercentage);
        Assert.Null(snapshot.UsedPercentage);
        Assert.False(snapshot.HasPercentage);
    }

    [Fact]
    public async Task ReadAsync_InconsistentOrOutOfRangePercentages_ReturnsUnavailable()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":80,\"used_percentage\":50,\"available\":800,\"monthly\":1000}}";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:02:00.000Z")
        };

        var service = new AntigravityQuotaService(_tempDirectory, clock);
        var snapshot = await service.ReadAsync();

        Assert.Equal(QuotaFreshness.Unavailable, snapshot.Freshness);
        Assert.Null(snapshot.RemainingPercentage);
        Assert.Null(snapshot.UsedPercentage);
        Assert.False(snapshot.HasPercentage);
    }

    [Fact]
    public async Task ReadAsync_OutOfRangePercentage_ReturnsUnavailable()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"prompt_credits\":{\"remaining_percentage\":101,\"used_percentage\":0}}";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:02:00.000Z")
        };

        var snapshot = await new AntigravityQuotaService(_tempDirectory, clock).ReadAsync();

        Assert.Equal(QuotaFreshness.Unavailable, snapshot.Freshness);
        Assert.False(snapshot.HasPercentage);
    }

    [Fact]
    public async Task ReadAsync_OnlyUsedPercentage_DerivesRemainingPercentage()
    {
        var logFilePath = Path.Combine(_tempDirectory, "Antigravity Quota.log");
        var logContent = "Quota update received: {\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"prompt_credits\":{\"used_percentage\":30,\"available\":700,\"monthly\":1000}}";
        await File.WriteAllTextAsync(logFilePath, logContent);

        var clock = new TestClock
        {
            UtcNow = DateTimeOffset.Parse("2026-07-27T10:05:00.000Z")
        };

        var service = new AntigravityQuotaService(_tempDirectory, clock);
        var snapshot = await service.ReadAsync();

        Assert.Equal(QuotaFreshness.Fresh, snapshot.Freshness);
        Assert.Equal(70, snapshot.RemainingPercentage);
        Assert.Equal(30, snapshot.UsedPercentage);
        Assert.Equal(700, snapshot.AvailablePromptCredits);
        Assert.Equal(1000, snapshot.MonthlyPromptCredits);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
