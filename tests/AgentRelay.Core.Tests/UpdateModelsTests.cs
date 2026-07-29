using AgentRelay.Core;

namespace AgentRelay.Core.Tests;

public sealed class UpdateModelsTests
{
    [Theory]
    [InlineData("0.3.0", 0, 3, 0)]
    [InlineData("v1.12.4", 1, 12, 4)]
    [InlineData("2.0.1+abcdef", 2, 0, 1)]
    [InlineData("3.4.5-preview.1", 3, 4, 5)]
    public void ReleaseVersion_TryParse_NormalizesSupportedVersions(
        string text,
        int major,
        int minor,
        int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("latest")]
    [InlineData("-1.2.3")]
    public void ReleaseVersion_TryParse_RejectsInvalidVersions(string text)
        => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void ReleaseVersion_CompareTo_UsesSemanticOrdering()
    {
        Assert.True(new ReleaseVersion(0, 3, 0).CompareTo(new ReleaseVersion(0, 2, 9)) > 0);
        Assert.True(new ReleaseVersion(1, 0, 0).CompareTo(new ReleaseVersion(0, 99, 99)) > 0);
        Assert.Equal(0, new ReleaseVersion(2, 4, 6).CompareTo(new ReleaseVersion(2, 4, 6)));
    }

    [Fact]
    public void UpdateSettings_Default_IsEnabledAndValid()
    {
        var settings = UpdateSettings.CreateDefault(
            new FixedClock(DateTimeOffset.Parse("2026-07-29T08:00:00Z")));

        settings.Validate();
        Assert.True(settings.Enabled);
        Assert.Equal(6, settings.CheckIntervalHours);
    }

    [Fact]
    public void UpdateState_StagedWithoutVerifiedPackageMetadata_IsRejected()
    {
        var state = new UpdateState(
            1,
            UpdateStatus.Staged,
            "0.2.0",
            "0.3.0",
            DateTimeOffset.Parse("2026-07-29T08:00:00Z"),
            "ready");

        Assert.Throws<InvalidDataException>(state.Validate);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
