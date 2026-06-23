// Tests for AtomicFile — mirrors the Rust test suite in atomic.rs.
//
// All tests use isolated temporary directories so they never touch real files.

using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class AtomicFileTests : IDisposable
{
    // One temp directory per test instance; cleaned up in Dispose.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ccswitcher-tests-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // Write tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Write_CreatesTargetFileWithCorrectContent()
    {
        var target = Path.Combine(_dir, "config.json");

        AtomicFile.Write(target, "hello world");

        Assert.True(File.Exists(target));
        Assert.Equal("hello world", File.ReadAllText(target));
    }

    [Fact]
    public void Write_LeavesNoTmpFileAfterSuccess()
    {
        var target = Path.Combine(_dir, "config.json");

        AtomicFile.Write(target, "data");

        var leftovers = Directory.EnumerateFiles(_dir)
            .Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(leftovers);
    }

    [Fact]
    public void Write_OverwritesExistingFile()
    {
        var target = Path.Combine(_dir, "config.json");

        AtomicFile.Write(target, "first");
        AtomicFile.Write(target, "second");

        Assert.Equal("second", File.ReadAllText(target));
    }

    [Fact]
    public void Write_CreatesParentDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "nested", "deeper", "config.json");

        AtomicFile.Write(nested, "x");

        Assert.True(File.Exists(nested));
        Assert.Equal("x", File.ReadAllText(nested));
    }

    // -----------------------------------------------------------------------
    // Backup tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Backup_CreatesBakFileInBackupsDir()
    {
        var source = Path.Combine(_dir, "config.json");
        var backupsDir = Path.Combine(_dir, "backups");
        File.WriteAllText(source, "original");

        AtomicFile.Backup(source, backupsDir);

        Assert.True(Directory.Exists(backupsDir));
        var baks = Directory.GetFiles(backupsDir, "*.bak");
        Assert.Single(baks);

        // Backup must be a faithful copy.
        Assert.Equal("original", File.ReadAllText(baks[0]));

        // Backup file name must follow pattern: config.json.<timestamp>.bak
        var name = Path.GetFileName(baks[0]);
        Assert.StartsWith("config.json.", name);
        Assert.EndsWith(".bak", name);
    }

    [Fact]
    public void Backup_OnMissingSourceIsNoOp()
    {
        var source = Path.Combine(_dir, "does-not-exist.json");
        var backupsDir = Path.Combine(_dir, "backups");

        // Must not throw.
        var ex = Record.Exception(() => AtomicFile.Backup(source, backupsDir));
        Assert.Null(ex);

        // backupsDir must NOT be created for a no-op (matches Rust behaviour).
        Assert.False(Directory.Exists(backupsDir));
    }

    [Fact]
    public void Backup_Prunes_WhenMoreThanMaxKeepBackupsExist()
    {
        const int maxKeep = 3;
        var source = Path.Combine(_dir, "config.json");
        var backupsDir = Path.Combine(_dir, "backups");

        // Create 6 backups, sleeping briefly between each so timestamps differ.
        for (int i = 0; i < 6; i++)
        {
            File.WriteAllText(source, $"v{i}");
            AtomicFile.Backup(source, backupsDir, maxKeep);
            // Brief pause to guarantee distinct millisecond timestamps.
            Thread.Sleep(5);
        }

        var baks = Directory.GetFiles(backupsDir, "*.bak")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();

        Assert.Equal(maxKeep, baks.Count);

        // The newest backup must contain the last-written content ("v5").
        Assert.Equal("v5", File.ReadAllText(baks.Last()));
    }

    [Fact]
    public void Backup_WithMaxKeepOne_KeepsOnlyNewest()
    {
        var source = Path.Combine(_dir, "config.json");
        var backupsDir = Path.Combine(_dir, "backups");

        File.WriteAllText(source, "first");
        AtomicFile.Backup(source, backupsDir, maxKeep: 1);
        Thread.Sleep(5);

        File.WriteAllText(source, "second");
        AtomicFile.Backup(source, backupsDir, maxKeep: 1);

        var baks = Directory.GetFiles(backupsDir, "*.bak").ToList();
        Assert.Single(baks);
        Assert.Equal("second", File.ReadAllText(baks[0]));
    }

    [Fact]
    public void Backup_DoesNotPruneUnrelatedFilesInBackupsDir()
    {
        var source = Path.Combine(_dir, "config.json");
        var backupsDir = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backupsDir);

        // Place an unrelated file before any backups are taken.
        var unrelated = Path.Combine(backupsDir, "other.txt");
        File.WriteAllText(unrelated, "keep me");

        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(source, $"v{i}");
            AtomicFile.Backup(source, backupsDir, maxKeep: 2);
            Thread.Sleep(5);
        }

        Assert.True(File.Exists(unrelated), "Unrelated file was incorrectly pruned");

        var baks = Directory.GetFiles(backupsDir, "*.bak").ToList();
        Assert.Equal(2, baks.Count);
    }
}
