# CLAUDE.md — ccswitcher

Guidance for working in this repository.

## What this is

A Tauri 2 tray app that switches Claude Code between multiple accounts by editing
Claude Code's own config (`~/.claude/settings.json` and the OAuth credential
store). ccswitcher is an external manager — Claude Code itself is unchanged and
unaware of it.

## Architecture

- **`src-tauri/src/core/`** — platform-agnostic Rust core: data model, config
  store, secret store, credential store, env-merge engine, switching logic,
  import detection. Fully unit-testable with in-memory mocks.
- **Platform adapters behind traits**:
  - `CredentialStore` — OAuth credential snapshot/restore. Windows =
    `~/.claude/.credentials.json` (atomic write + timestamped backup);
    macOS = Keychain service `Claude Code-credentials`.
  - `SecretStore` — per-account secrets (tokens, OAuth credential snapshots)
    via the `keyring` crate.
- **`src-tauri/src/` (bin/lib)** — Tauri runtime: commands, tray menu, settings
  window. Thin shell over the core. The library crate (`ccswitcher_lib`) holds
  the core so `cargo test` runs without a webview.

## Two invariants (do not violate)

1. **App owns only managed keys.** ccswitcher only ever touches a known set of
   keys inside `settings.json`'s `env` object (`ANTHROPIC_BASE_URL`,
   `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY`, `HTTP_PROXY`, `HTTPS_PROXY`,
   `NO_PROXY`) plus the active account's `extra_env` keys. The user's own env
   keys and all non-`env` settings are never lost. The whole file is never
   blindly rewritten.

2. **Capture-on-switch-out for OAuth.** Claude Code refreshes OAuth tokens in
   place, so a one-time import snapshot goes stale. Before switching *away* from
   an Anthropic OAuth account, re-snapshot its live credential blob into the
   keyring. Restore-on-switch-in then always uses the freshest blob. This keyring
   write is intentional and is never rolled back, even if a later step fails.

## Storage locations

- Non-secret app config: `%APPDATA%/ccswitcher/config.json` (Windows),
  `~/Library/Application Support/ccswitcher/config.json` (macOS).
- Secrets: OS keyring, service `ccswitcher`, account = account id. Never written
  to `config.json` or logs.
- Backups: a dedicated `backups/` dir, timestamped copies with a retention cap;
  every destructive write is atomic (temp + rename) and preceded by a backup.

## Build / test

```sh
cd src-tauri
cargo build
cargo test
```

## Conventions discovered during implementation

### Module structure under `core/`

Each module in `src-tauri/src/core/` has a single responsibility and is
testable in isolation:

- `model.rs` — data structures (Account, AppConfig, etc.) with serde bounds
- `atomic.rs` — low-level atomic write and timestamped backup primitives
- `claude_paths.rs` — path resolution for Claude Code config
- `config_store.rs` — load/save ccswitcher's own config.json
- `secret_store.rs` — OS keyring abstraction with in-memory mock for tests
- `credential_store.rs` — OAuth credential snapshot/restore, platform-specific
- `settings_env.rs` — parse and merge Claude Code's settings.json env block
- `env_builder.rs` — construct env for a target account (token vs OAuth, proxy)
- `switcher.rs` — the main switching flow (capture-on-switch-out, apply account)
- `proxy.rs` — proxy toggle (lighter than full switch, no credential I/O)
- `import.rs` — detect and import the current Claude Code login

### Trait-based adapter pattern

Platform-specific I/O (keyring, credential store) sits behind traits:

- Define a trait with the methods the core needs (`SecretStore`, `CredentialStore`)
- Provide a real implementation (`KeyringSecretStore`, `FileCredentialStore`,
  `KeychainCredentialStore`) gated by `#[cfg(target_os = "...")]`
- Provide an in-memory mock for tests (`InMemorySecretStore`,
  `InMemoryCredentialStore`)
- Core functions accept `impl Trait` or `Box<dyn Trait>` so tests inject mocks

This pattern keeps the core platform-agnostic and fully unit-testable without
a real OS keychain or filesystem.

### Error handling pattern

Each core module defines its own error enum with variants for the failure modes
that module can encounter. These errors propagate through `Result` types and
are eventually converted to a unified `CommandError` in the Tauri commands layer.

- Use `thiserror` for error enums with `#[from]` on variants that wrap other errors
- Keep error messages descriptive but avoid leaking secrets (tokens, credential
  blobs)
- In `commands.rs`, map domain errors to `CommandError` with a `kind` field for
  frontend conditional handling

### Mutex serialization

All mutating operations (switch account, add/update/delete account, toggle proxy,
import) acquire a single async mutex in `AppState` before touching `config.json`
or `settings.json`. This prevents interleaved read-modify-write races.

- The mutex is `Arc<Mutex<AppConfig>>` so it's shared across all command handlers
- Even read-only commands acquire the mutex to avoid seeing partially-mutated state
- The mutex is held only for the duration of the config read/write; I/O operations
  that don't touch config (credential snapshot, keyring) can run outside the lock

### Atomic write + backup pattern

Every destructive write to `settings.json`, `config.json`, or `.credentials.json`
follows the same sequence:

1. Create a timestamped backup in `backups/<filename>.<timestamp>.bak`
2. Prune old backups to keep only the newest N (default: 10)
3. Write to a temp file in the same directory
4. Rename the temp file over the target (atomic on Windows, macOS, Linux)

This ensures the target file is never left half-written, and a rollback is always
available from the backups directory.

### Testing approach

- **Unit tests first**: Write tests alongside or immediately after the code.
- **Success + error scenarios**: Every core function has tests for both happy
  path and failure modes (missing files, invalid JSON, keyring errors, etc.).
- **Mock the outside**: Use in-memory mocks for `SecretStore` and
  `CredentialStore` so tests run without real OS keychain/Filesystem dependencies.
- **Test against the contract**: For `merge_env`, verify that managed keys are
  replaced, user keys survive, and the union of old+new managed keys is stripped.

### Idempotency where possible

The switching flow is designed to be idempotent: re-running the same switch heals
any partial cross-store state from an aborted run. This is especially important
because the operation spans three independent stores (keyring, credential store,
settings/config files) and is not transactional across them.

- Credential restore failing after settings write: re-run the switch and it will
  retry the credential restore with a fresh backup of settings
- Capture-on-switch-out failing: the settings/config write proceeds anyway,
  and the next switch will re-attempt the capture

Non-idempotent operations (e.g., adding an account) are explicitly marked and
validated in the UI.

### Frontend integration pattern

The Tauri commands layer (`src-tauri/src/commands.rs`) exposes the core's
functionality to the frontend (settings window) and tray event handlers.

**Command signature pattern:**
```rust
#[tauri::command]
pub async fn some_command(
    params: Params,
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<ResponseType, CommandError>
```

**State access:**
- `state.mutex.lock().await` acquires the single async mutex for serialized
  config access
- All mutating commands (`switch_account`, `add_token_account`, `update_account`,
  `delete_account`, `set_proxy_enabled`, `import_current`) must acquire this lock
- The lock is held only during config read/write; I/O that doesn't touch config
  (credential store, keyring) can run outside the lock

**Notification pattern:**
- Commands emit `notification` events via `app.emit()` for success/error/warning
- Events use `NotificationEvent` enum (`Success`, `Error`, `Warning`)
- Error messages are sanitized via `sanitize_secrets()` to prevent secret leakage
- The frontend (`settings.js`) listens for these events to show inline feedback

### Tray menu rebuild pattern

The tray menu must reflect the current app state (active account, proxy status).
After any state-changing operation:

1. The command handler acquires the mutex and updates `config.json`
2. On success, the handler emits an event (`proxy_toggle`, `switch_account`)
3. The main.rs listener receives the event and calls `update_tray_icon()`
4. `update_tray_icon()` clones the config (under lock) and rebuilds the menu

**Why this pattern?**
- The Tauri tray API doesn't support fine-grained menu updates; we rebuild
- The state snapshot ensures the menu reflects the state at the time of rebuild
- The mutex prevents races between menu rebuild and config updates

**Example flow:**
```
user clicks proxy toggle
  -> tray emits "tray_toggle_proxy" event
  -> main.rs listener spawns async task
  -> task acquires mutex, toggles proxy.enabled
  -> task writes config.json (atomic + backup)
  -> task emits "proxy_toggle" event on success
  -> main.rs listener calls update_tray_icon()
  -> update_tray_icon() acquires mutex, clones config
  -> build_tray_menu() renders menu with updated proxy state
```
