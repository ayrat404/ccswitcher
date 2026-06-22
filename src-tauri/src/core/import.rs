//! Import current Claude Code login as a new account.
//!
//! This module detects whatever login Claude Code is currently using and turns
//! it into a ccswitcher account. Two detection paths:
//! 1. **Token-based**: `settings.json` env contains `ANTHROPIC_AUTH_TOKEN` or
//!    `ANTHROPIC_API_KEY` that ccswitcher isn't already managing. This captures
//!    manual token edits or third-party provider setups.
//! 2. **OAuth-based**: the platform credential store has a non-empty OAuth blob.
//!    This captures a native Anthropic login.
//!
//! Crucially, detection **ignores** env keys that are already tracked in
//! `config.managed_keys`. This prevents ccswitcher from "re-importing" the
//! account it just switched to — we only want to capture external logins, not
//! our own injected values.

use std::collections::BTreeSet;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde_json::Value;
use thiserror::Error;

use super::claude_paths::{settings_path, ClaudePathError};
use super::credential_store::{CredentialStore, CredentialStoreError};
use super::model::{Account, AccountType, AuthKind};
use super::secret_store::{SecretStore, SecretStoreError};
use super::settings_env::{load_settings, SettingsEnvError};

/// Errors raised during import detection or account creation.
#[derive(Debug, Error)]
pub enum ImportError {
    /// An I/O error occurred while reading settings or credentials.
    #[error("I/O error: {0}")]
    Io(#[from] io::Error),
    /// Settings.json could not be parsed.
    #[error("settings.json is not valid JSON: {0}")]
    InvalidJson(serde_json::Error),
    /// The user home directory could not be resolved.
    #[error("could not resolve user home directory")]
    NoHomeDir,
    /// Secret store operation failed.
    #[error("secret store error: {0}")]
    SecretStore(#[from] SecretStoreError),
    /// Credential store operation failed.
    #[error("credential store error: {0}")]
    CredentialStore(#[from] CredentialStoreError),
}

impl From<ClaudePathError> for ImportError {
    fn from(err: ClaudePathError) -> Self {
        match err {
            ClaudePathError::NoHomeDir => ImportError::NoHomeDir,
        }
    }
}

impl From<SettingsEnvError> for ImportError {
    fn from(err: SettingsEnvError) -> Self {
        match err {
            SettingsEnvError::Io(e) => ImportError::Io(e),
            SettingsEnvError::InvalidJson(e) => ImportError::InvalidJson(e),
            SettingsEnvError::NotAnObject => {
                // Treat NotAnObject as a JSON parsing error.
                // We can't use serde_json::Error::custom directly, so we create a synthetic error.
                ImportError::InvalidJson(serde_json::Error::io(std::io::Error::new(
                    std::io::ErrorKind::InvalidData,
                    "settings.json top level is not a JSON object",
                )))
            }
        }
    }
}

/// A detected current login that can be imported as a new account.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ImportCandidate {
    /// A token-based login (API key or auth token with optional base URL).
    Token {
        /// The token value to store in the keyring.
        secret: String,
        /// Which env variable this token belongs to.
        auth_kind: AuthKind,
        /// Optional base URL from the env.
        base_url: Option<String>,
    },
    /// An OAuth-based login (native Anthropic or compatible provider).
    Oauth {
        /// The raw credential blob to store in the keyring.
        blob: String,
        /// A stable identity (email/account id) extracted from the blob if available.
        /// Used for duplicate detection; `None` means we couldn't extract one.
        identity: Option<String>,
    },
}

/// Result of an import operation indicating whether a duplicate was detected.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ImportResult {
    /// Account was created successfully (no duplicate detected).
    Created(Account),
    /// Account was created but a duplicate may exist (warning returned to caller).
    CreatedWithWarning(Account, String),
}

/// Dependencies required for import detection and account creation.
pub struct ImportDeps<'a> {
    /// Path to Claude Code's `settings.json`. If `None`, uses the default path.
    pub settings_path: Option<PathBuf>,
    /// Path to Claude Code's user-level `~/.claude.json`. If `None`, uses the
    /// default path. Read for stable account identity (accountUuid/email) used
    /// in OAuth duplicate detection.
    pub user_config_path: Option<PathBuf>,
    /// Credential store reader (for OAuth detection).
    pub credential_store: &'a dyn CredentialStore,
    /// Secret store writer (for persisting imported secrets).
    pub secret_store: &'a dyn SecretStore,
}

impl ImportDeps<'_> {
    /// Resolve the settings path (default if not provided).
    fn resolve_settings_path(&self) -> Result<PathBuf, ImportError> {
        if let Some(ref p) = self.settings_path {
            Ok(p.clone())
        } else {
            Ok(settings_path()?)
        }
    }

    /// Resolve the user-config (`~/.claude.json`) path (default if not provided).
    fn resolve_user_config_path(&self) -> PathBuf {
        self.user_config_path
            .clone()
            .unwrap_or_else(|| super::claude_paths::user_config_path().unwrap_or_default())
    }
}

/// Detect the current Claude Code login.
///
/// Returns `None` if no importable login is found. The detection logic:
/// 1. Load `settings.json` and look for `ANTHROPIC_AUTH_TOKEN` or
///    `ANTHROPIC_API_KEY` in `env` that are **not** in `managed_keys`.
///    (Keys in `managed_keys` are ccswitcher's own injected values from a prior
///    switch, so we ignore them to avoid re-importing ourselves.)
/// 2. If no non-managed token key exists, read the credential store.
///    A non-empty blob → OAuth candidate.
/// 3. Otherwise → `None`.
///
/// For OAuth, we attempt to extract a stable `identity` field from the blob's
/// JSON (e.g., an email or account_id). This is best-effort; `None` is fine
/// and just means we can't deduplicate by identity.
///
/// **IMPORTANT**: Only `managed_keys` (from `config.json`) is used for ignore.
/// The constant `MANAGED_KEYS` is NOT used because it represents the set of
/// keys ccswitcher *may* write, not the ones it *has* written in this session.
pub fn detect_current(
    managed_keys: &[String],
    deps: &ImportDeps,
) -> Result<Option<ImportCandidate>, ImportError> {
    let settings_path = deps.resolve_settings_path()?;
    let settings = load_settings(&settings_path)?;

    // Build the set of keys ccswitcher is already managing from config.managed_keys.
    // NOTE: We do NOT include the constant MANAGED_KEYS here because that set
    // represents keys we *might* manage, not the ones we've *actually* written.
    // Only config.managed_keys reflects what we've injected in this session.
    let managed_set: BTreeSet<String> = managed_keys.iter().cloned().collect();

    // Check env for a non-managed token key.
    if let Some(env) = settings.get("env").and_then(Value::as_object) {
        // Prefer AUTH_TOKEN over API_KEY if both exist (but we only check one).
        if let Some(token) = env.get("ANTHROPIC_AUTH_TOKEN").and_then(Value::as_str) {
            if !managed_set.contains("ANTHROPIC_AUTH_TOKEN") && !token.is_empty() {
                let base_url = env
                    .get("ANTHROPIC_BASE_URL")
                    .and_then(Value::as_str)
                    .map(str::to_string);
                return Ok(Some(ImportCandidate::Token {
                    secret: token.to_string(),
                    auth_kind: AuthKind::AuthToken,
                    base_url,
                }));
            }
        }

        if let Some(api_key) = env.get("ANTHROPIC_API_KEY").and_then(Value::as_str) {
            if !managed_set.contains("ANTHROPIC_API_KEY") && !api_key.is_empty() {
                let base_url = env
                    .get("ANTHROPIC_BASE_URL")
                    .and_then(Value::as_str)
                    .map(str::to_string);
                return Ok(Some(ImportCandidate::Token {
                    secret: api_key.to_string(),
                    auth_kind: AuthKind::ApiKey,
                    base_url,
                }));
            }
        }
    }

    // No non-managed token key → fall back to OAuth credential store.
    match deps.credential_store.read() {
        Ok(Some(blob)) => {
            // Identity: prefer the stable accountUuid/emailAddress from the
            // user-level ~/.claude.json (oauthAccount object); fall back to any
            // identity embedded in the blob itself.
            let identity = extract_identity_from_user_config_at(&deps.resolve_user_config_path())
                .or_else(|| extract_identity(&blob));
            Ok(Some(ImportCandidate::Oauth {
                blob,
                identity,
            }))
        }
        Ok(None) => Ok(None),
        Err(e) => Err(ImportError::CredentialStore(e)),
    }
}

/// Extract a stable identity (email/account_id) from an OAuth credential blob.
///
/// The blob shape is `{ "claudeAiOauth": { ... } }`. We look for common
/// identity fields like "email" or "account_id" within the nested object.
/// Returns `None` if parsing fails or no such field is found.
fn extract_identity(blob: &str) -> Option<String> {
    let parsed: Value = serde_json::from_str(blob).ok()?;
    let oauth_obj = parsed.get("claudeAiOauth").and_then(Value::as_object)?;

    // Try a few known field names that might carry a stable identity.
    oauth_obj
        .get("email")
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| {
            oauth_obj
                .get("account_id")
                .and_then(Value::as_str)
                .map(str::to_string)
        })
        .or_else(|| {
            oauth_obj
                .get("accountId")
                .and_then(Value::as_str)
                .map(str::to_string)
        })
}

/// Extract a stable identity from Claude Code's user-level `~/.claude.json`.
///
/// The file holds an `oauthAccount` object with stable fields `accountUuid`
/// (preferred) and `emailAddress`. These do not change across token refreshes,
/// unlike the credential blob, so they make a reliable duplicate key.
fn extract_identity_from_user_config(content: &str) -> Option<String> {
    let parsed: Value = serde_json::from_str(content).ok()?;
    let oauth = parsed.get("oauthAccount").and_then(Value::as_object)?;
    oauth
        .get("accountUuid")
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| {
            oauth
                .get("emailAddress")
                .and_then(Value::as_str)
                .map(str::to_string)
        })
}

/// Read `~/.claude.json` from `path` and extract its identity, or `None` if the
/// file is missing or has no recognizable identity field.
fn extract_identity_from_user_config_at(path: &Path) -> Option<String> {
    let content = fs::read_to_string(path).ok()?;
    extract_identity_from_user_config(&content)
}

/// Credential-blob fields that change over a session and must NOT take part in
/// duplicate comparison. Removing them yields a stable "fingerprint" of the
/// login itself.
const VOLATILE_BLOB_FIELDS: &[&str] = &[
    "accessToken",
    "refreshToken",
    "expiresAt",
    "expiresAtTimestamp",
    "tokenResponse",
    "idToken",
];

/// Normalize an OAuth credential blob into a canonical, comparable string.
///
/// Strips the volatile fields (`accessToken`, `refreshToken`, expiry, token
/// response) so that two snapshots of the *same* login — taken before and
/// after Claude Code refreshes its tokens in place — compare equal. Returns
/// `None` if the blob isn't valid JSON. Key order is canonical because
/// `serde_json` (without `preserve_order`) backs objects with a `BTreeMap`.
fn normalize_blob(blob: &str) -> Option<String> {
    let mut parsed: Value = serde_json::from_str(blob).ok()?;
    if let Some(oauth) = parsed.get_mut("claudeAiOauth").and_then(Value::as_object_mut) {
        for key in VOLATILE_BLOB_FIELDS {
            oauth.remove(*key);
        }
    }
    Some(parsed.to_string())
}

/// Generate a default display name for an import candidate.
///
/// - Token with base_url → extract host (e.g., "api.anthropic.com").
/// - Token without base_url → generic "Token Account".
/// - OAuth with identity → use identity if it looks like an email.
/// - OAuth without identity → "Anthropic".
pub fn default_name(candidate: &ImportCandidate) -> String {
    match candidate {
        ImportCandidate::Token { base_url, .. } => {
            if let Some(url) = base_url {
                // Strip scheme and path, keep only host.
                url.trim_start_matches("https://")
                    .trim_start_matches("http://")
                    .split('/')
                    .next()
                    .unwrap_or("Token Account")
                    .to_string()
            } else {
                "Token Account".to_string()
            }
        }
        ImportCandidate::Oauth { identity, .. } => {
            if let Some(id) = identity {
                // If it looks like an email, use it; otherwise fall back.
                if id.contains('@') {
                    id.clone()
                } else {
                    "Anthropic".to_string()
                }
            } else {
                "Anthropic".to_string()
            }
        }
    }
}

/// Import a candidate as a new account.
///
/// Creates an `Account` with a fresh UUID, stores its secret in the keyring,
/// and returns it. Duplicate detection is performed:
///
/// - **Token**: if an existing account matches on `base_url` + `auth_kind`,
///   a warning is returned but the account is still created.
/// - **OAuth**: if `identity` is `Some` and matches an existing account's
///   `identity`, a warning is returned. If `identity` is `None`, dedup is
///   skipped (we don't compare raw blobs because they change on refresh).
///
/// The returned `ImportResult` contains the created account and an optional
/// warning message.
pub fn import(
    candidate: ImportCandidate,
    name: &str,
    existing_accounts: &[Account],
    secret_store: &dyn SecretStore,
) -> Result<ImportResult, ImportError> {
    use uuid::Uuid;

    let id = Uuid::new_v4().to_string();

    // Build the account based on candidate type.
    let (account, secret_value, warning) = match candidate {
        ImportCandidate::Token {
            secret,
            auth_kind,
            base_url,
        } => {
            // Check for duplicate token accounts.
            let warning = existing_accounts
                .iter()
                .find(|acc| {
                    acc.account_type == AccountType::Token
                        && acc.auth_kind == Some(auth_kind)
                        && acc.base_url.as_deref() == base_url.as_deref()
                })
                .map(|dup| {
                    format!(
                        "An account with the same provider ({}) already exists.",
                        dup.name
                    )
                });

            let account = Account {
                id: id.clone(),
                name: name.to_string(),
                account_type: AccountType::Token,
                base_url,
                auth_kind: Some(auth_kind),
                identity: None,
                extra_env: Default::default(),
            };
            (account, secret, warning)
        }
        ImportCandidate::Oauth { blob, identity } => {
            // 1. Identity-based dedup (only when a stable identity is known).
            let mut warning = if let Some(ref ident) = identity {
                existing_accounts
                    .iter()
                    .find(|acc| {
                        acc.account_type == AccountType::AnthropicOauth
                            && acc.identity.as_deref() == Some(ident)
                    })
                    .map(|dup| {
                        format!(
                            "An account with the same identity ({}) already exists.",
                            dup.name
                        )
                    })
            } else {
                None
            };

            // 2. Blob-based dedup: compare a normalized fingerprint (volatile
            //    token/expiry fields stripped) against each existing OAuth
            //    account's stored blob. This catches the same login even when
            //    no identity field was extractable.
            if warning.is_none() {
                if let Some(norm) = normalize_blob(&blob) {
                    for acc in existing_accounts
                        .iter()
                        .filter(|a| a.account_type == AccountType::AnthropicOauth)
                    {
                        if let Ok(Some(stored)) = secret_store.get(&acc.id) {
                            if normalize_blob(&stored).as_deref() == Some(norm.as_str()) {
                                warning = Some(format!(
                                    "An account with the same login ({}) already exists.",
                                    acc.name
                                ));
                                break;
                            }
                        }
                    }
                }
            }

            let account = Account {
                id: id.clone(),
                name: name.to_string(),
                account_type: AccountType::AnthropicOauth,
                base_url: None,
                auth_kind: None,
                identity,
                extra_env: Default::default(),
            };
            (account, blob, warning)
        }
    };

    // Store the secret in the keyring.
    secret_store
        .set(&id, &secret_value)
        .map_err(ImportError::SecretStore)?;

    Ok(if let Some(warning) = warning {
        ImportResult::CreatedWithWarning(account, warning)
    } else {
        ImportResult::Created(account)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::claude_paths::CLAUDE_DIR_NAME;
    use crate::core::credential_store::InMemoryCredentialStore;
    use crate::core::secret_store::InMemorySecretStore;
    use std::fs;
    use tempfile::TempDir;

    /// Helper: create a temp dir with a settings.json file.
    fn setup_settings(settings_content: &str) -> (TempDir, PathBuf) {
        let temp = TempDir::new().unwrap();
        let claude_base = temp.path().join(CLAUDE_DIR_NAME);
        fs::create_dir_all(&claude_base).unwrap();
        let settings_path = claude_base.join("settings.json");
        fs::write(&settings_path, settings_content).unwrap();
        (temp, settings_path)
    }

    #[test]
    fn detect_current_returns_token_when_auth_token_present() {
        let (temp, settings_path) = setup_settings(r#"{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-123"}}"#);
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        let result = detect_current(&[], &deps).unwrap();
        assert!(matches!(result, Some(ImportCandidate::Token { .. })));

        drop(temp); // Clean up.
    }

    #[test]
    fn detect_current_returns_token_when_api_key_present() {
        let (temp, settings_path) =
            setup_settings(r#"{"env":{"ANTHROPIC_API_KEY":"sk-456"}}"#);
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        let result = detect_current(&[], &deps).unwrap();
        match result {
            Some(ImportCandidate::Token {
                auth_kind: AuthKind::ApiKey,
                ..
            }) => {}
            _ => panic!("expected API key token candidate"),
        }

        drop(temp);
    }

    #[test]
    fn detect_current_extracts_base_url_from_token() {
        let (temp, settings_path) = setup_settings(
            r#"{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-abc","ANTHROPIC_BASE_URL":"https://api.example.com"}}"#,
        );
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        let result = detect_current(&[], &deps).unwrap();
        match result {
            Some(ImportCandidate::Token { base_url, .. }) => {
                assert_eq!(base_url, Some("https://api.example.com".to_string()));
            }
            _ => panic!("expected token with base_url"),
        }

        drop(temp);
    }

    #[test]
    fn detect_current_returns_oauth_when_credentials_non_empty() {
        let (temp, settings_path) = setup_settings(r#"{"env":{}}"#);
        let creds = InMemoryCredentialStore::new();
        creds
            .write(r#"{"claudeAiOauth":{"accessToken":"a"}}"#)
            .unwrap();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        let result = detect_current(&[], &deps).unwrap();
        assert!(matches!(result, Some(ImportCandidate::Oauth { .. })));

        drop(temp);
    }

    #[test]
    fn detect_current_returns_none_when_neither_exists() {
        let (temp, settings_path) = setup_settings(r#"{"env":{}}"#);
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        let result = detect_current(&[], &deps).unwrap();
        assert!(result.is_none());

        drop(temp);
    }

    #[test]
    fn detect_current_ignores_managed_auth_token_key() {
        // When AUTH_TOKEN is in managed_keys, it's ccswitcher's own injected value.
        let (temp, settings_path) = setup_settings(r#"{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-123"}}"#);
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        // managed_keys includes AUTH_TOKEN → should be ignored.
        let result = detect_current(&["ANTHROPIC_AUTH_TOKEN".to_string()], &deps).unwrap();
        assert!(result.is_none(), "managed AUTH_TOKEN should be ignored");

        drop(temp);
    }

    #[test]
    fn detect_current_ignores_managed_api_key_key() {
        let (temp, settings_path) = setup_settings(r#"{"env":{"ANTHROPIC_API_KEY":"sk-456"}}"#);
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        // managed_keys includes API_KEY → should be ignored.
        let result = detect_current(&["ANTHROPIC_API_KEY".to_string()], &deps).unwrap();
        assert!(result.is_none(), "managed API_KEY should be ignored");

        drop(temp);
    }

    #[test]
    fn detect_current_falls_back_to_oauth_when_token_is_managed() {
        // AUTH_TOKEN is managed (should be ignored), but credential store has data.
        let (temp, settings_path) = setup_settings(r#"{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-123"}}"#);
        let creds = InMemoryCredentialStore::new();
        creds
            .write(r#"{"claudeAiOauth":{"accessToken":"a"}}"#)
            .unwrap();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        // Should skip the managed token and detect OAuth instead.
        let result =
            detect_current(&["ANTHROPIC_AUTH_TOKEN".to_string()], &deps).unwrap();
        assert!(matches!(result, Some(ImportCandidate::Oauth { .. })));

        drop(temp);
    }

    #[test]
    fn detect_current_prefers_auth_token_over_api_key() {
        // When both are present, AUTH_TOKEN wins.
        let (temp, settings_path) = setup_settings(
            r#"{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-auth","ANTHROPIC_API_KEY":"sk-api"}}"#,
        );
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        let result = detect_current(&[], &deps).unwrap();
        match result {
            Some(ImportCandidate::Token {
                auth_kind: AuthKind::AuthToken,
                secret,
                ..
            }) => {
                assert_eq!(secret, "sk-auth");
            }
            _ => panic!("expected AUTH_TOKEN candidate"),
        }

        drop(temp);
    }

    #[test]
    fn default_name_token_with_base_url_extracts_host() {
        let candidate = ImportCandidate::Token {
            secret: "sk-123".to_string(),
            auth_kind: AuthKind::AuthToken,
            base_url: Some("https://api.anthropic.com/v1".to_string()),
        };
        assert_eq!(default_name(&candidate), "api.anthropic.com");
    }

    #[test]
    fn default_name_token_without_base_url_is_generic() {
        let candidate = ImportCandidate::Token {
            secret: "sk-123".to_string(),
            auth_kind: AuthKind::AuthToken,
            base_url: None,
        };
        assert_eq!(default_name(&candidate), "Token Account");
    }

    #[test]
    fn default_name_oauth_with_email_identity() {
        let candidate = ImportCandidate::Oauth {
            blob: "{}".to_string(),
            identity: Some("user@example.com".to_string()),
        };
        assert_eq!(default_name(&candidate), "user@example.com");
    }

    #[test]
    fn default_name_oauth_with_non_email_identity() {
        let candidate = ImportCandidate::Oauth {
            blob: "{}".to_string(),
            identity: Some("account-123".to_string()),
        };
        assert_eq!(default_name(&candidate), "Anthropic");
    }

    #[test]
    fn default_name_oauth_without_identity() {
        let candidate = ImportCandidate::Oauth {
            blob: "{}".to_string(),
            identity: None,
        };
        assert_eq!(default_name(&candidate), "Anthropic");
    }

    #[test]
    fn import_creates_token_account_and_stores_secret() {
        let candidate = ImportCandidate::Token {
            secret: "sk-secret".to_string(),
            auth_kind: AuthKind::AuthToken,
            base_url: Some("https://api.anthropic.com".to_string()),
        };
        let store = InMemorySecretStore::new();

        let result = import(candidate, "Work", &[], &store).unwrap();
        match result {
            ImportResult::Created(acc) => {
                assert_eq!(acc.name, "Work");
                assert_eq!(acc.account_type, AccountType::Token);
                assert_eq!(acc.base_url, Some("https://api.anthropic.com".to_string()));
                assert_eq!(acc.auth_kind, Some(AuthKind::AuthToken));

                // Verify secret was stored.
                assert_eq!(store.get(&acc.id).unwrap(), Some("sk-secret".to_string()));
            }
            _ => panic!("expected Created, got {:?}", result),
        }
    }

    #[test]
    fn import_creates_oauth_account_and_stores_blob() {
        let candidate = ImportCandidate::Oauth {
            blob: r#"{"claudeAiOauth":{"accessToken":"tok"}}"#.to_string(),
            identity: Some("user@example.com".to_string()),
        };
        let store = InMemorySecretStore::new();

        let result = import(candidate, "Personal", &[], &store).unwrap();
        match result {
            ImportResult::Created(acc) => {
                assert_eq!(acc.name, "Personal");
                assert_eq!(acc.account_type, AccountType::AnthropicOauth);
                assert_eq!(acc.identity, Some("user@example.com".to_string()));

                // Verify blob was stored.
                assert_eq!(
                    store.get(&acc.id).unwrap(),
                    Some(r#"{"claudeAiOauth":{"accessToken":"tok"}}"#.to_string())
                );
            }
            _ => panic!("expected Created"),
        }
    }

    #[test]
    fn import_token_duplicate_returns_warning_flag() {
        let candidate = ImportCandidate::Token {
            secret: "sk-new".to_string(),
            auth_kind: AuthKind::AuthToken,
            base_url: Some("https://api.anthropic.com".to_string()),
        };

        // Existing account with same base_url + auth_kind.
        let existing = vec![Account {
            id: "old-id".to_string(),
            name: "Old Work".to_string(),
            account_type: AccountType::Token,
            base_url: Some("https://api.anthropic.com".to_string()),
            auth_kind: Some(AuthKind::AuthToken),
            identity: None,
            extra_env: Default::default(),
        }];

        let store = InMemorySecretStore::new();
        let result = import(candidate, "New Work", &existing, &store).unwrap();
        match result {
            ImportResult::CreatedWithWarning(acc, warning) => {
                assert_eq!(acc.name, "New Work");
                assert!(warning.contains("Old Work"));
                assert!(warning.contains("already exists"));
            }
            _ => panic!("expected CreatedWithWarning, got {:?}", result),
        }
    }

    #[test]
    fn import_token_with_different_auth_kind_no_warning() {
        let candidate = ImportCandidate::Token {
            secret: "sk-new".to_string(),
            auth_kind: AuthKind::ApiKey,
            base_url: Some("https://api.anthropic.com".to_string()),
        };

        // Existing account has same base_url but different auth_kind → no dup.
        let existing = vec![Account {
            id: "old-id".to_string(),
            name: "Old AuthToken".to_string(),
            account_type: AccountType::Token,
            base_url: Some("https://api.anthropic.com".to_string()),
            auth_kind: Some(AuthKind::AuthToken),
            identity: None,
            extra_env: Default::default(),
        }];

        let store = InMemorySecretStore::new();
        let result = import(candidate, "API Key Account", &existing, &store).unwrap();
        assert!(matches!(result, ImportResult::Created(_)));
    }

    #[test]
    fn import_oauth_duplicate_by_identity_returns_warning() {
        let candidate = ImportCandidate::Oauth {
            blob: r#"{"claudeAiOauth":{"accessToken":"new"}}"#.to_string(),
            identity: Some("user@example.com".to_string()),
        };

        // Existing OAuth account with same identity.
        let existing = vec![Account {
            id: "old-id".to_string(),
            name: "Old Personal".to_string(),
            account_type: AccountType::AnthropicOauth,
            base_url: None,
            auth_kind: None,
            identity: Some("user@example.com".to_string()),
            extra_env: Default::default(),
        }];

        let store = InMemorySecretStore::new();
        let result = import(candidate, "New Personal", &existing, &store).unwrap();
        match result {
            ImportResult::CreatedWithWarning(acc, warning) => {
                assert_eq!(acc.name, "New Personal");
                assert!(warning.contains("Old Personal"));
            }
            _ => panic!("expected CreatedWithWarning"),
        }
    }

    #[test]
    fn import_oauth_with_no_identity_skips_dedup() {
        let candidate = ImportCandidate::Oauth {
            blob: r#"{"claudeAiOauth":{"accessToken":"new"}}"#.to_string(),
            identity: None,
        };

        // Existing OAuth account (identity is None for both).
        let existing = vec![Account {
            id: "old-id".to_string(),
            name: "Old OAuth".to_string(),
            account_type: AccountType::AnthropicOauth,
            base_url: None,
            auth_kind: None,
            identity: None,
            extra_env: Default::default(),
        }];

        let store = InMemorySecretStore::new();
        let result = import(candidate, "Another OAuth", &existing, &store).unwrap();
        // No warning: identity is None AND the existing account has no stored
        // blob to compare against (empty keyring), so dedup has nothing to match.
        assert!(matches!(result, ImportResult::Created(_)));
    }

    #[test]
    fn import_oauth_duplicate_by_blob_returns_warning() {
        // Same login (identity absent), different access tokens — the
        // normalized fingerprint still matches, so this is a duplicate.
        let candidate = ImportCandidate::Oauth {
            blob: r#"{"claudeAiOauth":{"accessToken":"fresh","refreshToken":"r","expiresAt":999}}"#
                .to_string(),
            identity: None,
        };

        let existing = vec![Account {
            id: "old-id".to_string(),
            name: "Old OAuth".to_string(),
            account_type: AccountType::AnthropicOauth,
            base_url: None,
            auth_kind: None,
            identity: None,
            extra_env: Default::default(),
        }];

        // The existing account's blob is in the keyring with different volatile
        // values but the same stable fields.
        let store = InMemorySecretStore::new();
        store
            .set("old-id", r#"{"claudeAiOauth":{"accessToken":"stale","expiresAt":1}}"#)
            .unwrap();

        let result = import(candidate, "Dup OAuth", &existing, &store).unwrap();
        match result {
            ImportResult::CreatedWithWarning(_, warning) => {
                assert!(warning.contains("Old OAuth"));
                assert!(warning.contains("already exists"));
            }
            _ => panic!("expected CreatedWithWarning for duplicate blob"),
        }
    }

    #[test]
    fn normalize_blob_strips_volatile_fields() {
        let a = normalize_blob(r#"{"claudeAiOauth":{"accessToken":"x","expiresAt":1,"email":"u@x"}}"#);
        let b = normalize_blob(r#"{"claudeAiOauth":{"expiresAt":2,"accessToken":"y","email":"u@x"}}"#);
        // Volatile fields removed, stable field kept, key order canonical.
        assert_eq!(a, b);
        assert_eq!(
            a.as_deref(),
            Some(r#"{"claudeAiOauth":{"email":"u@x"}}"#)
        );
    }

    #[test]
    fn normalize_blob_returns_none_for_invalid_json() {
        assert_eq!(normalize_blob("not json"), None);
    }

    #[test]
    fn extract_identity_parses_email_from_blob() {
        let blob = r#"{"claudeAiOauth":{"email":"user@example.com","accessToken":"tok"}}"#;
        assert_eq!(
            extract_identity(blob),
            Some("user@example.com".to_string())
        );
    }

    #[test]
    fn extract_identity_parses_account_id_from_blob() {
        let blob = r#"{"claudeAiOauth":{"account_id":"acc-123","accessToken":"tok"}}"#;
        assert_eq!(extract_identity(blob), Some("acc-123".to_string()));
    }

    #[test]
    fn extract_identity_returns_none_for_malformed_blob() {
        assert_eq!(extract_identity("not json"), None);
        assert_eq!(extract_identity(r#"{"wrongKey":{}}"#), None);
        assert_eq!(extract_identity(r#"{"claudeAiOauth":{}}"#), None);
    }

    #[test]
    fn extract_identity_from_user_config_prefers_account_uuid() {
        let content = r#"{"userID":"u","oauthAccount":{"accountUuid":"acc-uuid","emailAddress":"u@x.com"}}"#;
        assert_eq!(
            extract_identity_from_user_config(content),
            Some("acc-uuid".to_string())
        );
    }

    #[test]
    fn extract_identity_from_user_config_falls_back_to_email() {
        let content = r#"{"oauthAccount":{"emailAddress":"u@x.com"}}"#;
        assert_eq!(
            extract_identity_from_user_config(content),
            Some("u@x.com".to_string())
        );
    }

    #[test]
    fn extract_identity_from_user_config_none_when_no_oauth_account() {
        assert_eq!(extract_identity_from_user_config(r#"{"userID":"u"}"#), None);
        assert_eq!(extract_identity_from_user_config("not json"), None);
    }

    #[test]
    fn detect_current_uses_user_config_for_oauth_identity() {
        // credential blob has no identity, but ~/.claude.json (oauthAccount)
        // provides a stable accountUuid used for dedup.
        let (temp, settings_path) = setup_settings(r#"{"env":{}}"#);
        let user_config = temp.path().join(".claude.json");
        fs::write(
            &user_config,
            r#"{"oauthAccount":{"accountUuid":"acc-uuid","emailAddress":"u@x.com"}}"#,
        )
        .unwrap();

        let creds = InMemoryCredentialStore::new();
        creds
            .write(r#"{"claudeAiOauth":{"accessToken":"a"}}"#)
            .unwrap();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: Some(user_config),
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        let result = detect_current(&[], &deps).unwrap();
        match result {
            Some(ImportCandidate::Oauth { identity, .. }) => {
                assert_eq!(identity, Some("acc-uuid".to_string()));
            }
            _ => panic!("expected OAuth candidate"),
        }

        drop(temp);
    }

    #[test]
    fn constant_managed_keys_not_used_for_ignore() {
        // Confirm that ONLY config.managed_keys is used, not the constant MANAGED_KEYS.
        // When managed_keys is empty, we should detect a token even though AUTH_TOKEN
        // is in the constant MANAGED_KEYS set.
        let (temp, settings_path) = setup_settings(
            r#"{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-manual","ANTHROPIC_BASE_URL":"https://api.anthropic.com"}}"#,
        );
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        // Empty managed_keys → should detect the token (constant NOT used for ignore).
        let result = detect_current(&[], &deps).unwrap();
        assert!(matches!(result, Some(ImportCandidate::Token { .. })));

        drop(temp);
    }

    #[test]
    fn config_managed_keys_is_used_for_ignore() {
        // When config.managed_keys includes AUTH_TOKEN, we should NOT detect it.
        let (temp, settings_path) = setup_settings(
            r#"{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-manual"}}"#,
        );
        let creds = InMemoryCredentialStore::new();

        let deps = ImportDeps {
            settings_path: Some(settings_path),
            user_config_path: None,
            credential_store: &creds,
            secret_store: &InMemorySecretStore::new(),
        };

        // managed_keys includes AUTH_TOKEN → should be ignored.
        let result = detect_current(&["ANTHROPIC_AUTH_TOKEN".to_string()], &deps).unwrap();
        assert!(result.is_none(), "managed AUTH_TOKEN should be ignored");

        drop(temp);
    }
}
