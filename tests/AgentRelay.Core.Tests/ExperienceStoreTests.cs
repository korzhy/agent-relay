using AgentRelay.Core;

namespace AgentRelay.Core.Tests;

public sealed class ExperienceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RelayExperience_" + Guid.NewGuid().ToString("N"));
    private readonly AtomicFileStore _files = new();
    private readonly TestClock _clock = new();
    private readonly AppPaths _paths;
    private readonly RegisteredProject _project;
    private readonly ExperienceStore _store;
    private readonly ProtocolService _protocol;

    public ExperienceStoreTests()
    {
        var workspace = Path.Combine(_root, "repo");
        Directory.CreateDirectory(workspace);
        _paths = new AppPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        _project = new RegisteredProject(Guid.NewGuid().ToString("N"), "repo", workspace, _clock.UtcNow);
        _store = new ExperienceStore(_paths, _files, _clock);
        _protocol = new ProtocolService(_files, _clock);
    }

    [Fact]
    public async Task PassReportAloneDoesNotCreateExperience_AndAcceptanceNeedsControllerChecks()
    {
        var handoff = await PublishWithReport();
        Assert.Equal(0, (await _store.RecallAsync(_project.Id)).SampleCount);
        var input = Input(handoff) with { ReviewCommands = [] };
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RecordAsync(_project, input));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RecordAsync(_project,
            Input(handoff) with { ReviewCommands = [new ExecutedCommand("test", 1)] }));
        await _store.RecordAsync(_project, Input(handoff));
        Assert.Equal(1, (await _store.RecallAsync(_project.Id)).Accepted);
        var recalled = System.Text.Json.JsonSerializer.Serialize(await _store.RecallAsync(_project.Id), JsonSupport.Options);
        Assert.DoesNotContain("reviewCommands", recalled);
        Assert.DoesNotContain("Independent test and diff inspection passed", recalled);
        Assert.False(Directory.Exists(Path.Combine(_project.Path, "experience")));
    }

    [Fact]
    public async Task MissingEnvelopeOrTamperedTaskCannotBeAccepted()
    {
        var handoff = await _protocol.PublishAsync(_project.Path, new MissionRequest("task", "do work", ["test"]));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RecordAsync(_project, Input(handoff)));
        await WriteReport(handoff);
        await File.AppendAllTextAsync(handoff.TaskPath, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RecordAsync(_project, Input(handoff)));
    }

    [Fact]
    public async Task RetryIsIdempotent_DifferentReviewCannotOverwrite()
    {
        var handoff = await PublishWithReport();
        var input = Input(handoff);
        var first = await _store.RecordAsync(_project, input);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        var retry = await _store.RecordAsync(_project, input);
        Assert.Equal(first.RecordedAt, retry.RecordedAt);
        Assert.Equal(1, (await _store.RecallAsync(_project.Id)).SampleCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.RecordAsync(_project,
            input with { Lesson = "Different conclusion" }));
    }

    [Fact]
    public async Task RecallFiltersByProjectKindModelAge_AndLimitsContext()
    {
        for (var i = 0; i < 7; i++)
        {
            var handoff = await PublishWithReport();
            await _store.RecordAsync(_project, Input(handoff));
            _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        }
        var summary = await _store.RecallAsync(_project.Id);
        Assert.Equal(7, summary.SampleCount);
        Assert.Equal(5, summary.Recent.Count);
        Assert.Empty((await _store.RecallAsync(Guid.NewGuid().ToString("N"))).Recent);
        Assert.Empty((await _store.RecallAsync(_project.Id, DelegationTaskKind.Investigation)).Recent);
        Assert.Empty((await _store.RecallAsync(_project.Id, model: "different-model")).Recent);
        _clock.UtcNow = _clock.UtcNow.AddDays(91);
        Assert.Empty((await _store.RecallAsync(_project.Id)).Recent);
    }

    [Fact]
    public async Task BlockedWithoutReportIsRecorded_AndCorruptHistoryIsVisible()
    {
        var handoff = await _protocol.PublishAsync(_project.Path, new MissionRequest("task", "do work", []));
        await _store.RecordAsync(_project, Input(handoff) with
        {
            Outcome = ReviewOutcome.Blocked, Failure = DelegationFailure.Quota,
            ReviewCommands = [], Evidence = "Runner quota exhausted; no result was accepted."
        });
        var directory = Path.Combine(_paths.DataRoot, "experience", _project.Id);
        await File.WriteAllTextAsync(Path.Combine(directory, "corrupt.json"), "{");
        var summary = await _store.RecallAsync(_project.Id);
        Assert.Equal(1, summary.Blocked);
        Assert.Equal(0, summary.Accepted);
        Assert.Equal(1, summary.SkippedInvalid);
    }

    [Fact]
    public async Task RejectsUnsafeIdsAndContradictoryOutcomes()
    {
        var handoff = await PublishWithReport();
        await Assert.ThrowsAsync<ArgumentException>(() => _store.RecallAsync("../outside"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RecordAsync(_project,
            Input(handoff) with { HandoffId = "../../outside" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RecordAsync(_project,
            Input(handoff) with { Corrections = 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.RecordAsync(_project,
            Input(handoff) with { Outcome = ReviewOutcome.Corrected }));
        await _store.RecordAsync(_project, Input(handoff) with
        { Outcome = ReviewOutcome.Corrected, Corrections = 1, Failure = DelegationFailure.GateFailure });
        Assert.Equal(1, (await _store.RecallAsync(_project.Id)).Corrected);
    }

    private async Task<PublishedHandoff> PublishWithReport()
    {
        var handoff = await _protocol.PublishAsync(_project.Path, new MissionRequest("task", "do work", ["test"]));
        await WriteReport(handoff);
        return handoff;
    }

    private async Task WriteReport(PublishedHandoff handoff)
    {
        var c = handoff.Control;
        await _files.WriteJsonAsync(handoff.ExpectedReportPath, new ReportPayload(c.ProtocolVersion,
            c.HandoffId, c.MissionId, c.Revision, c.RunAttemptId, c.Executor, _clock.UtcNow, ReportClaim.Pass,
            ["file.cs"], [new ExecutedCommand("test", 0)], null, [],
            new ProhibitedActionConfirmation(true, true, true, true, true, true, true), "Done"));
        await _protocol.AcceptReportAsync(handoff);
    }

    private static ExperienceInput Input(PublishedHandoff handoff) => new(handoff.Control.HandoffId,
        handoff.Control.Revision, DelegationTaskKind.Implementation, ReviewOutcome.Accepted, 0,
        DelegationFailure.None, "Caller-contract test was useful.", "Independent test and diff inspection passed.",
        [new ExecutedCommand("test", 0)]);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
