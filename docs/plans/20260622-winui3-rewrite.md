# WinUI 3 Rewrite

## Overview

Full rewrite of ccswitcher from Tauri 2 (Rust) to WinUI 3 (C#/.NET). The new
implementation lives in `src-winui/` alongside the existing `src-tauri/`. The
Tauri version stays in place until the WinUI version reaches feature parity.

The new app is distributed as a self-contained single `.exe` via GitHub Releases
(`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`).
No installer, no MSIX, no runtime dependency on the target machine.

## Context (from discovery)

- **Current core modules:** `src-tauri/src/core/` — switcher, model, atomic, credential_store, secret_store, settings_env, env_builder, import, proxy, config_store, claude_paths, user_config
- **Current UI:** `dist/settings.html` + `dist/settings.js` — account management, proxy toggle, import flow
- **Two hard invariants** from CLAUDE.md that must survive the rewrite:
  1. Only touch managed keys in `settings.json`'s `env` block; never blindly rewrite the file
  2. Capture-on-switch-out for OAuth accounts before switching away (includes BOTH the live credential blob AND the `oauthAccount` section of `~/.claude.json`, stored under `{id}#oauthAccount` in the keyring)
- **Key 3rd-party NuGet:** `H.NotifyIcon.WinUI` for system tray
- **Keyring replacement:** `Windows.Security.Credentials.PasswordVault` (WinRT, built-in)
- **JSON:** `System.Text.Json` (built-in .NET)
- **Single instance:** named `Mutex` + IPC to focus existing window (not silent exit)
- **Startup at login:** registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

## Development Approach

- **Testing approach:** Regular (core classes first, then xUnit tests in a sibling test project)
- Complete each task fully before moving to the next
- Unit-test the `Core/` layer thoroughly; WinUI UI layer is manually verified
- All tests must pass before starting next task
- Update this plan when scope changes

## Testing Strategy

- **Unit tests:** separate `CCSwitcher.Tests` xUnit project targeting `Core/` classes
- **UI testing:** manual verification of the settings window and tray menu
- Test both success and error scenarios for every Core class
- Mirror the existing Rust test coverage (switcher invariants, merge_env contract, importer dedup logic, etc.)

## Progress Tracking

- Mark completed items with `[x]` immediately when done
- Add newly discovered tasks with ➕ prefix
- Document issues/blockers with ⚠️ prefix

## Solution Overview

```
src-winui/
  CCSwitcher.sln
  CCSwitcher/                         ← WinUI 3 app project (Unpackaged)
    App.xaml / App.xaml.cs            ← entry, single-instance mutex+IPC, hidden MainWindow
    MainWindow.xaml                   ← invisible host (WinUI 3 lifecycle requirement)
    SettingsWindow.xaml               ← account management UI
    TrayIcon.cs                       ← H.NotifyIcon setup + context menu rebuild
    Core/
      Models.cs                       ← Account, AppConfig, ProxySettings (← model.rs)
      AtomicFile.cs                   ← atomic temp+rename write + timestamped backup (← atomic.rs)
      ClaudePaths.cs                  ← path resolution for Claude Code config (← claude_paths.rs)
      ConfigStore.cs                  ← load/save %APPDATA%/ccswitcher/config.json (← config_store.rs)
      UserConfig.cs                   ← oauthAccount capture/restore in ~/.claude.json (← user_config.rs)
      SecretStore.cs                  ← PasswordVault wrapper (← secret_store.rs)
      CredentialStore.cs              ← ~/.claude/.credentials.json read/write (← credential_store.rs)
      SettingsEnv.cs                  ← parse + merge settings.json env block (← settings_env.rs)
      EnvBuilder.cs                   ← construct env dict for target account (← env_builder.rs)
      Switcher.cs                     ← main switching flow, two invariants (← switcher.rs)
      Proxy.cs                        ← proxy-only toggle, no credential I/O (← proxy.rs)
      Importer.cs                     ← detect + import current Claude Code login (← import.rs)
      AccountManager.cs               ← add/update/delete account CRUD (← commands.rs)
      Secrets.cs                      ← sanitize_secrets for error messages (← commands.rs)
  CCSwitcher.Tests/                   ← xUnit test project
    Core/
      ModelsTests.cs
      AtomicFileTests.cs
      UserConfigTests.cs
      SettingsEnvTests.cs
      EnvBuilderTests.cs
      SwitcherTests.cs
      ProxyTests.cs
      ImporterTests.cs
      AccountManagerTests.cs
      SecretsTests.cs
```

Mutex serialization: a single `SemaphoreSlim(1,1)` in `App` serializes all
mutating operations (same role as the Tauri `Arc<Mutex<AppConfig>>`).

## Technical Details

- **AppConfig JSON shape:** identical to the existing `config.json` so existing
  user configs load without migration (`schema_version`, `active_account_id`,
  `proxy`, `managed_keys`, `accounts`). The C# `AppConfigDir` must resolve to
  `%APPDATA%\ccswitcher\` — same as the Tauri app — verified by a test.
- **PasswordVault:** service name `ccswitcher`, resource = account id. Maps 1:1
  to the Rust `keyring` crate usage. `{id}#oauthAccount` keyring key for
  `UserConfig` snapshots (never collides with the bare credential blob key).
- **Atomic write:** `File.WriteAllText(tmpPath)` + `File.Move(tmp, target, overwrite: true)`.
  Backup: copy to `backups/<filename>.<timestamp:yyyyMMdd_HHmmss_fff>.bak`, keep newest 10.
  Timestamp format must match Rust's atomic.rs to keep existing `backups/` dirs consistent.
- **MANAGED_KEYS constant:** same set as Rust —
  `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY`,
  `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`.
- **Two user config candidate paths (priority order):**
  1. `%USERPROFILE%\.claude\.claude.json`
  2. `%USERPROFILE%\.claude.json`
  `ClaudePaths.FindUserConfig()` returns the first that exists on disk.
- **VOLATILE_BLOB_FIELDS** (stripped during import dedup normalization):
  `accessToken`, `refreshToken`, `expiresAt`, `expiresAtTimestamp`, `tokenResponse`, `idToken`
- **Single instance:** named `Mutex` detects second instance; the second instance
  sends a signal (named pipe or `AppInstance` API) to the first, which then
  shows and focuses its settings window. The second instance exits silently.

## What Goes Where

**Implementation Steps** — code changes in this repo.
**Post-Completion** — manual steps outside the repo.

## Implementation Steps

### Task 1: Project scaffold

**Files:**
- Create: `src-winui/CCSwitcher.sln`
- Create: `src-winui/CCSwitcher/CCSwitcher.csproj` (WinUI 3 Unpackaged, net8.0-windows10.0.19041.0)
- Create: `src-winui/CCSwitcher.Tests/CCSwitcher.Tests.csproj` (xUnit, net8.0)
- Create: `src-winui/CCSwitcher/App.xaml` + `App.xaml.cs`
- Create: `src-winui/CCSwitcher/MainWindow.xaml` + `MainWindow.xaml.cs`

- [x] create solution + app csproj targeting `net8.0-windows10.0.19041.0`, Unpackaged, `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`
- [x] add NuGet refs: `Microsoft.WindowsAppSDK`, `H.NotifyIcon.WinUI` (no DI container — YAGNI)
- [x] create `App.xaml.cs` with named Mutex single-instance check; on second instance launch: signal the first via a named pipe, then exit
- [x] create `MainWindow.xaml` that hides itself immediately on `Activated` (0×0, not in taskbar; WinUI 3 Window has no Loaded event — uses Activated); named-pipe listener calls `ShowSettingsWindow()` (placeholder) when signalled by a second instance
- [x] create test project with xUnit referencing the app project's `Core/` classes
- [x] confirm `dotnet build src-winui/CCSwitcher.sln` succeeds (empty app compiles)

### Task 2: Core/Models.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/Models.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/ModelsTests.cs`

- [x] define `AccountType` enum (`AnthropicOauth`, `Token`) with `[JsonConverter]` matching snake_case JSON values `"anthropic_oauth"` / `"token"`
- [x] define `AuthKind` enum (`AuthToken`, `ApiKey`) matching `"auth_token"` / `"api_key"`
- [x] define `Account` record: `Id`, `Name`, `AccountType`, `BaseUrl?`, `AuthKind?`, `Identity?`, `ExtraEnv` — JSON field `type` (not `AccountType`)
- [x] define `ProxySettings` record with defaults matching Rust (`enabled:false`, `url:"http://127.0.0.1:8080"`, `no_proxy:"localhost,127.0.0.1"`)
- [x] define `AppConfig` record: `SchemaVersion`, `ActiveAccountId?`, `Proxy`, `ManagedKeys`, `Accounts`
- [x] write round-trip serialization tests: both account types, optional fields absent, JSON field renames (`type` not `AccountType`), minimal config using defaults
- [x] run tests — must pass before task 3

### Task 3: Core/AtomicFile.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/AtomicFile.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/AtomicFileTests.cs`

- [x] implement `AtomicFile.Write(path, content)`: write to `<path>.tmp` then `File.Move(..., overwrite: true)`
- [x] implement `AtomicFile.Backup(path, backupsDir, maxKeep=10)`: copy to `backups/<filename>.<yyyyMMdd_HHmmss_fff>.bak`, prune oldest beyond maxKeep
- [x] write tests: atomic write creates target and leaves no `.tmp`; backup creates `.bak` in backups dir; prune retains only newest N; backup on missing source is no-op
- [x] run tests — must pass before task 4

### Task 4: Core/ClaudePaths.cs + Core/ConfigStore.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/ClaudePaths.cs`
- Create: `src-winui/CCSwitcher/Core/ConfigStore.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/ConfigStoreTests.cs`

- [x] `ClaudePaths.SettingsPath` → `%USERPROFILE%\.claude\settings.json`
- [x] `ClaudePaths.CredentialsPath` → `%USERPROFILE%\.claude\.credentials.json`
- [x] `ClaudePaths.FindUserConfig()` → returns the first of `[%USERPROFILE%\.claude\.claude.json, %USERPROFILE%\.claude.json]` that exists on disk, or `null`
- [x] `ClaudePaths.AppConfigDir` → `%APPDATA%\ccswitcher\`
- [x] `ConfigStore.Load(dir)` → deserialize `config.json`, return `AppConfig.Default` if missing; throw on invalid JSON
- [x] `ConfigStore.Save(dir, config)` → atomic write (backup + temp + rename); creates dir if absent
- [x] write tests using temp directories: load missing returns default; invalid JSON throws; save+load round-trip; backup created on save; `AppConfigDir` resolves to same path as Tauri app (`%APPDATA%\ccswitcher\`)
- [x] run tests — must pass before task 5

### Task 5: Core/UserConfig.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/UserConfig.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/UserConfigTests.cs`

- [x] define `OauthAccountKey(accountId) → string` returning `"{accountId}#oauthAccount"` — separate keyring key for the oauthAccount snapshot, never collides with the bare credential blob key
- [x] implement `UserConfig.ReadOauthAccount(path) → JsonNode?`: read `oauthAccount` field from `~/.claude.json`; return `null` if file missing or key absent; throw `UserConfigException` on invalid JSON
- [x] implement `UserConfig.MergeOauthAccount(path, oauth)`: load existing config (or `{}`), replace ONLY the `oauthAccount` key, write atomically with backup; validate `oauth` is a JSON object before writing; create file if absent
- [x] write tests: read returns section when present; read returns null when file missing or key absent; read throws on invalid JSON; merge replaces only oauthAccount preserving all other fields (`userID`, `projects`, etc.); merge creates file when absent; merge rejects non-object oauth; merge creates backup
- [x] run tests — must pass before task 6

### Task 6: Core/SecretStore.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/SecretStore.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/SecretStoreTests.cs`

- [x] define `ISecretStore` interface: `Set(id, value)`, `Get(id) → string?`, `Delete(id)`
- [x] implement `PasswordVaultSecretStore`: service = `"ccswitcher"`, resource = account id; catch `COMException` / `Exception` on missing entry (returns null from `Get`)
- [x] implement `InMemorySecretStore` (Dictionary-backed) for tests
- [x] write tests against `InMemorySecretStore`: set+get round-trip, get missing returns null, delete removes, set overwrites
- [x] run tests — must pass before task 7

### Task 7: Core/CredentialStore.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/CredentialStore.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/CredentialStoreTests.cs`

- [x] define `ICredentialStore` interface: `Read() → string?`, `Write(blob)`
- [x] implement `FileCredentialStore`: reads/writes `~/.claude/.credentials.json` atomically (backup + temp + rename)
- [x] implement `InMemoryCredentialStore` for tests
- [x] write tests: read missing returns null; write+read round-trip; atomic write leaves no `.tmp`; backup created on write
- [ ] run tests — must pass before task 8

### Task 8: Core/SettingsEnv.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/SettingsEnv.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/SettingsEnvTests.cs`

- [ ] `SettingsEnv.Load(path) → JsonNode`: return `{}` if missing; throw `SettingsException` on invalid JSON; throw `SettingsException` if top-level is not a JSON object
- [ ] `SettingsEnv.MergeEnv(settings, oldManagedKeys, newEnv) → (JsonNode merged, List<string> newManagedKeys)`: strip union of `MANAGED_KEYS + oldManagedKeys` from `settings["env"]`; if `env` is absent or not an object, treat as `{}`; inject `newEnv`; return new managed-key list
- [ ] define `MANAGED_KEYS` constant set: `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY`, `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`
- [ ] write tests: managed keys replaced; user keys survive; union of old+new managed keys stripped; missing file returns empty object; invalid JSON throws; non-object top-level throws; non-object `env` treated as `{}`
- [ ] run tests — must pass before task 9

### Task 9: Core/EnvBuilder.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/EnvBuilder.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/EnvBuilderTests.cs`

- [ ] `EnvBuilder.Build(account, secret, proxy) → Dictionary<string,string>`: produce the env dict
  - Token + AuthToken → `ANTHROPIC_AUTH_TOKEN = secret` (throw `MissingSecretException` if null)
  - Token + ApiKey → `ANTHROPIC_API_KEY = secret` (throw `MissingSecretException` if null)
  - OAuth → no token key
  - Any → `ANTHROPIC_BASE_URL` if `account.BaseUrl` is set
  - Proxy enabled → `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`
  - Merge `account.ExtraEnv` last
- [ ] write tests: token with auth_token, token with api_key, oauth writes no token key, proxy keys present when enabled and absent when disabled, extra_env merged, missing secret throws
- [ ] run tests — must pass before task 10

### Task 10: Core/Switcher.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/Switcher.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/SwitcherTests.cs`

- [ ] implement `Switcher.ApplyAccount(config, accountId, deps)` with the exact 8-step order from `switcher.rs`:
  1. Validate target exists (throw `UnknownAccountException` if not) — unknown id must not touch any store
  2. Capture-on-switch-out: ONLY if active account is a **different, still-existing OAuth** account — re-snapshot live cred blob into keyring (skip silently if live blob missing); also re-snapshot live `oauthAccount` from `~/.claude.json` via `UserConfig.ReadOauthAccount` + `ISecretStore.Set(OauthAccountKey(activeId), ...)` — these keyring writes are intentional and never rolled back
  3. Load `settings.json` (invalid JSON aborts before any mutation)
  4. Build target env via `EnvBuilder.Build` (missing secret aborts before any write)
  5. Merge env via `SettingsEnv.MergeEnv`
  6. Backup + atomic write `settings.json`
  7. Restore OAuth credential snapshot for OAuth target (if stored snapshot exists; no snapshot = switch still succeeds); also restore `oauthAccount` via `UserConfig.MergeOauthAccount` (best-effort, failure must not fail the whole switch)
  8. Persist config (managed_keys + active_account_id)
- [ ] implement `Switcher.ClearActiveIfMissing(config) → bool`: clear `active_account_id` when it refers to a non-existent account; return true if cleared
- [ ] define `SwitchDeps` with: `settingsPath`, `configDir`, `userConfigPath?`, `ISecretStore`, `ICredentialStore`
- [ ] write tests mirroring Rust test suite:
  - unknown target returns typed error and touches no store
  - token switch writes env and persists managed_keys
  - oauth restores snapshot and writes no token key
  - token→oauth→token leaves no stale keys
  - capture-on-switch-out preserves refreshed blob (A→B→A cycle)
  - missing secret aborts before settings write
  - cross-store post-abort is idempotent on re-run
  - oauth switch captures+restores oauthAccount section in ~/.claude.json
  - `ClearActiveIfMissing` clears dangling id; keeps existing id; noop when None
- [ ] run tests — must pass before task 11

### Task 11: Core/Proxy.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/Proxy.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/ProxyTests.cs`

- [ ] define `ProxyDeps` with: `settingsPath`, `configDir`, `ISecretStore` — **no credential store** (structural guarantee that proxy toggle never touches OAuth credentials)
- [ ] implement `Proxy.SetEnabled(config, enabled, deps)`:
  1. Update `config.proxy.enabled`
  2. No active account → persist flag only, no settings write (return)
  3. Dangling active id (account deleted) → same: persist flag only
  4. Load settings, build active account's env with updated proxy, merge, backup+atomic write, persist config with new managed_keys
- [ ] write tests: enabling adds proxy keys; disabling removes them; account env survives toggle; no active account stores flag only without settings write; dangling id stores flag only; `PanicCredentialStore` test proves credential store is never called (by construction — `ProxyDeps` has no credential store field)
- [ ] run tests — must pass before task 12

### Task 12: Core/Importer.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/Importer.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/ImporterTests.cs`

- [ ] define `ImportCandidate` discriminated union: `Token { Secret, AuthKind, BaseUrl? }` and `Oauth { Blob, Identity? }`
- [ ] define `ImportResult` discriminated union: `Created(Account)` and `CreatedWithWarning(Account, string)`
- [ ] implement `Importer.Detect(managedKeys, settingsPath, userConfigPath, credentialStore) → ImportCandidate?`:
  - load `settings.json`; check env for non-managed `ANTHROPIC_AUTH_TOKEN` (prefer over API_KEY) then `ANTHROPIC_API_KEY` — use **only `config.managed_keys`**, NOT the constant `MANAGED_KEYS`
  - if no non-managed token: read credential store; if non-empty blob → OAuth candidate with identity from `~/.claude.json` `oauthAccount` (`accountUuid` preferred over `emailAddress`) falling back to `extract_identity(blob)` fields (`email`, `account_id`, `accountId` inside `claudeAiOauth`)
  - return null if neither found
- [ ] implement `Importer.DefaultName(candidate) → string`:
  - Token with base_url → strip scheme, take host only (e.g. `"api.anthropic.com"`)
  - Token without base_url → `"Token Account"`
  - OAuth with email identity → use identity
  - OAuth with non-email identity or none → `"Anthropic"`
- [ ] implement `Importer.Import(candidate, name, existingAccounts, secretStore) → ImportResult`:
  - generate fresh UUID for id
  - Token duplicate: match on `base_url + auth_kind` → `CreatedWithWarning`
  - OAuth duplicate (1): identity match on existing accounts → `CreatedWithWarning`
  - OAuth duplicate (2): normalized blob fingerprint match (strip `VOLATILE_BLOB_FIELDS`: `accessToken`, `refreshToken`, `expiresAt`, `expiresAtTimestamp`, `tokenResponse`, `idToken`) against stored blobs in keyring → `CreatedWithWarning`
  - store secret in keyring; return `Created` or `CreatedWithWarning`
- [ ] write tests mirroring Rust test suite: detect returns token when auth_token present; detect returns token when api_key present; detect extracts base_url; detect returns oauth when credentials non-empty; detect returns null when neither exists; detect ignores managed auth_token key; detect ignores managed api_key key; detect falls back to oauth when token is managed; detect prefers auth_token over api_key; detect uses user_config for oauth identity; constant MANAGED_KEYS NOT used for ignore; default_name all cases; import creates token account and stores secret; import creates oauth account and stores blob; import token dup returns warning; import token different auth_kind no warning; import oauth dup by identity returns warning; import oauth no identity skips identity dedup; import oauth dup by blob returns warning; normalize_blob strips volatile fields; normalize_blob returns null for invalid json; extract_identity all cases
- [ ] run tests — must pass before task 13

### Task 13: Core/AccountManager.cs + Core/Secrets.cs

**Files:**
- Create: `src-winui/CCSwitcher/Core/AccountManager.cs`
- Create: `src-winui/CCSwitcher/Core/Secrets.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/AccountManagerTests.cs`
- Create: `src-winui/CCSwitcher.Tests/Core/SecretsTests.cs`

- [ ] implement `AccountManager.AddTokenAccount(config, name, baseUrl, authKind, secret, secretStore, configDir)`: generate UUID, create `Account`, store secret in keyring, append to `config.accounts`, save config
- [ ] implement `AccountManager.UpdateAccount(config, accountId, name, baseUrl, authKind, newSecret?, secretStore, configDir)`: find account, update fields, update keyring secret only if `newSecret` provided, save config
- [ ] implement `AccountManager.DeleteAccount(config, accountId, secretStore, configDir)`: remove account from list; if `config.active_account_id == accountId` clear it (dangling id drives buggy capture-on-switch-out); delete keyring secret (also delete `{id}#oauthAccount` key if present); save config
- [ ] implement `Secrets.Sanitize(message) → string`: redact `sk-ant-*` and `sk-*` tokens, Bearer tokens, and OAuth JSON blobs (mirroring `sanitize_secrets` in commands.rs); applied to ALL user-facing error messages
- [ ] write `AccountManager` tests: add creates account and stores secret; update renames and optionally updates secret; delete removes account and keyring secret; delete clears active_account_id when it matches; delete removes `{id}#oauthAccount` keyring entry
- [ ] write `Secrets` tests: sk-ant-* redacted; sk-* redacted; Bearer token redacted; OAuth blob fields redacted; plain text unchanged
- [ ] run tests — must pass before task 14

### Task 14: TrayIcon + App wiring

**Files:**
- Create: `src-winui/CCSwitcher/TrayIcon.cs`
- Modify: `src-winui/CCSwitcher/App.xaml.cs`

- [ ] `TrayIcon.Build(config, callbacks)` — construct `H.NotifyIcon` with context menu: accounts list (checkmark on active), proxy toggle (checked when enabled), separator, "Settings", "Import current login", "Launch at startup" toggle, "Exit"
- [ ] `TrayIcon.Rebuild(config)` — tear down + recreate menu to reflect updated state
- [ ] wire `App.xaml.cs`: load config on startup, call `Switcher.ClearActiveIfMissing`, build tray; all mutating callbacks acquire `SemaphoreSlim`, call core function, call `TrayIcon.Rebuild` on success, show sanitized error notification on failure
- [ ] startup-at-login toggle: read/write `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` via `Microsoft.Win32.Registry`
- [ ] named-pipe listener in `MainWindow.xaml.cs`: listen for focus signal; on receive show settings window and call `Activate()`
- [ ] verify tray appears, menu reflects state, clicking account switches correctly, second app launch focuses settings window

### Task 15: SettingsWindow.xaml

**Files:**
- Create: `src-winui/CCSwitcher/SettingsWindow.xaml`
- Create: `src-winui/CCSwitcher/SettingsWindow.xaml.cs`

- [ ] account list: name + type badge (OAuth / Token); active account highlighted; Edit + Delete buttons per row
- [ ] "Add Token Account" button → dialog: Name, Base URL (optional), Auth Kind (AuthToken / ApiKey), Token field
- [ ] "Import current login" button → calls `Importer.Detect`; if detected shows name prompt pre-filled with `Importer.DefaultName`; on confirm calls `Importer.Import`; shows warning if `CreatedWithWarning`
- [ ] proxy section: enabled toggle, URL field, No-Proxy field; Save calls `Proxy.SetEnabled` + save
- [ ] "Launch at startup" toggle reads/writes registry key
- [ ] all mutating operations go through `App.StateMutex` (SemaphoreSlim), apply `Secrets.Sanitize` to any displayed error, call `TrayIcon.Rebuild` on success
- [ ] manually verify: add, edit, delete, import, proxy toggle, startup toggle, duplicate warning UX

### Task 16: GitHub Actions build workflow

**Files:**
- Create: `.github/workflows/build-winui.yml`

- [ ] trigger on push to `main` and on tag `v*`
- [ ] `dotnet test src-winui/CCSwitcher.Tests/CCSwitcher.Tests.csproj` step
- [ ] `dotnet publish` step: `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
- [ ] on tag push: upload the produced `.exe` as a GitHub Release asset
- [ ] verify workflow passes on a test tag

### Task 17: Verify acceptance criteria

- [ ] switch between a Token account and an OAuth account — no stale env keys remain
- [ ] switch away from OAuth while Claude Code is running — live credential blob AND oauthAccount section are captured
- [ ] switch back to OAuth — oauthAccount restored in ~/.claude.json, other user fields intact
- [ ] user keys in `settings.json` env are never lost after a switch
- [ ] proxy toggle never touches the credential store
- [ ] app has single instance: second launch shows and focuses settings window
- [ ] launch-at-startup toggle works (registry key present/absent)
- [ ] delete account removes keyring secret and clears active_account_id if matched
- [ ] error messages with tokens/blobs are sanitized (no raw secrets in UI)
- [ ] `dotnet test` passes all unit tests
- [ ] published `.exe` runs on a clean Windows machine without .NET installed

### Task 18: [Final] Documentation

- [ ] update `README.md` to document the WinUI 3 build (`cd src-winui && dotnet build`)
- [ ] update `CLAUDE.md` if new patterns discovered during C# implementation
- [ ] move this plan to `docs/plans/completed/`

## Post-Completion

**Manual verification:**
- Test on a machine where Claude Code has an active Anthropic OAuth login
- Verify OAuth capture-on-switch-out preserves tokens after Claude Code refreshes them
- Verify `oauthAccount` in `~/.claude.json` is correctly swapped between two OAuth accounts
- Verify SmartScreen behavior on a clean machine (expected: "Unknown publisher" warning, user clicks "More info → Run anyway")

**Distribution:**
- After first tagged release, consider submitting to `winget-pkgs` repository for `winget install` support
