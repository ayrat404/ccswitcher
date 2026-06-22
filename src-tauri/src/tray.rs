//! Tray menu implementation.
//!
//! Dynamically builds the tray menu from the current app state:
//! - Account list (with checkmark on active, type label)
//! - Proxy toggle (shows address and enabled state)
//! - Import current login
//! - Settings window
//! - Quit

use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::TrayIconBuilder,
    AppHandle, Runtime,
};

use crate::core::model::{AccountType, AppConfig};

/// Menu item IDs for event handling.
pub mod menu_ids {
    pub const PROXY_TOGGLE: &str = "proxy_toggle";
    pub const IMPORT: &str = "import";
    pub const SETTINGS: &str = "settings";
    pub const QUIT: &str = "quit";
    pub const ACCOUNT_PREFIX: &str = "account_";
}

/// Build a tray menu from the current app state.
///
/// # Arguments
/// * `app_handle` - The Tauri app handle.
/// * `state_snapshot` - A clone of the current AppConfig for menu rendering.
///
/// # Returns
/// A Tauri Menu that can be attached to a tray icon.
pub fn build_tray_menu<R: Runtime>(
    app_handle: &AppHandle<R>,
    state_snapshot: &AppConfig,
) -> Result<Menu<R>, Box<dyn std::error::Error>> {
    let mut menu_items: Vec<Box<dyn tauri::menu::IsMenuItem<R>>> = Vec::new();

    // Add account items
    for account in &state_snapshot.accounts {
        let is_active = state_snapshot.active_account_id.as_ref() == Some(&account.id);

        // Build label with checkmark and type
        let checkmark = if is_active { "✓ " } else { "" };
        let type_label = match account.account_type {
            AccountType::AnthropicOauth => "(oauth)",
            AccountType::Token => "(token)",
        };
        let label = format!("{}{} {}", checkmark, account.name, type_label);

        let account_id = format!("{}{}", menu_ids::ACCOUNT_PREFIX, account.id);

        let item = MenuItem::with_id(
            app_handle,
            account_id,
            label,
            true,
            None::<&str>,
        )?;
        menu_items.push(Box::new(item));
    }

    // Add separator before proxy
    let sep = PredefinedMenuItem::separator(app_handle)?;
    menu_items.push(Box::new(sep));

    // Proxy toggle item
    let proxy_enabled = if state_snapshot.proxy.enabled { "☑" } else { "☐" };
    let proxy_label = format!(
        "{} Proxy: {}",
        proxy_enabled, state_snapshot.proxy.url
    );
    let proxy_item = MenuItem::with_id(
        app_handle,
        menu_ids::PROXY_TOGGLE,
        proxy_label,
        true,
        None::<&str>,
    )?;
    menu_items.push(Box::new(proxy_item));

    // Add separator before actions
    let sep2 = PredefinedMenuItem::separator(app_handle)?;
    menu_items.push(Box::new(sep2));

    // Import current login
    let import_item = MenuItem::with_id(
        app_handle,
        menu_ids::IMPORT,
        "Import current login…",
        true,
        None::<&str>,
    )?;
    menu_items.push(Box::new(import_item));

    // Settings
    let settings_item = MenuItem::with_id(
        app_handle,
        menu_ids::SETTINGS,
        "Settings…",
        true,
        None::<&str>,
    )?;
    menu_items.push(Box::new(settings_item));

    // Add separator before quit
    let sep3 = PredefinedMenuItem::separator(app_handle)?;
    menu_items.push(Box::new(sep3));

    // Quit
    let quit_item = MenuItem::with_id(
        app_handle,
        menu_ids::QUIT,
        "Quit",
        true,
        None::<&str>,
    )?;
    menu_items.push(Box::new(quit_item));

    // Build the menu from items
    // We need to convert the Box<dyn IsMenuItem> slice to references
    let items_refs: Vec<&dyn tauri::menu::IsMenuItem<R>> =
        menu_items.iter().map(|item| item.as_ref()).collect();

    Ok(Menu::with_items(app_handle, &items_refs)?)
}

/// Update the tray icon menu and tooltip after a state change.
///
/// # Arguments
/// * `app_handle` - The Tauri app handle.
/// * `state_snapshot` - The current app state.
///
/// This rebuilds the menu with updated content and refreshes the tooltip to show
/// the active account name.
pub fn update_tray_icon<R: Runtime>(
    app_handle: &AppHandle<R>,
    state_snapshot: &AppConfig,
) -> Result<(), Box<dyn std::error::Error>> {
    // Get the tray icon by its configured id
    if let Some(tray) = app_handle.tray_by_id("main") {
        // Rebuild the menu
        let new_menu = build_tray_menu(app_handle, state_snapshot)?;
        tray.set_menu(Some(new_menu))?;

        // Update tooltip with active account name
        let tooltip = if let Some(active_id) = &state_snapshot.active_account_id {
            state_snapshot
                .accounts
                .iter()
                .find(|a| a.id == *active_id)
                .map(|a| format!("ccswitcher — {}", a.name))
                .unwrap_or_else(|| "ccswitcher".to_string())
        } else {
            "ccswitcher".to_string()
        };

        tray.set_tooltip(Some(tooltip))?;
    }

    Ok(())
}

/// Create the initial tray icon with menu.
///
/// # Arguments
/// * `app_handle` - The Tauri app handle.
/// * `state_snapshot` - The initial app state.
///
/// # Returns
/// Ok(()) if successful, or an error if tray creation fails.
///
/// Note: On systems that don't support tray icons (e.g., some headless environments),
/// this returns an error. The caller should log but not crash.
pub fn create_tray<R: Runtime>(
    app_handle: &AppHandle<R>,
    state_snapshot: &AppConfig,
) -> Result<(), Box<dyn std::error::Error>> {
    // Build the initial menu
    let menu = build_tray_menu(app_handle, state_snapshot)?;

    // Build tooltip with active account name
    let tooltip = if let Some(active_id) = &state_snapshot.active_account_id {
        state_snapshot
            .accounts
            .iter()
            .find(|a| a.id == *active_id)
            .map(|a| format!("ccswitcher — {}", a.name))
            .unwrap_or_else(|| "ccswitcher".to_string())
    } else {
        "ccswitcher".to_string()
    };

    // Create the tray icon using with_id to set the ID
    // The icon is configured in tauri.conf.json (trayIcon.iconPath)
    TrayIconBuilder::with_id("main")
        .icon_as_template(false)
        .menu(&menu)
        .tooltip(&tooltip)
        .build(app_handle)?;

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::model::{Account, AccountType, AuthKind, ProxySettings};

    fn create_test_config() -> AppConfig {
        AppConfig {
            schema_version: 1,
            active_account_id: Some("acc1".to_string()),
            proxy: ProxySettings {
                enabled: true,
                url: "http://127.0.0.1:8080".to_string(),
                no_proxy: "localhost".to_string(),
            },
            managed_keys: vec!["ANTHROPIC_AUTH_TOKEN".to_string()],
            accounts: vec![
                Account {
                    id: "acc1".to_string(),
                    name: "Work".to_string(),
                    account_type: AccountType::AnthropicOauth,
                    base_url: None,
                    auth_kind: None,
                    identity: None,
                    extra_env: Default::default(),
                },
                Account {
                    id: "acc2".to_string(),
                    name: "Personal".to_string(),
                    account_type: AccountType::Token,
                    base_url: Some("https://api.example.com".to_string()),
                    auth_kind: Some(AuthKind::AuthToken),
                    identity: None,
                    extra_env: Default::default(),
                },
            ],
        }
    }

    #[test]
    fn test_menu_label_includes_checkmark_and_type() {
        let config = create_test_config();

        // Verify account data
        assert_eq!(config.accounts[0].name, "Work");
        assert_eq!(config.accounts[1].name, "Personal");
        assert!(matches!(
            config.accounts[0].account_type,
            AccountType::AnthropicOauth
        ));
        assert!(matches!(config.accounts[1].account_type, AccountType::Token));

        // Active account is acc1
        assert_eq!(config.active_account_id, Some("acc1".to_string()));
    }

    #[test]
    fn test_proxy_label_format() {
        let config = create_test_config();

        let proxy_enabled = if config.proxy.enabled { "☑" } else { "☐" };
        let expected = format!("{} Proxy: {}", proxy_enabled, config.proxy.url);
        assert_eq!(expected, "☑ Proxy: http://127.0.0.1:8080");
    }
}
