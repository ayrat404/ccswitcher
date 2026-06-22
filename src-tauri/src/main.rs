// Prevent an extra console window on Windows in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! ccswitcher binary entry point.
//!
//! Initializes the application state (paths, stores, config) and registers the
//! Tauri commands. The runtime shell is minimal — all behaviour lives in the
//! core library and the command layer.

use std::path::PathBuf;
use std::sync::Arc;

use tauri::Manager;

use ccswitcher_lib::core::claude_paths::settings_path;
use ccswitcher_lib::core::config_store::{ConfigStore, ConfigStoreError};
use ccswitcher_lib::core::credential_store::default_credential_store;
use ccswitcher_lib::core::model::AppConfig;
use ccswitcher_lib::core::secret_store::KeyringSecretStore;
use ccswitcher_lib::commands::AppState;

/// Resolve the ccswitcher config directory.
///
/// Returns an error if the platform config dir cannot be resolved (very rare).
fn resolve_config_dir() -> Result<PathBuf, ConfigStoreError> {
    ConfigStore::app_config_dir()
}

/// Initialize the application state on startup.
///
/// This function runs once before the Tauri app starts. It:
/// 1. Resolves the config directory and creates it if missing.
/// 2. Resolves Claude Code's settings path.
/// 3. Creates the keyring-backed secret store.
/// 4. Creates the platform-default credential store.
/// 5. Loads (or creates) the AppConfig.
/// 6. Wraps everything in an AppState behind an async mutex.
///
/// Returns a tuple of (AppState, AppConfig). The AppConfig is returned
/// separately so the caller can inspect the initial state without holding the
/// mutex.
fn init_app_state() -> Result<(AppState, AppConfig), Box<dyn std::error::Error>> {
    // Resolve paths.
    let config_dir = resolve_config_dir()?;
    std::fs::create_dir_all(&config_dir)?;

    let settings_path = settings_path()?;

    // Create stores.
    let secret_store = Arc::new(KeyringSecretStore::new());
    let credential_store = default_credential_store()?;

    // Load config (creates default if missing).
    let config = ConfigStore::load(&config_dir)?;

    // Wrap in AppState with async mutex.
    let app_state = AppState {
        config_dir: config_dir.clone(),
        settings_path,
        secret_store,
        credential_store,
        mutex: Arc::new(tokio::sync::Mutex::new(config.clone())),
    };

    Ok((app_state, config))
}

fn main() {
    // Initialize app state before starting Tauri.
    let (app_state, _initial_config) = init_app_state().expect("failed to initialize app state");

    // Build and run the Tauri app.
    tauri::Builder::default()
        .manage(app_state)
        .invoke_handler(tauri::generate_handler![
            ccswitcher_lib::commands::list_accounts,
            ccswitcher_lib::commands::switch_account,
            ccswitcher_lib::commands::set_proxy_enabled,
            ccswitcher_lib::commands::get_proxy,
            ccswitcher_lib::commands::add_token_account,
            ccswitcher_lib::commands::update_account,
            ccswitcher_lib::commands::delete_account,
            ccswitcher_lib::commands::import_current,
            ccswitcher_lib::commands::get_state,
        ])
        .setup(|app| {
            // TODO: Task 12 will set up the tray menu here.
            // TODO: Task 14 will set up notification permissions here.
            #[cfg(debug_assertions)]
            {
                let window = app.get_webview_window("main").unwrap();
                window.open_devtools();
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running ccswitcher");
}
