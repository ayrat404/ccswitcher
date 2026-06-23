//! The global HTTP proxy toggle.
//!
//! Flipping the proxy on or off is a **lighter** operation than a full account
//! switch: it must re-write *only* the active account's env in `settings.json`
//! so the proxy keys (`HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`) appear or disappear,
//! while leaving the rest of the switching machinery alone. In particular it:
//!
//! - does **not** touch the OAuth credential store, and
//! - does **not** perform capture-on-switch-out.
//!
//! This is enforced **by construction**: [`set_proxy_enabled`] takes a
//! [`ProxyDeps`] that has no credential store at all, so it cannot read or write
//! one even by accident.
//!
//! ## Flow
//! 1. Update `config.proxy.enabled`.
//! 2. If there is no active account → persist `config` only (no settings write).
//! 3. Otherwise rebuild the active account's env via [`build_env`] (using its
//!    secret from the keyring and the *updated* proxy), then [`merge_env`] it
//!    into `settings.json` with a timestamped backup + atomic write, updating
//!    `config.managed_keys`.
//! 4. Persist `config`.

use thiserror::Error;

use super::config_store::{ConfigStore, ConfigStoreError};
use super::env_builder::{build_env, EnvBuilderError};
use super::model::{AccountType, AppConfig};
use super::secret_store::{SecretStore, SecretStoreError};
use super::settings_env::{load_settings, merge_env, SettingsEnvError};

use std::path::{Path, PathBuf};

/// Errors raised while toggling the global proxy.
#[derive(Debug, Error)]
pub enum ProxyError {
    /// Loading `settings.json` failed (e.g. invalid JSON). No mutation occurred.
    #[error(transparent)]
    Settings(#[from] SettingsEnvError),
    /// Building the active account's env failed (e.g. a token account missing its
    /// secret). No settings write occurred.
    #[error(transparent)]
    EnvBuilder(#[from] EnvBuilderError),
    /// Reading the OS keyring (secret store) failed.
    #[error(transparent)]
    Secret(#[from] SecretStoreError),
    /// Persisting the updated `config.json` failed.
    #[error(transparent)]
    Config(#[from] ConfigStoreError),
    /// A destructive `settings.json` write failed.
    #[error("settings.json write error: {0}")]
    SettingsWrite(std::io::Error),
}

/// I/O dependencies for a proxy toggle.
///
/// Deliberately *smaller* than [`super::switcher::SwitchDeps`]: there is no
/// credential store, because a proxy toggle never touches OAuth credentials.
/// This makes the "no credential-store I/O" guarantee structural rather than a
/// matter of discipline.
pub struct ProxyDeps<'a> {
    /// Path to Claude Code's `settings.json` (the file whose `env` is edited).
    pub settings_path: &'a Path,
    /// Directory holding ccswitcher's own `config.json`.
    pub config_dir: &'a Path,
    /// OS keyring for per-account secrets (needed to rebuild a token account's env).
    pub secret_store: &'a dyn SecretStore,
}

/// The `backups/` directory used when backing up `settings.json`, located next
/// to the settings file itself. Mirrors the switcher's layout.
fn settings_backups_dir(settings_path: &Path) -> PathBuf {
    let parent = settings_path.parent().unwrap_or_else(|| Path::new("."));
    parent.join("backups")
}

/// Set whether the global HTTP proxy is enabled and re-apply it to the active
/// account's env.
///
/// Updates `config.proxy.enabled`, then (only if an account is active) rewrites
/// that account's env in `settings.json` so the proxy keys are added or removed.
/// `config` is mutated in place (`proxy.enabled`, and on a settings write also
/// `managed_keys`) and persisted to disk.
pub fn set_proxy_enabled(
    config: &mut AppConfig,
    enabled: bool,
    deps: &ProxyDeps<'_>,
) -> Result<(), ProxyError> {
    config.proxy.enabled = enabled;

    // No active account → just persist the flag; never create/modify settings.json.
    let active_id = match config.active_account_id.clone() {
        Some(id) => id,
        None => {
            ConfigStore::save(deps.config_dir, config)?;
            return Ok(());
        }
    };

    // Find the active account. A dangling id (account deleted) is treated like
    // "no active account": persist the flag only, no settings write.
    let active = match config.accounts.iter().find(|a| a.id == active_id) {
        Some(a) => a.clone(),
        None => {
            ConfigStore::save(deps.config_dir, config)?;
            return Ok(());
        }
    };

    // Load settings (invalid JSON aborts before any mutation).
    let settings = load_settings(deps.settings_path)?;

    // Rebuild the active account's env with the *updated* proxy setting. Token
    // accounts need their secret; OAuth accounts do not.
    let secret = match active.account_type {
        AccountType::Token => deps.secret_store.get(&active_id)?,
        AccountType::AnthropicOauth => None,
    };
    let new_env = build_env(&active, secret.as_deref(), &config.proxy)?;

    // Merge (strip union of MANAGED_KEYS and stored keys, insert new env).
    let (merged, new_managed_keys) = merge_env(settings, &config.managed_keys, &new_env);

    // Timestamped backup + atomic write.
    let bytes = serde_json::to_vec_pretty(&merged).map_err(|e| {
        ProxyError::SettingsWrite(std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    })?;
    super::atomic::backup(deps.settings_path, &settings_backups_dir(deps.settings_path))
        .map_err(ProxyError::SettingsWrite)?;
    super::atomic::atomic_write(deps.settings_path, &bytes).map_err(ProxyError::SettingsWrite)?;

    // Persist config (managed_keys may have changed; proxy.enabled already set).
    config.managed_keys = new_managed_keys;
    ConfigStore::save(deps.config_dir, config)?;

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::credential_store::CredentialStoreError;
    use crate::core::model::{Account, AuthKind, ProxySettings};
    use crate::core::secret_store::InMemorySecretStore;
    use serde_json::Value;
    use std::collections::BTreeMap;

    /// A credential store that PANICS on any access. Passing this through code
    /// that should never touch credentials proves, at runtime, that it doesn't.
    /// (Proxy code does not even accept a credential store, so this is belt and
    /// braces — see `toggle_does_no_credential_store_io`.)
    struct PanicCredentialStore;

    impl crate::core::credential_store::CredentialStore for PanicCredentialStore {
        fn read(&self) -> Result<Option<String>, CredentialStoreError> {
            panic!("proxy toggle must never read the credential store");
        }
        fn write(&self, _blob: &str) -> Result<(), CredentialStoreError> {
            panic!("proxy toggle must never write the credential store");
        }
    }

    struct Env {
        _settings_dir: tempfile::TempDir,
        _config_dir: tempfile::TempDir,
        settings_path: PathBuf,
        config_dir: PathBuf,
        secrets: InMemorySecretStore,
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
            }
        }

        fn deps(&self) -> ProxyDeps<'_> {
            ProxyDeps {
                settings_path: &self.settings_path,
                config_dir: &self.config_dir,
                secret_store: &self.secrets,
            }
        }

        fn settings_env(&self) -> Value {
            let v = load_settings(&self.settings_path).unwrap();
            v.get("env").cloned().unwrap_or(Value::Null)
        }
    }

    fn token_account(id: &str) -> Account {
        Account {
            id: id.to_string(),
            name: format!("token-{id}"),
            account_type: AccountType::Token,
            base_url: None,
            auth_kind: Some(AuthKind::AuthToken),
            identity: None,
            extra_env: BTreeMap::new(),
        }
    }

    fn config_with_active(accounts: Vec<Account>, active: Option<&str>) -> AppConfig {
        AppConfig {
            active_account_id: active.map(str::to_string),
            proxy: ProxySettings {
                enabled: false,
                url: "http://127.0.0.1:8080".to_string(),
                no_proxy: "localhost,127.0.0.1".to_string(),
            },
            accounts,
            ..AppConfig::default()
        }
    }

    #[test]
    fn enabling_proxy_adds_proxy_keys_to_active_env() {
        let env = Env::new();
        env.secrets.set("tok", "sk-secret").unwrap();
        let mut cfg = config_with_active(vec![token_account("tok")], Some("tok"));

        set_proxy_enabled(&mut cfg, true, &env.deps()).unwrap();

        let e = env.settings_env();
        assert_eq!(
            e["HTTP_PROXY"],
            Value::String("http://127.0.0.1:8080".into())
        );
        assert_eq!(
            e["HTTPS_PROXY"],
            Value::String("http://127.0.0.1:8080".into())
        );
        assert_eq!(e["NO_PROXY"], Value::String("localhost,127.0.0.1".into()));
        // The account's own env (token) is still present.
        assert_eq!(e["ANTHROPIC_AUTH_TOKEN"], Value::String("sk-secret".into()));
        assert!(cfg.proxy.enabled);

        // Config persisted with proxy keys in managed_keys.
        let reloaded = ConfigStore::load(&env.config_dir).unwrap();
        assert!(reloaded.proxy.enabled);
        assert!(reloaded.managed_keys.contains(&"HTTP_PROXY".to_string()));
    }

    #[test]
    fn disabling_proxy_removes_proxy_keys_from_active_env() {
        let env = Env::new();
        env.secrets.set("tok", "sk-secret").unwrap();
        let mut cfg = config_with_active(vec![token_account("tok")], Some("tok"));

        // First enable, then disable.
        set_proxy_enabled(&mut cfg, true, &env.deps()).unwrap();
        assert!(env.settings_env().get("HTTP_PROXY").is_some());

        set_proxy_enabled(&mut cfg, false, &env.deps()).unwrap();

        let e = env.settings_env();
        assert!(e.get("HTTP_PROXY").is_none());
        assert!(e.get("HTTPS_PROXY").is_none());
        assert!(e.get("NO_PROXY").is_none());
        // Account env survives the toggle.
        assert_eq!(e["ANTHROPIC_AUTH_TOKEN"], Value::String("sk-secret".into()));
        assert!(!cfg.proxy.enabled);

        let reloaded = ConfigStore::load(&env.config_dir).unwrap();
        assert!(!reloaded.proxy.enabled);
        assert!(!reloaded.managed_keys.contains(&"HTTP_PROXY".to_string()));
    }

    #[test]
    fn toggle_with_no_active_account_stores_flag_only() {
        let env = Env::new();
        let mut cfg = config_with_active(vec![token_account("tok")], None);

        set_proxy_enabled(&mut cfg, true, &env.deps()).unwrap();

        // Flag stored in memory and on disk.
        assert!(cfg.proxy.enabled);
        let reloaded = ConfigStore::load(&env.config_dir).unwrap();
        assert!(reloaded.proxy.enabled);
        // No settings.json was created or modified.
        assert!(!env.settings_path.exists());
    }

    #[test]
    fn toggle_does_no_credential_store_io() {
        // Build deps with a panicking credential store *in scope* and assert,
        // by construction, that the proxy API has no field to receive it: the
        // call below cannot reach the credential store at all. If a future
        // refactor wired one in, this test (and the type system) would force a
        // decision. We still construct the panic store to document intent.
        let _never = PanicCredentialStore;

        let env = Env::new();
        env.secrets.set("tok", "sk-secret").unwrap();
        let mut cfg = config_with_active(vec![token_account("tok")], Some("tok"));

        // A full enable+disable cycle: if any path tried credential I/O it would
        // need a store, which ProxyDeps simply does not provide.
        set_proxy_enabled(&mut cfg, true, &env.deps()).unwrap();
        set_proxy_enabled(&mut cfg, false, &env.deps()).unwrap();

        // Sanity: the toggle still did its real work without any credential store.
        assert!(env.settings_env().get("HTTP_PROXY").is_none());
    }
}
