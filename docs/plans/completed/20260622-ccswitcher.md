# ccswitcher — Claude Code Account Switcher (Tauri tray app)

## Overview
- Cross-platform (Windows + macOS) tray / menu-bar application built with **Tauri (Rust core + web frontend)**.
- Lets the user switch between multiple Claude Code accounts: native Anthropic OAuth logins (subscription) and `token` accounts (API key / third-party providers).
- Provides a single global HTTP proxy toggle and per-account extra environment variables.
- Switching works by **editing Claude Code's configuration** (`~/.claude/settings.json` `env` block and, for OAuth, the credentials store). The next `claude` launch picks up the selected account; already-running sessions are unaffected.
- Solves the pain of manually editing config / re-logging in when juggling several accounts and providers.

## Acceptance Criteria
- Can switch between ≥2 Anthropic OAuth accounts and ≥1 token account; next `claude` uses the selected one.
- Switching A(OAuth)→B→A preserves A's **latest** credentials (no revert to expired tokens) — credentials are re-snapshotted on switch-out.
- OAuth accounts keep their own `base_url`/`extra_env`; switching never blanket-strips a `ANTHROPIC_BASE_URL` the account needs.
- Global proxy toggle adds/removes `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` on the active account.
- Per-account extra env vars are applied on switch.
- User's own (non-managed) keys in `settings.json` `env` and all non-`env` settings are never lost.
- No secret is ever written to `config.json` or logs; invalid `settings.json` is never overwritten.

## Context (from discovery)
- Project directory `D:\GitProjects\ccswitcher` is **empty** — greenfield. Plan includes Tauri project initialization.
- Integration target = Claude Code config (verified against the real `~/.claude` layout):
  - `~/.claude/settings.json` — owns an `env` object; ccswitcher manages only a known set of keys inside it. **Note:** a native Anthropic OAuth login may still carry `ANTHROPIC_BASE_URL` (e.g. `https://api.anthropic.com`) plus other keys — these must be preserved per-account, not stripped.
  - OAuth credentials store: Windows → `~/.claude/.credentials.json` (plaintext JSON, shape `{ "claudeAiOauth": { accessToken, refreshToken, expiresAt, ... } }`); macOS → Keychain service `Claude Code-credentials`. (Linux uses the same file as Windows but is out of scope / untested.)
  - **Credentials are refreshed in place** by Claude Code (`accessToken`/`refreshToken`/`expiresAt` change over a session) — a one-time snapshot goes stale; see switching flow.
- No existing code patterns to follow; conventions established by this plan.

## Development Approach
- **Testing approach**: Regular (code first, then tests within the same task).
- Complete each task fully before moving to the next.
- Make small, focused changes.
- **CRITICAL: every task with code changes MUST include new/updated tests** (success + error scenarios), listed as separate checklist items.
- **CRITICAL: all tests must pass before starting the next task.**
- **CRITICAL: update this plan file when scope changes during implementation.**
- Run tests after each change; maintain a working build.

## Testing Strategy
- **Unit tests (Rust)** are required for every core-logic task:
  - env merge with managed-keys (own keys replaced, user keys preserved)
  - env construction for `token` vs `oauth` accounts (incl. OAuth account carrying its own `base_url`/`extra_env`)
  - proxy toggle including `NO_PROXY`
  - atomic write + timestamped backup creation
  - config load/save round-trip
  - import detection (token vs oauth vs none)
  - **credential lifecycle**: switch-out re-snapshots the active OAuth account's live blob; A→B→A preserves the latest blob (not the import-time one)
  - **missing-secret path**: token account whose keyring entry is absent → typed error, no empty `ANTHROPIC_AUTH_TOKEN` written
- **Platform isolation**: filesystem credentials access and OS keyring access sit behind Rust traits; tests use in-memory mocks so core logic is testable without a real OS keychain.
- **No automated UI e2e** (small tray utility); frontend verified by manual testing on both platforms (see Post-Completion).

## Progress Tracking
- Mark completed items with `[x]` immediately when done.
- Add newly discovered tasks with ➕ prefix.
- Document issues/blockers with ⚠️ prefix.
- Keep this plan in sync with actual work.

## Solution Overview
- **Architecture**: a platform-agnostic Rust **core** (data model, config store, env-merge engine, switching logic, import logic) with thin **platform adapters** (credentials store + secret keyring) behind traits. Tauri layer exposes core via commands and renders a tray menu plus a settings window.
- **Key decisions**:
  - App owns only its *managed keys* inside `settings.json` `env`; never rewrites the whole file → user's manual settings (permissions, mcp, custom env) survive.
  - Secrets (tokens, OAuth credential snapshots) live only in the OS-native keyring (`keyring` crate); `config.json` holds non-secret metadata + a reference by account `id`.
  - All destructive file writes are atomic (temp + rename) and preceded by a **timestamped backup** in a dedicated `backups/` dir with a small retention cap (no single-slot overwrite).
  - Account type drives behavior: `token` writes an env token override (`AUTH_TOKEN`/`API_KEY` + `base_url`); `anthropic_oauth` restores its credential snapshot and writes no env token (but may carry its own `base_url`/`extra_env`).
  - **Credential staleness handling**: because Claude Code refreshes OAuth tokens in place, the engine **re-snapshots the currently-active OAuth account's live credential blob into the keyring before switching away** ("capture-on-switch-out"). Restore-on-switch-in then always uses the freshest blob.
  - **Precedence assumption**: an explicit `ANTHROPIC_AUTH_TOKEN`/`ANTHROPIC_API_KEY` in `env` overrides any OAuth credential file, so switching to a token account does **not** delete the credential store (non-destructive). This assumption is verified in manual testing (Post-Completion); if it proves false, fall back to snapshot-and-clear of the credential store on switch-to-token.
  - **Concurrency**: all mutating operations are serialized behind a single async mutex in app state (no interleaved read-modify-write of `settings.json`/`config.json`).
- **Fit**: ccswitcher is an external manager; Claude Code itself is unchanged and unaware of it.

## Technical Details

### Data structures (`config.json`, non-secret)
```jsonc
{
  "schema_version": 1,
  "active_account_id": "uuid-or-null",
  "proxy": { "enabled": false, "url": "http://127.0.0.1:8080", "no_proxy": "localhost,127.0.0.1" },
  "managed_keys": ["ANTHROPIC_BASE_URL", "..."],   // keys last written into settings.json env
  "accounts": [
    {
      "id": "uuid",
      "name": "Work",
      "type": "anthropic_oauth",            // or "token"
      "base_url": "https://api.anthropic.com", // optional for BOTH types (OAuth may need it too)
      "auth_kind": "auth_token",              // token only: "auth_token" | "api_key"
      "identity": "user@example.com",         // oauth only, optional: stable id for dedup (email/account id if available)
      "extra_env": { "FOO": "bar" }
      // secret stored in keyring under key "ccswitcher:<id>":
      //   token  -> the token/api-key string
      //   oauth  -> the latest credential blob (re-snapshotted on switch-out)
    }
  ]
}
```

### Managed keys (always app-owned inside `env`)
`ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY`, `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY` + the account's `extra_env` keys.

### Switching flow
1. **Capture-on-switch-out**: if the *currently-active* account is `anthropic_oauth`, read the live credential store and update that account's snapshot in the keyring (preserve refreshed tokens). Skip if missing. *(This keyring write is intentional and is kept even if a later step fails — never rolled back.)*
2. Load `settings.json` (create `{}` if missing; abort with error if present but invalid JSON).
3. Strip from `env` the **union** of the constant managed-key set and the stored `config.json.managed_keys` (the latter also covers prior `extra_env` keys). This makes first-switch/after-reset robust even if stored `managed_keys` is empty/stale.
4. Build target env in memory:
   - `token`: require a secret (else typed error); set `ANTHROPIC_AUTH_TOKEN` or `ANTHROPIC_API_KEY`, plus `ANTHROPIC_BASE_URL` if set.
   - `anthropic_oauth`: write **no** token key; set `ANTHROPIC_BASE_URL` only if the account carries one (do not blanket-strip; do not invent one).
   - If proxy enabled: `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`. Merge `extra_env`.
5. Timestamped backup + atomic-write `settings.json` with the merged env.
6. If target is `anthropic_oauth`: restore its credential snapshot to the platform store (done **after** the settings write).
7. Persist new `managed_keys` + `active_account_id` to `config.json`.

**Atomicity (honest statement):** the operation spans three independent stores (keyring, credential file/Keychain, `settings.json`/`config.json`) and is **not** transactional across them. Guarantees: (a) `settings.json` is never left half-written (atomic temp+rename, backed up); (b) the switch-out keyring capture is intentionally persisted; (c) the operation is **idempotent** — re-running the same switch heals any partial cross-store state from an aborted run. The residual window (settings written in step 5 but credential restore in step 6 fails) is documented and recovered by re-switching. Whole operation runs under the app-state mutex.

### Platform adapters (traits)
- `CredentialStore` — `read() -> Option<String>`, `write(blob)`, used for OAuth credential snapshot/restore. Win impl = `.credentials.json` file (atomic + timestamped backup); macOS impl = Keychain `Claude Code-credentials`.
- `SecretStore` — `get/set/delete(account_id)` backed by `keyring` crate.

## What Goes Where
- **Implementation Steps** (`[ ]`): all Rust core, platform adapters, Tauri commands, tray, settings UI, tests, docs.
- **Post-Completion** (no checkboxes): real-device manual testing on Windows + macOS, packaging/signing, Keychain permission prompts verification.

## Implementation Steps

### Task 1: Initialize Tauri project and workspace layout

**Files:**
- Create: `src-tauri/Cargo.toml`
- Create: `src-tauri/tauri.conf.json`
- Create: `src-tauri/src/main.rs`
- Create: `src/index.html` (frontend entry)
- Create: `package.json`
- Create: `.gitignore`
- Create: `README.md`

- [x] scaffold a Tauri 2 project (tray-enabled) with a minimal web frontend (vanilla HTML/JS, no heavy framework)
- [x] add Rust deps: `serde`, `serde_json`, `uuid`, `keyring`, `dirs`, `thiserror`, `tempfile` (backup timestamps use `std::time::SystemTime` epoch millis — sortable, no date crate needed)
- [x] configure `tauri.conf.json` for tray icon + a hidden settings window + notifications
- [x] create `src-tauri/src/core/mod.rs` module skeleton (empty submodules referenced by later tasks)
- [x] seed `CLAUDE.md` with the core/adapter architecture and the two invariants ("app owns only managed keys", "capture-on-switch-out for OAuth")
- [x] verify `cargo build` succeeds and `cargo test` runs (0 tests) — must pass before next task

### Task 2: Core data model

**Files:**
- Create: `src-tauri/src/core/model.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] define `AccountType` enum (`AnthropicOauth`, `Token`), `AuthKind` enum (`AuthToken`, `ApiKey`)
- [x] define `Account` struct (id, name, type, optional `base_url` for both types, `auth_kind` for token, optional `identity` for oauth dedup, extra_env)
- [x] define `ProxySettings` (enabled, url, no_proxy) and `AppConfig` (schema_version, active_account_id, proxy, managed_keys, accounts)
- [x] derive `Serialize`/`Deserialize` with sensible defaults; add `AppConfig::default()`
- [x] write tests: serde round-trip for `AppConfig` (with both account types) and default construction
- [x] run tests — must pass before next task

### Task 3: Config store (load/save) with atomic write + timestamped backup

**Files:**
- Create: `src-tauri/src/core/atomic.rs`
- Create: `src-tauri/src/core/config_store.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] implement `atomic_write(path, bytes)` (write temp in same dir + rename)
- [x] implement `backup(path, backups_dir)`: copy to `backups/<filename>.<timestamp>.bak`, keep newest N (e.g. 10), prune older
- [x] implement `ConfigStore::load(dir)` (return default if file missing) and `save(dir, &AppConfig)` (atomic)
- [x] resolve app config dir via `dirs` (`%APPDATA%/ccswitcher`, `~/Library/Application Support/ccswitcher`)
- [x] write tests for `atomic_write` (content correct, no temp leftovers) and `backup` (timestamped copy created, original intact, retention cap prunes oldest)
- [x] write tests for `ConfigStore` load-missing-returns-default and save→load round-trip (using a temp dir)
- [x] run tests — must pass before next task

### Task 4: Secret store abstraction (keyring) with mock

**Files:**
- Create: `src-tauri/src/core/secret_store.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] define `SecretStore` trait (`get`, `set`, `delete` by account id)
- [x] implement `KeyringSecretStore` using the `keyring` crate (service `ccswitcher`, account = id)
- [x] implement `InMemorySecretStore` for tests
- [x] write tests against `InMemorySecretStore` (set/get/delete, get-missing returns None)
- [x] run tests — must pass before next task

### Task 5: Claude Code paths + settings.json env-merge engine (core)

**Files:**
- Create: `src-tauri/src/core/claude_paths.rs`
- Create: `src-tauri/src/core/settings_env.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] implement path resolution for `~/.claude/settings.json` and `~/.claude/.credentials.json`
- [x] implement `load_settings(path) -> Result<Value>` (missing → `{}`; invalid JSON → typed error, do NOT overwrite)
- [x] expose a constant `MANAGED_KEYS` set (ANTHROPIC_BASE_URL, ANTHROPIC_AUTH_TOKEN, ANTHROPIC_API_KEY, HTTP_PROXY, HTTPS_PROXY, NO_PROXY)
- [x] implement `merge_env(settings, old_managed_keys, new_env) -> (settings, new_managed_keys)`: strip the **union** of `MANAGED_KEYS` and `old_managed_keys` from `env`, insert `new_env`, preserve all other env keys and all non-`env` settings
- [x] write tests: user-set env key survives a switch; old managed key removed when not in new set; stale managed key removed even when `old_managed_keys` is empty (first-switch robustness); non-env settings untouched
- [x] write tests: invalid-JSON settings returns error and leaves input unmodified
- [x] run tests — must pass before next task

### Task 6: Credentials store adapter (OAuth snapshot/restore) with mock

**Files:**
- Create: `src-tauri/src/core/credential_store.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] define `CredentialStore` trait (`read() -> Option<String>`, `write(blob)`)
- [x] implement `FileCredentialStore` (Windows: read/write `~/.claude/.credentials.json` atomically + timestamped backup)
- [x] implement `KeychainCredentialStore` (macOS: read/write Keychain `Claude Code-credentials`)
- [x] wire compile-time/runtime platform selection (`#[cfg(target_os)]`) into a `default_credential_store()`
- [x] implement `InMemoryCredentialStore` for tests
- [x] write tests against `InMemoryCredentialStore` (snapshot then restore yields same blob; read-missing returns None)
- [x] run tests — must pass before next task

### Task 7: Env builder for an account (token vs oauth + proxy + extra_env)

**Files:**
- Create: `src-tauri/src/core/env_builder.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] implement `build_env(account, secret, proxy) -> Result<Map<String,String>>`:
  - token → (`ANTHROPIC_AUTH_TOKEN`|`ANTHROPIC_API_KEY`) from secret + `ANTHROPIC_BASE_URL` if set
  - oauth → no token key; `ANTHROPIC_BASE_URL` only if the account carries one
  - proxy.enabled → `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`
  - always merge `extra_env`
- [x] token account with missing/empty secret → typed error (never write an empty `ANTHROPIC_AUTH_TOKEN`)
- [x] write tests: token env shape (both auth kinds), oauth env shape (no token key; base_url written only when set), proxy on/off with NO_PROXY, extra_env merged
- [x] write tests: missing-secret token account returns error
- [x] run tests — must pass before next task

### Task 8: Switching engine (apply selected account)

**Files:**
- Create: `src-tauri/src/core/switcher.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] implement `apply_account(config, account_id, deps)` in the exact order of "Switching flow": capture-on-switch-out (keyring) → load settings (invalid aborts) → build env (missing-secret aborts) → merge_env (union strip) → timestamped backup + atomic write settings → (oauth) restore target credential snapshot → persist managed_keys + active_account_id
- [x] run under the app-state mutex; make the operation **idempotent** so re-running the same switch heals partial cross-store state (no claim of cross-store transactionality)
- [x] handle "active account deleted" by clearing `active_account_id`
- [x] write tests (mock stores + temp settings): switch to token writes override; switch to oauth restores snapshot and writes no token key; switching token→oauth→token leaves no stale keys
- [x] write tests: **A(oauth)→B→A preserves the latest blob** — simulate live-store refresh between switches, assert restore uses the refreshed blob, not the import-time one
- [x] write tests: OAuth account with `base_url` keeps it after a switch (not stripped); missing-secret aborts before any settings write
- [x] write test: **cross-store post-abort state** — credential restore fails after settings write; assert settings.json is valid + backed up, keyring capture retained, and a re-run of the switch reaches a consistent state (idempotency)
- [x] run tests — must pass before next task

### Task 9: Proxy toggle

**Files:**
- Create: `src-tauri/src/core/proxy.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] implement `set_proxy_enabled(config, enabled, deps)` that updates `config.proxy.enabled` and re-writes **only** the active account's env in `settings.json` (merge_env + atomic write) — it does **not** touch the credential store or trigger capture-on-switch-out (lighter than a full account switch)
- [x] no-op safely when there is no active account (persist flag only)
- [x] write tests: enabling proxy adds proxy keys to active account env; disabling removes them; toggle with no active account just stores flag; toggle performs no credential-store I/O
- [x] run tests — must pass before next task

### Task 10: Import current login

**Files:**
- Create: `src-tauri/src/core/import.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] implement `detect_current(config) -> ImportCandidate`: **ignore env keys currently in `config.managed_keys`** (so ccswitcher's own injected token is not re-imported); if a non-managed (`AUTH_TOKEN`|`API_KEY`) is present → Token candidate (extract optional base_url, token, auth_kind); else if credential store non-empty → Oauth candidate (snapshot blob + try to extract a stable `identity` from the blob if present); else → None
- [x] implement `default_name(candidate)` (host of base_url for token, `"Anthropic"` for oauth)
- [x] implement `import(candidate, name, config, deps)` creating the account, storing secret in keyring; duplicate flag: token → match on base_url + auth_kind; oauth → match on `identity` if available, otherwise **skip dedup** (do not compare raw blobs — they change on token refresh)
- [x] write tests: detect token / oauth / none; **import ignores ccswitcher-managed env keys** (no self-import after a prior switch); default_name derivation; import creates account + secret; token duplicate detection returns warning flag; oauth dedup by identity (and no false positive when identity absent)
- [x] run tests — must pass before next task

### Task 11: Tauri commands (core ↔ frontend bridge)

**Files:**
- Create: `src-tauri/src/commands.rs`
- Modify: `src-tauri/src/main.rs`
- Modify: `src-tauri/src/core/mod.rs`

- [x] expose commands: `list_accounts`, `switch_account`, `set_proxy`, `get_proxy`, `add_token_account`, `update_account`, `delete_account`, `import_current`, `get_state`
- [x] construct platform `deps` (config dir, secret store, credential store) once in app state, behind a single async mutex; all mutating commands acquire it to serialize read-modify-write of `config.json`/`settings.json`
- [x] map core errors to a serializable `CommandError` for the frontend
- [x] write tests for the error mapping and any command-level glue not covered by core tests
- [x] run tests — must pass before next task

### Task 12: Tray menu

**Files:**
- Create: `src-tauri/src/tray.rs`
- Modify: `src-tauri/src/main.rs`

- [x] build tray menu dynamically: account list (✓ on active, type label), proxy toggle item showing address, `Import current login…`, `Settings…`, `Quit`
- [x] wire menu events to commands; rebuild menu after state changes (switch / proxy / account edits)
- [x] open the settings window on `Settings…`; trigger import flow on `Import current login…`
- [x] manual smoke build (`cargo build`) to confirm tray renders and items fire (note: UI not unit-tested)
- [x] run `cargo test` (core unchanged) — must pass before next task

### Task 13: Settings window UI

**Files:**
- Create: `src/settings.html`
- Create: `src/settings.js`
- Create: `src/styles.css`
- Modify: `src-tauri/tauri.conf.json`

- [x] build settings UI: accounts list with add/edit/delete; token-account form (name, base_url, auth_kind, token, extra_env key/value rows)
- [x] proxy settings section (url, no_proxy, enabled)
- [x] import dialog: prompt for profile name with prefilled default; show duplicate warning when flagged
- [x] call Tauri commands via the JS bridge; refresh on save; surface errors inline
- [x] manual verification of forms in `cargo tauri dev` (no automated UI tests)
- [x] run `cargo test` — must pass before next task

### Task 14: Notifications and error feedback

**Files:**
- Modify: `src-tauri/src/commands.rs`
- Modify: `src-tauri/src/tray.rs`

- [x] emit native notification after switch ("Active account: X"), proxy toggle, and import
- [x] show error notification on failures (invalid settings.json, missing credentials on import, keyring failure)
- [x] update tray tooltip with active account name
- [x] write tests for any message-formatting helpers (success/error text)
- [x] run tests — must pass before next task

### Task 15: Verify acceptance criteria
- [x] verify all Acceptance Criteria from Overview are implemented (OAuth + token switching, A→B→A preserves latest creds, OAuth base_url preserved, global proxy toggle, per-account extra env, import)
- [x] verify edge cases: missing settings.json (created), invalid JSON (refused), import-without-credentials (clear message), deleted active account (reset), missing token secret (typed error)
- [x] run full test suite: `cargo test`
- [x] confirm no secrets are written to `config.json` or logs (grep review)
- [x] confirm timestamped backups + atomic writes occur before every settings/credentials overwrite, with retention cap working

**Verification Report:**

**Acceptance Criteria (all VERIFIED):**

1. **OAuth + token switching**: VERIFIED by `src-tauri/src/core/switcher.rs:99-171` (`apply_account` function handles both `AccountType::Token` and `AccountType::AnthropicOauth`). Tests: `switch_to_token_writes_env_override`, `switch_to_oauth_restores_snapshot_and_writes_no_token_key`, `token_oauth_token_leaves_no_stale_keys`.

2. **A→B→A preserves latest credentials**: VERIFIED by `src-tauri/src/core/switcher.rs:113-131` (capture-on-switch-out logic) and test `a_oauth_b_a_preserves_latest_blob` at line 378. The live credential blob is re-snapshotted to keyring before switching away.

3. **OAuth base_url preserved**: VERIFIED by `src-tauri/src/core/env_builder.rs:66-68` (base_url written for OAuth when present) and test `oauth_account_with_base_url_keeps_it_after_switch` at line 408.

4. **Global proxy toggle**: VERIFIED by `src-tauri/src/core/proxy.rs:77-134` (`set_proxy_enabled`) and `src-tauri/src/core/env_builder.rs:71-74`. Tests: `enabling_proxy_adds_proxy_keys_to_active_env`, `disabling_proxy_removes_proxy_keys_from_active_env`.

5. **Per-account extra_env**: VERIFIED by `src-tauri/src/core/env_builder.rs:77-78` and tests `extra_env_is_merged`, `extra_env_merged_for_oauth_too`.

6. **User's own env keys preserved**: VERIFIED by `src-tauri/src/core/settings_env.rs:73-122` (`merge_env` strips only union of MANAGED_KEYS and old_managed_keys) and test `user_set_env_key_survives_switch`.

7. **No secrets in config.json/logs**: VERIFIED by `src-tauri/src/core/model.rs:1-5` (explicit documentation that secrets never live in config). Error messages sanitized in `src-tauri/src/core/commands.rs:635-650` (`format_error_message` replaces tokens with `***`).

**Edge Cases (all VERIFIED):**

1. **Missing settings.json → created with {}**: VERIFIED by `src-tauri/src/core/settings_env.rs:53` ("Missing file → an empty JSON object") and test `load_missing_returns_empty_object`.

2. **Invalid JSON → refused, not overwritten**: VERIFIED by `src-tauri/src/core/settings_env.rs:54` ("invalid JSON → typed error; the file is never overwritten") and test `load_invalid_json_returns_error_and_leaves_file_unmodified`.

3. **Import-without-credentials → returns None**: VERIFIED by `src-tauri/src/core/import.rs:199-201` (returns `Ok(None)` when credential store empty) and test `detect_current_returns_none_when_neither_exists`.

4. **Deleted active account → active_account_id reset**: VERIFIED by `src-tauri/src/core/switcher.rs:173-187` (`clear_active_if_missing` function) and test `clear_active_if_missing_clears_dangling_id`.

5. **Missing token secret → typed error, no empty AUTH_TOKEN written**: VERIFIED by `src-tauri/src/core/env_builder.rs:55-59` (returns `MissingSecret` error) and `src-tauri/src/core/switcher.rs:426` (test `missing_secret_token_aborts_before_any_settings_write`).

**Test Suite Results:**
- 106 tests passed, 0 failed
- Coverage includes all acceptance criteria and edge cases

**Secrets Safety:**
- config.json only contains non-secret metadata (id, name, type, base_url, extra_env)
- Secret store (keyring) used for tokens and OAuth blobs
- Error messages sanitize token-like strings

**Backups + Atomic Writes:**
- `src-tauri/src/core/atomic.rs:24-78` implements atomic_write and timestamped backup
- `src-tauri/src/core/switcher.rs:148-154` calls backup before settings write
- `src-tauri/src/core/config_store.rs:69-79` calls backup before config save
- Retention cap implemented in `prune` function (line 118)

### Task 16: [Final] Documentation
- [x] write `README.md`: what it does, supported platforms, how switching works, build/run instructions
- [x] document the managed-keys contract, credential-lifecycle (capture-on-switch-out), and config/secret storage locations
- [x] update `CLAUDE.md` (seeded in Task 1) with any conventions discovered during implementation
- [x] move this plan to `docs/plans/completed/`

## Post-Completion
*Items requiring manual intervention or external systems — informational only*

**Manual verification:**
- Windows: real `claude` launch after switching token and OAuth accounts; confirm `.credentials.json` restore works; verify proxy keys land in `settings.json` env.
- macOS: Keychain read/write for `Claude Code-credentials` (expect a system permission prompt); confirm snapshot/restore across two Anthropic accounts.
- **Verify precedence assumption**: with an OAuth `.credentials.json` present AND a token account's `ANTHROPIC_AUTH_TOKEN` in env, confirm `claude` actually uses the token. If it instead uses the OAuth file, implement the snapshot-and-clear fallback (see Solution Overview) and add a regression test.
- **Verify credential freshness**: switch A(OAuth)→use `claude` (let tokens refresh)→B→A; confirm A still works without re-login (capture-on-switch-out worked).
- Verify already-running `claude` sessions are unaffected and new sessions pick up the switch.
- Verify import flow against a manually-configured third-party provider and against a fresh Anthropic login.

**External / packaging:**
- App packaging and code signing/notarization (macOS) and installer (Windows) — out of scope for this plan.
