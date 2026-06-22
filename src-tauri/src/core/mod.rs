//! Platform-agnostic core for ccswitcher.
//!
//! The core holds the data model, config/secret/credential stores, the
//! env-merge engine, the switching logic and import detection. Platform
//! specifics (OS keyring, Claude Code credential store) sit behind traits so
//! the core logic is fully unit-testable with in-memory mocks.
//!
//! ## Two invariants enforced throughout the core
//! 1. **App owns only managed keys** — ccswitcher only ever touches a known set
//!    of keys inside `settings.json`'s `env` object (plus the active account's
//!    `extra_env` keys). The user's own settings and env keys are preserved.
//! 2. **Capture-on-switch-out for OAuth** — before switching away from an
//!    Anthropic OAuth account, its *live* credential blob is re-snapshotted into
//!    the keyring so a later switch-back restores the freshest tokens.
//!
//! Submodules are added by subsequent tasks; `pub mod` declarations are
//! introduced as each module lands.

pub mod atomic;
pub mod config_store;
pub mod model;
pub mod secret_store;
