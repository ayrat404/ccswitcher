// Prevent an extra console window on Windows in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! ccswitcher binary entry point.
//!
//! Initializes the application state (paths, stores, config), creates the
//! tray menu, and registers the Tauri commands and event handlers.

use std::path::PathBuf;
use std::sync::Arc;

use tauri::{Emitter, Listener, Manager};

use ccswitcher_lib::core::claude_paths::settings_path;
use ccswitcher_lib::core::config_store::{ConfigStore, ConfigStoreError};
use ccswitcher_lib::core::credential_store::default_credential_store;
use ccswitcher_lib::core::model::AppConfig;
use ccswitcher_lib::core::secret_store::KeyringSecretStore;
use ccswitcher_lib::commands::AppState;
use ccswitcher_lib::tray::menu_ids;

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

/// Handle a tray menu event.
///
/// This function is called when a user clicks an item in the tray menu.
/// It dispatches the appropriate action based on the menu item ID.
///
/// For actions that need to invoke commands, we emit events that will be
/// handled by listeners that run in the async context.
fn handle_tray_menu_event<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    id: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    match id {
        menu_ids::QUIT => {
            // Exit the app
            app.exit(0);
            Ok(())
        }
        menu_ids::SETTINGS => {
            // Open the settings window
            if let Some(settings_window) = app.get_webview_window("settings") {
                settings_window.show()?;
                settings_window.set_focus()?;
            }
            Ok(())
        }
        menu_ids::IMPORT => {
            // Emit an event for the import flow
            app.emit("tray_import", ())?;
            Ok(())
        }
        menu_ids::PROXY_TOGGLE => {
            // Emit an event to toggle the proxy
            app.emit("tray_toggle_proxy", ())?;
            Ok(())
        }
        id if id.starts_with(menu_ids::ACCOUNT_PREFIX) => {
            // Extract account ID from the menu item ID
            let account_id = id[menu_ids::ACCOUNT_PREFIX.len()..].to_string();

            // Emit an event to switch accounts
            app.emit("tray_switch_account", account_id)?;
            Ok(())
        }
        _ => Ok(()),
    }
}

fn main() {
    // Initialize app state before starting Tauri.
    let (app_state, initial_config) =
        init_app_state().expect("failed to initialize app state");

    // Clone the config for tray creation (we'll need it in the setup closure)
    let config_for_tray = initial_config.clone();

    tauri::Builder::default()
        .manage(app_state)
        .invoke_handler(tauri::generate_handler![
            ccswitcher_lib::commands::list_accounts,
            ccswitcher_lib::commands::switch_account,
            ccswitcher_lib::commands::set_proxy_enabled,
            ccswitcher_lib::commands::set_proxy,
            ccswitcher_lib::commands::get_proxy,
            ccswitcher_lib::commands::add_token_account,
            ccswitcher_lib::commands::update_account,
            ccswitcher_lib::commands::delete_account,
            ccswitcher_lib::commands::import_current,
            ccswitcher_lib::commands::get_state,
        ])
        .setup(move |app| {
            // Create the initial tray menu
            let app_handle = app.handle();
            if let Err(e) = ccswitcher_lib::tray::create_tray(app_handle, &config_for_tray) {
                eprintln!("Failed to create tray icon: {}", e);
                // Don't fail the app startup if tray creation fails
                // (e.g., in headless environments)
            }

            // Register a global menu event listener for the tray
            let app_handle = app.handle().clone();
            app.on_menu_event(move |_app, event| {
                if let Err(e) = handle_tray_menu_event(&app_handle, event.id().as_ref()) {
                    eprintln!("Failed to handle menu event: {}", e);
                }
            });

            // Listen for tray_toggle_proxy event and invoke the command
            let app_handle = app.handle().clone();
            app.listen("tray_toggle_proxy", move |_event| {
                let handle = app_handle.clone();
                tauri::async_runtime::spawn(async move {
                    let state = handle.state::<AppState>();

                    // Get current proxy state and compute the new enabled flag
                    let new_enabled = {
                        let config = state.mutex.lock().await;
                        !config.proxy.enabled
                    };

                    let result =
                        ccswitcher_lib::commands::set_proxy_enabled(new_enabled, handle.clone(), state.clone()).await;

                    if let Err(e) = result {
                        eprintln!("Failed to toggle proxy: {:?}", e);
                    } else {
                        // Refresh the tray menu with the updated config
                        let config = state.mutex.lock().await.clone();
                        let _ = ccswitcher_lib::tray::update_tray_icon(&handle, &config);
                    }
                });
            });

            // Listen for tray_switch_account event and invoke the command.
            // The payload is a JSON-serialized string (e.g. "\"uuid\""), so we
            // must deserialize it — using event.payload().to_string() verbatim
            // would include the surrounding quotes and the account would never
            // be found.
            let app_handle = app.handle().clone();
            app.listen("tray_switch_account", move |event| {
                let payload = event.payload();
                let account_id = serde_json::from_str::<String>(payload)
                    .unwrap_or_else(|_| payload.to_string());
                let handle = app_handle.clone();

                tauri::async_runtime::spawn(async move {
                    let state = handle.state::<AppState>();

                    let result =
                        ccswitcher_lib::commands::switch_account(account_id, handle.clone(), state.clone()).await;

                    if let Err(e) = result {
                        eprintln!("Failed to switch account: {:?}", e);
                    } else {
                        // Refresh the tray menu with the updated config
                        let config = state.mutex.lock().await.clone();
                        let _ = ccswitcher_lib::tray::update_tray_icon(&handle, &config);
                    }
                });
            });

            // Listen for tray_refresh: rebuild the tray menu after account
            // list changes (add/update/delete/import) emitted by the commands.
            let app_handle = app.handle().clone();
            app.listen("tray_refresh", move |_event| {
                let handle = app_handle.clone();
                tauri::async_runtime::spawn(async move {
                    let state = handle.state::<AppState>();
                    let config = state.mutex.lock().await.clone();
                    let _ = ccswitcher_lib::tray::update_tray_icon(&handle, &config);
                });
            });

            // Listen for tray_import: show the settings window and ask its UI
            // to open the "import current login" name dialog. The actual import
            // command (which needs a profile name) is invoked from the frontend
            // after the user confirms the name.
            let app_handle = app.handle().clone();
            app.listen("tray_import", move |_event| {
                if let Some(settings_window) = app_handle.get_webview_window("settings") {
                    let _ = settings_window.show();
                    let _ = settings_window.set_focus();
                    // Tell the settings UI to open the import name dialog.
                    let _ = app_handle.emit_to("settings", "show_import_dialog", ());
                }
            });

            #[cfg(debug_assertions)]
            {
                if let Some(window) = app.get_webview_window("main") {
                    window.open_devtools();
                }
            }
            Ok(())
        })
        // Keep the settings window alive when the user closes it: hide instead
        // of destroy, so the app (driven by the tray) keeps running. Without
        // this, closing the settings window would exit the whole app.
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                if window.label() == "settings" {
                    let _ = window.hide();
                    api.prevent_close();
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running ccswitcher");
}
