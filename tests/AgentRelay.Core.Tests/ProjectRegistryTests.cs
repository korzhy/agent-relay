using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class ProjectRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AtomicFileStore _files;
    private readonly string _registryFile;
    private readonly ProjectRegistry _registry;

    public ProjectRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRelayCoreTests_Registry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _files = new AtomicFileStore();
        _registryFile = Path.Combine(_tempDir, "projects.json");
        _registry = new ProjectRegistry(_files, _registryFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task AddAsync_LeavesRepositoryDirectoryUntouched()
    {
        var projectPath = Path.Combine(_tempDir, "sample-repo");
        Directory.CreateDirectory(projectPath);
        var dummyFile = Path.Combine(projectPath, "app.cs");
        await File.WriteAllTextAsync(dummyFile, "// sample code");

        var entriesBefore = Directory.GetFileSystemEntries(projectPath, "*", SearchOption.AllDirectories)
            .OrderBy(x => x).ToArray();
        var bytesBefore = await File.ReadAllBytesAsync(dummyFile);

        var registered = await _registry.AddAsync(projectPath);

        Assert.NotNull(registered);
        var entriesAfter = Directory.GetFileSystemEntries(projectPath, "*", SearchOption.AllDirectories)
            .OrderBy(x => x).ToArray();
        var bytesAfter = await File.ReadAllBytesAsync(dummyFile);

        Assert.Equal(entriesBefore, entriesAfter);
        Assert.Equal(bytesBefore, bytesAfter);
    }

    [Fact]
    public async Task AddAsync_RejectsDriveRootPath()
    {
        var driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _registry.AddAsync(driveRoot));
    }

    [Fact]
    public async Task AddAsync_RejectsSystemWorkspacePaths()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (!string.IsNullOrWhiteSpace(winDir) && Directory.Exists(winDir))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _registry.AddAsync(winDir));
        }

        if (!string.IsNullOrWhiteSpace(progFiles) && Directory.Exists(progFiles))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _registry.AddAsync(progFiles));
        }
    }

    [Fact]
    public async Task AddAsync_IsIdempotent_AndListTrustRemoveWork()
    {
        var projectPath = Path.Combine(_tempDir, "my-repo");
        Directory.CreateDirectory(projectPath);

        var reg1 = await _registry.AddAsync(projectPath);
        var reg2 = await _registry.AddAsync(projectPath);

        Assert.Equal(reg1.Id, reg2.Id);

        var list = await _registry.ListAsync();
        Assert.Single(list);
        Assert.Equal(reg1.Id, list[0].Id);

        var found = await _registry.FindAsync(projectPath);
        Assert.NotNull(found);
        Assert.Null(found.TrustedAt);

        var trusted = await _registry.TrustAsync(reg1.Id);
        Assert.NotNull(trusted.TrustedAt);

        var removed = await _registry.RemoveAsync(reg1.Id);
        Assert.True(removed);

        var listEmpty = await _registry.ListAsync();
        Assert.Empty(listEmpty);
    }
}
