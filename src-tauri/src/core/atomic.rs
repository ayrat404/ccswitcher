//! Atomic file writes and timestamped backups.
//!
//! Every destructive write ccswitcher performs goes through [`atomic_write`]
//! (temp file in the same directory + rename) so a target file is never left
//! half-written. Before overwriting an existing file, callers take a
//! [`backup`] into a dedicated `backups/` directory; backups are timestamped
//! (epoch millis, lexicographically sortable) and pruned to a small retention
//! cap so the directory never grows without bound.

use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

/// Default number of timestamped backups to retain per file.
pub const DEFAULT_RETENTION: usize = 10;

/// Atomically write `bytes` to `path`.
///
/// Writes to a uniquely-named temporary file in the **same directory** as the
/// target (so the final rename stays on one filesystem and is atomic), flushes
/// it to disk, then renames it over `path`. On success no temporary file is
/// left behind; on failure the temp file is best-effort removed.
pub fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let dir = path.parent().unwrap_or_else(|| Path::new("."));
    fs::create_dir_all(dir)?;

    // Unique temp name in the same dir: <filename>.<nanos>.<pid>.tmp
    let file_name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("config");
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let tmp_name = format!("{file_name}.{nanos}.{}.tmp", std::process::id());
    let tmp_path = dir.join(tmp_name);

    // Scope the file handle so it is fully closed before the rename (required
    // on Windows, where renaming over a file held open can fail).
    let write_result = (|| -> io::Result<()> {
        let mut f = fs::File::create(&tmp_path)?;
        f.write_all(bytes)?;
        f.flush()?;
        f.sync_all()?;
        Ok(())
    })();

    if let Err(e) = write_result {
        let _ = fs::remove_file(&tmp_path);
        return Err(e);
    }

    // On Windows, `rename` fails if the destination exists; remove first.
    // This is best-effort and only matters on platforms without atomic
    // replace semantics for `fs::rename`.
    #[cfg(windows)]
    {
        if path.exists() {
            let _ = fs::remove_file(path);
        }
    }

    if let Err(e) = fs::rename(&tmp_path, path) {
        let _ = fs::remove_file(&tmp_path);
        return Err(e);
    }

    Ok(())
}

/// Backup `path` into `backups_dir` as `<filename>.<timestamp>.bak`, keeping the
/// newest [`DEFAULT_RETENTION`] backups and pruning older ones.
///
/// No-op (returns `Ok(None)`) if the source file does not exist. On success
/// returns the path of the created backup.
pub fn backup(path: &Path, backups_dir: &Path) -> io::Result<Option<PathBuf>> {
    backup_with_retention(path, backups_dir, DEFAULT_RETENTION)
}

/// Like [`backup`] but with an explicit retention count.
pub fn backup_with_retention(
    path: &Path,
    backups_dir: &Path,
    retention: usize,
) -> io::Result<Option<PathBuf>> {
    if !path.exists() {
        return Ok(None);
    }

    fs::create_dir_all(backups_dir)?;

    let file_name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("config");

    let ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);

    // Zero-pad millis so lexicographic order == chronological order.
    let backup_name = format!("{file_name}.{ts:013}.bak");
    let backup_path = backups_dir.join(backup_name);

    fs::copy(path, &backup_path)?;

    prune(backups_dir, file_name, retention)?;

    Ok(Some(backup_path))
}

/// Remove the oldest backups for `file_name` in `backups_dir`, keeping at most
/// `retention`. Backup names sort chronologically because the timestamp is
/// zero-padded epoch millis.
fn prune(backups_dir: &Path, file_name: &str, retention: usize) -> io::Result<()> {
    let prefix = format!("{file_name}.");
    let suffix = ".bak";

    let mut backups: Vec<PathBuf> = fs::read_dir(backups_dir)?
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| {
            p.file_name()
                .and_then(|n| n.to_str())
                .map(|n| n.starts_with(&prefix) && n.ends_with(suffix))
                .unwrap_or(false)
        })
        .collect();

    // Sort ascending (oldest first) by file name.
    backups.sort();

    if backups.len() > retention {
        let to_remove = backups.len() - retention;
        for p in backups.into_iter().take(to_remove) {
            let _ = fs::remove_file(p);
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::thread::sleep;
    use std::time::Duration;

    #[test]
    fn atomic_write_writes_correct_content() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("config.json");

        atomic_write(&target, b"hello world").unwrap();

        let read = fs::read(&target).unwrap();
        assert_eq!(read, b"hello world");
    }

    #[test]
    fn atomic_write_overwrites_existing() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("config.json");

        atomic_write(&target, b"first").unwrap();
        atomic_write(&target, b"second").unwrap();

        assert_eq!(fs::read(&target).unwrap(), b"second");
    }

    #[test]
    fn atomic_write_leaves_no_temp_files() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("config.json");

        atomic_write(&target, b"data").unwrap();

        let leftovers: Vec<_> = fs::read_dir(dir.path())
            .unwrap()
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().to_string())
            .filter(|n| n.ends_with(".tmp"))
            .collect();
        assert!(leftovers.is_empty(), "found temp leftovers: {leftovers:?}");

        // Only the target file should be present.
        let all: Vec<_> = fs::read_dir(dir.path())
            .unwrap()
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().to_string())
            .collect();
        assert_eq!(all, vec!["config.json".to_string()]);
    }

    #[test]
    fn atomic_write_creates_missing_parent_dirs() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("nested/deeper/config.json");

        atomic_write(&target, b"x").unwrap();

        assert_eq!(fs::read(&target).unwrap(), b"x");
    }

    #[test]
    fn backup_noop_when_source_missing() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("config.json");
        let backups = dir.path().join("backups");

        let result = backup(&target, &backups).unwrap();
        assert!(result.is_none());
        // Backups dir not created for a no-op.
        assert!(!backups.exists());
    }

    #[test]
    fn backup_creates_timestamped_copy_and_leaves_original() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("config.json");
        let backups = dir.path().join("backups");
        fs::write(&target, b"original").unwrap();

        let backup_path = backup(&target, &backups).unwrap().unwrap();

        // Original intact.
        assert_eq!(fs::read(&target).unwrap(), b"original");
        // Backup is a faithful copy.
        assert_eq!(fs::read(&backup_path).unwrap(), b"original");
        // Backup name shape: config.json.<digits>.bak
        let name = backup_path.file_name().unwrap().to_string_lossy();
        assert!(name.starts_with("config.json."), "name: {name}");
        assert!(name.ends_with(".bak"), "name: {name}");
    }

    #[test]
    fn backup_retention_prunes_oldest() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("config.json");
        let backups = dir.path().join("backups");

        // Create more backups than the retention cap, with distinct timestamps.
        let retention = 3;
        for i in 0..6 {
            fs::write(&target, format!("v{i}")).unwrap();
            backup_with_retention(&target, &backups, retention).unwrap();
            // Ensure distinct millisecond timestamps so names sort uniquely.
            sleep(Duration::from_millis(3));
        }

        let mut names: Vec<String> = fs::read_dir(&backups)
            .unwrap()
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().to_string())
            .filter(|n| n.ends_with(".bak"))
            .collect();
        names.sort();

        // Only `retention` backups kept.
        assert_eq!(names.len(), retention, "kept: {names:?}");

        // The kept backups are the newest ones, whose contents are the latest writes.
        let newest = backups.join(names.last().unwrap());
        assert_eq!(fs::read(newest).unwrap(), b"v5");
    }

    #[test]
    fn backup_only_prunes_matching_filename() {
        let dir = tempfile::tempdir().unwrap();
        let backups = dir.path().join("backups");
        fs::create_dir_all(&backups).unwrap();

        // An unrelated file in the backups dir must not be pruned.
        let unrelated = backups.join("other.txt");
        fs::write(&unrelated, b"keep me").unwrap();

        let target = dir.path().join("config.json");
        for i in 0..5 {
            fs::write(&target, format!("v{i}")).unwrap();
            backup_with_retention(&target, &backups, 2).unwrap();
            sleep(Duration::from_millis(3));
        }

        assert!(unrelated.exists(), "unrelated file was pruned");
        let kept: Vec<_> = fs::read_dir(&backups)
            .unwrap()
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().to_string())
            .filter(|n| n.ends_with(".bak"))
            .collect();
        assert_eq!(kept.len(), 2);
    }
}
