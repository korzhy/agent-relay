using System.Text.Json;
using AgentRelay.Core;
using AgentRelay.Windows;

namespace AgentRelay.IntegrationTests;

public sealed class RuntimePresentationServicesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "AgentRelayPresentation_" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files = new();
    private readonly FixedClock _clock =
        new(DateTimeOffset.Parse("2026-07-28T08:00:00Z"));

    public RuntimePresentationServicesTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        Directory.CreateDirectory(_paths.HomeDirectory);
        Directory.CreateDirectory(_paths.LocalAppDataDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    [Fact]
    public async Task ActivityStore_WritesAtomicValidatedStatusWithDefaultExpiry()
    {
        var registry = new ProjectRegistry(_files, _paths.ProjectsFile, _clock);
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var project = await registry.AddAsync(workspace);
        var store = new SolActivityStore(_paths, _files, _clock);

        var activity = await store.SetAsync(
            project,
            SolActivityPhase.Evaluating,
            "Sol оценивает внешнее делегирование.");
        var loaded = await store.GetAsync(project.Id);

        Assert.Equal(activity, loaded);
        Assert.Equal(_clock.UtcNow.AddMinutes(15), loaded!.ExpiresAt);
        Assert.Equal(SolActivity.CodexSource, loaded.Source);
    }

    [Fact]
    public async Task ActionLogReader_AcceptsPrettyCompactMixedAndIncompleteTail()
    {
        var runtime = new RuntimeStore(_paths, _files);
        var first = new ActionLogEntry(_clock.UtcNow, "p1", "dispatch", "first");
        var second = new ActionLogEntry(_clock.UtcNow.AddSeconds(1), "p1", "complete", "second", 0);
        var path = runtime.ActionLogPath("p1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var legacy = JsonSerializer.Serialize(first, JsonSupport.Options) + Environment.NewLine +
                     JsonSerializer.Serialize(second, JsonSupport.CompactOptions) + Environment.NewLine +
                     "{\"timestamp\":";
        await File.WriteAllTextAsync(path, legacy);

        var result = await runtime.ReadLogAsync("p1", 10);

        Assert.Collection(
            result.Entries,
            entry => Assert.Equal("dispatch", entry.Action),
            entry => Assert.Equal("complete", entry.Action));
        Assert.Equal(0, result.InvalidRecordCount);
        Assert.True(result.HasIncompleteTail);

        await runtime.AppendLogAsync(
            new ActionLogEntry(_clock.UtcNow.AddSeconds(2), "p1", "review", "third"));
        var finalLine = (await File.ReadAllLinesAsync(path)).Last();
        Assert.StartsWith("{", finalLine);
        Assert.EndsWith("}", finalLine);
    }

    [Fact]
    public async Task ReviewDelivery_CopiesUniqueAttemptOnlyOnce()
    {
        var registry = new ProjectRegistry(_files, _paths.ProjectsFile, _clock);
        var workspace = Path.Combine(_root, "delivery");
        Directory.CreateDirectory(workspace);
        var project = await registry.AddAsync(workspace);
        var prompt = Path.Combine(workspace, "prompt.txt");
        await File.WriteAllTextAsync(prompt, "review exact hashes");
        var clipboard = new RecordingClipboard();
        var delivery = new ReviewPromptDeliveryService(_paths, _files, clipboard, _clock);
        var attempt = Guid.NewGuid().ToString("N");

        var first = await delivery.DeliverAsync(project, attempt, prompt);
        var duplicate = await delivery.DeliverAsync(project, attempt, prompt);

        Assert.True(first.Succeeded);
        Assert.False(first.AlreadyDelivered);
        Assert.True(duplicate.AlreadyDelivered);
        Assert.Equal(1, clipboard.WriteCount);
        Assert.Equal("review exact hashes", clipboard.LastText);
    }

    [Fact]
    public async Task ReviewDelivery_PreservesReadyStateWhenClipboardFails()
    {
        var registry = new ProjectRegistry(_files, _paths.ProjectsFile, _clock);
        var workspace = Path.Combine(_root, "delivery-fail");
        Directory.CreateDirectory(workspace);
        var project = await registry.AddAsync(workspace);
        var prompt = Path.Combine(workspace, "prompt.txt");
        await File.WriteAllTextAsync(prompt, "review");
        var delivery = new ReviewPromptDeliveryService(
            _paths, _files, new FailingClipboard(), _clock);

        var result = await delivery.DeliverAsync(project, Guid.NewGuid().ToString("N"), prompt);
        var state = await delivery.GetAsync(project.Id);

        Assert.False(result.Succeeded);
        Assert.NotNull(state);
        Assert.False(state.Succeeded);
        Assert.Contains("busy", state.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class RecordingClipboard : IClipboardWriter
    {
        public int WriteCount { get; private set; }
        public string? LastText { get; private set; }

        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            LastText = text;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingClipboard : IClipboardWriter
    {
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Clipboard is busy.");
    }
}
