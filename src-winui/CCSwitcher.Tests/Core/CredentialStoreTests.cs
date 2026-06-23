// Tests for CredentialStore — mirrors the Rust test suite in credential_store.rs.
//
// Only InMemoryCredentialStore and FileCredentialStore (at a temp path) are
// tested here.  Tests never touch the real ~/.claude/.credentials.json file.

using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class CredentialStoreTests : IDisposable
{
    // One isolated temp directory per test instance; cleaned up in Dispose.
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"ccswitcher-cred-tests-{Guid.NewGuid():N}");

    public CredentialStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------

    private const string Blob =
        """{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1}}""";

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private FileCredentialStore NewFileStore(string fileName = ".credentials.json")
        => new(Path.Combine(_dir, fileName));

    // -----------------------------------------------------------------------
    // InMemoryCredentialStore tests
    // -----------------------------------------------------------------------

    [Fact]
    public void InMemory_Read_InitiallyReturnsNull()
    {
        var store = new InMemoryCredentialStore();
        Assert.Null(store.Read());
    }

    [Fact]
    public void InMemory_WriteThenRead_ReturnsBlob()
    {
        var store = new InMemoryCredentialStore();
        store.Write(Blob);
        Assert.Equal(Blob, store.Read());
    }

    [Fact]
    public void InMemory_WriteEmptyString_ReadReturnsNull()
    {
        var store = new InMemoryCredentialStore();
        store.Write("something");
        store.Write("");          // overwrite with empty
        Assert.Null(store.Read());
    }

    [Fact]
    public void InMemory_WriteOverwrites()
    {
        var store = new InMemoryCredentialStore();
        store.Write("first");
        store.Write(Blob);
        Assert.Equal(Blob, store.Read());
    }

    // -----------------------------------------------------------------------
    // FileCredentialStore tests
    // -----------------------------------------------------------------------

    [Fact]
    public void File_ReadMissing_ReturnsNull()
    {
        var store = NewFileStore();
        Assert.Null(store.Read());
    }

    [Fact]
    public void File_WriteThenRead_ReturnsBlob()
    {
        var store = NewFileStore();
        store.Write(Blob);
        Assert.Equal(Blob, store.Read());
    }

    [Fact]
    public void File_AtomicWrite_LeavesNoTmpFile()
    {
        var store = NewFileStore();
        store.Write(Blob);

        var leftovers = Directory.EnumerateFiles(_dir, "*.tmp",
                SearchOption.AllDirectories)
            .ToList();

        Assert.Empty(leftovers);
    }

    [Fact]
    public void File_SecondWrite_CreatesBackup()
    {
        var store = NewFileStore();

        // First write: no prior file → no backup.
        store.Write(Blob);
        var backupsDir = Path.Combine(_dir, "backups");
        Assert.False(Directory.Exists(backupsDir) &&
                     Directory.GetFiles(backupsDir, "*.bak").Length > 0,
            "No backup should exist after the first write.");

        // Second write: existing file → backup created.
        const string Updated = """{"claudeAiOauth":{"accessToken":"b"}}""";
        store.Write(Updated);

        Assert.True(Directory.Exists(backupsDir));
        var baks = Directory.GetFiles(backupsDir, "*.bak");
        Assert.Single(baks);

        // The backup must contain the content that was there before the second write.
        Assert.Equal(Blob, File.ReadAllText(baks[0]));

        // Current content is the updated blob.
        Assert.Equal(Updated, store.Read());
    }

    [Fact]
    public void File_EmptyFileContents_ReadReturnsNull()
    {
        var path = Path.Combine(_dir, ".credentials.json");
        File.WriteAllText(path, "");
        var store = new FileCredentialStore(path);
        Assert.Null(store.Read());
    }

    [Fact]
    public void File_WhitespaceOnlyContents_ReadReturnsNull()
    {
        var path = Path.Combine(_dir, ".credentials.json");
        File.WriteAllText(path, "   \n\t  ");
        var store = new FileCredentialStore(path);
        Assert.Null(store.Read());
    }
}
