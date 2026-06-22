//! ccswitcher library crate.
//!
//! Exposes the platform-agnostic [`core`] module (unit-tested without a webview)
//! and the Tauri application entry point [`run`]. Splitting the binary from the
//! library lets `cargo test` exercise the core directly.

pub mod commands;
pub mod core;

/// Re-export the keyring secret store for use in commands.
pub use core::secret_store::KeyringSecretStore;

/// Launch the Tauri application (tray + hidden settings window).
///
/// This is a thin runtime shell; all behaviour lives in [`core`] and the
/// command/tray layers added by later tasks.
pub fn run() {
    tauri::Builder::default()
        .run(tauri::generate_context!())
        .expect("error while running ccswitcher");
}
