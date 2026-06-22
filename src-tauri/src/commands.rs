//! Tauri commands — the bridge between the Rust core and the frontend.
//!
//! All mutating operations (switch account, add/update/delete account, toggle
//! proxy, import) are serialized behind a single async mutex in [`AppState`].
//! This prevents interleaved read-modify-write of `config.json` and `settings.json`.
//!
//! Each async command receives a [`State<'_, AppState>`] and acquires the mutex
//! before reading or writing config. Read-only commands also acquire the mutex
//! to avoid seeing partially-mutated state.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;

use serde::Serialize;
use tauri::State;
use tokio::sync::Mutex;
use uuid::Uuid;

use crate::core::config_store::{ConfigStore, ConfigStoreError};
use crate::core::credential_store::{CredentialStore, CredentialStoreError};
use crate::core::import::{detect_current, default_name, import as import_account, ImportDeps, ImportError, ImportResult};
use crate::core::model::{Account, AccountType, AppConfig, AuthKind, ProxySettings};
use crate::core::proxy::{set_proxy_enabled as apply_proxy_enabled, ProxyDeps, ProxyError};
use crate::core::secret_store::{KeyringSecretStore, SecretStore, SecretStoreError};
use crate::core::settings_env::SettingsEnvError;
use crate::core::switcher::{apply_account as apply_switch_account, SwitchDeps, SwitchError};

/// Application state shared across all Tauri commands.
///
/// Holds the I/O dependencies (paths, stores) and the current config behind a
/// mutex so all mutating operations are serialized.
pub struct AppState {
    /// Directory holding ccswitcher's own `config.json`.
    pub config_dir: PathBuf,
    /// Path to Claude Code's `settings.json`.
    pub settings_path: PathBuf,
    /// OS keyring for per-account secrets.
    pub secret_store: Arc<KeyringSecretStore>,
    /// Claude Code's OAuth credential store (snapshot/restore).
    pub credential_store: Box<dyn CredentialStore + Send + Sync>,
    /// The current config, behind an async mutex for serialization.
    pub mutex: Arc<Mutex<AppConfig>>,
}

/// Serializable error type returned to the frontend.
#[derive(Debug, Serialize)]
pub struct CommandError {
    /// Error kind for frontend handling.
    pub kind: CommandErrorKind,
    /// Human-readable error message.
    pub message: String,
}

/// Categories of command errors for frontend conditional handling.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum CommandErrorKind {
    /// The requested account id does not exist.
    AccountNotFound,
    /// Claude Code's `settings.json` is not valid JSON (no mutation occurred).
    InvalidSettings,
    /// An OS keyring operation failed.
    KeyringFailed,
    /// An OAuth credential store operation failed.
    CredentialStoreFailed,
    /// Config file I/O or parsing failed.
    ConfigFailed,
    /// Serialization/deserialization failed.
    SerializeFailed,
    /// Proxy operation failed.
    ProxyFailed,
    /// Import detection or creation failed.
    ImportFailed,
    /// General I/O error.
    IoFailed,
}

impl From<CommandError> for String {
    fn from(err: CommandError) -> Self {
        serde_json::to_string(&err).unwrap_or_else(|_| err.message)
    }
}

/// Convert [`SwitchError`] into a [`CommandError`].
impl From<SwitchError> for CommandError {
    fn from(err: SwitchError) -> Self {
        match err {
            SwitchError::UnknownAccount(_) => CommandError {
                kind: CommandErrorKind::AccountNotFound,
                message: err.to_string(),
            },
            SwitchError::Settings(e) => CommandError {
                kind: CommandErrorKind::InvalidSettings,
                message: e.to_string(),
            },
            SwitchError::Secret(_) => CommandError {
                kind: CommandErrorKind::KeyringFailed,
                message: err.to_string(),
            },
            SwitchError::Credential(_) => CommandError {
                kind: CommandErrorKind::CredentialStoreFailed,
                message: err.to_string(),
            },
            SwitchError::Config(_) => CommandError {
                kind: CommandErrorKind::ConfigFailed,
                message: err.to_string(),
            },
            SwitchError::SettingsWrite(e) => CommandError {
                kind: CommandErrorKind::IoFailed,
                message: e.to_string(),
            },
            SwitchError::EnvBuilder(e) => CommandError {
                kind: CommandErrorKind::KeyringFailed,
                message: e.to_string(),
            },
        }
    }
}

/// Convert [`ProxyError`] into a [`CommandError`].
impl From<ProxyError> for CommandError {
    fn from(err: ProxyError) -> Self {
        match err {
            ProxyError::Settings(e) => CommandError {
                kind: CommandErrorKind::InvalidSettings,
                message: e.to_string(),
            },
            ProxyError::Secret(_) => CommandError {
                kind: CommandErrorKind::KeyringFailed,
                message: err.to_string(),
            },
            ProxyError::Config(_) => CommandError {
                kind: CommandErrorKind::ConfigFailed,
                message: err.to_string(),
            },
            ProxyError::SettingsWrite(e) => CommandError {
                kind: CommandErrorKind::IoFailed,
                message: e.to_string(),
            },
            ProxyError::EnvBuilder(_) => CommandError {
                kind: CommandErrorKind::KeyringFailed,
                message: err.to_string(),
            },
        }
    }
}

/// Convert [`ImportError`] into a [`CommandError`].
impl From<ImportError> for CommandError {
    fn from(err: ImportError) -> Self {
        match err {
            ImportError::Io(e) => CommandError {
                kind: CommandErrorKind::IoFailed,
                message: e.to_string(),
            },
            ImportError::InvalidJson(e) => CommandError {
                kind: CommandErrorKind::InvalidSettings,
                message: e.to_string(),
            },
            ImportError::NoHomeDir => CommandError {
                kind: CommandErrorKind::IoFailed,
                message: err.to_string(),
            },
            ImportError::SecretStore(_) => CommandError {
                kind: CommandErrorKind::KeyringFailed,
                message: err.to_string(),
            },
            ImportError::CredentialStore(_) => CommandError {
                kind: CommandErrorKind::CredentialStoreFailed,
                message: err.to_string(),
            },
        }
    }
}

/// Convert [`SecretStoreError`] into a [`CommandError`].
impl From<SecretStoreError> for CommandError {
    fn from(err: SecretStoreError) -> Self {
        CommandError {
            kind: CommandErrorKind::KeyringFailed,
            message: err.to_string(),
        }
    }
}

/// Convert [`CredentialStoreError`] into a [`CommandError`].
impl From<CredentialStoreError> for CommandError {
    fn from(err: CredentialStoreError) -> Self {
        CommandError {
            kind: CommandErrorKind::CredentialStoreFailed,
            message: err.to_string(),
        }
    }
}

/// Convert [`ConfigStoreError`] into a [`CommandError`].
impl From<ConfigStoreError> for CommandError {
    fn from(err: ConfigStoreError) -> Self {
        CommandError {
            kind: CommandErrorKind::ConfigFailed,
            message: err.to_string(),
        }
    }
}

/// Convert [`SettingsEnvError`] into a [`CommandError`].
impl From<SettingsEnvError> for CommandError {
    fn from(err: SettingsEnvError) -> Self {
        CommandError {
            kind: CommandErrorKind::InvalidSettings,
            message: err.to_string(),
        }
    }
}

/// A light account representation for the frontend (non-secret only).
#[derive(Debug, Clone, Serialize)]
pub struct AccountDto {
    pub id: String,
    pub name: String,
    #[serde(rename = "type")]
    pub account_type: String,
    pub base_url: Option<String>,
    pub auth_kind: Option<String>,
    pub identity: Option<String>,
    pub extra_env: BTreeMap<String, String>,
}

impl From<Account> for AccountDto {
    fn from(acc: Account) -> Self {
        AccountDto {
            id: acc.id,
            name: acc.name,
            account_type: match acc.account_type {
                AccountType::AnthropicOauth => "anthropic_oauth".to_string(),
                AccountType::Token => "token".to_string(),
            },
            base_url: acc.base_url,
            auth_kind: acc.auth_kind.map(|k| match k {
                AuthKind::AuthToken => "auth_token".to_string(),
                AuthKind::ApiKey => "api_key".to_string(),
            }),
            identity: acc.identity,
            extra_env: acc.extra_env,
        }
    }
}

/// The current app state for the UI (non-secret only).
#[derive(Debug, Clone, Serialize)]
pub struct StateDto {
    pub active_account_id: Option<String>,
    pub proxy: ProxySettings,
    pub managed_keys: Vec<String>,
    pub accounts: Vec<AccountDto>,
}

impl From<AppConfig> for StateDto {
    fn from(cfg: AppConfig) -> Self {
        StateDto {
            active_account_id: cfg.active_account_id,
            proxy: cfg.proxy,
            managed_keys: cfg.managed_keys,
            accounts: cfg.accounts.into_iter().map(AccountDto::from).collect(),
        }
    }
}

/// Parameters for adding a new token account.
#[derive(Debug, serde::Deserialize)]
pub struct AddTokenAccountParams {
    pub name: String,
    pub base_url: Option<String>,
    pub auth_kind: String,
    pub secret: String,
    pub extra_env: Option<BTreeMap<String, String>>,
}

/// Parameters for updating an existing account.
#[derive(Debug, serde::Deserialize)]
pub struct UpdateAccountParams {
    pub id: String,
    pub name: Option<String>,
    pub base_url: Option<String>,
    pub extra_env: Option<BTreeMap<String, String>>,
    pub secret: Option<String>,
}

/// Result of an import operation.
#[derive(Debug, Serialize)]
pub struct ImportResultDto {
    pub account: AccountDto,
    pub warning: Option<String>,
}

/// List all accounts.
#[tauri::command]
pub async fn list_accounts(state: State<'_, AppState>) -> Result<Vec<AccountDto>, CommandError> {
    let config = state.mutex.lock().await;
    Ok(config.accounts.iter().cloned().map(AccountDto::from).collect())
}

/// Switch to the account with the given id.
#[tauri::command]
pub async fn switch_account(
    account_id: String,
    state: State<'_, AppState>,
) -> Result<(), CommandError> {
    let mut config = state.mutex.lock().await;

    let deps = SwitchDeps {
        settings_path: &state.settings_path,
        config_dir: &state.config_dir,
        secret_store: state.secret_store.as_ref(),
        credential_store: state.credential_store.as_ref(),
    };

    apply_switch_account(&mut config, &account_id, &deps)?;
    Ok(())
}

/// Set whether the global proxy is enabled.
#[tauri::command]
pub async fn set_proxy_enabled(
    enabled: bool,
    state: State<'_, AppState>,
) -> Result<(), CommandError> {
    let mut config = state.mutex.lock().await;

    let deps = ProxyDeps {
        settings_path: &state.settings_path,
        config_dir: &state.config_dir,
        secret_store: state.secret_store.as_ref(),
    };

    apply_proxy_enabled(&mut config, enabled, &deps)?;
    Ok(())
}

/// Get the current proxy settings.
#[tauri::command]
pub async fn get_proxy(state: State<'_, AppState>) -> Result<ProxySettings, CommandError> {
    let config = state.mutex.lock().await;
    Ok(config.proxy.clone())
}

/// Add a new token-based account.
#[tauri::command]
pub async fn add_token_account(
    params: AddTokenAccountParams,
    state: State<'_, AppState>,
) -> Result<AccountDto, CommandError> {
    let mut config = state.mutex.lock().await;

    // Parse auth_kind.
    let auth_kind = match params.auth_kind.as_str() {
        "auth_token" => AuthKind::AuthToken,
        "api_key" => AuthKind::ApiKey,
        _ => {
            return Err(CommandError {
                kind: CommandErrorKind::SerializeFailed,
                message: format!("invalid auth_kind: {}", params.auth_kind),
            });
        }
    };

    // Generate a UUID for the new account.
    let id = Uuid::new_v4().to_string();

    // Store the secret in the keyring.
    state.secret_store.as_ref().set(&id, &params.secret)?;

    // Create the account.
    let account = Account {
        id: id.clone(),
        name: params.name,
        account_type: AccountType::Token,
        base_url: params.base_url,
        auth_kind: Some(auth_kind),
        identity: None,
        extra_env: params.extra_env.unwrap_or_default(),
    };

    // Add to config and persist.
    config.accounts.push(account);
    ConfigStore::save(&state.config_dir, &config)?;

    Ok(AccountDto::from(
        config.accounts.iter().find(|a| a.id == id).unwrap().clone(),
    ))
}

/// Update an existing account.
#[tauri::command]
pub async fn update_account(
    params: UpdateAccountParams,
    state: State<'_, AppState>,
) -> Result<AccountDto, CommandError> {
    let mut config = state.mutex.lock().await;

    // Find the account.
    let account = config
        .accounts
        .iter_mut()
        .find(|a| a.id == params.id)
        .ok_or_else(|| CommandError {
            kind: CommandErrorKind::AccountNotFound,
            message: format!("account not found: {}", params.id),
        })?;

    // Update fields.
    if let Some(name) = params.name {
        account.name = name;
    }
    if let Some(base_url) = params.base_url {
        account.base_url = Some(base_url);
    }
    if let Some(extra_env) = params.extra_env {
        account.extra_env = extra_env;
    }

    // Update secret if provided.
    if let Some(secret) = params.secret {
        state.secret_store.as_ref().set(&params.id, &secret)?;
    }

    // Clone the account before persisting (borrow checker).
    let updated_account = account.clone();

    // Persist config.
    ConfigStore::save(&state.config_dir, &config)?;

    Ok(AccountDto::from(updated_account))
}

/// Delete an account.
#[tauri::command]
pub async fn delete_account(
    account_id: String,
    state: State<'_, AppState>,
) -> Result<(), CommandError> {
    let mut config = state.mutex.lock().await;

    // Check if account exists.
    let exists = config.accounts.iter().any(|a| a.id == account_id);
    if !exists {
        return Err(CommandError {
            kind: CommandErrorKind::AccountNotFound,
            message: format!("account not found: {}", account_id),
        });
    }

    // Remove from accounts.
    config.accounts.retain(|a| a.id != account_id);

    // Clear secret from keyring.
    state.secret_store.as_ref().delete(&account_id)?;

    // Clear active_account_id if we deleted the active account.
    if config.active_account_id.as_ref() == Some(&account_id) {
        config.active_account_id = None;
    }

    // Persist config.
    ConfigStore::save(&state.config_dir, &config)?;

    Ok(())
}

/// Import the current Claude Code login as a new account.
#[tauri::command]
pub async fn import_current(
    name: Option<String>,
    state: State<'_, AppState>,
) -> Result<ImportResultDto, CommandError> {
    let mut config = state.mutex.lock().await;

    // Detect the current login.
    let deps = ImportDeps {
        settings_path: Some(state.settings_path.clone()),
        credential_store: state.credential_store.as_ref(),
        secret_store: state.secret_store.as_ref(),
    };

    let candidate = detect_current(&config.managed_keys, &deps)?
        .ok_or_else(|| CommandError {
            kind: CommandErrorKind::ImportFailed,
            message: "no importable login found".to_string(),
        })?;

    // Use provided name or generate default.
    let account_name = name.unwrap_or_else(|| default_name(&candidate));

    // Import the account.
    let result = import_account(
        candidate,
        &account_name,
        &config.accounts,
        state.secret_store.as_ref(),
    )?;

    match result {
        ImportResult::Created(account) => {
            config.accounts.push(account.clone());
            ConfigStore::save(&state.config_dir, &config)?;
            Ok(ImportResultDto {
                account: AccountDto::from(account),
                warning: None,
            })
        }
        ImportResult::CreatedWithWarning(account, warning) => {
            config.accounts.push(account.clone());
            ConfigStore::save(&state.config_dir, &config)?;
            Ok(ImportResultDto {
                account: AccountDto::from(account),
                warning: Some(warning),
            })
        }
    }
}

/// Get the current app state (for UI sync on startup).
#[tauri::command]
pub async fn get_state(state: State<'_, AppState>) -> Result<StateDto, CommandError> {
    let config = state.mutex.lock().await;
    Ok(StateDto::from(config.clone()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::credential_store::InMemoryCredentialStore;
    use crate::core::model::{Account, AccountType, AuthKind};
    use crate::core::secret_store::InMemorySecretStore;
    use std::collections::BTreeMap;

    #[test]
    fn command_error_serializes_to_json() {
        let err = CommandError {
            kind: CommandErrorKind::AccountNotFound,
            message: "account 'foo' not found".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("account_not_found"));
        assert!(json.contains("account 'foo' not found"));
    }

    #[test]
    fn unknown_account_error_maps_correctly() {
        let err = CommandError::from(SwitchError::UnknownAccount("foo".to_string()));
        assert!(matches!(err.kind, CommandErrorKind::AccountNotFound));
        assert!(err.message.contains("foo"));
    }

    #[test]
    fn invalid_settings_error_maps_correctly() {
        // Create a SettingsEnvError::InvalidJson from a serde_json::Error.
        let json_err = serde_json::from_str::<serde_json::Value>("{ not valid }").unwrap_err();
        let inner = SettingsEnvError::InvalidJson(json_err);
        let err = CommandError::from(inner);
        assert!(matches!(err.kind, CommandErrorKind::InvalidSettings));
    }

    #[test]
    fn account_dto_from_account() {
        let mut extra = BTreeMap::new();
        extra.insert("FOO".to_string(), "bar".to_string());
        let account = Account {
            id: "test-id".to_string(),
            name: "Test".to_string(),
            account_type: AccountType::Token,
            base_url: Some("https://api.example.com".to_string()),
            auth_kind: Some(AuthKind::AuthToken),
            identity: None,
            extra_env: extra.clone(),
        };

        let dto = AccountDto::from(account);
        assert_eq!(dto.id, "test-id");
        assert_eq!(dto.name, "Test");
        assert_eq!(dto.account_type, "token");
        assert_eq!(dto.base_url, Some("https://api.example.com".to_string()));
        assert_eq!(dto.auth_kind, Some("auth_token".to_string()));
        assert_eq!(dto.extra_env, extra);
    }

    #[test]
    fn state_dto_from_config() {
        let mut extra = BTreeMap::new();
        extra.insert("FOO".to_string(), "bar".to_string());
        let config = AppConfig {
            active_account_id: Some("acc-1".to_string()),
            proxy: ProxySettings {
                enabled: true,
                url: "http://127.0.0.1:8080".to_string(),
                no_proxy: "localhost".to_string(),
            },
            managed_keys: vec!["ANTHROPIC_AUTH_TOKEN".to_string()],
            accounts: vec![Account {
                id: "acc-1".to_string(),
                name: "Work".to_string(),
                account_type: AccountType::Token,
                base_url: None,
                auth_kind: Some(AuthKind::AuthToken),
                identity: None,
                extra_env: extra.clone(),
            }],
            ..Default::default()
        };

        let dto = StateDto::from(config);
        assert_eq!(dto.active_account_id, Some("acc-1".to_string()));
        assert!(dto.proxy.enabled);
        assert_eq!(dto.managed_keys.len(), 1);
        assert_eq!(dto.accounts.len(), 1);
        assert_eq!(dto.accounts[0].id, "acc-1");
        assert_eq!(dto.accounts[0].extra_env, extra);
    }
}
