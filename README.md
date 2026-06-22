# ccswitcher

A cross-platform (Windows + macOS) tray / menu-bar app for switching between
multiple Claude Code accounts — native Anthropic OAuth logins and `token`
accounts (API key / third-party providers) — plus a global HTTP proxy toggle and
per-account extra environment variables.

Switching works by editing Claude Code's configuration
(`~/.claude/settings.json` `env` block and, for OAuth, the credentials store).
The next `claude` launch picks up the selected account; already-running sessions
are unaffected.

## Features

- **Multiple Anthropic OAuth accounts** — switch between different Anthropic
  subscription accounts without re-logging in. Credentials are refreshed
  in-place and preserved across switches.
- **Token accounts** — support for API keys and third-party providers (e.g.,
  cloud deployments, custom endpoints). Each token account can have its own
  `base_url` and auth kind (`ANTHROPIC_AUTH_TOKEN` or `ANTHROPIC_API_KEY`).
- **Global HTTP proxy toggle** — configure an HTTP proxy once and enable/disable
  it globally. Proxy settings (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`) are
  injected into the active account's environment.
- **Per-account extra environment variables** — each account can define custom
  env vars that are applied when that account is active.

## How switching works

ccswitcher is an external manager — Claude Code itself is unchanged and unaware
of it. Switching involves editing two locations:

1. **`~/.claude/settings.json`** — the `env` block is updated with the active
   account's configuration (auth token, base URL, proxy settings, extra env).
2. **OAuth credentials store** — for Anthropic OAuth accounts, the credential
   snapshot is restored from the OS keyring to the appropriate platform store
   (Windows: `~/.claude/.credentials.json`; macOS: Keychain service
   `Claude Code-credentials`).

### Managed keys contract

ccswitcher only manages a known set of keys inside `settings.json`'s `env`
object:

- Constant set: `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`,
  `ANTHROPIC_API_KEY`, `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`
- Plus: the active account's `extra_env` keys

Your own env keys and all non-`env` settings (permissions, MCP servers, etc.)
are never touched. The whole file is never blindly rewritten.

### Credential lifecycle (OAuth)

Claude Code refreshes OAuth tokens in-place, so a one-time import snapshot would
go stale. ccswitcher implements **capture-on-switch-out**:

- When switching **away** from an Anthropic OAuth account, its *live* credential
  blob is re-snapshotted into the OS keyring.
- The next switch **back** to that account restores the freshest tokens.
- This keyring write is intentional and is never rolled back, even if a later
  step fails.

## Storage locations

- **Non-secret app config**: `%APPDATA%/ccswitcher/config.json` (Windows),
  `~/Library/Application Support/ccswitcher/config.json` (macOS). Holds
  account metadata, proxy settings, and the active account ID — no secrets.
- **Secrets**: OS keyring, service `ccswitcher`, account = account ID. Tokens and
  OAuth credential blobs are stored here, never in `config.json` or logs.
- **Backups**: a dedicated `backups/` directory next to each managed file,
  containing timestamped copies with a retention cap. Every destructive write
  is atomic (temp file + rename) and preceded by a backup.

## Build / run

Prerequisites: a Rust toolchain (`rustup`, stable). On Windows the MSVC build
tools are required; on macOS the standard Xcode command-line tools.

```sh
# from the repository root
cd src-tauri
cargo build      # build the core + app
cargo test       # run the core unit tests
cargo tauri dev  # run the tray app in development mode
```

For a release build:

```sh
cargo tauri build
```

The Rust core (`src-tauri/src/core`) is platform-agnostic and fully unit-tested
with in-memory mocks, so `cargo test` does not require a real OS keychain.

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

See `CLAUDE.md` for deeper architecture notes and implementation conventions.

## License

MIT
