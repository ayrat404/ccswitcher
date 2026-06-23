//! Persistence for the non-secret [`AppConfig`] (`config.json`).
//!
//! Loading a missing config yields [`AppConfig::default`] so a first run starts
//! clean. Saving is atomic (temp + rename) and preceded by a timestamped backup
//! of any prior `config.json`, mirroring how `settings.json` is written.

use std::io;
use std::path::{Path, PathBuf};

use thiserror::Error;

use super::atomic::{atomic_write, backup};
use super::model::AppConfig;

/// File name of the persisted config inside the app config directory.
pub const CONFIG_FILE_NAME: &str = "config.json";

/// Sub-directory (inside the app config dir) holding timestamped backups.
pub const BACKUPS_DIR_NAME: &str = "backups";

/// Errors raised while loading or saving the config.
#[derive(Debug, Error)]
pub enum ConfigStoreError {
    /// Underlying filesystem I/O failed.
    #[error("config I/O error: {0}")]
    Io(#[from] io::Error),
    /// The existing `config.json` could not be parsed as valid JSON.
    #[error("config.json is not valid JSON: {0}")]
    Parse(#[from] serde_json::Error),
    /// The app config directory could not be resolved.
    #[error("could not resolve the app config directory")]
    NoConfigDir,
}

/// Load/save the non-secret [`AppConfig`] under a given directory.
pub struct ConfigStore;

impl ConfigStore {
    /// Resolve the ccswitcher app config directory via the `dirs` crate.
    ///
    /// - Windows: `%APPDATA%/ccswitcher`
    /// - macOS: `~/Library/Application Support/ccswitcher`
    /// - Linux: `~/.config/ccswitcher`
    pub fn app_config_dir() -> Result<PathBuf, ConfigStoreError> {
        let base = dirs::config_dir().ok_or(ConfigStoreError::NoConfigDir)?;
        Ok(base.join("ccswitcher"))
    }

    /// Full path to `config.json` inside `dir`.
    pub fn config_path(dir: &Path) -> PathBuf {
        dir.join(CONFIG_FILE_NAME)
    }

    /// Load the config from `dir/config.json`.
    ///
    /// Returns [`AppConfig::default`] if the file does not exist. Returns a
    /// [`ConfigStoreError::Parse`] (without modifying anything) if the file
    /// exists but is not valid JSON.
    pub fn load(dir: &Path) -> Result<AppConfig, ConfigStoreError> {
        let path = Self::config_path(dir);
        if !path.exists() {
            return Ok(AppConfig::default());
        }
        let bytes = std::fs::read(&path)?;
        let cfg: AppConfig = serde_json::from_slice(&bytes)?;
        Ok(cfg)
    }

    /// Atomically save `config` to `dir/config.json`, taking a timestamped
    /// backup of any prior file first.
    pub fn save(dir: &Path, config: &AppConfig) -> Result<(), ConfigStoreError> {
        let path = Self::config_path(dir);
        let backups_dir = dir.join(BACKUPS_DIR_NAME);

        // Backup is a no-op if no prior config.json exists.
        backup(&path, &backups_dir)?;

        let bytes = serde_json::to_vec_pretty(config)?;
        atomic_write(&path, &bytes)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::model::{Account, AccountType, AuthKind, ProxySettings, SCHEMA_VERSION};
    use std::collections::BTreeMap;

    fn sample_config() -> AppConfig {
        let mut extra = BTreeMap::new();
        extra.insert("FOO".to_string(), "bar".to_string());
        AppConfig {
            schema_version: SCHEMA_VERSION,
            active_account_id: Some("tok-id".to_string()),
            proxy: ProxySettings {
                enabled: true,
                url: "http://localhost:9000".to_string(),
                no_proxy: "localhost".to_string(),
            },
            managed_keys: vec!["ANTHROPIC_BASE_URL".to_string()],
            accounts: vec![Account {
                id: "tok-id".to_string(),
                name: "Work".to_string(),
                account_type: AccountType::Token,
                base_url: Some("https://proxy.example.com".to_string()),
                auth_kind: Some(AuthKind::AuthToken),
                identity: None,
                extra_env: extra,
            }],
        }
    }

    #[test]
    fn load_missing_returns_default() {
        let dir = tempfile::tempdir().unwrap();
        let cfg = ConfigStore::load(dir.path()).unwrap();
        assert_eq!(cfg, AppConfig::default());
        // Loading must not create the file.
        assert!(!ConfigStore::config_path(dir.path()).exists());
    }

    #[test]
    fn save_then_load_round_trip() {
        let dir = tempfile::tempdir().unwrap();
        let cfg = sample_config();

        ConfigStore::save(dir.path(), &cfg).unwrap();
        assert!(ConfigStore::config_path(dir.path()).exists());

        let loaded = ConfigStore::load(dir.path()).unwrap();
        assert_eq!(loaded, cfg);
    }

    #[test]
    fn save_takes_backup_of_prior_config() {
        let dir = tempfile::tempdir().unwrap();
        let backups = dir.path().join(BACKUPS_DIR_NAME);

        // First save: no prior file, so no backup.
        ConfigStore::save(dir.path(), &AppConfig::default()).unwrap();
        assert!(!backups.exists() || backup_count(&backups) == 0);

        // Second save: prior config.json exists, so a backup is taken.
        ConfigStore::save(dir.path(), &sample_config()).unwrap();
        assert!(backup_count(&backups) >= 1);
    }

    #[test]
    fn load_invalid_json_returns_parse_error() {
        let dir = tempfile::tempdir().unwrap();
        let path = ConfigStore::config_path(dir.path());
        std::fs::write(&path, b"{ not valid json").unwrap();

        let err = ConfigStore::load(dir.path()).unwrap_err();
        assert!(matches!(err, ConfigStoreError::Parse(_)));
        // The invalid file is left untouched.
        assert_eq!(std::fs::read(&path).unwrap(), b"{ not valid json");
    }

    fn backup_count(backups: &Path) -> usize {
        std::fs::read_dir(backups)
            .map(|rd| {
                rd.filter_map(|e| e.ok())
                    .filter(|e| {
                        e.file_name()
                            .to_string_lossy()
                            .ends_with(".bak")
                    })
                    .count()
            })
            .unwrap_or(0)
    }
}
