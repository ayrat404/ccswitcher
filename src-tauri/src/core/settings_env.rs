//! The `settings.json` env-merge engine.
//!
//! ccswitcher edits only the `env` object inside Claude Code's `settings.json`,
//! and only a known set of *managed keys* within it. Everything else — the
//! user's own env keys and all non-`env` settings (permissions, mcp, …) — is
//! preserved untouched. This module loads `settings.json` defensively (never
//! overwriting an invalid file) and performs the merge that a switch applies.
//!
//! ## Merge semantics (see plan "Switching flow")
//! On every switch the engine strips the **union** of the constant
//! [`MANAGED_KEYS`] set and the caller-supplied `old_managed_keys` (the latter
//! covers prior `extra_env` keys and survives a stale/empty stored list), then
//! inserts the freshly-built `new_env`. The returned `new_managed_keys` is the
//! exact set of keys this app just wrote, to be persisted for the next switch.

use std::collections::BTreeMap;
use std::io;
use std::path::Path;

use serde_json::{Map, Value};
use thiserror::Error;

/// The constant set of env keys ccswitcher always owns inside `settings.json`.
///
/// On top of these, an account's `extra_env` keys are also managed; those are
/// passed in via `old_managed_keys`/`new_env` rather than living in this set.
pub const MANAGED_KEYS: &[&str] = &[
    "ANTHROPIC_BASE_URL",
    "ANTHROPIC_AUTH_TOKEN",
    "ANTHROPIC_API_KEY",
    "HTTP_PROXY",
    "HTTPS_PROXY",
    "NO_PROXY",
];

/// Errors raised while loading `settings.json`.
#[derive(Debug, Error)]
pub enum SettingsEnvError {
    /// Reading the settings file failed.
    #[error("settings.json I/O error: {0}")]
    Io(#[from] io::Error),
    /// The file exists but is not valid JSON. The file is left untouched; the
    /// caller must abort the switch rather than overwrite it.
    #[error("settings.json is not valid JSON: {0}")]
    InvalidJson(serde_json::Error),
    /// The file is valid JSON but its top level is not an object.
    #[error("settings.json top level is not a JSON object")]
    NotAnObject,
}

/// Load Claude Code's `settings.json`.
///
/// - Missing file → an empty JSON object `{}` (a first run starts clean).
/// - Present but invalid JSON → [`SettingsEnvError::InvalidJson`]; the file is
///   **not** modified. The caller must abort rather than clobber user data.
/// - Present, valid JSON, but not an object → [`SettingsEnvError::NotAnObject`].
pub fn load_settings(path: impl AsRef<Path>) -> Result<Value, SettingsEnvError> {
    let path = path.as_ref();
    if !path.exists() {
        return Ok(Value::Object(Map::new()));
    }
    let bytes = std::fs::read(path)?;
    let value: Value = serde_json::from_slice(&bytes).map_err(SettingsEnvError::InvalidJson)?;
    if !value.is_object() {
        return Err(SettingsEnvError::NotAnObject);
    }
    Ok(value)
}

/// Merge the app-managed env into `settings`.
///
/// Operates on `settings["env"]` (created if absent):
/// 1. Strips the **union** of [`MANAGED_KEYS`] and `old_managed_keys` from `env`
///    (so stale managed keys are removed even when `old_managed_keys` is empty,
///    and prior `extra_env` keys named in `old_managed_keys` are cleaned up).
/// 2. Inserts every entry from `new_env`.
/// 3. Preserves all other `env` keys and every non-`env` setting.
///
/// Returns the updated `settings` and the `new_managed_keys` list — exactly the
/// keys this call wrote (the keys of `new_env`), for the caller to persist.
pub fn merge_env(
    mut settings: Value,
    old_managed_keys: &[String],
    new_env: &BTreeMap<String, String>,
) -> (Value, Vec<String>) {
    // Ensure the top level is an object with an `env` object we can edit.
    let root = match settings.as_object_mut() {
        Some(map) => map,
        None => {
            // Non-object input: replace with a fresh object. (load_settings
            // rejects non-objects, so this is just a defensive fallback.)
            settings = Value::Object(Map::new());
            settings.as_object_mut().expect("just set to object")
        }
    };

    let env = root
        .entry("env")
        .or_insert_with(|| Value::Object(Map::new()));

    // If `env` exists but isn't an object, reset it to an empty object so we
    // don't carry forward malformed data.
    if !env.is_object() {
        *env = Value::Object(Map::new());
    }
    let env_map = env.as_object_mut().expect("env is an object");

    // Strip the union of constant managed keys and the previously-written ones.
    for key in MANAGED_KEYS {
        env_map.remove(*key);
    }
    for key in old_managed_keys {
        env_map.remove(key);
    }

    // Insert the freshly-built env.
    for (key, val) in new_env {
        env_map.insert(key.clone(), Value::String(val.clone()));
    }

    let new_managed_keys: Vec<String> = new_env.keys().cloned().collect();
    (settings, new_managed_keys)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn env_of(settings: &Value) -> &Map<String, Value> {
        settings
            .get("env")
            .and_then(Value::as_object)
            .expect("env object present")
    }

    fn new_env(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs
            .iter()
            .map(|(k, v)| (k.to_string(), v.to_string()))
            .collect()
    }

    #[test]
    fn load_missing_returns_empty_object() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        let v = load_settings(&path).unwrap();
        assert_eq!(v, json!({}));
        // Loading must not create the file.
        assert!(!path.exists());
    }

    #[test]
    fn load_valid_settings_returns_value() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        std::fs::write(&path, br#"{"env":{"FOO":"bar"},"permissions":{}}"#).unwrap();
        let v = load_settings(&path).unwrap();
        assert_eq!(v["env"]["FOO"], json!("bar"));
        assert!(v.get("permissions").is_some());
    }

    #[test]
    fn load_invalid_json_returns_error_and_leaves_file_unmodified() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        let original = b"{ not valid json";
        std::fs::write(&path, original).unwrap();

        let err = load_settings(&path).unwrap_err();
        assert!(matches!(err, SettingsEnvError::InvalidJson(_)));
        // The invalid file must be left byte-for-byte untouched.
        assert_eq!(std::fs::read(&path).unwrap(), original);
    }

    #[test]
    fn load_non_object_top_level_returns_error() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        std::fs::write(&path, b"[1,2,3]").unwrap();
        let err = load_settings(&path).unwrap_err();
        assert!(matches!(err, SettingsEnvError::NotAnObject));
    }

    #[test]
    fn user_set_env_key_survives_switch() {
        let settings = json!({ "env": { "MY_OWN_KEY": "keep-me" } });
        let env = new_env(&[("ANTHROPIC_AUTH_TOKEN", "tok")]);
        let (merged, _) = merge_env(settings, &[], &env);
        let e = env_of(&merged);
        // User's own key is preserved...
        assert_eq!(e["MY_OWN_KEY"], json!("keep-me"));
        // ...and the new managed key is written.
        assert_eq!(e["ANTHROPIC_AUTH_TOKEN"], json!("tok"));
    }

    #[test]
    fn old_managed_key_removed_when_not_in_new_set() {
        // Previous switch wrote a custom extra_env key tracked in managed_keys.
        let settings = json!({ "env": { "CUSTOM_PROXY_VAR": "old", "MY_OWN_KEY": "keep" } });
        let old = vec!["CUSTOM_PROXY_VAR".to_string()];
        let env = new_env(&[("ANTHROPIC_AUTH_TOKEN", "tok")]);
        let (merged, new_keys) = merge_env(settings, &old, &env);
        let e = env_of(&merged);
        // The previously-managed extra key is gone (not in new set).
        assert!(!e.contains_key("CUSTOM_PROXY_VAR"));
        // User key untouched.
        assert_eq!(e["MY_OWN_KEY"], json!("keep"));
        assert_eq!(new_keys, vec!["ANTHROPIC_AUTH_TOKEN".to_string()]);
    }

    #[test]
    fn stale_managed_key_removed_even_when_old_keys_empty() {
        // First-switch robustness: a leftover ANTHROPIC_API_KEY must be stripped
        // purely on the strength of the constant MANAGED_KEYS set, with an
        // empty old_managed_keys list.
        let settings = json!({ "env": { "ANTHROPIC_API_KEY": "stale", "MY_OWN_KEY": "keep" } });
        let env = new_env(&[("ANTHROPIC_AUTH_TOKEN", "tok")]);
        let (merged, _) = merge_env(settings, &[], &env);
        let e = env_of(&merged);
        assert!(!e.contains_key("ANTHROPIC_API_KEY"));
        assert_eq!(e["ANTHROPIC_AUTH_TOKEN"], json!("tok"));
        assert_eq!(e["MY_OWN_KEY"], json!("keep"));
    }

    #[test]
    fn non_env_settings_untouched() {
        let settings = json!({
            "env": { "ANTHROPIC_API_KEY": "old" },
            "permissions": { "allow": ["Bash"] },
            "mcpServers": { "foo": { "command": "bar" } }
        });
        let env = new_env(&[("ANTHROPIC_AUTH_TOKEN", "tok")]);
        let (merged, _) = merge_env(settings, &[], &env);
        assert_eq!(merged["permissions"], json!({ "allow": ["Bash"] }));
        assert_eq!(merged["mcpServers"], json!({ "foo": { "command": "bar" } }));
    }

    #[test]
    fn creates_env_object_when_absent() {
        let settings = json!({ "permissions": {} });
        let env = new_env(&[("ANTHROPIC_BASE_URL", "https://api.anthropic.com")]);
        let (merged, new_keys) = merge_env(settings, &[], &env);
        let e = env_of(&merged);
        assert_eq!(e["ANTHROPIC_BASE_URL"], json!("https://api.anthropic.com"));
        assert_eq!(merged["permissions"], json!({}));
        assert_eq!(new_keys, vec!["ANTHROPIC_BASE_URL".to_string()]);
    }

    #[test]
    fn new_managed_keys_includes_extra_env_keys() {
        let env = new_env(&[("ANTHROPIC_AUTH_TOKEN", "tok"), ("FOO", "bar")]);
        let (_, new_keys) = merge_env(json!({}), &[], &env);
        // BTreeMap iteration is sorted, so the order is deterministic.
        assert_eq!(
            new_keys,
            vec!["ANTHROPIC_AUTH_TOKEN".to_string(), "FOO".to_string()]
        );
    }
}
