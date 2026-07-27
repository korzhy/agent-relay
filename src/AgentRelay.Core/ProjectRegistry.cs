namespace AgentRelay.Core;

public sealed record RegisteredProject(
    string Id,
    string Name,
    string Path,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? TrustedAt = null);

public sealed record ProjectRegistryDocument(
    int SchemaVersion,
    IReadOnlyList<RegisteredProject> Projects);

public sealed class ProjectRegistry
{
    private readonly AtomicFileStore _files;
    private readonly string _path;
    private readonly IClock _clock;

    public ProjectRegistry(AtomicFileStore files, string path, IClock? clock = null)
    {
        _files = files;
        _path = path;
        _clock = clock ?? new SystemClock();
    }

    public async Task<IReadOnlyList<RegisteredProject>> ListAsync(
        CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken).ConfigureAwait(false)).Projects;

    public async Task<RegisteredProject> AddAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var canonical = WorkspaceSafety.Validate(projectPath);
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = document.Projects.FirstOrDefault(
            item => string.Equals(item.Path, canonical, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var project = new RegisteredProject(
            Guid.NewGuid().ToString("N"),
            new DirectoryInfo(canonical).Name,
            canonical,
            _clock.UtcNow);
        var projects = document.Projects.Append(project).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await SaveAsync(new ProjectRegistryDocument(1, projects), cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<bool> RemoveAsync(string idOrPath, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var projects = document.Projects.Where(
            item => !string.Equals(item.Id, idOrPath, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Path, TryFullPath(idOrPath), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projects.Length == document.Projects.Count)
        {
            return false;
        }

        await SaveAsync(new ProjectRegistryDocument(1, projects), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<RegisteredProject> TrustAsync(
        string idOrPath,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var target = Find(document.Projects, idOrPath)
            ?? throw new KeyNotFoundException($"Project is not registered: {idOrPath}");
        var updated = target with { TrustedAt = _clock.UtcNow };
        var projects = document.Projects.Select(item => item.Id == target.Id ? updated : item).ToArray();
        await SaveAsync(new ProjectRegistryDocument(1, projects), cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<RegisteredProject?> FindAsync(
        string idOrPath,
        CancellationToken cancellationToken = default)
        => Find((await LoadAsync(cancellationToken).ConfigureAwait(false)).Projects, idOrPath);

    private async Task<ProjectRegistryDocument> LoadAsync(CancellationToken cancellationToken)
    {
        var document = await _files.ReadJsonAsync<ProjectRegistryDocument>(_path, cancellationToken)
            .ConfigureAwait(false) ?? new ProjectRegistryDocument(1, []);
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported projects schemaVersion: {document.SchemaVersion}");
        }

        return document;
    }

    private Task SaveAsync(ProjectRegistryDocument document, CancellationToken cancellationToken)
        => _files.WriteJsonAsync(_path, document, createBackup: File.Exists(_path), cancellationToken);

    private static RegisteredProject? Find(IEnumerable<RegisteredProject> projects, string idOrPath)
    {
        var fullPath = TryFullPath(idOrPath);
        return projects.FirstOrDefault(
            item => string.Equals(item.Id, idOrPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }
}

public static class WorkspaceSafety
{
    public static string Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Workspace path is required.", nameof(path));
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Workspace does not exist: {full}");
        }

        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("A workspace root cannot be a symlink or junction.");
        }

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A drive or filesystem root cannot be trusted.");
        }

        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        }.Where(item => !string.IsNullOrWhiteSpace(item))
         .Select(item => Path.GetFullPath(item).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (var systemPath in forbidden)
        {
            if (string.Equals(full, systemPath, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(systemPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(systemPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"System workspace is forbidden: {full}");
            }
        }

        return full;
    }

    public static string ResolveRelative(string workspaceRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Protocol path must be relative: {relativePath}");
        }

        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Protocol path escapes workspace: {relativePath}");
        }

        EnsureNoReparsePoint(root, candidate);
        return candidate;
    }

    private static void EnsureNoReparsePoint(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Protocol path crosses a symlink or junction: {relative}");
            }
        }
    }
}
