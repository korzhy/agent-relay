using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRelay.Core;

public enum AccountEligibility
{
    Unknown,
    Eligible,
    IneligibleFree,
    IneligibleRestricted,
    IneligibleTierUnavailable,
    CredentialInvalid
}

public sealed record GeminiQuotaSnapshot(
    int WeeklyPercent,
    int FiveHourPercent,
    DateTimeOffset CheckedAt,
    DateTimeOffset? WeeklyResetAt = null,
    DateTimeOffset? FiveHourResetAt = null)
{
    public int RemainingPercent => Math.Min(WeeklyPercent, FiveHourPercent);
}

public sealed record ManagedAccount(
    string Id,
    string Label,
    string? Email,
    string? Tier,
    AccountEligibility Eligibility,
    GeminiQuotaSnapshot? Quota,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastUsedAt,
    string? Diagnostic)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Label) || EnrolledAt == default)
        {
            throw new InvalidDataException("Managed account metadata is incomplete.");
        }
    }
}

public sealed record ManagedAccountRegistry(
    int SchemaVersion,
    string? ActiveAccountId,
    IReadOnlyList<ManagedAccount> Accounts)
{
    public const int CurrentSchemaVersion = 1;
    public static ManagedAccountRegistry Empty { get; } = new(CurrentSchemaVersion, null, []);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Accounts is null)
        {
            throw new InvalidDataException("Unsupported managed-account registry.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in Accounts)
        {
            account.Validate();
            if (!ids.Add(account.Id))
            {
                throw new InvalidDataException($"Duplicate managed account id: {account.Id}");
            }
        }

        if (ActiveAccountId is not null && !ids.Contains(ActiveAccountId))
        {
            throw new InvalidDataException("The active account is missing from the registry.");
        }
    }
}

public sealed record AccountSettings(int SchemaVersion, int RotationThreshold)
{
    public const int CurrentSchemaVersion = 1;
    public static AccountSettings Default { get; } = new(CurrentSchemaVersion, 10);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || RotationThreshold is < 1 or > 50)
        {
            throw new InvalidDataException("rotationThreshold must be between 1 and 50.");
        }
    }
}

public sealed record AccountRecoveryJournal(
    int SchemaVersion,
    string OperationId,
    string Stage,
    bool HadPreviousCredential,
    DateTimeOffset CreatedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record AccountPreflightResult(
    string Status,
    ManagedAccount? Account,
    string Detail);

public static class AccountSelection
{
    public static bool IsAboveOrAtThreshold(ManagedAccount account, int threshold)
        => account is { Eligibility: AccountEligibility.Eligible, Quota: not null } &&
           account.Quota.RemainingPercent >= threshold;

    public static ManagedAccount? Select(
        IEnumerable<ManagedAccount> accounts,
        int threshold,
        ISet<string>? excludedIds = null)
        => accounts
            .Where(account => (excludedIds is null || !excludedIds.Contains(account.Id)) &&
                              IsAboveOrAtThreshold(account, threshold))
            .OrderByDescending(account => account.Quota!.RemainingPercent)
            .ThenBy(account => account.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(account => account.Id, StringComparer.Ordinal)
            .FirstOrDefault();
}

public static class AgyUsageParser
{
    public static GeminiQuotaSnapshot Parse(string json, DateTimeOffset checkedAt)
    {
        using var document = JsonDocument.Parse(json);
        var buckets = FindBuckets(document.RootElement).ToArray();
        var weekly = buckets.FirstOrDefault(item => item.Name == "gemini-weekly");
        var fiveHour = buckets.FirstOrDefault(item => item.Name == "gemini-5h");
        if (weekly.Name is null || fiveHour.Name is null)
        {
            throw new InvalidDataException("agy /usage did not return gemini-weekly and gemini-5h buckets.");
        }

        return new GeminiQuotaSnapshot(
            ToPercent(weekly.Remaining),
            ToPercent(fiveHour.Remaining),
            checkedAt,
            weekly.Reset,
            fiveHour.Reset);
    }

    private static IEnumerable<(string? Name, double Remaining, DateTimeOffset? Reset)> FindBuckets(
        JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
            {
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (!bucket.TryGetProperty("name", out var name) ||
                        !bucket.TryGetProperty("remaining_fraction", out var remaining))
                    {
                        continue;
                    }
                    DateTimeOffset? reset = null;
                    if (bucket.TryGetProperty("reset_time", out var resetValue) &&
                        resetValue.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(resetValue.GetString(), out var parsed))
                    {
                        reset = parsed;
                    }
                    yield return (name.GetString(), remaining.GetDouble(), reset);
                }
            }
            foreach (var property in root.EnumerateObject())
            {
                foreach (var item in FindBuckets(property.Value)) yield return item;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                foreach (var item in FindBuckets(element)) yield return item;
            }
        }
    }

    private static int ToPercent(double fraction)
        => Math.Clamp((int)Math.Floor(fraction * 100d + 0.0000001d), 0, 100);
}

public static class TierClassifier
{
    public static AccountEligibility Classify(string? tierId, string? tierName, bool restricted)
    {
        if (restricted) return AccountEligibility.IneligibleRestricted;
        var value = $"{tierId} {tierName}";
        if (value.Contains("ultra", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("pro", StringComparison.OrdinalIgnoreCase))
        {
            return AccountEligibility.Eligible;
        }
        if (value.Contains("free", StringComparison.OrdinalIgnoreCase))
        {
            return AccountEligibility.IneligibleFree;
        }
        return AccountEligibility.IneligibleRestricted;
    }
}
