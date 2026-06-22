//! The account switching engine — the heart of ccswitcher.
//!
//! [`apply_account`] makes a chosen account the active one by editing Claude
//! Code's `settings.json` `env` block and (for OAuth accounts) its credential
//! store, in the exact order the plan's "Switching flow" prescribes:
//!
//! 1. **Capture-on-switch-out**: if the *currently* active account is
//!    `anthropic_oauth`, read the live credential store and update that account's
//!    snapshot in the keyring (so refreshed tokens are preserved). Missing live
//!    blob → skip silently. *This keyring write is intentional and is never
//!    rolled back, even if a later step fails.*
//! 2. **Load settings** (`settings.json`). Invalid JSON aborts the switch
//!    **before any mutation** so a corrupt file is never overwritten.
//! 3. **Build target env** via [`build_env`]. A token account missing its secret
//!    aborts here, **before any settings write**.
//! 4. **Merge env** ([`merge_env`]) using the stored `managed_keys` as the
//!    old-keys list (the engine strips the union of [`MANAGED_KEYS`] and those).
//! 5. **Backup + atomic write** `settings.json` with the merged env.
//! 6. **Restore credential snapshot** for an `anthropic_oauth` target — done
//!    **after** the settings write.
//! 7. **Persist config**: new `managed_keys` + `active_account_id`.
//!
//! ## Atomicity (honest statement)
//! The operation spans three independent stores (keyring, credential
//! file/Keychain, `settings.json`/`config.json`) and is **not** transactional
//! across them. It is, however, **idempotent**: re-running the same switch heals
//! any partial cross-store state left by an aborted run (e.g. a credential
//! restore that failed after the settings write). Callers serialize switches
//! behind the app-state mutex.

use thiserror::Error;

use super::config_store::{ConfigStore, ConfigStoreError};
use super::credential_store::{CredentialStore, CredentialStoreError};
use super::env_builder::{build_env, EnvBuilderError};
use super::model::{AccountType, AppConfig};
use super::secret_store::{SecretStore, SecretStoreError};
use super::settings_env::{load_settings, merge_env, SettingsEnvError};

use std::path::{Path, PathBuf};

/// Errors raised while applying (switching to) an account.
#[derive(Debug, Error)]
pub enum SwitchError {
    /// The requested target account id does not exist in the config.
    #[error("unknown account id: {0}")]
    UnknownAccount(String),
    /// Loading `settings.json` failed (e.g. invalid JSON). No mutation occurred.
    #[error(transparent)]
    Settings(#[from] SettingsEnvError),
    /// Building the target env failed (e.g. a token account missing its secret).
    /// No settings write occurred.
    #[error(transparent)]
    EnvBuilder(#[from] EnvBuilderError),
    /// Reading or writing the OS keyring (secret store) failed.
    #[error(transparent)]
    Secret(#[from] SecretStoreError),
    /// Reading or writing the OAuth credential store failed.
    #[error(transparent)]
    Credential(#[from] CredentialStoreError),
    /// Persisting the updated `config.json` failed.
    #[error(transparent)]
    Config(#[from] ConfigStoreError),
    /// A destructive `settings.json` write failed.
    #[error("settings.json write error: {0}")]
    SettingsWrite(std::io::Error),
}

/// References to the I/O dependencies an [`apply_account`] switch needs.
///
/// Bundled (rather than passed individually) so the engine stays easy to call
/// and to mock: tests supply an in-memory secret store, an in-memory credential
/// store, and temp paths. The two store fields are trait objects behind shared
/// references so the same value can be reused across switches.
pub struct SwitchDeps<'a> {
    /// Path to Claude Code's `settings.json` (the file whose `env` is edited).
    pub settings_path: &'a Path,
    /// Directory holding ccswitcher's own `config.json` (persisted last).
    pub config_dir: &'a Path,
    /// OS keyring for per-account secrets (token strings, OAuth snapshots).
    pub secret_store: &'a dyn SecretStore,
    /// Claude Code's OAuth credential store (snapshot/restore).
    pub credential_store: &'a dyn CredentialStore,
}

/// The `backups/` directory used when backing up `settings.json`, located next
/// to the settings file itself.
fn settings_backups_dir(settings_path: &Path) -> PathBuf {
    let parent = settings_path.parent().unwrap_or_else(|| Path::new("."));
    parent.join("backups")
}

/// Make `account_id` the active account, applying its env to `settings.json` and
/// (for OAuth) restoring its credential snapshot.
///
/// On success `config` is mutated in place (its `managed_keys` and
/// `active_account_id` updated) and persisted to disk. See the module docs for
/// the precise ordering and atomicity guarantees.
pub fn apply_account(
    config: &mut AppConfig,
    account_id: &str,
    deps: &SwitchDeps<'_>,
) -> Result<(), SwitchError> {
    // Validate the target up front: an unknown id is a typed error and must not
    // touch any store.
    let target = config
        .accounts
        .iter()
        .find(|a| a.id == account_id)
        .ok_or_else(|| SwitchError::UnknownAccount(account_id.to_string()))?
        .clone();

    // --- Step 1: capture-on-switch-out -------------------------------------
    // If the currently-active account is OAuth, re-snapshot its live credential
    // blob into the keyring so refreshed tokens are preserved. This keyring
    // write is intentional and is never rolled back, even if a later step fails.
    if let Some(active_id) = config.active_account_id.clone() {
        // Only capture for a *different*, still-existing, OAuth active account.
        let active_is_oauth = config
            .accounts
            .iter()
            .find(|a| a.id == active_id)
            .map(|a| a.account_type == AccountType::AnthropicOauth)
            .unwrap_or(false);
        if active_is_oauth {
            if let Some(live) = deps.credential_store.read()? {
                deps.secret_store.set(&active_id, &live)?;
            }
            // Missing live blob → skip silently (nothing to capture).
        }
    }

    // --- Step 2: load settings (invalid JSON aborts before any mutation) ----
    let settings = load_settings(deps.settings_path)?;

    // --- Step 3: build target env (missing-secret aborts before any write) --
    // For token accounts, fetch the secret from the keyring; OAuth accounts do
    // not need a secret to build env (it is restored to the credential store).
    let secret = match target.account_type {
        AccountType::Token => deps.secret_store.get(account_id)?,
        AccountType::AnthropicOauth => None,
    };
    let new_env = build_env(&target, secret.as_deref(), &config.proxy)?;

    // --- Step 4: merge env (strip union of MANAGED_KEYS and stored keys) ----
    let (merged, new_managed_keys) = merge_env(settings, &config.managed_keys, &new_env);

    // --- Step 5: timestamped backup + atomic write settings.json -----------
    let bytes = serde_json::to_vec_pretty(&merged).map_err(|e| {
        SwitchError::SettingsWrite(std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    })?;
    super::atomic::backup(deps.settings_path, &settings_backups_dir(deps.settings_path))
        .map_err(SwitchError::SettingsWrite)?;
    super::atomic::atomic_write(deps.settings_path, &bytes).map_err(SwitchError::SettingsWrite)?;

    // --- Step 6: restore OAuth credential snapshot (after settings write) ---
    if target.account_type == AccountType::AnthropicOauth {
        if let Some(snapshot) = deps.secret_store.get(account_id)? {
            deps.credential_store.write(&snapshot)?;
        }
        // No snapshot stored yet → nothing to restore (e.g. a freshly-imported
        // account whose blob has not been captured). The switch still succeeds.
    }

    // --- Step 7: persist config (managed_keys + active_account_id) ----------
    config.managed_keys = new_managed_keys;
    config.active_account_id = Some(account_id.to_string());
    ConfigStore::save(deps.config_dir, config)?;

    Ok(())
}

/// Clear `active_account_id` when it refers to an account that no longer exists.
///
/// Returns `true` when the active id was cleared. Callers (e.g. after deleting
/// an account, or on startup) use this to keep `config.active_account_id`
/// consistent — a dangling id would otherwise mis-drive capture-on-switch-out.
pub fn clear_active_if_missing(config: &mut AppConfig) -> bool {
    if let Some(active_id) = &config.active_account_id {
        let exists = config.accounts.iter().any(|a| &a.id == active_id);
        if !exists {
            config.active_account_id = None;
            return true;
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::credential_store::InMemoryCredentialStore;
    use crate::core::model::{Account, AuthKind, ProxySettings};
    use crate::core::secret_store::InMemorySecretStore;
    use serde_json::Value;
    use std::collections::BTreeMap;

    /// A failing credential store: `read` works, but `write` always errors.
    /// Used to simulate a cross-store post-abort (settings written, restore
    /// fails). `read` returns whatever was last (force-)written.
    struct FailingWriteCredentialStore {
        inner: std::sync::Mutex<Option<String>>,
    }

    impl FailingWriteCredentialStore {
        fn new() -> Self {
            Self {
                inner: std::sync::Mutex::new(None),
            }
        }
        /// Seed the readable blob without going through the failing `write`.
        fn force_set(&self, blob: &str) {
            *self.inner.lock().unwrap() = Some(blob.to_string());
        }
    }

    impl CredentialStore for FailingWriteCredentialStore {
        fn read(&self) -> Result<Option<String>, CredentialStoreError> {
            Ok(self.inner.lock().unwrap().clone().filter(|b| !b.is_empty()))
        }
        fn write(&self, _blob: &str) -> Result<(), CredentialStoreError> {
            Err(CredentialStoreError::Lock)
        }
    }

    struct Env {
        _settings_dir: tempfile::TempDir,
        _config_dir: tempfile::TempDir,
        settings_path: PathBuf,
        config_dir: PathBuf,
        secrets: InMemorySecretStore,
        creds: InMemoryCredentialStore,
    }

    impl Env {
        fn new() -> Self {
            let settings_dir = tempfile::tempdir().unwrap();
            let config_dir = tempfile::tempdir().unwrap();
            let settings_path = settings_dir.path().join("settings.json");
            let config_dir_path = config_dir.path().to_path_buf();
            Env {
                _settings_dir: settings_dir,
                _config_dir: config_dir,
                settings_path,
                config_dir: config_dir_path,
                secrets: InMemorySecretStore::new(),
                creds: InMemoryCredentialStore::new(),
            }
        }

        fn deps(&self) -> SwitchDeps<'_> {
            SwitchDeps {
                settings_path: &self.settings_path,
                config_dir: &self.config_dir,
                secret_store: &self.secrets,
                credential_store: &self.creds,
            }
        }

        fn settings_env(&self) -> Value {
            let v = load_settings(&self.settings_path).unwrap();
            v.get("env").cloned().unwrap_or(Value::Null)
        }
    }

    fn token_account(id: &str, base_url: Option<&str>) -> Account {
        Account {
            id: id.to_string(),
            name: format!("token-{id}"),
            account_type: AccountType::Token,
            base_url: base_url.map(str::to_string),
            auth_kind: Some(AuthKind::AuthToken),
            identity: None,
            extra_env: BTreeMap::new(),
        }
    }

    fn oauth_account(id: &str, base_url: Option<&str>) -> Account {
        Account {
            id: id.to_string(),
            name: format!("oauth-{id}"),
            account_type: AccountType::AnthropicOauth,
            base_url: base_url.map(str::to_string),
            auth_kind: None,
            identity: Some(format!("{id}@example.com")),
            extra_env: BTreeMap::new(),
        }
    }

    fn config_with(accounts: Vec<Account>) -> AppConfig {
        AppConfig {
            accounts,
            ..AppConfig::default()
        }
    }

    #[test]
    fn unknown_target_returns_typed_error() {
        let env = Env::new();
        let mut cfg = config_with(vec![token_account("a", None)]);
        let err = apply_account(&mut cfg, "does-not-exist", &env.deps()).unwrap_err();
        assert!(matches!(err, SwitchError::UnknownAccount(id) if id == "does-not-exist"));
        // Nothing should have been written.
        assert!(!env.settings_path.exists());
    }

    #[test]
    fn switch_to_token_writes_env_override() {
        let env = Env::new();
        env.secrets.set("tok", "sk-secret").unwrap();
        let mut cfg = config_with(vec![token_account("tok", Some("https://proxy.example.com"))]);

        apply_account(&mut cfg, "tok", &env.deps()).unwrap();

        let e = env.settings_env();
        assert_eq!(e["ANTHROPIC_AUTH_TOKEN"], Value::String("sk-secret".into()));
        assert_eq!(
            e["ANTHROPIC_BASE_URL"],
            Value::String("https://proxy.example.com".into())
        );
        assert_eq!(cfg.active_account_id.as_deref(), Some("tok"));
        // Config persisted with the keys we just wrote.
        let reloaded = ConfigStore::load(&env.config_dir).unwrap();
        assert_eq!(reloaded.active_account_id.as_deref(), Some("tok"));
        assert!(reloaded
            .managed_keys
            .contains(&"ANTHROPIC_AUTH_TOKEN".to_string()));
    }

    #[test]
    fn switch_to_oauth_restores_snapshot_and_writes_no_token_key() {
        let env = Env::new();
        let blob = r#"{"claudeAiOauth":{"accessToken":"a"}}"#;
        env.secrets.set("oa", blob).unwrap();
        let mut cfg = config_with(vec![oauth_account("oa", Some("https://api.anthropic.com"))]);

        apply_account(&mut cfg, "oa", &env.deps()).unwrap();

        // No token key in env, base_url preserved.
        let e = env.settings_env();
        assert!(e.get("ANTHROPIC_AUTH_TOKEN").is_none());
        assert!(e.get("ANTHROPIC_API_KEY").is_none());
        assert_eq!(
            e["ANTHROPIC_BASE_URL"],
            Value::String("https://api.anthropic.com".into())
        );
        // Snapshot restored to the credential store.
        assert_eq!(env.creds.read().unwrap(), Some(blob.to_string()));
    }

    #[test]
    fn token_oauth_token_leaves_no_stale_keys() {
        let env = Env::new();
        env.secrets.set("t1", "sk-1").unwrap();
        env.secrets
            .set("oa", r#"{"claudeAiOauth":{"accessToken":"a"}}"#)
            .unwrap();
        env.secrets.set("t2", "sk-2").unwrap();
        let mut cfg = config_with(vec![
            token_account("t1", None),
            oauth_account("oa", None),
            token_account("t2", None),
        ]);

        apply_account(&mut cfg, "t1", &env.deps()).unwrap();
        apply_account(&mut cfg, "oa", &env.deps()).unwrap();
        // After switching to OAuth (no token key), the prior token must be gone.
        assert!(env.settings_env().get("ANTHROPIC_AUTH_TOKEN").is_none());

        apply_account(&mut cfg, "t2", &env.deps()).unwrap();
        let e = env.settings_env();
        // Only t2's token is present; no stale keys linger.
        assert_eq!(e["ANTHROPIC_AUTH_TOKEN"], Value::String("sk-2".into()));
        assert!(e.get("ANTHROPIC_API_KEY").is_none());
    }

    #[test]
    fn a_oauth_b_a_preserves_latest_blob() {
        let env = Env::new();
        let import_blob = r#"{"claudeAiOauth":{"accessToken":"import","expiresAt":1}}"#;
        let refreshed_blob = r#"{"claudeAiOauth":{"accessToken":"refreshed","expiresAt":2}}"#;

        env.secrets.set("a", import_blob).unwrap();
        env.secrets.set("b", "sk-b").unwrap();
        let mut cfg = config_with(vec![oauth_account("a", None), token_account("b", None)]);

        // Switch to A: restores the import-time snapshot into the live store.
        apply_account(&mut cfg, "a", &env.deps()).unwrap();
        assert_eq!(env.creds.read().unwrap(), Some(import_blob.to_string()));

        // Simulate Claude Code refreshing the live credential store in place.
        env.creds.write(refreshed_blob).unwrap();

        // Switch to B: capture-on-switch-out must re-snapshot A's *refreshed*
        // blob into the keyring before leaving A.
        apply_account(&mut cfg, "b", &env.deps()).unwrap();
        assert_eq!(
            env.secrets.get("a").unwrap(),
            Some(refreshed_blob.to_string())
        );

        // Switch back to A: restore must use the refreshed blob, not import-time.
        apply_account(&mut cfg, "a", &env.deps()).unwrap();
        assert_eq!(env.creds.read().unwrap(), Some(refreshed_blob.to_string()));
    }

    #[test]
    fn oauth_account_with_base_url_keeps_it_after_switch() {
        let env = Env::new();
        env.secrets
            .set("oa", r#"{"claudeAiOauth":{"accessToken":"a"}}"#)
            .unwrap();
        let mut cfg = config_with(vec![oauth_account("oa", Some("https://api.anthropic.com"))]);

        apply_account(&mut cfg, "oa", &env.deps()).unwrap();
        // A second switch (to itself) must still keep the base_url, not strip it.
        apply_account(&mut cfg, "oa", &env.deps()).unwrap();

        assert_eq!(
            env.settings_env()["ANTHROPIC_BASE_URL"],
            Value::String("https://api.anthropic.com".into())
        );
    }

    #[test]
    fn missing_secret_token_aborts_before_any_settings_write() {
        let env = Env::new();
        // No secret stored for the token account.
        let mut cfg = config_with(vec![token_account("tok", None)]);

        let err = apply_account(&mut cfg, "tok", &env.deps()).unwrap_err();
        assert!(matches!(err, SwitchError::EnvBuilder(EnvBuilderError::MissingSecret)));
        // No settings file was created.
        assert!(!env.settings_path.exists());
        // Active id unchanged.
        assert_eq!(cfg.active_account_id, None);
    }

    #[test]
    fn cross_store_post_abort_is_recoverable_idempotently() {
        // Settings write succeeds, but the OAuth credential restore fails. The
        // settings.json must be valid + backed up, the keyring capture retained,
        // and a re-run with a working store must reach a consistent state.
        let settings_dir = tempfile::tempdir().unwrap();
        let config_dir = tempfile::tempdir().unwrap();
        let settings_path = settings_dir.path().join("settings.json");
        // Seed an existing settings.json so the write produces a backup.
        std::fs::write(&settings_path, br#"{"env":{"MY_OWN":"keep"}}"#).unwrap();

        let secrets = InMemorySecretStore::new();
        let blob = r#"{"claudeAiOauth":{"accessToken":"a"}}"#;
        secrets.set("oa", blob).unwrap();
        let failing = FailingWriteCredentialStore::new();

        let mut cfg = config_with(vec![oauth_account("oa", None)]);

        let deps = SwitchDeps {
            settings_path: &settings_path,
            config_dir: config_dir.path(),
            secret_store: &secrets,
            credential_store: &failing,
        };
        let err = apply_account(&mut cfg, "oa", &deps).unwrap_err();
        assert!(matches!(err, SwitchError::Credential(_)));

        // settings.json was written (valid JSON) and the user key preserved.
        let written = load_settings(&settings_path).unwrap();
        assert_eq!(written["env"]["MY_OWN"], Value::String("keep".into()));
        // A timestamped backup of the prior settings was taken.
        let backups_dir = settings_backups_dir(&settings_path);
        let backup_count = std::fs::read_dir(&backups_dir)
            .map(|rd| {
                rd.filter_map(|e| e.ok())
                    .filter(|e| e.file_name().to_string_lossy().ends_with(".bak"))
                    .count()
            })
            .unwrap_or(0);
        assert_eq!(backup_count, 1);
        // The keyring capture for the OAuth account is retained (still the blob).
        assert_eq!(secrets.get("oa").unwrap(), Some(blob.to_string()));

        // Re-run with a working credential store: switch heals to a consistent
        // state (restore succeeds, config persists).
        let working = InMemoryCredentialStore::new();
        let deps2 = SwitchDeps {
            settings_path: &settings_path,
            config_dir: config_dir.path(),
            secret_store: &secrets,
            credential_store: &working,
        };
        apply_account(&mut cfg, "oa", &deps2).unwrap();
        assert_eq!(working.read().unwrap(), Some(blob.to_string()));
        assert_eq!(cfg.active_account_id.as_deref(), Some("oa"));
        let reloaded = ConfigStore::load(config_dir.path()).unwrap();
        assert_eq!(reloaded.active_account_id.as_deref(), Some("oa"));
    }

    #[test]
    fn clear_active_if_missing_clears_dangling_id() {
        let mut cfg = config_with(vec![token_account("a", None)]);
        cfg.active_account_id = Some("ghost".to_string());
        assert!(clear_active_if_missing(&mut cfg));
        assert_eq!(cfg.active_account_id, None);
    }

    #[test]
    fn clear_active_if_missing_keeps_existing_id() {
        let mut cfg = config_with(vec![token_account("a", None)]);
        cfg.active_account_id = Some("a".to_string());
        assert!(!clear_active_if_missing(&mut cfg));
        assert_eq!(cfg.active_account_id.as_deref(), Some("a"));
    }

    #[test]
    fn clear_active_if_missing_noop_when_none() {
        let mut cfg = config_with(vec![token_account("a", None)]);
        assert!(!clear_active_if_missing(&mut cfg));
        assert_eq!(cfg.active_account_id, None);
    }
}
