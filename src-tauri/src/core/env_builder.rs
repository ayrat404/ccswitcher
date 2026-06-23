//! Per-account env builder.
//!
//! Given an [`Account`], its (optional) secret, and the global [`ProxySettings`],
//! this module produces the exact set of env key/value pairs ccswitcher will
//! inject into `settings.json`'s `env` object on a switch. It implements step 4
//! of the plan's "Switching flow".
//!
//! ## Semantics (see plan "Switching flow" step 4 and "Managed keys")
//! - **token** account: requires a non-empty secret (else [`EnvBuilderError::MissingSecret`]).
//!   Writes the secret into `ANTHROPIC_AUTH_TOKEN` (when [`AuthKind::AuthToken`],
//!   the default) or `ANTHROPIC_API_KEY` (when [`AuthKind::ApiKey`]). Writes
//!   `ANTHROPIC_BASE_URL` only when the account carries one.
//! - **anthropic_oauth** account: writes **no** token key; writes
//!   `ANTHROPIC_BASE_URL` only when the account carries one. The secret is not
//!   required (it is restored to the credential store, not the env).
//! - If `proxy.enabled`: adds `HTTP_PROXY`/`HTTPS_PROXY` (= `proxy.url`) and
//!   `NO_PROXY` (= `proxy.no_proxy`).
//! - The account's `extra_env` is always merged last (it may add arbitrary keys).
//!
//! A [`BTreeMap`] is returned for deterministic ordering.

use std::collections::BTreeMap;

use thiserror::Error;

use super::model::{Account, AccountType, AuthKind, ProxySettings};

/// Errors raised while building an account's env.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum EnvBuilderError {
    /// A `token` account had no secret (absent or empty). We never write an
    /// empty `ANTHROPIC_AUTH_TOKEN`/`ANTHROPIC_API_KEY`; the caller must abort.
    #[error("token account is missing its secret")]
    MissingSecret,
}

/// Build the env map ccswitcher will write for `account`.
///
/// See the module docs for full semantics. Returns
/// [`EnvBuilderError::MissingSecret`] (and no env) for a `token` account whose
/// `secret` is `None` or empty.
pub fn build_env(
    account: &Account,
    secret: Option<&str>,
    proxy: &ProxySettings,
) -> Result<BTreeMap<String, String>, EnvBuilderError> {
    let mut env: BTreeMap<String, String> = BTreeMap::new();

    match account.account_type {
        AccountType::Token => {
            let secret = secret.filter(|s| !s.is_empty());
            let secret = secret.ok_or(EnvBuilderError::MissingSecret)?;
            // Default to AuthToken when auth_kind is unset.
            let key = match account.auth_kind.unwrap_or(AuthKind::AuthToken) {
                AuthKind::AuthToken => "ANTHROPIC_AUTH_TOKEN",
                AuthKind::ApiKey => "ANTHROPIC_API_KEY",
            };
            env.insert(key.to_string(), secret.to_string());
        }
        AccountType::AnthropicOauth => {
            // No token key is written for OAuth accounts; secret (if any) is
            // restored to the credential store elsewhere.
        }
    }

    // base_url applies to BOTH account types, only when the account carries one.
    if let Some(base_url) = &account.base_url {
        env.insert("ANTHROPIC_BASE_URL".to_string(), base_url.clone());
    }

    if proxy.enabled {
        env.insert("HTTP_PROXY".to_string(), proxy.url.clone());
        env.insert("HTTPS_PROXY".to_string(), proxy.url.clone());
        env.insert("NO_PROXY".to_string(), proxy.no_proxy.clone());
    }

    // extra_env merged last; may add arbitrary keys (or override the above).
    for (k, v) in &account.extra_env {
        env.insert(k.clone(), v.clone());
    }

    Ok(env)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn token_account(auth_kind: Option<AuthKind>, base_url: Option<&str>) -> Account {
        Account {
            id: "tok-id".to_string(),
            name: "Work".to_string(),
            account_type: AccountType::Token,
            base_url: base_url.map(str::to_string),
            auth_kind,
            identity: None,
            extra_env: BTreeMap::new(),
        }
    }

    fn oauth_account(base_url: Option<&str>) -> Account {
        Account {
            id: "oauth-id".to_string(),
            name: "Personal".to_string(),
            account_type: AccountType::AnthropicOauth,
            base_url: base_url.map(str::to_string),
            auth_kind: None,
            identity: Some("user@example.com".to_string()),
            extra_env: BTreeMap::new(),
        }
    }

    fn proxy_off() -> ProxySettings {
        ProxySettings {
            enabled: false,
            url: "http://127.0.0.1:8080".to_string(),
            no_proxy: "localhost,127.0.0.1".to_string(),
        }
    }

    fn proxy_on() -> ProxySettings {
        ProxySettings {
            enabled: true,
            url: "http://127.0.0.1:8080".to_string(),
            no_proxy: "localhost,127.0.0.1".to_string(),
        }
    }

    #[test]
    fn token_auth_token_kind_without_base_url() {
        let acc = token_account(Some(AuthKind::AuthToken), None);
        let env = build_env(&acc, Some("sk-secret"), &proxy_off()).unwrap();
        assert_eq!(env.get("ANTHROPIC_AUTH_TOKEN").map(String::as_str), Some("sk-secret"));
        assert!(!env.contains_key("ANTHROPIC_API_KEY"));
        assert!(!env.contains_key("ANTHROPIC_BASE_URL"));
        assert_eq!(env.len(), 1);
    }

    #[test]
    fn token_auth_token_is_default_when_kind_absent() {
        let acc = token_account(None, None);
        let env = build_env(&acc, Some("sk-secret"), &proxy_off()).unwrap();
        assert_eq!(env.get("ANTHROPIC_AUTH_TOKEN").map(String::as_str), Some("sk-secret"));
        assert!(!env.contains_key("ANTHROPIC_API_KEY"));
    }

    #[test]
    fn token_api_key_kind_with_base_url() {
        let acc = token_account(Some(AuthKind::ApiKey), Some("https://proxy.example.com"));
        let env = build_env(&acc, Some("key-123"), &proxy_off()).unwrap();
        assert_eq!(env.get("ANTHROPIC_API_KEY").map(String::as_str), Some("key-123"));
        assert!(!env.contains_key("ANTHROPIC_AUTH_TOKEN"));
        assert_eq!(
            env.get("ANTHROPIC_BASE_URL").map(String::as_str),
            Some("https://proxy.example.com")
        );
        assert_eq!(env.len(), 2);
    }

    #[test]
    fn oauth_writes_no_token_key_and_no_base_url_when_absent() {
        let acc = oauth_account(None);
        let env = build_env(&acc, None, &proxy_off()).unwrap();
        assert!(!env.contains_key("ANTHROPIC_AUTH_TOKEN"));
        assert!(!env.contains_key("ANTHROPIC_API_KEY"));
        assert!(!env.contains_key("ANTHROPIC_BASE_URL"));
        assert!(env.is_empty());
    }

    #[test]
    fn oauth_writes_base_url_only_when_set() {
        let acc = oauth_account(Some("https://api.anthropic.com"));
        let env = build_env(&acc, None, &proxy_off()).unwrap();
        assert!(!env.contains_key("ANTHROPIC_AUTH_TOKEN"));
        assert!(!env.contains_key("ANTHROPIC_API_KEY"));
        assert_eq!(
            env.get("ANTHROPIC_BASE_URL").map(String::as_str),
            Some("https://api.anthropic.com")
        );
        assert_eq!(env.len(), 1);
    }

    #[test]
    fn oauth_ignores_a_provided_secret() {
        // Even if a secret is passed, OAuth never writes a token key.
        let acc = oauth_account(None);
        let env = build_env(&acc, Some("should-be-ignored"), &proxy_off()).unwrap();
        assert!(env.is_empty());
    }

    #[test]
    fn proxy_on_adds_three_keys() {
        let acc = token_account(Some(AuthKind::AuthToken), None);
        let env = build_env(&acc, Some("sk"), &proxy_on()).unwrap();
        assert_eq!(env.get("HTTP_PROXY").map(String::as_str), Some("http://127.0.0.1:8080"));
        assert_eq!(env.get("HTTPS_PROXY").map(String::as_str), Some("http://127.0.0.1:8080"));
        assert_eq!(env.get("NO_PROXY").map(String::as_str), Some("localhost,127.0.0.1"));
    }

    #[test]
    fn proxy_off_omits_proxy_keys() {
        let acc = token_account(Some(AuthKind::AuthToken), None);
        let env = build_env(&acc, Some("sk"), &proxy_off()).unwrap();
        assert!(!env.contains_key("HTTP_PROXY"));
        assert!(!env.contains_key("HTTPS_PROXY"));
        assert!(!env.contains_key("NO_PROXY"));
    }

    #[test]
    fn extra_env_is_merged() {
        let mut acc = token_account(Some(AuthKind::AuthToken), None);
        acc.extra_env.insert("FOO".to_string(), "bar".to_string());
        acc.extra_env.insert("BAZ".to_string(), "qux".to_string());
        let env = build_env(&acc, Some("sk"), &proxy_off()).unwrap();
        assert_eq!(env.get("FOO").map(String::as_str), Some("bar"));
        assert_eq!(env.get("BAZ").map(String::as_str), Some("qux"));
        assert_eq!(env.get("ANTHROPIC_AUTH_TOKEN").map(String::as_str), Some("sk"));
    }

    #[test]
    fn extra_env_merged_for_oauth_too() {
        let mut acc = oauth_account(None);
        acc.extra_env.insert("CUSTOM".to_string(), "value".to_string());
        let env = build_env(&acc, None, &proxy_off()).unwrap();
        assert_eq!(env.get("CUSTOM").map(String::as_str), Some("value"));
        assert_eq!(env.len(), 1);
    }

    #[test]
    fn token_missing_secret_is_typed_error() {
        let acc = token_account(Some(AuthKind::AuthToken), None);
        let err = build_env(&acc, None, &proxy_off()).unwrap_err();
        assert_eq!(err, EnvBuilderError::MissingSecret);
    }

    #[test]
    fn token_empty_secret_is_typed_error() {
        let acc = token_account(Some(AuthKind::ApiKey), None);
        let err = build_env(&acc, Some(""), &proxy_off()).unwrap_err();
        assert_eq!(err, EnvBuilderError::MissingSecret);
    }
}
