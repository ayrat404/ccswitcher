//! Resolution of the Claude Code config paths ccswitcher integrates with.
//!
//! ccswitcher edits Claude Code's own configuration:
//! - `~/.claude/settings.json` — owns an `env` object; ccswitcher manages only a
//!   known set of keys inside it (see [`super::settings_env`]).
//! - `~/.claude/.credentials.json` — the OAuth credential store on Windows/Linux
//!   (macOS uses the Keychain instead; see the credential-store adapter).
//!
//! The base `.claude` directory can be overridden so tests never touch the real
//! user directory.

use std::path::{Path, PathBuf};

use thiserror::Error;

/// Name of the Claude Code base directory inside the user's home.
pub const CLAUDE_DIR_NAME: &str = ".claude";

/// File name of the Claude Code settings file.
pub const SETTINGS_FILE_NAME: &str = "settings.json";

/// File name of the Claude Code credential store (Windows/Linux).
pub const CREDENTIALS_FILE_NAME: &str = ".credentials.json";

/// File name of Claude Code's user-level config (lives in the home dir, not
/// under `~/.claude/`). Holds stable account identifiers (`oauthAccount`:
/// `accountUuid`, `emailAddress`) used for duplicate detection on import.
pub const USER_CONFIG_FILE_NAME: &str = ".claude.json";

/// Errors raised while resolving Claude Code paths.
#[derive(Debug, Error)]
pub enum ClaudePathError {
    /// The user's home directory could not be resolved.
    #[error("could not resolve the user home directory")]
    NoHomeDir,
}

/// Resolve the Claude Code base directory (`~/.claude`) via the `dirs` crate.
pub fn claude_dir() -> Result<PathBuf, ClaudePathError> {
    let home = dirs::home_dir().ok_or(ClaudePathError::NoHomeDir)?;
    Ok(home.join(CLAUDE_DIR_NAME))
}

/// Path to `settings.json` inside the default `~/.claude` directory.
pub fn settings_path() -> Result<PathBuf, ClaudePathError> {
    Ok(settings_path_in(claude_dir()?))
}

/// Path to `.credentials.json` inside the default `~/.claude` directory.
pub fn credentials_path() -> Result<PathBuf, ClaudePathError> {
    Ok(credentials_path_in(claude_dir()?))
}

/// Path to the user-level `~/.claude.json` config (in the home dir, not under
/// `~/.claude/`). Holds stable account identity fields used for dedup on import.
pub fn user_config_path() -> Result<PathBuf, ClaudePathError> {
    let home = dirs::home_dir().ok_or(ClaudePathError::NoHomeDir)?;
    Ok(home.join(USER_CONFIG_FILE_NAME))
}

/// Path to `settings.json` inside the given `.claude` base directory.
///
/// Taking the base dir as a parameter keeps the path logic testable without
/// touching the real home directory.
pub fn settings_path_in(base: impl AsRef<Path>) -> PathBuf {
    base.as_ref().join(SETTINGS_FILE_NAME)
}

/// Path to `.credentials.json` inside the given `.claude` base directory.
pub fn credentials_path_in(base: impl AsRef<Path>) -> PathBuf {
    base.as_ref().join(CREDENTIALS_FILE_NAME)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_path_in_joins_file_name() {
        let base = Path::new("/tmp/fake-claude");
        assert_eq!(
            settings_path_in(base),
            PathBuf::from("/tmp/fake-claude").join("settings.json")
        );
    }

    #[test]
    fn credentials_path_in_joins_file_name() {
        let base = Path::new("/tmp/fake-claude");
        assert_eq!(
            credentials_path_in(base),
            PathBuf::from("/tmp/fake-claude").join(".credentials.json")
        );
    }

    #[test]
    fn default_paths_live_under_claude_dir() {
        // These only succeed when a home dir is resolvable, which it is in the
        // test environment. They assert the expected file names are appended.
        if let Ok(p) = settings_path() {
            assert!(p.ends_with("settings.json"));
            assert!(p.parent().unwrap().ends_with(".claude"));
        }
        if let Ok(p) = credentials_path() {
            assert!(p.ends_with(".credentials.json"));
            assert!(p.parent().unwrap().ends_with(".claude"));
        }
    }
}
