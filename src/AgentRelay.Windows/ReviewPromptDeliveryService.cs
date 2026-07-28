using AgentRelay.Core;

namespace AgentRelay.Windows;

public interface IClipboardWriter
{
    Task WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record ReviewDeliveryState(
    int SchemaVersion,
    string ProjectId,
    string ReviewAttemptId,
    string PromptSha256,
    bool Succeeded,
    DateTimeOffset UpdatedAt,
    string? Error);

public sealed record ReviewDeliveryResult(
    bool Succeeded,
    bool AlreadyDelivered,
    string? Error);

public sealed class ReviewPromptDeliveryService
{
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files;
    private readonly IClipboardWriter _clipboard;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _deliveryLock = new(1, 1);

    public ReviewPromptDeliveryService(
        AppPaths paths,
        AtomicFileStore files,
        IClipboardWriter clipboard,
        IClock? clock = null)
    {
        _paths = paths;
        _files = files;
        _clipboard = clipboard;
        _clock = clock ?? new SystemClock();
    }

    public string PathFor(string projectId)
        => Path.Combine(_paths.RuntimeDirectory, projectId, "review-delivery.json");

    public Task<ReviewDeliveryState?> GetAsync(
        string projectId,
        CancellationToken cancellationToken = default)
        => _files.ReadJsonAsync<ReviewDeliveryState>(PathFor(projectId), cancellationToken);

    public async Task<ReviewDeliveryResult> DeliverAsync(
        RegisteredProject project,
        string reviewAttemptId,
        string reviewPromptPath,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(reviewAttemptId, out var parsedAttempt))
        {
            throw new ArgumentException("reviewAttemptId must be a GUID.", nameof(reviewAttemptId));
        }
        if (!File.Exists(reviewPromptPath))
        {
            throw new FileNotFoundException("Review prompt was not found.", reviewPromptPath);
        }

        var normalizedAttempt = parsedAttempt.ToString("N");
        var promptHash = await AtomicFileStore.Sha256Async(reviewPromptPath, cancellationToken)
            .ConfigureAwait(false);
        await _deliveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await GetAsync(project.Id, cancellationToken).ConfigureAwait(false);
            if (existing is
                {
                    Succeeded: true
                } &&
                string.Equals(existing.ReviewAttemptId, normalizedAttempt, StringComparison.Ordinal) &&
                string.Equals(existing.PromptSha256, promptHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ReviewDeliveryResult(true, true, null);
            }

            try
            {
                var prompt = await File.ReadAllTextAsync(reviewPromptPath, cancellationToken)
                    .ConfigureAwait(false);
                await _clipboard.WriteTextAsync(prompt, cancellationToken).ConfigureAwait(false);
                await WriteStateAsync(
                    project.Id, normalizedAttempt, promptHash, true, null, cancellationToken).ConfigureAwait(false);
                return new ReviewDeliveryResult(true, false, null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await WriteStateAsync(
                    project.Id, normalizedAttempt, promptHash, false, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
                return new ReviewDeliveryResult(false, false, exception.Message);
            }
        }
        finally
        {
            _deliveryLock.Release();
        }
    }

    private Task WriteStateAsync(
        string projectId,
        string reviewAttemptId,
        string promptHash,
        bool succeeded,
        string? error,
        CancellationToken cancellationToken)
        => _files.WriteJsonAsync(
            PathFor(projectId),
            new ReviewDeliveryState(
                1, projectId, reviewAttemptId, promptHash, succeeded, _clock.UtcNow, error),
            false,
            cancellationToken);
}
