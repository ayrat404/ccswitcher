//! Read and merge the `oauthAccount` section of Claude Code's user-level config
//! (`~/.claude.json` or `~/.claude/.claude.json`).
//!
//! Mirrors what `claude-swap` does: on a switch, only the `oauthAccount` object
//! is swapped between accounts — everything else in the config (userID,
//! projects, tips, settings, …) is the user's own and must be left intact.
//!
//! The snapshot is stored in the keyring by the switcher/import code; this
//! module is the pure file-level read/merge.

use std::path::{Path, PathBuf};

use serde_json::Value;
use thiserror::Error;

use super::atomic;
use super::claude_paths::user_config_candidates;

/// Name of the `oauthAccount` key inside the user config.
pub const OAUTH_ACCOUNT_KEY: &str = "oauthAccount";

/// Keyring key under which an account's `oauthAccount` snapshot is stored.
/// Separate from the credential-blob key (which is the bare account id) so the
/// two never collide.
pub fn oauth_account_key(account_id: &str) -> String {
    format!("{account_id}#oauthAccount")
}

/// Errors raised while reading or merging the user config.
#[derive(Debug, Error)]
pub enum UserConfigError {
    /// The config file exists but is not valid JSON.
    #[error("user config is not valid JSON: {0}")]
    InvalidJson(serde_json::Error),
    /// The `oauthAccount` value was not a JSON object.
    #[error("oauthAccount must be a JSON object")]
    NotAnObject,
    /// An I/O error occurred.
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
}

/// The `backups/` directory used when backing up the user config, located next
/// to the config file itself.
fn backups_dir(config_path: &Path) -> PathBuf {
    let parent = config_path.parent().unwrap_or_else(|| Path::new("."));
    parent.join("backups")
}

/// Find the user config file: the first candidate (in priority order) that
/// exists on disk. Returns `None` if neither candidate exists.
pub fn find_user_config() -> Option<PathBuf> {
    let candidates = user_config_candidates().ok()?;
    candidates.into_iter().find(|p| p.exists())
}

/// Read the `oauthAccount` object from the config at `path`.
///
/// Returns `Ok(None)` if the file is missing or has no `oauthAccount` key.
pub fn read_oauth_account(path: &Path) -> Result<Option<Value>, UserConfigError> {
    let content = match std::fs::read_to_string(path) {
        Ok(c) => c,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(UserConfigError::Io(e)),
    };
    let parsed: Value = serde_json::from_str(&content).map_err(UserConfigError::InvalidJson)?;
    Ok(parsed.get(OAUTH_ACCOUNT_KEY).cloned())
}

/// Merge an `oauthAccount` object into the config at `path`, replacing only
/// that one key and preserving every other field. The file is backed up and
/// written atomically. If the file does not exist, it is created with just the
/// `oauthAccount` object.
///
/// `oauth` must be a JSON object; otherwise this returns an error without
/// writing.
pub fn merge_oauth_account(path: &Path, oauth: &Value) -> Result<(), UserConfigError> {
    if !oauth.is_object() {
        return Err(UserConfigError::NotAnObject);
    }

    // Load existing config (or start empty if missing).
    let mut config: Value = match std::fs::read_to_string(path) {
        Ok(content) => serde_json::from_str(&content).map_err(UserConfigError::InvalidJson)?,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Value::Object(Default::default()),
        Err(e) => return Err(UserConfigError::Io(e)),
    };

    // Ensure the top level is an object, then set only oauthAccount.
    if !config.is_object() {
        config = Value::Object(Default::default());
    }
    if let Some(obj) = config.as_object_mut() {
        obj.insert(OAUTH_ACCOUNT_KEY.to_string(), oauth.clone());
    }

    let bytes = serde_json::to_vec_pretty(&config)
        .map_err(UserConfigError::InvalidJson)?;

    // Best-effort backup before the atomic write (no-op if the file is new).
    let _ = atomic::backup(path, &backups_dir(path));
    atomic::atomic_write(path, &bytes)?;

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use tempfile::TempDir;

    fn config_path(dir: &TempDir) -> PathBuf {
        dir.path().join(".claude.json")
    }

    #[test]
    fn read_oauth_account_returns_section_when_present() {
        let dir = TempDir::new().unwrap();
        let path = config_path(&dir);
        std::fs::write(
            &path,
            r#"{"userID":"u","oauthAccount":{"emailAddress":"a@x","accountUuid":"uuid-a"}}"#,
        )
        .unwrap();
        let oauth = read_oauth_account(&path).unwrap();
        assert_eq!(oauth, Some(json!({"emailAddress": "a@x", "accountUuid": "uuid-a"})));
    }

    #[test]
    fn read_oauth_account_none_when_missing_or_absent_key() {
        let dir = TempDir::new().unwrap();
        let path = config_path(&dir);
        // File without oauthAccount.
        std::fs::write(&path, r#"{"userID":"u"}"#).unwrap();
        assert_eq!(read_oauth_account(&path).unwrap(), None);

        // File absent.
        let other = dir.path().join("none.json");
        assert_eq!(read_oauth_account(&other).unwrap(), None);
    }

    #[test]
    fn read_oauth_account_errors_on_invalid_json() {
        let dir = TempDir::new().unwrap();
        let path = config_path(&dir);
        std::fs::write(&path, "not json").unwrap();
        assert!(matches!(
            read_oauth_account(&path),
            Err(UserConfigError::InvalidJson(_))
        ));
    }

    #[test]
    fn merge_replaces_only_oauth_account_preserving_other_fields() {
        let dir = TempDir::new().unwrap();
        let path = config_path(&dir);
        std::fs::write(
            &path,
            r#"{"userID":"keep","projects":{"x":1},"oauthAccount":{"emailAddress":"old@x"}}"#,
        )
        .unwrap();

        merge_oauth_account(
            &path,
            &json!({"emailAddress": "new@x", "accountUuid": "uuid-new"}),
        )
        .unwrap();

        let after: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        // oauthAccount swapped.
        assert_eq!(
            after["oauthAccount"]["emailAddress"],
            json!("new@x")
        );
        assert_eq!(after["oauthAccount"]["accountUuid"], json!("uuid-new"));
        // Other fields untouched.
        assert_eq!(after["userID"], json!("keep"));
        assert_eq!(after["projects"]["x"], json!(1));
    }

    #[test]
    fn merge_creates_file_when_absent() {
        let dir = TempDir::new().unwrap();
        let path = config_path(&dir);
        assert!(!path.exists());

        merge_oauth_account(&path, &json!({"emailAddress": "a@x"})).unwrap();

        let after: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(after["oauthAccount"]["emailAddress"], json!("a@x"));
    }

    #[test]
    fn merge_rejects_non_object_oauth() {
        let dir = TempDir::new().unwrap();
        let path = config_path(&dir);
        // Non-object must be rejected, file untouched.
        let res = merge_oauth_account(&path, &json!("not-an-object"));
        assert!(res.is_err());
        assert!(!path.exists());
    }

    #[test]
    fn merge_creates_backup() {
        let dir = TempDir::new().unwrap();
        let path = config_path(&dir);
        std::fs::write(&path, r#"{"oauthAccount":{"emailAddress":"old@x"}}"#).unwrap();

        merge_oauth_account(&path, &json!({"emailAddress": "new@x"})).unwrap();

        let backups = dir.path().join("backups");
        assert!(backups.is_dir(), "backups dir should exist");
        // At least one .bak file present.
        let count = std::fs::read_dir(&backups)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.path().extension().and_then(|x| x.to_str()) == Some("bak"))
            .count();
        assert!(count >= 1, "expected at least one backup, got {count}");
    }
}
