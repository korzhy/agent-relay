using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Core.Tests;

public sealed class AccountModelsTests
{
    [Fact]
    public void UsageParser_UsesMinimumOfWeeklyAndFiveHour()
    {
        var snapshot = AgyUsageParser.Parse(
            """
            {"command":{"data":{"groups":[{"buckets":[
              {"name":"gemini-weekly","remaining_fraction":0.91,"reset_time":"2026-09-07T00:00:00Z"},
              {"name":"gemini-5h","remaining_fraction":0.37,"reset_time":"2026-09-06T12:00:00Z"}
            ]}]}}}
            """, DateTimeOffset.Parse("2026-09-06T08:00:00Z"));

        Assert.Equal(91, snapshot.WeeklyPercent);
        Assert.Equal(37, snapshot.FiveHourPercent);
        Assert.Equal(37, snapshot.RemainingPercent);
    }

    [Fact]
    public void Threshold_IsStrictlyBelowBoundary()
    {
        var account = Account("a", 10);
        Assert.True(AccountSelection.IsAboveOrAtThreshold(account, 10));
        Assert.False(AccountSelection.IsAboveOrAtThreshold(account with
        {
            Quota = account.Quota! with { FiveHourPercent = 9 }
        }, 10));
    }

    [Theory]
    [InlineData("standard-pro", "Google AI Pro", false, AccountEligibility.Eligible)]
    [InlineData("ultra", "Ultra", false, AccountEligibility.Eligible)]
    [InlineData("free", "Free", false, AccountEligibility.IneligibleFree)]
    [InlineData("pro", "Pro", true, AccountEligibility.IneligibleRestricted)]
    [InlineData(null, null, false, AccountEligibility.IneligibleRestricted)]
    public void TierClassifier_IsFailClosed(
        string? id, string? name, bool restricted, AccountEligibility expected)
        => Assert.Equal(expected, TierClassifier.Classify(id, name, restricted));

    [Fact]
    public void Selection_IsDeterministicByQuotaThenLeastRecentlyUsedThenId()
    {
        var old = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var accounts = new[]
        {
            Account("c", 80) with { LastUsedAt = old },
            Account("b", 90) with { LastUsedAt = old.AddDays(1) },
            Account("a", 90) with { LastUsedAt = old.AddDays(1) }
        };
        Assert.Equal("a", AccountSelection.Select(accounts, 10)?.Id);
        Assert.Equal("c", AccountSelection.Select(accounts, 10, new HashSet<string> { "a", "b" })?.Id);
    }

    [Fact]
    public void RegistrySerialization_HasNoCredentialFields()
    {
        var json = JsonSerializer.Serialize(
            new ManagedAccountRegistry(1, "a", [Account("a", 90)]), JsonSupport.Options);
        Assert.DoesNotContain("access_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    private static ManagedAccount Account(string id, int quota)
        => new(
            id, id, null, "PRO", AccountEligibility.Eligible,
            new GeminiQuotaSnapshot(quota, quota, DateTimeOffset.Parse("2026-09-06T08:00:00Z")),
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-06T08:00:00Z"), null, null);
}
