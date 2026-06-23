//! Core data model for ccswitcher.
//!
//! These types describe the **non-secret** persisted state (`config.json`).
//! Secrets (token strings, OAuth credential snapshots) never live here; they
//! are stored in the OS keyring and referenced by account `id`.
//!
//! The serde shapes deliberately match the documented `config.json` layout:
//! - [`AccountType`] serializes as `"anthropic_oauth"` / `"token"`.
//! - [`AuthKind`] serializes as `"auth_token"` / `"api_key"`.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

/// Kind of account ccswitcher manages.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AccountType {
    /// Native Anthropic OAuth login (subscription). Restores a credential
    /// snapshot and writes no env token (but may carry its own `base_url`).
    AnthropicOauth,
    /// Token account (API key / third-party provider). Writes an env token
    /// override (`ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_API_KEY`).
    Token,
}

/// Which env variable a `token` account writes its secret into.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AuthKind {
    /// Write the secret into `ANTHROPIC_AUTH_TOKEN`.
    AuthToken,
    /// Write the secret into `ANTHROPIC_API_KEY`.
    ApiKey,
}

/// A single managed account. Non-secret metadata only.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Account {
    /// Stable unique id (UUID). Also the keyring entry name suffix.
    pub id: String,
    /// User-facing display name.
    pub name: String,
    /// Account kind.
    #[serde(rename = "type")]
    pub account_type: AccountType,
    /// Optional `ANTHROPIC_BASE_URL`. Valid for BOTH account types — a native
    /// OAuth login may legitimately need its own base url.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub base_url: Option<String>,
    /// Token-only: whether the secret is an auth token or an api key.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub auth_kind: Option<AuthKind>,
    /// OAuth-only, optional: a stable identity (email / account id) used to
    /// deduplicate imports. Raw credential blobs change on refresh and must not
    /// be used for dedup.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub identity: Option<String>,
    /// Extra environment variables applied on switch.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extra_env: BTreeMap<String, String>,
}

/// Global single HTTP proxy toggle.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProxySettings {
    /// Whether the proxy keys are written into the active account's env.
    pub enabled: bool,
    /// Proxy URL used for `HTTP_PROXY` / `HTTPS_PROXY`.
    pub url: String,
    /// Value used for `NO_PROXY`.
    pub no_proxy: String,
}

impl Default for ProxySettings {
    fn default() -> Self {
        ProxySettings {
            enabled: false,
            url: "http://127.0.0.1:8080".to_string(),
            no_proxy: "localhost,127.0.0.1".to_string(),
        }
    }
}

/// Current `config.json` schema version.
pub const SCHEMA_VERSION: u32 = 1;

/// Root persisted, non-secret application config.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AppConfig {
    /// Schema version for forward/backward migration.
    #[serde(default = "default_schema_version")]
    pub schema_version: u32,
    /// Currently active account id, or `None` if no account is active.
    #[serde(default)]
    pub active_account_id: Option<String>,
    /// Global proxy settings.
    #[serde(default)]
    pub proxy: ProxySettings,
    /// The set of env keys last written by ccswitcher into `settings.json`'s
    /// `env`. Used to robustly strip prior managed/extra keys on the next switch.
    #[serde(default)]
    pub managed_keys: Vec<String>,
    /// All known accounts.
    #[serde(default)]
    pub accounts: Vec<Account>,
}

fn default_schema_version() -> u32 {
    SCHEMA_VERSION
}

impl Default for AppConfig {
    fn default() -> Self {
        AppConfig {
            schema_version: SCHEMA_VERSION,
            active_account_id: None,
            proxy: ProxySettings::default(),
            managed_keys: Vec::new(),
            accounts: Vec::new(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn token_account() -> Account {
        let mut extra = BTreeMap::new();
        extra.insert("FOO".to_string(), "bar".to_string());
        Account {
            id: "tok-id".to_string(),
            name: "Work".to_string(),
            account_type: AccountType::Token,
            base_url: Some("https://proxy.example.com".to_string()),
            auth_kind: Some(AuthKind::AuthToken),
            identity: None,
            extra_env: extra,
        }
    }

    fn oauth_account() -> Account {
        Account {
            id: "oauth-id".to_string(),
            name: "Personal".to_string(),
            account_type: AccountType::AnthropicOauth,
            base_url: Some("https://api.anthropic.com".to_string()),
            auth_kind: None,
            identity: Some("user@example.com".to_string()),
            extra_env: BTreeMap::new(),
        }
    }

    #[test]
    fn default_app_config() {
        let cfg = AppConfig::default();
        assert_eq!(cfg.schema_version, SCHEMA_VERSION);
        assert_eq!(cfg.active_account_id, None);
        assert!(!cfg.proxy.enabled);
        assert_eq!(cfg.proxy.url, "http://127.0.0.1:8080");
        assert_eq!(cfg.proxy.no_proxy, "localhost,127.0.0.1");
        assert!(cfg.managed_keys.is_empty());
        assert!(cfg.accounts.is_empty());
    }

    #[test]
    fn account_type_serializes_as_documented() {
        assert_eq!(
            serde_json::to_value(AccountType::AnthropicOauth).unwrap(),
            serde_json::json!("anthropic_oauth")
        );
        assert_eq!(
            serde_json::to_value(AccountType::Token).unwrap(),
            serde_json::json!("token")
        );
    }

    #[test]
    fn auth_kind_serializes_as_documented() {
        assert_eq!(
            serde_json::to_value(AuthKind::AuthToken).unwrap(),
            serde_json::json!("auth_token")
        );
        assert_eq!(
            serde_json::to_value(AuthKind::ApiKey).unwrap(),
            serde_json::json!("api_key")
        );
    }

    #[test]
    fn account_uses_type_rename_in_json() {
        let acc = token_account();
        let v = serde_json::to_value(&acc).unwrap();
        // `account_type` field is renamed to `type` in JSON.
        assert_eq!(v["type"], serde_json::json!("token"));
        assert_eq!(v["auth_kind"], serde_json::json!("auth_token"));
        assert!(v.get("account_type").is_none());
    }

    #[test]
    fn app_config_round_trip_with_both_account_types() {
        let cfg = AppConfig {
            schema_version: SCHEMA_VERSION,
            active_account_id: Some("oauth-id".to_string()),
            proxy: ProxySettings {
                enabled: true,
                url: "http://localhost:9000".to_string(),
                no_proxy: "localhost".to_string(),
            },
            managed_keys: vec![
                "ANTHROPIC_BASE_URL".to_string(),
                "ANTHROPIC_AUTH_TOKEN".to_string(),
            ],
            accounts: vec![token_account(), oauth_account()],
        };

        let json = serde_json::to_string(&cfg).unwrap();
        let back: AppConfig = serde_json::from_str(&json).unwrap();
        assert_eq!(cfg, back);
    }

    #[test]
    fn deserialize_from_documented_shape() {
        let raw = r#"{
            "schema_version": 1,
            "active_account_id": "uuid-1",
            "proxy": {
                "enabled": false,
                "url": "http://127.0.0.1:8080",
                "no_proxy": "localhost,127.0.0.1"
            },
            "managed_keys": ["ANTHROPIC_BASE_URL"],
            "accounts": [
                {
                    "id": "uuid-1",
                    "name": "Work",
                    "type": "anthropic_oauth",
                    "base_url": "https://api.anthropic.com",
                    "identity": "user@example.com",
                    "extra_env": { "FOO": "bar" }
                },
                {
                    "id": "uuid-2",
                    "name": "Provider",
                    "type": "token",
                    "auth_kind": "api_key"
                }
            ]
        }"#;

        let cfg: AppConfig = serde_json::from_str(raw).unwrap();
        assert_eq!(cfg.accounts.len(), 2);

        let oauth = &cfg.accounts[0];
        assert_eq!(oauth.account_type, AccountType::AnthropicOauth);
        assert_eq!(oauth.base_url.as_deref(), Some("https://api.anthropic.com"));
        assert_eq!(oauth.identity.as_deref(), Some("user@example.com"));
        assert_eq!(oauth.auth_kind, None);
        assert_eq!(oauth.extra_env.get("FOO").map(String::as_str), Some("bar"));

        let token = &cfg.accounts[1];
        assert_eq!(token.account_type, AccountType::Token);
        assert_eq!(token.auth_kind, Some(AuthKind::ApiKey));
        assert_eq!(token.base_url, None);
        assert_eq!(token.identity, None);
        assert!(token.extra_env.is_empty());
    }

    #[test]
    fn missing_optional_fields_use_defaults() {
        // Minimal config: only required-ish fields present, rest default.
        let raw = r#"{ "accounts": [] }"#;
        let cfg: AppConfig = serde_json::from_str(raw).unwrap();
        assert_eq!(cfg.schema_version, SCHEMA_VERSION);
        assert_eq!(cfg.active_account_id, None);
        assert!(!cfg.proxy.enabled);
        assert!(cfg.managed_keys.is_empty());
    }
}
