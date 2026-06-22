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

Conventions discovered during implementation should be appended here.
