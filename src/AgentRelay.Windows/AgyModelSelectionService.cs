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

public sealed record ModelDiscoveryEntry(string Model, DateTimeOffset FirstSeenAt);

public sealed record ModelDiscoveryState(
    int SchemaVersion,
    IReadOnlyList<ModelDiscoveryEntry> Models)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported model discovery schemaVersion: {SchemaVersion}");
        }

        if (Models is null || Models.Count == 0)
        {
            throw new InvalidDataException("Model discovery history is empty.");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Models)
        {
            if (!GeminiModelIdentity.IsSupported(entry.Model) || entry.FirstSeenAt == default)
            {
                throw new InvalidDataException("Model discovery history contains an invalid entry.");
            }

            if (!unique.Add(entry.Model))
            {
                throw new InvalidDataException($"Duplicate model discovery entry: {entry.Model}");
            }
        }
    }
}

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
            var available = AgyModelCatalog.ParseModels(catalog)
                .Where(GeminiModelIdentity.IsSupported)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (available.Length == 0)
            {
                throw new InvalidDataException(
                    "agy models returned no supported gemini-<version>-<family>-high model.");
            }

            var observedAt = _clock.UtcNow;
            var discovery = await ReadDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            var updatedDiscovery = AgyModelCatalog.RecordObservedModels(
                discovery, available, observedAt, out var changed);
            var model = AgyModelCatalog.SelectMostRecentlyObservedHigh(available, updatedDiscovery)
                        ?? throw new InvalidDataException(
                            "Model discovery history does not cover the available Gemini High catalog.");

            if (changed)
            {
                await _files.WriteJsonAsync(
                    _paths.ModelDiscoveryFile,
                    updatedDiscovery,
                    createBackup: File.Exists(_paths.ModelDiscoveryFile),
                    cancellationToken).ConfigureAwait(false);
            }

            var state = new ModelSelectionState(
                1, AgentRelayConstants.Provider, model, observedAt);
            await _files.WriteJsonAsync(
                _paths.ModelSelectionFile,
                state,
                createBackup: File.Exists(_paths.ModelSelectionFile),
                cancellationToken).ConfigureAwait(false);
            var firstSeenAt = updatedDiscovery.Models.Single(entry => entry.Model == model).FirstSeenAt;
            return new ModelSelectionResult(
                new ExecutorIdentity(state.Provider, state.Model),
                ModelSelectionSource.Catalog,
                $"Most recently observed available Gemini High model: {model} " +
                $"(first seen {firstSeenAt:O}).");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or
                System.ComponentModel.Win32Exception or OperationCanceledException or
                System.Text.Json.JsonException)
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

    private async Task<ModelDiscoveryState?> ReadDiscoveryAsync(CancellationToken cancellationToken)
    {
        var discovery = await _files.ReadJsonAsync<ModelDiscoveryState>(
            _paths.ModelDiscoveryFile, cancellationToken).ConfigureAwait(false);
        discovery?.Validate();
        return discovery;
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
            } && GeminiModelIdentity.IsSupported(cached.Model)
                ? cached
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or System.Text.Json.JsonException)
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

    public static ModelDiscoveryState RecordObservedModels(
        ModelDiscoveryState? existing,
        IEnumerable<string> availableModels,
        DateTimeOffset observedAt,
        out bool changed)
    {
        existing?.Validate();
        var entries = existing?.Models.ToList() ?? [];
        var known = entries.Select(entry => entry.Model).ToHashSet(StringComparer.Ordinal);
        foreach (var model in availableModels
                     .Where(GeminiModelIdentity.IsSupported)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (known.Add(model))
            {
                entries.Add(new ModelDiscoveryEntry(model, observedAt));
            }
        }

        changed = existing is null || entries.Count != existing.Models.Count;
        var result = new ModelDiscoveryState(
            ModelDiscoveryState.CurrentSchemaVersion,
            entries.OrderBy(entry => entry.Model, StringComparer.Ordinal).ToArray());
        result.Validate();
        return result;
    }

    public static string? SelectMostRecentlyObservedHigh(
        IEnumerable<string> availableModels,
        ModelDiscoveryState discovery)
    {
        discovery.Validate();
        var firstSeen = discovery.Models.ToDictionary(
            entry => entry.Model, entry => entry.FirstSeenAt, StringComparer.Ordinal);
        return availableModels
            .Distinct(StringComparer.Ordinal)
            .Select(model =>
            {
                var validVersion = GeminiModelIdentity.TryGetVersion(model, out var version);
                var observed = firstSeen.TryGetValue(model, out var firstSeenAt);
                return new
                {
                    Model = model,
                    Valid = validVersion && observed,
                    Version = version,
                    FirstSeenAt = firstSeenAt
                };
            })
            .Where(item => item.Valid)
            .OrderByDescending(item => item.FirstSeenAt)
            .ThenByDescending(item => item.Version)
            .ThenByDescending(item => item.Model, StringComparer.Ordinal)
            .Select(item => item.Model)
            .FirstOrDefault();
    }

    public static bool ContainsExactModel(string output, string exactModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactModel);
        return ParseModels(output).Contains(exactModel, StringComparer.Ordinal);
    }
}
