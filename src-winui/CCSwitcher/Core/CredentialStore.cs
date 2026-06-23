// OAuth credential store behind an interface, with a file-backed backend
// and an in-memory mock for tests.
//
// Port of src-tauri/src/core/credential_store.rs.
//
// Claude Code stores its OAuth credential blob ({"claudeAiOauth":{...}}) in
// ~/.claude/.credentials.json on Windows/Linux.  ccswitcher snapshots this
// blob before switching away from an OAuth account and restores it when
// switching back.  The ICredentialStore interface keeps the switching logic
// testable with InMemoryCredentialStore so unit tests never touch the real
// credential file.

using System.IO;

namespace CCSwitcher.Core;

/// <summary>
/// Reads and writes Claude Code's OAuth credential blob for snapshot/restore.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Read the current credential blob.
    /// Returns <see langword="null"/> when the store is absent or empty.
    /// </summary>
    string? Read();

    /// <summary>
    /// Write (overwrite) the credential blob atomically, preceded by a
    /// timestamped backup of any previously existing file.
    /// </summary>
    void Write(string blob);
}

/// <summary>
/// <see cref="ICredentialStore"/> backed by
/// <c>~/.claude/.credentials.json</c> (Windows/Linux).
/// Writes are atomic (temp + rename) and preceded by a timestamped backup in
/// a <c>backups/</c> directory next to the credential file.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private readonly string _path;
    private readonly string _backupsDir;

    /// <summary>
    /// Construct a file-backed credential store at an explicit path.
    /// The <c>backups/</c> directory is placed next to the credential file.
    /// </summary>
    /// <param name="path">Absolute path to the credential file.</param>
    public FileCredentialStore(string path)
    {
        _path = path;
        _backupsDir = Path.Combine(
            Path.GetDirectoryName(path) ?? ".",
            "backups");
    }

    /// <inheritdoc/>
    public string? Read()
    {
        if (!File.Exists(_path))
            return null;

        var content = File.ReadAllText(_path, System.Text.Encoding.UTF8);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <inheritdoc/>
    public void Write(string blob)
    {
        // Back up any existing credential file before overwriting it.
        // AtomicFile.Backup is a no-op when the file does not yet exist.
        AtomicFile.Backup(_path, _backupsDir);
        AtomicFile.Write(_path, blob);
    }
}

/// <summary>
/// In-memory <see cref="ICredentialStore"/> for tests.
/// Stores a single optional string; empty string is treated as absent,
/// matching the file backend behaviour.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private string? _blob;

    /// <inheritdoc/>
    public string? Read() => string.IsNullOrEmpty(_blob) ? null : _blob;

    /// <inheritdoc/>
    public void Write(string blob) => _blob = blob;
}
