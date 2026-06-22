//! OAuth credential-store adapter behind a trait, with platform backends and an
//! in-memory mock for tests.
//!
//! Claude Code keeps its OAuth credential blob (`{ "claudeAiOauth": { ... } }`)
//! in a platform-specific store:
//! - Windows/Linux → the file `~/.claude/.credentials.json` (plaintext JSON).
//! - macOS → the Keychain service `Claude Code-credentials`.
//!
//! ccswitcher uses this adapter to **snapshot** (read) the live blob before
//! switching away from an OAuth account and to **restore** (write) a stored blob
//! when switching back. The [`CredentialStore`] trait keeps the switching logic
//! testable with [`InMemoryCredentialStore`] so unit tests never touch the real
//! credential file or the OS Keychain.

use std::io;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use thiserror::Error;

use super::atomic::{atomic_write, backup};
use super::claude_paths::{credentials_path, ClaudePathError};

/// Keychain service name Claude Code uses for the OAuth credential blob (macOS).
pub const KEYCHAIN_SERVICE: &str = "Claude Code-credentials";

/// Errors raised while accessing the OAuth credential store.
#[derive(Debug, Error)]
pub enum CredentialStoreError {
    /// An underlying filesystem operation failed.
    #[error("credential store io error: {0}")]
    Io(#[from] io::Error),
    /// A Claude Code path could not be resolved.
    #[error(transparent)]
    Path(#[from] ClaudePathError),
    /// The macOS Keychain backend failed.
    #[cfg(target_os = "macos")]
    #[error("keychain error: {0}")]
    Keychain(#[from] keyring::Error),
    /// An in-memory mock's lock was poisoned (test-only path).
    #[error("credential store lock poisoned")]
    Lock,
}

/// Reads and writes Claude Code's OAuth credential blob for snapshot/restore.
pub trait CredentialStore {
    /// Read the current credential blob. Returns `Ok(None)` when the store is
    /// absent or empty.
    fn read(&self) -> Result<Option<String>, CredentialStoreError>;

    /// Write (overwrite) the credential blob.
    fn write(&self, blob: &str) -> Result<(), CredentialStoreError>;
}

/// [`CredentialStore`] backed by the `~/.claude/.credentials.json` file
/// (Windows/Linux). Writes are atomic and preceded by a timestamped backup in a
/// `backups/` directory next to the file.
pub struct FileCredentialStore {
    path: PathBuf,
}

impl FileCredentialStore {
    /// Construct a file-backed store at an explicit path (used by tests and the
    /// platform default).
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    /// Construct a file-backed store at the default `~/.claude/.credentials.json`
    /// path.
    pub fn at_default_path() -> Result<Self, CredentialStoreError> {
        Ok(Self::new(credentials_path()?))
    }

    /// The `backups/` directory used for timestamped backups, located next to the
    /// credential file.
    fn backups_dir(&self) -> PathBuf {
        let parent = self.path.parent().unwrap_or_else(|| Path::new("."));
        parent.join("backups")
    }
}

impl CredentialStore for FileCredentialStore {
    fn read(&self) -> Result<Option<String>, CredentialStoreError> {
        if !self.path.exists() {
            return Ok(None);
        }
        let contents = std::fs::read_to_string(&self.path)?;
        if contents.is_empty() {
            return Ok(None);
        }
        Ok(Some(contents))
    }

    fn write(&self, blob: &str) -> Result<(), CredentialStoreError> {
        // Back up any existing credential file before overwriting it.
        backup(&self.path, &self.backups_dir())?;
        atomic_write(&self.path, blob.as_bytes())?;
        Ok(())
    }
}

/// [`CredentialStore`] backed by the macOS Keychain service
/// [`KEYCHAIN_SERVICE`]. Only compiled on macOS.
#[cfg(target_os = "macos")]
pub struct KeychainCredentialStore;

#[cfg(target_os = "macos")]
impl KeychainCredentialStore {
    /// Construct a Keychain-backed credential store.
    pub fn new() -> Self {
        Self
    }

    fn entry() -> Result<keyring::Entry, CredentialStoreError> {
        // The Keychain entry's "account" field is unused for this service; an
        // empty string keeps it consistent with Claude Code's own usage.
        Ok(keyring::Entry::new(KEYCHAIN_SERVICE, "")?)
    }
}

#[cfg(target_os = "macos")]
impl Default for KeychainCredentialStore {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(target_os = "macos")]
impl CredentialStore for KeychainCredentialStore {
    fn read(&self) -> Result<Option<String>, CredentialStoreError> {
        let entry = Self::entry()?;
        match entry.get_password() {
            Ok(blob) if blob.is_empty() => Ok(None),
            Ok(blob) => Ok(Some(blob)),
            Err(keyring::Error::NoEntry) => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    fn write(&self, blob: &str) -> Result<(), CredentialStoreError> {
        let entry = Self::entry()?;
        entry.set_password(blob)?;
        Ok(())
    }
}

/// In-memory [`CredentialStore`] for tests. Backed by a `Mutex<Option<String>>`
/// so it can be shared behind a shared reference like the real backends.
#[derive(Default)]
pub struct InMemoryCredentialStore {
    inner: Mutex<Option<String>>,
}

impl InMemoryCredentialStore {
    /// Construct an empty in-memory credential store.
    pub fn new() -> Self {
        Self::default()
    }
}

impl CredentialStore for InMemoryCredentialStore {
    fn read(&self) -> Result<Option<String>, CredentialStoreError> {
        let guard = self.inner.lock().map_err(|_| CredentialStoreError::Lock)?;
        // Treat an empty stored blob as absent, matching the file/keychain
        // backends.
        Ok(guard.clone().filter(|b| !b.is_empty()))
    }

    fn write(&self, blob: &str) -> Result<(), CredentialStoreError> {
        let mut guard = self.inner.lock().map_err(|_| CredentialStoreError::Lock)?;
        *guard = Some(blob.to_string());
        Ok(())
    }
}

/// Construct the platform-appropriate credential store as a boxed trait object.
///
/// - macOS → [`KeychainCredentialStore`] (service [`KEYCHAIN_SERVICE`]).
/// - Otherwise (Windows/Linux) → [`FileCredentialStore`] at the default
///   `~/.claude/.credentials.json` path.
pub fn default_credential_store() -> Result<Box<dyn CredentialStore>, CredentialStoreError> {
    #[cfg(target_os = "macos")]
    {
        Ok(Box::new(KeychainCredentialStore::new()))
    }
    #[cfg(not(target_os = "macos"))]
    {
        Ok(Box::new(FileCredentialStore::at_default_path()?))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // NOTE: tests only exercise InMemoryCredentialStore and FileCredentialStore
    // at a temp path. We deliberately never hit the real macOS Keychain or the
    // real ~/.claude/.credentials.json file.

    const BLOB: &str = r#"{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1}}"#;

    #[test]
    fn in_memory_snapshot_then_restore_round_trips() {
        let store = InMemoryCredentialStore::new();
        store.write(BLOB).unwrap();
        assert_eq!(store.read().unwrap(), Some(BLOB.to_string()));
    }

    #[test]
    fn in_memory_read_missing_returns_none() {
        let store = InMemoryCredentialStore::new();
        assert_eq!(store.read().unwrap(), None);
    }

    #[test]
    fn in_memory_empty_blob_reads_as_none() {
        let store = InMemoryCredentialStore::new();
        store.write("").unwrap();
        assert_eq!(store.read().unwrap(), None);
    }

    #[test]
    fn file_read_missing_returns_none() {
        let dir = tempfile::tempdir().unwrap();
        let store = FileCredentialStore::new(dir.path().join(".credentials.json"));
        assert_eq!(store.read().unwrap(), None);
    }

    #[test]
    fn file_write_then_read_round_trips() {
        let dir = tempfile::tempdir().unwrap();
        let store = FileCredentialStore::new(dir.path().join(".credentials.json"));

        store.write(BLOB).unwrap();
        assert_eq!(store.read().unwrap(), Some(BLOB.to_string()));
    }

    #[test]
    fn file_second_write_creates_backup() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(".credentials.json");
        let store = FileCredentialStore::new(&path);

        // First write: no prior file, so no backup is created.
        store.write(BLOB).unwrap();
        let backups_dir = dir.path().join("backups");
        let count_backups = |d: &Path| -> usize {
            if !d.exists() {
                return 0;
            }
            std::fs::read_dir(d)
                .unwrap()
                .filter_map(|e| e.ok())
                .filter(|e| e.file_name().to_string_lossy().ends_with(".bak"))
                .count()
        };
        assert_eq!(count_backups(&backups_dir), 0);

        // Second write backs up the existing file before overwriting.
        let updated = r#"{"claudeAiOauth":{"accessToken":"b"}}"#;
        store.write(updated).unwrap();

        assert_eq!(count_backups(&backups_dir), 1);
        assert_eq!(store.read().unwrap(), Some(updated.to_string()));
    }

    #[test]
    fn file_empty_contents_read_as_none() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(".credentials.json");
        std::fs::write(&path, "").unwrap();
        let store = FileCredentialStore::new(&path);
        assert_eq!(store.read().unwrap(), None);
    }
}
