using AgentRelay.Core;

namespace AgentRelay.Core.Tests;

public sealed class SolActivityTests
{
    [Fact]
    public void Validate_AcceptsUtcBoundedActivity()
    {
        var now = DateTimeOffset.Parse("2026-07-28T08:00:00Z");
        var activity = new SolActivity(
            1,
            "project",
            SolActivityPhase.Reviewing,
            "Sol independently reviews the report.",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            now,
            now.AddMinutes(15),
            SolActivity.CodexSource);

        activity.Validate();

        Assert.True(activity.IsFresh(now.AddMinutes(14)));
        Assert.False(activity.IsFresh(now.AddMinutes(16)));
    }

    [Fact]
    public void Validate_RejectsInvalidIdentityAndExpiry()
    {
        var now = DateTimeOffset.Parse("2026-07-28T08:00:00Z");
        var activity = new SolActivity(
            1, "project", SolActivityPhase.Working, "summary", "not-guid", null,
            now, now, SolActivity.CodexSource);

        Assert.Throws<InvalidDataException>(activity.Validate);
    }

    [Fact]
    public void MissionSelector_PrefersLiveThenAttentionThenLatestReady()
    {
        var now = DateTimeOffset.Parse("2026-07-28T08:00:00Z");
        var selected = MissionSelector.Select(
        [
            new MissionCandidate("ready-new", RelayState.Ready, now),
            new MissionCandidate("report", RelayState.ReportReady, now.AddMinutes(-3)),
            new MissionCandidate("running-old", RelayState.Running, now.AddHours(-1)),
            new MissionCandidate("running-new", RelayState.Waiting, now.AddMinutes(-2))
        ]);

        Assert.NotNull(selected);
        Assert.Equal("running-new", selected.ProjectId);
    }
}
