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

## WinUI 3 (Windows)

The `src-winui/` directory contains a native WinUI 3 implementation for Windows,
distributed as a self-contained single `.exe` with no runtime dependency on the
target machine.

### Build

```
cd src-winui
dotnet build CCSwitcher.sln
```

### Run tests

```
cd src-winui
dotnet test CCSwitcher.Tests/CCSwitcher.Tests.csproj
```

### Publish (single self-contained .exe)

```
cd src-winui
dotnet publish CCSwitcher/CCSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/
```

## Installation / Distribution

Currently ccswitcher must be built from source. Pre-built binaries for Windows
and macOS may be provided in future releases.

### Building from source

Follow the "Build / run" section above. The `cargo tauri build` command produces
platform-specific installers in `src-tauri/target/release/bundle/`.

### Windows

After building, the installer is at:
```
src-tauri/target/release/bundle/msi/ccswitcher_<version>_x64_en-US.msi
```

Run the MSI to install ccswitcher. The tray icon will appear in the system
tray after the first launch.

### macOS

After building, the app bundle is at:
```
src-tauri/target/release/bundle/dmg/ccswitcher_<version>.dmg
```

Open the DMG and drag ccswitcher to Applications. On first launch, you may be
prompted to grant Keychain access for the `Claude Code-credentials` service.

## Troubleshooting

### Keychain permission prompt (macOS)

When switching to an Anthropic OAuth account for the first time, macOS will
prompt: *"ccswitcher wants to access your keychain."* This is expected —
ccswitcher needs to read/write the `Claude Code-credentials` entry to manage
OAuth snapshots. Click "Always Allow" to avoid future prompts.

### Tray icon not appearing (Windows)

Some system tray configurations hide icons by default. Click the up-arrow in
the system tray to reveal hidden icons, then drag ccswitcher to the visible
tray area.

### "Invalid settings.json" error

If `~/.claude/settings.json` contains malformed JSON, ccswitcher will refuse
to touch it. Fix the JSON manually or restore from a backup in
`~/.claude/backups/`.

### Switching doesn't take effect

Claude Code reads its configuration at startup. Already-running sessions are
not affected by switching. Start a new `claude` session to pick up the change.

### OAuth account shows "logout required" after switch

This can happen if ccswitcher's snapshot is out of sync with Claude Code's
live credential store. Manually log into the account in Claude Code once,
then use "Import current login" in ccswitcher to refresh the snapshot.

## Configuration schema reference

The `config.json` file (stored in the OS-specific app data directory) has the
following structure:

```jsonc
{
  "schema_version": 1,                    // file format version
  "active_account_id": "uuid-or-null",   // currently active account
  "proxy": {
    "enabled": false,
    "url": "http://127.0.0.1:8080",
    "no_proxy": "localhost,127.0.0.1"
  },
  "managed_keys": [                       // keys written by ccswitcher
    "ANTHROPIC_BASE_URL",
    "ANTHROPIC_AUTH_TOKEN",
    "HTTP_PROXY"
  ],
  "accounts": [
    {
      "id": "uuid",
      "name": "Work",
      "type": "anthropic_oauth",         // or "token"
      "base_url": "https://api.anthropic.com",  // optional
      "auth_kind": "auth_token",         // token only: "auth_token" | "api_key"
      "identity": "user@example.com",   // oauth only: stable identifier
      "extra_env": {                     // per-account env vars
        "CUSTOM_VAR": "value"
      }
    }
  ]
}
```

Secrets (tokens and OAuth blobs) are stored in the OS keyring under the
`ccswitcher` service, keyed by account ID. They never appear in `config.json`.

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
