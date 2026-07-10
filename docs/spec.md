# ccswitcher — platform-independent specification

This document is the **single source of truth** for ccswitcher's behaviour,
independent of any implementation language. The Windows app (`src-winui/`, C#)
and any future native app (e.g. macOS / Swift) **must** conform to it
byte-for-byte where this spec says so, because they read and write the **same
on-disk files** and the **same OS-keychain entries** for the same user.

> If an implementation must deviate, update this spec in the same change and
> note the per-platform difference explicitly (see [§9 Platform matrix](#9-platform-matrix)).

---

## 1. What ccswitcher is

ccswitcher is an **external manager** for Claude Code accounts. Claude Code
itself is unchanged and unaware of ccswitcher. Switching accounts means editing
Claude Code's own configuration so the next `claude` launch picks up a different
identity:

- **`~/.claude/settings.json`** — the `env` object is edited (managed keys only).
- **OAuth credential store** — for native Anthropic OAuth accounts, a saved
  credential snapshot is restored.
- **User config `oauthAccount`** — best-effort swap of the active OAuth
  account's identity block. The user config is the first existing of
  `~/.claude/.claude.json` then `~/.claude.json` (see
  [§9 Platform matrix](#9-platform-matrix) for the exact candidate order).

Already-running `claude` sessions are unaffected; only new launches pick up the
change.

---

## 2. Two hard invariants (never violate)

### INV-1 — App owns only managed keys
ccswitcher only ever touches a known set of keys inside `settings.json`'s `env`
object, plus the active account's `extra_env` keys. **All other `env` keys and
every non-`env` setting (permissions, mcp, hooks, …) are preserved.** The file
is never blindly rewritten — it is parsed, the managed keys are surgically
replaced, and the rest is left exactly as found.

The constant managed-key set is:

```
ANTHROPIC_BASE_URL
ANTHROPIC_AUTH_TOKEN
ANTHROPIC_API_KEY
HTTP_PROXY
HTTPS_PROXY
NO_PROXY
```

### INV-2 — Capture-on-switch-out for OAuth
Claude Code refreshes OAuth tokens **in place**, so a one-time import snapshot
goes stale. Before switching *away* from a native OAuth account, ccswitcher
re-snapshots that account's **live** credential blob (and its `oauthAccount`
identity block) into the OS keychain. Restore-on-switch-in then always uses the
freshest blob. **These keychain writes are intentional and are never rolled
back, even if a later step of the switch fails.**

---

## 3. Data model (`config.json`)

ccswitcher persists its own **non-secret** state in a single JSON file.
Secrets never appear here. JSON field names and enum string values below are
**normative** — both platforms must serialize identically.

### 3.1 Root object

| JSON field              | Type              | Default                        | Notes |
|-------------------------|-------------------|--------------------------------|-------|
| `schema_version`        | int               | `1`                            | Bump only on a breaking change. |
| `active_account_id`     | string \| null    | omitted when null              | Currently active account's `id`. |
| `proxy`                 | object            | see [§3.3](#33-proxysettings)  | Global single proxy toggle. |
| `managed_keys`          | string[]          | `[]`                           | Exact env keys ccswitcher wrote on the **last** switch. Drives stale-key cleanup. |
| `tracked_settings_keys` | string[]          | `["model"]`                    | Top-level `settings.json` keys captured/restored per account. `[]` disables. |
| `accounts`              | Account[]         | `[]`                           | All known accounts. |

Serialization rules:
- Pretty-printed (indented) UTF-8 **without BOM** (see [INV in §6](#6-atomic-write--backup)).
- Null-valued optional fields are **omitted**, not written as `null`
  (`active_account_id`, and the optional Account fields below).

### 3.2 Account

| JSON field      | Type                       | Applies to | Notes |
|-----------------|----------------------------|------------|-------|
| `id`            | string (UUID)              | both       | Stable unique id. Also the keychain entry name (see [§5](#5-secret-storage)). |
| `name`          | string                     | both       | User-facing display name. |
| `type`          | `"anthropic_oauth"` \| `"token"` | both | **Field name is `type`, not `account_type`.** |
| `base_url`      | string (optional)          | both       | Written as `ANTHROPIC_BASE_URL` when present. Omitted when null. |
| `auth_kind`     | `"auth_token"` \| `"api_key"` (optional) | token only | Which env var the secret goes into. Omitted for OAuth. |
| `identity`      | string (optional)          | oauth only | Stable identity (email / accountUuid) for dedup. Omitted when null. |
| `extra_env`     | object<string,string> (optional) | both | Extra env vars applied on switch-in and **re-captured from the live `settings.json` env on switch-out** (so manual edits to these keys persist into the account). **Omitted entirely when empty.** |
| `saved_settings`| object (optional)          | both       | Per-account snapshot of `tracked_settings_keys` values. Omitted when null. |

### 3.3 ProxySettings

| JSON field  | Type   | Default                    |
|-------------|--------|----------------------------|
| `enabled`   | bool   | `false`                    |
| `url`       | string | `"http://127.0.0.1:8080"`  |
| `no_proxy`  | string | `"localhost,127.0.0.1"`    |

### 3.4 Documented example

```jsonc
{
  "schema_version": 1,
  "active_account_id": "11111111-1111-1111-1111-111111111111",
  "proxy": {
    "enabled": false,
    "url": "http://127.0.0.1:8080",
    "no_proxy": "localhost,127.0.0.1"
  },
  "managed_keys": ["ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN"],
  "tracked_settings_keys": ["model"],
  "accounts": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "name": "Work",
      "type": "token",
      "base_url": "https://api.anthropic.com",
      "auth_kind": "auth_token",
      "extra_env": { "CUSTOM_VAR": "value" }
    },
    {
      "id": "22222222-2222-2222-2222-222222222222",
      "name": "personal@example.com",
      "type": "anthropic_oauth",
      "identity": "personal@example.com"
    }
  ]
}
```

---

## 4. Env-build rules (per account)

Given a target `Account`, its secret (token accounts only), and the global
`ProxySettings`, build the env key/value map to inject into `settings.json`'s
`env`. Order of construction is normative because later steps may override
earlier ones.

1. **Token account:** require a **non-empty** secret. Empty/missing secret →
   abort the whole switch *before any write* (`MissingSecret` error). Write the
   secret into:
   - `ANTHROPIC_AUTH_TOKEN` when `auth_kind == "auth_token"` (this is also the
     default when `auth_kind` is unset);
   - `ANTHROPIC_API_KEY` when `auth_kind == "api_key"`.
2. **OAuth account:** write **no** token key. (The secret is the credential
   blob, restored to the credential store, not the env.)
3. **base_url (both types):** if the account has a non-empty `base_url`, set
   `ANTHROPIC_BASE_URL`.
4. **Proxy:** if `proxy.enabled`, set `HTTP_PROXY = proxy.url`,
   `HTTPS_PROXY = proxy.url`, `NO_PROXY = proxy.no_proxy`.
5. **extra_env (merged last):** copy every `extra_env` entry in. This **may add
   arbitrary keys or override any of the above** (including managed keys). These
   values are re-captured from the live env on switch-out (see [§7 step 4](#7-switch-flow-the-8-steps)),
   so `extra_env` is read-write, not write-only.

---

## 5. Secret storage

Secrets — token strings and OAuth credential blobs — **never** go in
`config.json` or logs. They live in the OS keychain.

- **Service / collection name:** `ccswitcher`.
- **Entry key (resource / account):**
  - the bare account `id` → the account's primary secret (token value, or OAuth
    credential blob snapshot);
  - `"{id}#oauthAccount"` → the account's snapshot of the `oauthAccount`
    identity block from `~/.claude.json`. **The `#oauthAccount` suffix is
    normative** so the two entries never collide.

On account delete, **both** keychain entries (`id` and `{id}#oauthAccount`) are
removed.

> This `ccswitcher` keychain is distinct from Claude Code's own OAuth credential
> store; see [§9](#9-platform-matrix) for the platform-specific location of each.

---

## 6. Atomic write + backup

Every destructive write to `settings.json`, `config.json`, `.credentials.json`,
or `~/.claude.json` follows the same sequence:

1. **Backup** the existing target into a `backups/` directory *next to the
   target file*: `backups/<filename>.<timestamp>.bak`. No-op if the target
   doesn't exist yet.
   - **Timestamp format (normative):** `yyyyMMdd_HHmmss_fff` in **UTC**, so
     filenames sort lexicographically in chronological order and stay consistent
     across implementations.
2. **Prune** old backups for that filename, keeping at most **N = 10** newest.
   Only files matching `<filename>.*.bak` are considered; other files are never
   touched.
3. **Write** to a temp file `<path>.tmp` in the same directory, then **rename**
   over the target (atomic on the same filesystem). Clean up the temp file on
   failure.

**Encoding (normative): UTF-8 without BOM.** Claude Code's `.credentials.json`
reader rejects a leading BOM, so *every* file ccswitcher writes must be BOM-free.

---

## 7. Switch flow (the 8 steps)

`ApplyAccount(config, accountId)` must execute in this exact order. The
operation spans three independent stores (keychain, credential store,
settings/config files) and is **not transactional** across them — but it **is
idempotent**: re-running the same switch heals partial cross-store state from an
aborted run.

1. **Validate** the target id exists. Unknown id → typed error, **no store
   touched**.
2. **Capture-on-switch-out** ([INV-2](#inv-2--capture-on-switch-out-for-oauth)):
   if the *currently active* account is a **still-existing OAuth** account,
   re-snapshot its live credential blob into the keychain (`id`), and best-effort
   re-snapshot its live `oauthAccount` block into `{id}#oauthAccount`. These
   writes are never rolled back.

   This capture is **unconditional** with respect to the target — it runs even
   when the target *is* the currently-active account. That is deliberate: the
   live blob is read here, *before* the step-9 restore overwrites it, so
   re-switching to the already-active account refreshes the stored snapshot with
   the latest in-place-refreshed token instead of clobbering it with a stale one.
   This is what makes a same-account re-switch idempotent. (Do **not** gate this
   on `active_id != target_id`.)
3. **Load** `settings.json`. Invalid JSON → abort **before any mutation**.
4. **Capture the outgoing account's live state into its own record**
   (persisted in step 8):
   - **Tracked settings:** snapshot the live values of `tracked_settings_keys`
     (e.g. top-level `model`) from `settings.json` into the *outgoing* account's
     `saved_settings`. Per-key tri-state: present→store deep clone;
     absent/null→store JSON `null` (means "default").
   - **`extra_env`:** re-read the live values of the outgoing account's **own**
     `extra_env` keys from `settings.json`'s `env` back into its `extra_env`, so
     manual edits the user made to those keys (e.g. `ANTHROPIC_*_MODEL` values)
     are saved on switch-out instead of being silently overwritten on the next
     switch. Per key: present & non-empty string→overwrite; absent / empty /
     non-string→drop the key (a manual deletion is respected). Only the
     account's own `extra_env` keys are read back; the constant managed keys
     (token, `base_url`, proxy) are owned by ccswitcher and are **never**
     captured here.
5. **Build target env** ([§4](#4-env-build-rules-per-account)). Missing token
   secret → abort **before any settings write**.
6. **Merge env:** in `settings.json`'s `env` object, remove the **union** of the
   constant managed set and `config.managed_keys` (the latter cleans up stale
   `extra_env` keys from the previous account), then insert the freshly-built
   env. The set of keys written becomes the new `managed_keys`.
7. **Restore tracked settings of the incoming account:** write back the target's
   `saved_settings` for `tracked_settings_keys`. Per-key tri-state: never
   captured (key absent / snapshot null)→leave as-is (first switch keeps current
   value); captured null→remove the key; captured value→write deep clone.
8. **Backup + atomic write** `settings.json` ([§6](#6-atomic-write--backup)).
9. **Restore OAuth credentials** (OAuth target only, *after* the settings
   write): if a credential snapshot exists for the target, write it to the
   credential store (**this may fail the switch**). No snapshot yet (freshly
   imported) → switch still succeeds. Then best-effort merge the target's
   `oauthAccount` snapshot back into `~/.claude.json` (failure must **not** fail
   the switch).
10. **Persist config:** set `config.managed_keys` and `config.active_account_id`,
    then atomic-write `config.json`.

> (Steps are numbered 1–10 above for precision; historically described as "8
> steps" because 4/7 and 9 are sub-phases of the env merge and credential
> restore. The ordering, not the count, is what matters.)

### Dangling active id
On startup (or before a switch), if `active_account_id` refers to an account
that no longer exists, clear it to `null` so it can't trigger a spurious
capture-on-switch-out.

### Editing the active account in-app re-applies its env
When an account is edited in-app (`name`, `base_url`, `auth_kind`, secret, or
`extra_env`) **and** it is the currently-active account, ccswitcher re-applies
that account's env to `settings.json` immediately — a "lighter than a switch"
re-apply (`Switcher.ReapplyActiveAccountEnv`) that performs **no**
capture-on-switch-out and **no** credential-store I/O (it cannot reuse
`ApplyAccount`, whose capture-on-switch-out would read the stale pre-edit values
and clobber the edit). So the edit takes effect at once rather than only on the
next switch. Editing a **non-active** account updates only its stored definition
and never touches `settings.json`.

---

## 8. Import (detect current login)

ccswitcher can adopt the login Claude Code is *currently* using as a new managed
account.

**Detection** (`Detect`) is **value-based** — it does *not* consult
`managed_keys` at all (that list is a sticky union across all switches, so
key-name matching would wrongly block importing a token the user swapped in
out-of-band, e.g. a different provider, even when no ccswitcher account matches
its value). Priority order:

1. **Token in env:** if `settings.json` `env` has a non-empty
   `ANTHROPIC_AUTH_TOKEN` → token candidate (`auth_kind = auth_token`,
   capturing `ANTHROPIC_BASE_URL` if present). Otherwise the same check for
   `ANTHROPIC_API_KEY` (`auth_kind = api_key`). `AUTH_TOKEN` takes priority over
   `API_KEY`. Whether it is already one of our accounts is decided later,
   value-based, by `FindDuplicate`.
2. **Live token suppresses OAuth:** if *any* token key (managed or not) is present
   and non-empty, the credential blob is not the live login (Claude Code prefers
   env tokens over OAuth), so detection returns the token candidate from step 1
   rather than falling back to OAuth.
3. **OAuth fallback:** only when no token key is live — if the credential store
   has a non-empty blob → OAuth candidate. Identity is taken from the user
   config's `oauthAccount` (accountUuid / emailAddress) when available, else
   extracted from the blob.

**Already-managed active account** (`FindCurrentManagedAccount`): a precise,
value-checked short-circuit the UI calls so it can report *"current login is
already imported as X"* instead of re-detecting. It considers **only the active
token account**: returns that account iff its auth key
(`ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_API_KEY`, per its `auth_kind`) is in
`managed_keys`, is present & non-empty in the live `settings.json` env, **and**
still equals the secret stored in the keychain (so a manually-swapped token isn't
misreported). OAuth accounts are intentionally not re-checked here — they are
handled by `Detect` + `FindDuplicate` via identity.

**Duplicate detection** (`FindDuplicate`):
- **Token:** duplicate iff same `base_url` **and** same `auth_kind` **and** same
  secret value (read from keychain). Two different keys for the same provider are
  **not** duplicates.
- **OAuth:** duplicate iff same `identity`. If the candidate has no identity, no
  duplicate is reported. (Blob fingerprinting is intentionally not used.)

**Default name** suggestion: token with base_url → host of the base_url; token
without → `"Token Account"`; OAuth with an email identity → that email; OAuth
otherwise → `"Anthropic"`.

**Adopting current env vars:** on import the UI pre-fills the account's
`extra_env` with only the **model-selector** entries from the current
`settings.json` `env` — keys that start with `ANTHROPIC_` **and** contain
`_MODEL` (e.g. `ANTHROPIC_MODEL`, `ANTHROPIC_SMALL_FAST_MODEL`,
`ANTHROPIC_DEFAULT_*_MODEL`). Model choice is inherently per-account, so it must
switch with the account. All **other** env entries are intentionally **not**
adopted: they are typically shared across logins, and by staying out of any
account's `extra_env` (and therefore out of `managed_keys`) they remain untouched
user env that survives every switch (INV-1). The constant managed keys
(`ANTHROPIC_BASE_URL`, the token keys, the proxy keys) are excluded regardless,
since they are captured separately as the secret / `base_url` / global proxy.
The user may edit the pre-filled set before confirming — including adding any
other variable they want to be account-specific. `Import` accepts this optional
`extra_env` and records it on the created account. (The adopted model keys are
already live in `settings.json`, so after import `managed_keys` — built from the
account's full env — matches on-disk state and a later switch strips them
cleanly.)

**Import result** (`Import`): creates the account, stores its secret in the
keychain under the new `id`, and returns one of two outcomes:
- **Created** — no duplicate detected.
- **CreatedWithWarning** — a duplicate *was* detected (per `FindDuplicate`); the
  account is **still created**, but the result carries a human-readable warning
  (e.g. *"An account with the same login (X) already exists."*) for the UI to
  surface. Duplicate-blocking is primarily enforced up-front by the UI; this
  in-core check is a safety net (and the single source of truth for the rule).

`oauthAccount` handling: only the single `oauthAccount` key of the user config
is ever read/swapped; **all other keys (userID, projects, tips, settings, …) are
the user's data and must never be lost.**

---

## 9. Platform matrix

Everything in §§2–8 is identical across platforms. These are the **only**
sanctioned per-platform differences — keep this table authoritative.

| Concern | Windows (`src-winui/`, C#) | macOS (future, Swift) |
|---|---|---|
| ccswitcher config dir | `%APPDATA%\ccswitcher\` (`config.json`) | `~/Library/Application Support/ccswitcher/` |
| ccswitcher secret store ([§5](#5-secret-storage)) | Windows Credential Manager via `PasswordVault`, service `ccswitcher` | Keychain generic password, service `ccswitcher` |
| Claude Code OAuth credential store | file `~/.claude/.credentials.json` (atomic write + backup) | Keychain service **`Claude Code-credentials`** |
| Claude `settings.json` | `%USERPROFILE%\.claude\settings.json` | `~/.claude/settings.json` |
| User config (`oauthAccount`) | `~/.claude\.claude.json` then `~/.claude.json` (first existing) | same candidate order under `$HOME` |
| Tray / menu-bar UI | WinUI 3 + `H.NotifyIcon.WinUI` | menu-bar (`NSStatusItem` / `MenuBarExtra`) |
| Distribution | self-contained single `.exe` | signed + notarized `.app` / `.dmg` |

> **macOS credential store note:** unlike Windows (a file), Claude Code stores
> its OAuth blob in the macOS **Keychain** under service `Claude Code-credentials`.
> The credential-store abstraction must therefore have a Keychain-backed
> implementation on macOS, while ccswitcher's *own* per-account secrets use a
> **separate** `ccswitcher` Keychain service. The atomic-write+backup contract
> in §6 still applies to the file-based stores (`settings.json`, `config.json`,
> `~/.claude.json`).

---

## 10. Concurrency

All mutating operations (switch, add/update/delete account, toggle proxy,
import) must serialize behind a single app-wide lock before touching
`config.json` or `settings.json`, to prevent interleaved read-modify-write
races. The lock is held only for the config read/write window; I/O that doesn't
touch those files (credential snapshot, keychain) may run outside it.

---

## 11. Reference implementation

The C# core under `src-winui/CCSwitcher/Core/` is the current reference
implementation of this spec. Key files map to the sections above:

| Spec section | C# file |
|---|---|
| §3 data model | `Models.cs` |
| §4 env-build | `EnvBuilder.cs` |
| §5 secret storage | `SecretStore.cs` (`ISecretStore`, `PasswordVaultSecretStore`) |
| §6 atomic write + backup | `AtomicFile.cs` |
| §7 switch flow | `Switcher.cs`, `SettingsEnv.cs` |
| §8 import | `Importer.cs`, `UserConfig.cs` |
| §9 paths | `ClaudePaths.cs` |

`src-winui/CCSwitcher.Tests/` is the executable conformance suite (xUnit, plain
`net8.0`, in-memory mocks). A second-platform implementation should mirror these
tests against the same contract.
