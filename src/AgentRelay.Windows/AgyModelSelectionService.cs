using System.Diagnostics;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public enum ModelSelectionSource
{
    Catalog,
    Cache,
    BuiltInFallback
}

public sealed record ModelSelectionState(
    int SchemaVersion,
    string Provider,
    string Model,
    DateTimeOffset VerifiedAt);

public sealed record ModelSelectionResult(
    ExecutorIdentity Executor,
    ModelSelectionSource Source,
    string Detail);

public sealed class AgyModelSelectionService
{
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files;
    private readonly IClock _clock;

    public AgyModelSelectionService(AppPaths paths, AtomicFileStore files, IClock? clock = null)
    {
        _paths = paths;
        _files = files;
        _clock = clock ?? new SystemClock();
    }

    public async Task<ModelSelectionResult> ResolveAsync(
        string agyPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var catalog = await ReadCatalogAsync(agyPath, cancellationToken).ConfigureAwait(false);
            var model = AgyModelCatalog.SelectLatestFlashHigh(catalog)
                        ?? throw new InvalidDataException(
                            "agy models returned no supported gemini-*-flash-high model.");
            var state = new ModelSelectionState(
                1, AgentRelayConstants.Provider, model, _clock.UtcNow);
            await _files.WriteJsonAsync(
                _paths.ModelSelectionFile,
                state,
                createBackup: File.Exists(_paths.ModelSelectionFile),
                cancellationToken).ConfigureAwait(false);
            return new ModelSelectionResult(
                new ExecutorIdentity(state.Provider, state.Model),
                ModelSelectionSource.Catalog,
                $"Latest available Flash High model resolved from agy models: {model}.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or
                System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = await ReadValidCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return new ModelSelectionResult(
                    new ExecutorIdentity(cached.Provider, cached.Model),
                    ModelSelectionSource.Cache,
                    $"Model discovery failed ({exception.Message}); using last verified model {cached.Model}.");
            }

            return new ModelSelectionResult(
                new ExecutorIdentity(AgentRelayConstants.Provider, AgentRelayConstants.FallbackModel),
                ModelSelectionSource.BuiltInFallback,
                $"Model discovery failed ({exception.Message}); using built-in fallback " +
                $"{AgentRelayConstants.FallbackModel}.");
        }
    }

    private static async Task<string> ReadCatalogAsync(
        string agyPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(agyPath))
        {
            throw new FileNotFoundException("agy.exe was not found.", agyPath);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(agyPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("models");
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("agy models did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"agy models exited with code {process.ExitCode}: {stderr.Trim()}");
        }
        return stdout;
    }

    private async Task<ModelSelectionState?> ReadValidCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _files.ReadJsonAsync<ModelSelectionState>(
                _paths.ModelSelectionFile, cancellationToken).ConfigureAwait(false);
            return cached is
            {
                SchemaVersion: 1,
                Provider: AgentRelayConstants.Provider
            } && FlashModelIdentity.IsSupported(cached.Model)
                ? cached
                : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return null;
        }
    }
}

public static class AgyModelCatalog
{
    public static IReadOnlyList<string> ParseModels(string output)
    {
        var models = new List<string>();
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.AsSpan().IndexOfAny(' ', '\t');
            var model = (separator < 0 ? line.AsSpan() : line.AsSpan(0, separator)).ToString();
            if (model.Length > 0)
            {
                models.Add(model);
            }
        }
        return models;
    }

    public static string? SelectLatestFlashHigh(string output)
        => ParseModels(output)
            .Select(model => new
            {
                Model = model,
                Valid = FlashModelIdentity.TryGetVersion(model, out var version),
                Version = version
            })
            .Where(item => item.Valid)
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.Model, StringComparer.Ordinal)
            .Select(item => item.Model)
            .FirstOrDefault();

    public static bool ContainsExactModel(string output, string exactModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactModel);
        return ParseModels(output).Contains(exactModel, StringComparer.Ordinal);
    }
}
