namespace AgentRelay.Core;

public sealed record UpdateSettings(
    int SchemaVersion,
    bool Enabled,
    int CheckIntervalHours,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultCheckIntervalHours = 6;

    public static UpdateSettings CreateDefault(IClock clock)
        => new(CurrentSchemaVersion, true, DefaultCheckIntervalHours, clock.UtcNow);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported update settings schema: {SchemaVersion}");
        }
        if (CheckIntervalHours is < 1 or > 168)
        {
            throw new InvalidDataException("Update check interval must be between 1 and 168 hours.");
        }
        if (UpdatedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Update settings timestamp must be UTC.");
        }
    }
}

public enum UpdateStatus
{
    Disabled,
    Current,
    Staged,
    Deferred,
    Installing,
    Failed
}

public sealed record UpdateState(
    int SchemaVersion,
    UpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    DateTimeOffset CheckedAt,
    string Detail,
    string? InstallerPath = null,
    string? InstallerSha256 = null)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported update state schema: {SchemaVersion}");
        }
        if (!ReleaseVersion.TryParse(CurrentVersion, out _))
        {
            throw new InvalidDataException("Current update version is invalid.");
        }
        if (LatestVersion is not null && !ReleaseVersion.TryParse(LatestVersion, out _))
        {
            throw new InvalidDataException("Latest update version is invalid.");
        }
        if (CheckedAt.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(Detail))
        {
            throw new InvalidDataException("Update state requires a UTC timestamp and detail.");
        }
        if (Status is UpdateStatus.Staged or UpdateStatus.Deferred or UpdateStatus.Installing &&
            (string.IsNullOrWhiteSpace(InstallerPath) ||
             InstallerSha256 is null ||
             InstallerSha256.Length != 64 ||
             !InstallerSha256.All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("Staged update requires an installer and SHA-256.");
        }
    }
}

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
    : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }
        var metadata = normalized.IndexOfAny(['+', '-']);
        if (metadata >= 0)
        {
            normalized = normalized[..metadata];
        }
        var parts = normalized.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch) ||
            major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
