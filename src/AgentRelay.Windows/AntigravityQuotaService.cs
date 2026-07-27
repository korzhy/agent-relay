using System.Text;
using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public enum QuotaFreshness
{
    Fresh,
    Stale,
    Unavailable
}

public sealed record QuotaSnapshot(
    int? RemainingPercentage,
    int? UsedPercentage,
    int? AvailablePromptCredits,
    int? MonthlyPromptCredits,
    DateTimeOffset? ObservedAt,
    QuotaFreshness Freshness,
    string Source,
    string Detail)
{
    public bool HasPercentage => RemainingPercentage is not null;
}

public sealed class AntigravityQuotaService
{
    public const string SourceName = "Antigravity quota log";
    private const string Marker = "Quota update received:";
    private const int MaximumTailBytes = 512 * 1024;
    private readonly string _logsRoot;
    private readonly IClock _clock;
    private readonly TimeSpan _staleAfter;

    public AntigravityQuotaService(
        string logsRoot,
        IClock? clock = null,
        TimeSpan? staleAfter = null)
    {
        _logsRoot = Path.GetFullPath(logsRoot);
        _clock = clock ?? new SystemClock();
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(10);
    }

    public static AntigravityQuotaService FromEnvironment(IClock? clock = null)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new AntigravityQuotaService(
            Path.Combine(roaming, "Antigravity", "logs"),
            clock);
    }

    public async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_logsRoot))
        {
            return Unavailable("Antigravity logs directory was not found.");
        }

        IReadOnlyList<FileInfo> candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(_logsRoot, "*Antigravity Quota.log", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Unavailable("Antigravity quota logs could not be enumerated.");
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await ReadTailAsync(candidate.FullName, cancellationToken).ConfigureAwait(false);
                if (TryParseLatest(text, candidate.LastWriteTimeUtc, out var snapshot))
                {
                    return snapshot;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A live extension can rotate or replace a log while it is being read.
            }
        }

        return Unavailable(
            "No compatible quota record was found. Open Antigravity with a compatible quota source.");
    }

    internal bool TryParseLatest(
        string text,
        DateTimeOffset fileTimestamp,
        out QuotaSnapshot snapshot)
    {
        snapshot = Unavailable("No compatible quota record was found.");
        for (var markerIndex = text.LastIndexOf(Marker, StringComparison.Ordinal);
             markerIndex >= 0;
             markerIndex = text.LastIndexOf(
                 Marker,
                 Math.Max(0, markerIndex - 1),
                 StringComparison.Ordinal))
        {
            var objectStart = text.IndexOf('{', markerIndex + Marker.Length);
            if (objectStart >= 0 &&
                TryExtractJsonObject(text, objectStart, out var json))
            {
                try
                {
                    if (TryParseSnapshot(json, fileTimestamp, out snapshot))
                    {
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // The extension can leave a complete-looking but invalid record while rotating a log.
                }
            }

            if (markerIndex == 0)
            {
                break;
            }
        }

        return false;
    }

    private bool TryParseSnapshot(
        string json,
        DateTimeOffset fileTimestamp,
        out QuotaSnapshot snapshot)
    {
        snapshot = Unavailable("Quota record was invalid.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("prompt_credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasRemaining = credits.TryGetProperty("remaining_percentage", out _);
        var hasUsed = credits.TryGetProperty("used_percentage", out _);
        var remaining = ReadPercentage(credits, "remaining_percentage");
        var used = ReadPercentage(credits, "used_percentage");
        if ((hasRemaining && remaining is null) || (hasUsed && used is null))
        {
            return false;
        }
        if (remaining is null && used is not null)
        {
            remaining = 100 - used;
        }
        if (used is null && remaining is not null)
        {
            used = 100 - remaining;
        }
        if (remaining is null || used is null || Math.Abs(100 - remaining.Value - used.Value) > 1)
        {
            return false;
        }

        var available = ReadNonNegativeInteger(credits, "available");
        var monthly = ReadNonNegativeInteger(credits, "monthly");
        var observedAt = ReadTimestamp(root) ?? fileTimestamp.ToUniversalTime();
        var age = _clock.UtcNow - observedAt;
        var freshness = age >= TimeSpan.Zero && age <= _staleAfter
            ? QuotaFreshness.Fresh
            : QuotaFreshness.Stale;
        var creditDetail = available is not null && monthly is not null
            ? $" · {available}/{monthly} prompt credits"
            : string.Empty;
        var staleDetail = freshness == QuotaFreshness.Stale
            ? " · last known / stale"
            : string.Empty;
        snapshot = new QuotaSnapshot(
            remaining,
            used,
            available,
            monthly,
            observedAt,
            freshness,
            SourceName,
            $"{remaining}% remaining{creditDetail}{staleDetail} · observed {observedAt:O}");
        return true;
    }

    private static int? ReadPercentage(JsonElement element, string propertyName)
    {
        var value = ReadNonNegativeInteger(element, propertyName);
        return value is >= 0 and <= 100 ? value : null;
    }

    private static int? ReadNonNegativeInteger(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt32(out var value) ||
            value < 0)
        {
            return null;
        }
        return value;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                property.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return null;
        }
        return timestamp;
    }

    private static bool TryExtractJsonObject(string text, int start, out string json)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                json = text[start..(index + 1)];
                return true;
            }
        }

        json = string.Empty;
        return false;
    }

    private static async Task<string> ReadTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = (int)Math.Min(stream.Length, MaximumTailBytes);
        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private static QuotaSnapshot Unavailable(string detail)
        => new(
            null,
            null,
            null,
            null,
            null,
            QuotaFreshness.Unavailable,
            SourceName,
            detail);
}
