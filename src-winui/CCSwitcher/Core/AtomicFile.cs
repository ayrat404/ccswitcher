// Atomic file write and timestamped backup primitives.
//
// Port of src-tauri/src/core/atomic.rs.
//
// AtomicFile.Write:  write to <path>.tmp then File.Move(overwrite:true) so the
//                    target is never half-written.
// AtomicFile.Backup: copy to backupsDir/<filename>.<yyyyMMdd_HHmmss_fff>.bak,
//                    then prune oldest files beyond maxKeep.
//
// The timestamp format MUST remain "yyyyMMdd_HHmmss_fff" so newly created
// backups sort consistently alongside any backup files already created by the
// Rust build of ccswitcher.

using System.IO;

namespace CCSwitcher.Core;

/// <summary>
/// Static helpers for atomic file writes and timestamped backups.
/// Every destructive write in ccswitcher goes through these helpers so target
/// files are never left in a half-written state.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Atomically write <paramref name="content"/> to <paramref name="path"/>.
    /// <para>
    /// Writes to <c><paramref name="path"/>.tmp</c> (in the same directory as the
    /// target so the rename stays on one filesystem), then moves the temp file over
    /// the target with <c>overwrite: true</c>.  On success no <c>.tmp</c> file is
    /// left behind; on failure a best-effort cleanup removes the temp file.
    /// </para>
    /// <para>
    /// The parent directory is created if it does not already exist.
    /// </para>
    /// </summary>
    /// <param name="path">Absolute or relative path of the target file.</param>
    /// <param name="content">UTF-8 text to write.</param>
    /// <exception cref="IOException">Thrown if the write or rename fails.</exception>
    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = path + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, content, System.Text.Encoding.UTF8);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup of the temp file on failure.
            try { File.Delete(tmpPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Copy <paramref name="path"/> to
    /// <c><paramref name="backupsDir"/>/<paramref name="path"/>'s filename.<timestamp>.bak</c>
    /// and prune the oldest <c>.bak</c> files so that at most
    /// <paramref name="maxKeep"/> are retained.
    /// <para>
    /// If the source file does not exist this method is a no-op (no exception is
    /// thrown and <paramref name="backupsDir"/> is not created).
    /// </para>
    /// <para>
    /// The timestamp format is <c>yyyyMMdd_HHmmss_fff</c> (UTC) so filenames sort
    /// lexicographically in chronological order and remain consistent with backup
    /// files created by the Rust build of ccswitcher.
    /// </para>
    /// </summary>
    /// <param name="path">Source file to back up.</param>
    /// <param name="backupsDir">Directory in which to place backup files.</param>
    /// <param name="maxKeep">Maximum number of backup files to retain (default 10).</param>
    /// <exception cref="IOException">Thrown if the copy or directory creation fails.</exception>
    public static void Backup(string path, string backupsDir, int maxKeep = 10)
    {
        if (!File.Exists(path))
            return;

        Directory.CreateDirectory(backupsDir);

        var fileName = Path.GetFileName(path);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var backupName = $"{fileName}.{timestamp}.bak";
        var backupPath = Path.Combine(backupsDir, backupName);

        File.Copy(path, backupPath, overwrite: false);

        Prune(backupsDir, fileName, maxKeep);
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Delete the oldest <c>.bak</c> files for <paramref name="fileName"/> in
    /// <paramref name="backupsDir"/> so that at most <paramref name="maxKeep"/>
    /// remain.  Files from other sources are never touched.
    /// </summary>
    private static void Prune(string backupsDir, string fileName, int maxKeep)
    {
        var prefix = fileName + ".";
        const string suffix = ".bak";

        // Collect all matching backup files; sort ascending by name so oldest
        // (smallest timestamp) come first.
        var backups = Directory.EnumerateFiles(backupsDir)
            .Where(p =>
            {
                var n = Path.GetFileName(p);
                return n.StartsWith(prefix, StringComparison.Ordinal)
                    && n.EndsWith(suffix, StringComparison.Ordinal);
            })
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();

        if (backups.Count <= maxKeep)
            return;

        var toRemove = backups.Count - maxKeep;
        foreach (var old in backups.Take(toRemove))
        {
            try { File.Delete(old); } catch { /* best-effort */ }
        }
    }
}
