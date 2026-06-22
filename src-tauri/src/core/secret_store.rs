//! Secret storage behind a trait, with an OS-keyring backend and an in-memory
//! mock for tests.
//!
//! Secrets (token strings, OAuth credential snapshots) never live in
//! `config.json`; they are keyed by account `id` in the OS-native keyring via
//! the [`keyring`] crate. The [`SecretStore`] trait keeps the core logic
//! testable with [`InMemorySecretStore`] so unit tests never touch the real OS
//! credential store.

use std::collections::HashMap;
use std::sync::Mutex;

use thiserror::Error;

/// Keyring service name under which all ccswitcher secrets are stored.
pub const SERVICE_NAME: &str = "ccswitcher";

/// Errors raised while accessing the secret store.
#[derive(Debug, Error)]
pub enum SecretStoreError {
    /// The underlying keyring backend failed.
    #[error("keyring error: {0}")]
    Keyring(#[from] keyring::Error),
    /// An in-memory mock's lock was poisoned (test-only path).
    #[error("secret store lock poisoned")]
    Lock,
}

/// Stores per-account secrets keyed by account `id`.
pub trait SecretStore {
    /// Fetch the secret for `account_id`. Returns `Ok(None)` when no entry
    /// exists for that account.
    fn get(&self, account_id: &str) -> Result<Option<String>, SecretStoreError>;

    /// Store (or overwrite) the secret for `account_id`.
    fn set(&self, account_id: &str, secret: &str) -> Result<(), SecretStoreError>;

    /// Delete the secret for `account_id`. Deleting a missing entry is a no-op
    /// and returns `Ok(())`.
    fn delete(&self, account_id: &str) -> Result<(), SecretStoreError>;
}

/// [`SecretStore`] backed by the OS-native keyring (`keyring` crate), using
/// service [`SERVICE_NAME`] and the account `id` as the keyring entry user.
pub struct KeyringSecretStore;

impl KeyringSecretStore {
    /// Construct a keyring-backed secret store.
    pub fn new() -> Self {
        Self
    }

    fn entry(account_id: &str) -> Result<keyring::Entry, SecretStoreError> {
        Ok(keyring::Entry::new(SERVICE_NAME, account_id)?)
    }
}

impl Default for KeyringSecretStore {
    fn default() -> Self {
        Self::new()
    }
}

impl SecretStore for KeyringSecretStore {
    fn get(&self, account_id: &str) -> Result<Option<String>, SecretStoreError> {
        let entry = Self::entry(account_id)?;
        match entry.get_password() {
            Ok(secret) => Ok(Some(secret)),
            // A missing entry is a normal "no secret yet" case, not an error.
            Err(keyring::Error::NoEntry) => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    fn set(&self, account_id: &str, secret: &str) -> Result<(), SecretStoreError> {
        let entry = Self::entry(account_id)?;
        entry.set_password(secret)?;
        Ok(())
    }

    fn delete(&self, account_id: &str) -> Result<(), SecretStoreError> {
        let entry = Self::entry(account_id)?;
        match entry.delete_credential() {
            Ok(()) => Ok(()),
            // Deleting a missing entry is idempotent.
            Err(keyring::Error::NoEntry) => Ok(()),
            Err(e) => Err(e.into()),
        }
    }
}

/// In-memory [`SecretStore`] for tests. Backed by a `Mutex<HashMap>` so it can
/// be shared behind a shared reference like the real keyring store.
#[derive(Default)]
pub struct InMemorySecretStore {
    inner: Mutex<HashMap<String, String>>,
}

impl InMemorySecretStore {
    /// Construct an empty in-memory secret store.
    pub fn new() -> Self {
        Self::default()
    }
}

impl SecretStore for InMemorySecretStore {
    fn get(&self, account_id: &str) -> Result<Option<String>, SecretStoreError> {
        let map = self.inner.lock().map_err(|_| SecretStoreError::Lock)?;
        Ok(map.get(account_id).cloned())
    }

    fn set(&self, account_id: &str, secret: &str) -> Result<(), SecretStoreError> {
        let mut map = self.inner.lock().map_err(|_| SecretStoreError::Lock)?;
        map.insert(account_id.to_string(), secret.to_string());
        Ok(())
    }

    fn delete(&self, account_id: &str) -> Result<(), SecretStoreError> {
        let mut map = self.inner.lock().map_err(|_| SecretStoreError::Lock)?;
        map.remove(account_id);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // NOTE: tests only exercise InMemorySecretStore. We deliberately never hit
    // the real OS keyring here — that would touch the user's credential store
    // and could fail in CI.

    #[test]
    fn set_then_get_returns_value() {
        let store = InMemorySecretStore::new();
        store.set("acc-1", "s3cr3t").unwrap();
        assert_eq!(store.get("acc-1").unwrap(), Some("s3cr3t".to_string()));
    }

    #[test]
    fn get_missing_returns_none() {
        let store = InMemorySecretStore::new();
        assert_eq!(store.get("nope").unwrap(), None);
    }

    #[test]
    fn set_overwrites_existing_value() {
        let store = InMemorySecretStore::new();
        store.set("acc-1", "first").unwrap();
        store.set("acc-1", "second").unwrap();
        assert_eq!(store.get("acc-1").unwrap(), Some("second".to_string()));
    }

    #[test]
    fn delete_removes_entry() {
        let store = InMemorySecretStore::new();
        store.set("acc-1", "s3cr3t").unwrap();
        store.delete("acc-1").unwrap();
        assert_eq!(store.get("acc-1").unwrap(), None);
    }

    #[test]
    fn delete_missing_is_ok() {
        let store = InMemorySecretStore::new();
        assert!(store.delete("nope").is_ok());
    }
}
