# CLAUDE.md — ccswitcher

Guidance for working in this repository.

## What this is

A native Windows (WinUI 3) tray app that switches Claude Code between multiple
accounts by editing Claude Code's own config (`~/.claude/settings.json` and the
OAuth credential store). ccswitcher is an external manager — Claude Code itself
is unchanged and unaware of it. It ships as a single self-contained `.exe`.

The platform-independent behaviour contract lives in **`docs/spec.md`** — the
on-disk `config.json` shape, managed-keys rule, OAuth capture-on-switch-out,
atomic-write/backup format, switch flow, import logic, and keychain naming.
Any native port (e.g. a future macOS app) must conform to it byte-for-byte where
it shares files/keychain entries with this build. Update `docs/spec.md` in the
same change whenever you alter any of that behaviour.

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

- Non-secret app config: `%APPDATA%/ccswitcher/config.json`.
- Secrets: Windows Credential Manager (`PasswordVault`), keyed by account id.
  Never written to `config.json` or logs.
- Backups: a dedicated `backups/` dir, timestamped copies with a retention cap;
  every destructive write is atomic (temp + rename) and preceded by a backup.

## Build / test

```sh
cd src-winui
dotnet build CCSwitcher.sln
dotnet test CCSwitcher.Tests/CCSwitcher.Tests.csproj
```

Publish the self-contained single `.exe`:

```sh
cd src-winui
dotnet publish CCSwitcher/CCSwitcher.csproj -c Release -r win-x64 \
    --self-contained true -p:PublishSingleFile=true -o publish/
```

## Conventions discovered during implementation

### Module structure under `Core/`

Each class in `src-winui/CCSwitcher/Core/` has a single responsibility and is
testable in isolation:

- `Models.cs` — data structures (`Account`, `AppConfig`, etc.) with
  `System.Text.Json` attributes
- `AtomicFile.cs` — low-level atomic write and timestamped backup primitives
- `ClaudePaths.cs` — path resolution for Claude Code config
- `ConfigStore.cs` — load/save ccswitcher's own config.json
- `PasswordVaultSecretStore.cs` / `ISecretStore` — OS secret store abstraction
  with in-memory mock for tests
- `CredentialStore.cs` — OAuth credential snapshot/restore
- `SettingsEnv.cs` — parse and merge Claude Code's settings.json env block
- `EnvBuilder.cs` — construct env for a target account (token vs OAuth, proxy)
- `Switcher.cs` — the main switching flow (capture-on-switch-out, apply account)
- `Proxy.cs` — proxy toggle (lighter than full switch, no credential I/O)
- `Importer.cs` — detect and import the current Claude Code login
- `AccountManager.cs` — add/update/delete account CRUD

### Interface-based adapter pattern

Platform-specific I/O (secret store, credential store) sits behind interfaces:

- Define an interface with the methods the core needs (`ISecretStore`,
  `ICredentialStore`)
- Provide a real implementation (`PasswordVaultSecretStore`, the file-based
  `CredentialStore`), guarding WinRT-only APIs with conditional compilation
- Provide an in-memory mock for tests (`InMemorySecretStore`,
  `InMemoryCredentialStore`)
- Core methods accept the interface so tests inject mocks

This keeps the core platform-agnostic and fully unit-testable without a real OS
keychain or filesystem.

### Stateless core helpers

The `Core/` classes hold no state. Methods like `AccountManager.AddTokenAccount`
receive the mutable `AppConfig` and all required dependencies (secret store,
config directory) as parameters, so they are fully testable without a live OS
keyring or filesystem.

### Atomic write + backup pattern

Every destructive write to `settings.json`, `config.json`, or `.credentials.json`
follows the same sequence (`AtomicFile`):

1. Create a timestamped backup in `backups/<filename>.<timestamp>.bak`
2. Prune old backups to keep only the newest N (default: 10)
3. Write to a temp file in the same directory
4. Rename the temp file over the target (atomic on Windows)

This ensures the target file is never left half-written, and a rollback is always
available from the backups directory.

### Mutex serialization

All mutating operations (switch account, add/update/delete account, toggle
proxy, import) acquire a single app-wide `App.StateMutex` (a `SemaphoreSlim`)
before touching `config.json` or `settings.json`. This prevents interleaved
read-modify-write races. The lock is held only for the duration of the config
read/write; I/O that doesn't touch config (credential snapshot, keyring) can run
outside the lock.

### Testing approach

- **Unit tests first**: Write tests alongside or immediately after the code.
- **Success + error scenarios**: Every core function has tests for both happy
  path and failure modes (missing files, invalid JSON, keyring errors, etc.).
- **Mock the outside**: Use in-memory mocks for `ISecretStore` and
  `ICredentialStore` so tests run without real OS keychain/filesystem
  dependencies. Filesystem-touching tests use isolated temp dirs and clean up
  via `IDisposable`.
- **Test against the contract**: For `SettingsEnv.MergeEnv`, verify that managed
  keys are replaced, user keys survive, and the union of old+new managed keys is
  stripped.
- The test project pulls `Core\**\*.cs` via a `<Compile Include>` (not a
  `ProjectReference`) so it targets plain `net8.0` and never loads WinUI/Windows
  App SDK assemblies. Only `Core/` classes are testable this way; UI code-behind
  is not covered.

### Idempotency where possible

The switching flow is designed to be idempotent: re-running the same switch heals
any partial cross-store state from an aborted run. This matters because the
operation spans three independent stores (keyring, credential store,
settings/config files) and is not transactional across them.

- Credential restore failing after settings write: re-run the switch and it will
  retry the credential restore with a fresh backup of settings
- Capture-on-switch-out failing: the settings/config write proceeds anyway,
  and the next switch will re-attempt the capture

Non-idempotent operations (e.g., adding an account) are explicitly marked and
validated in the UI.

### UI integration pattern

The settings window code-behind (`SettingsWindow.xaml.cs`) and tray menu drive
the core. After any state-changing operation:

1. Acquire `App.StateMutex`.
2. Call the relevant `Core/` method (`Switcher`, `AccountManager`, `Proxy`,
   `Importer`), which writes `config.json`/`settings.json` atomically.
3. Call `App.RebuildTray()` and `Refresh()` to reflect the new state.
4. Show inline feedback via `ShowSuccess` / `ShowError`; error messages are
   sanitized with `Secrets.Sanitize` to avoid leaking tokens.
5. Release the mutex in a `finally` block.

Add/edit account dialogs are built imperatively in code-behind as
`ContentDialog`s (not XAML), and account rows are built in code-behind
(`RebuildAccountList`) rather than via a DataTemplate.

### WinUI 3 (src-winui/) conventions

Patterns discovered during implementation that are non-obvious:

- **`EnableMsixTooling=true` required for WindowsAppSDK 1.6.x with
  `PublishSingleFile`.** Without this MSBuild property the publish fails at
  link time with packaging-related errors. Set it in the `.csproj` even though
  you are not producing an MSIX package.

- **`TargetFramework` is `net8.0-windows10.0.22621.0`, driven by
  `CommunityToolkit.WinUI.Controls.SettingsControls` 8.1.** That package ships
  its WinUI assemblies *only* under the `net8.0-windows10.0.22621` TFM — the
  `net8.0-windows10.0.18362` asset is an empty `_._` placeholder. A project
  targeting a platform version below 22621 (e.g. the original 19041) restores
  fine but NuGet resolves the empty placeholder, so the `CommunityToolkit.WinUI`
  namespace silently won't exist and the XAML compiler fails with CS0234. The
  fix is to bump the platform version to 22621; `TargetPlatformMinVersion` is
  kept at `10.0.17763.0` so the app still runs on older Windows 10 builds.

- **Native Windows 11 settings UI.** The settings window uses a `MicaBackdrop`
  with `ExtendsContentIntoTitleBar = true` and a custom draggable `AppTitleBar`
  grid (set via `SetTitleBar` after `InitializeComponent`). Rows are
  CommunityToolkit `SettingsCard` / `SettingsExpander` controls grouped under
  `BodyStrongTextBlockStyle` section headers. Account cards are still built in
  code-behind (`RebuildAccountList`) rather than via a DataTemplate. The 8.x
  toolkit needs no resource-dictionary merge in `App.xaml` — default styles load
  from the package's `generic.xaml` automatically.

- **WinUI 3 `Window` has no `Loaded` event.** Use the `Activated` event
  (fires once the HWND is live) for any initialization that requires the window
  handle. Guard with a `_initialized` flag so the body runs only once.

- **`PasswordVaultSecretStore` wrapped in `#if WINDOWS10_0_19041_0_OR_GREATER`.**
  The `Windows.Security.Credentials.PasswordVault` WinRT API is only available
  on the Windows 10 19041+ TFM. The test project targets a portable TFM that
  doesn't satisfy this condition, so the real implementation must be inside the
  conditional-compilation guard. Tests inject `InMemorySecretStore` instead.

- **`H.NotifyIcon.WinUI` tray integration.** Declare the `TaskbarIcon` in
  `App.xaml` as an application-level resource; access it from anywhere via
  `Application.Current.Resources`. The icon's context menu is a `MenuFlyout`
  defined in the same XAML resource; rebuild it by replacing menu items rather
  than recreating the whole `TaskbarIcon`.

- **Single-instance enforcement.** Use a named `Mutex` (acquired in
  `App.OnLaunched`). If acquisition fails, send a named-pipe message to the
  existing instance to bring its window to the foreground, then exit. The
  existing instance listens on the pipe in a background thread.

- **`dotnet publish` for self-contained single `.exe`:**
  ```
  dotnet publish CCSwitcher/CCSwitcher.csproj -c Release -r win-x64 \
      --self-contained true -p:PublishSingleFile=true -o publish/
  ```
  The output `publish/CCSwitcher.exe` has no external runtime dependency.
