# ccswitcher

A native Windows tray app for switching Claude Code between multiple accounts —
native Anthropic OAuth logins and `token` accounts (API key / third-party
providers) — plus a global HTTP proxy toggle and per-account extra environment
variables.

Switching works by editing Claude Code's configuration
(`~/.claude/settings.json` `env` block and, for OAuth, the credentials store).
The next `claude` launch picks up the selected account; already-running sessions
are unaffected.

ccswitcher is a WinUI 3 app distributed as a self-contained single `.exe` with
no runtime dependency on the target machine.

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
  env vars that are applied when that account is active, editable from the
  add/edit account dialog.

## How switching works

ccswitcher is an external manager — Claude Code itself is unchanged and unaware
of it. Switching involves editing two locations:

1. **`~/.claude/settings.json`** — the `env` block is updated with the active
   account's configuration (auth token, base URL, proxy settings, extra env).
2. **OAuth credentials store** — for Anthropic OAuth accounts, the credential
   snapshot is restored from the OS keyring to `~/.claude/.credentials.json`
   (atomic write + timestamped backup).

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

- **Non-secret app config**: `%APPDATA%/ccswitcher/config.json`. Holds account
  metadata, proxy settings, and the active account ID — no secrets.
- **Secrets**: Windows Credential Manager (via `PasswordVault`), keyed by
  account ID. Tokens and OAuth credential blobs are stored here, never in
  `config.json` or logs.
- **Backups**: a dedicated `backups/` directory next to each managed file,
  containing timestamped copies with a retention cap. Every destructive write
  is atomic (temp file + rename) and preceded by a backup.

## Build / run

Prerequisites: the .NET 8 SDK and the Windows App SDK workload.

```sh
cd src-winui

# build the solution
dotnet build CCSwitcher.sln

# run the core unit tests (no WinUI dependency)
dotnet test CCSwitcher.Tests/CCSwitcher.Tests.csproj
```

### Publish (single self-contained .exe)

```sh
cd src-winui
dotnet publish CCSwitcher/CCSwitcher.csproj -c Release -r win-x64 \
    --self-contained true -p:PublishSingleFile=true -o publish/
```

The output `publish/CCSwitcher.exe` has no external runtime dependency. Run it
to launch the app; the tray icon appears in the system tray.

## Installation / Distribution

CI builds the self-contained `.exe` on every push to `main` and attaches it to a
GitHub Release when a `v*` tag is pushed (see `.github/workflows/build-winui.yml`).
Otherwise, build from source with the publish command above.

## Troubleshooting

### Tray icon not appearing

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

The `config.json` file (stored in `%APPDATA%/ccswitcher/`) has the following
structure:

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

Secrets (tokens and OAuth blobs) are stored in Windows Credential Manager,
keyed by account ID. They never appear in `config.json`.

## Architecture

- **`src-winui/CCSwitcher/Core/`** — platform-agnostic C# core: data model,
  config store, secret store, credential store, env-merge engine, switching
  logic, import detection. Fully unit-testable with in-memory mocks. Key
  classes: `Models`, `AtomicFile`, `ClaudePaths`, `ConfigStore`,
  `PasswordVaultSecretStore`, `CredentialStore`, `SettingsEnv`, `EnvBuilder`,
  `Switcher`, `Proxy`, `Importer`, `AccountManager`.
- **Platform adapters behind interfaces**:
  - `CredentialStore` — OAuth credential snapshot/restore to
    `~/.claude/.credentials.json` (atomic write + timestamped backup).
  - `ISecretStore` — per-account secrets (tokens, OAuth credential snapshots)
    via Windows Credential Manager (`PasswordVault`).
- **`src-winui/CCSwitcher/` (UI)** — WinUI 3 shell: tray icon
  (`H.NotifyIcon.WinUI`) and a `Window`-based settings page. A thin shell over
  the core.
- **`src-winui/CCSwitcher.Tests/`** — xUnit test project that compiles the
  `Core/` sources directly (no WinUI dependency), injecting in-memory mocks.

See `CLAUDE.md` for deeper architecture notes and implementation conventions,
and [`docs/spec.md`](docs/spec.md) for the platform-independent behaviour spec
(the contract any native port — e.g. macOS — must conform to).

## License

MIT
