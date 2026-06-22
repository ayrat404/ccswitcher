# ccswitcher

A cross-platform (Windows + macOS) tray / menu-bar app for switching between
multiple Claude Code accounts — native Anthropic OAuth logins and `token`
accounts (API key / third-party providers) — plus a global HTTP proxy toggle and
per-account extra environment variables.

Switching works by editing Claude Code's configuration
(`~/.claude/settings.json` `env` block and, for OAuth, the credentials store).
The next `claude` launch picks up the selected account; already-running sessions
are unaffected.

## Status

Early development. Built with **Tauri 2** (Rust core + minimal web frontend).

## Build / run

Prerequisites: a Rust toolchain (`rustup`, stable). On Windows the MSVC build
tools are required; on macOS the standard Xcode command-line tools.

```sh
# from the repository root
cd src-tauri
cargo build      # build the core + app
cargo test       # run the core unit tests
```

The Rust core (`src-tauri/src/core`) is platform-agnostic and fully unit-tested
with in-memory mocks, so `cargo test` does not require a real OS keychain.

## How switching works

See `CLAUDE.md` for the managed-keys contract and the credential lifecycle
("capture-on-switch-out" for OAuth). Full documentation lands with the
documentation task.
