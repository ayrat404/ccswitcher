// Account switching engine — port of src-tauri/src/core/switcher.rs.
//
// ApplyAccount makes a chosen account the active one by editing Claude Code's
// settings.json env block and (for OAuth accounts) its credential store, in
// the exact 8-step order prescribed by the plan:
//
//  1. Validate target account exists → throw UnknownAccountException if not
//  2. Capture-on-switch-out: only if active is a different, still-existing OAuth
//     account — re-snapshot live cred blob into keyring; also re-snapshot live
//     oauthAccount from ~/.claude.json. Both writes are intentional and never
//     rolled back even if a later step fails.
//  3. Load settings.json — invalid JSON aborts before any mutation.
//  4. Build target env via EnvBuilder.Build — MissingSecretException aborts
//     before any settings write.
//  5. Merge env via SettingsEnv.MergeEnv.
//  6. Backup + atomic write settings.json.
//  7. Restore OAuth credential snapshot (after settings write); also restore
//     oauthAccount into ~/.claude.json (best-effort — failure must NOT fail the
//     switch).
//  8. Persist config: managed_keys + active_account_id.
//
// Atomicity
// ---------
// The operation spans three independent stores (keyring, credential file,
// settings/config files) and is NOT transactional across them. It IS
// idempotent: re-running the same switch heals any partial cross-store state
// left by an aborted run (e.g. a credential restore that failed after the
// settings write).

using System.Text.Json.Nodes;

namespace CCSwitcher.Core;

// ---------------------------------------------------------------------------
// Exceptions
// ---------------------------------------------------------------------------

/// <summary>The requested target account id does not exist in the config.</summary>
public sealed class UnknownAccountException : Exception
{
    public string AccountId { get; }

    public UnknownAccountException(string accountId)
        : base($"unknown account id: {accountId}")
    {
        AccountId = accountId;
    }
}

/// <summary>
/// Wraps an error that occurred during account switching, providing context
/// about which step failed.
/// </summary>
public sealed class SwitchException : Exception
{
    public SwitchException(string message) : base(message) { }
    public SwitchException(string message, Exception inner) : base(message, inner) { }
}

// ---------------------------------------------------------------------------
// SwitchDeps
// ---------------------------------------------------------------------------

/// <summary>
/// References to the I/O dependencies an <see cref="Switcher.ApplyAccount"/>
/// switch needs. Bundled so the engine is easy to call and to mock: tests supply
/// in-memory stores and temp paths.
/// </summary>
public sealed class SwitchDeps
{
    /// <summary>Path to Claude Code's <c>settings.json</c> (the file whose env is edited).</summary>
    public required string SettingsPath { get; init; }

    /// <summary>Directory holding ccswitcher's own <c>config.json</c> (persisted last).</summary>
    public required string ConfigDir { get; init; }

    /// <summary>
    /// Path to the user-level <c>~/.claude.json</c> (or <c>~/.claude/.claude.json</c>),
    /// used to capture/restore the <c>oauthAccount</c> section for OAuth accounts.
    /// <see langword="null"/> when no user config exists yet (capture/restore is skipped).
    /// </summary>
    public string? UserConfigPath { get; init; }

    /// <summary>OS keyring for per-account secrets (token strings, OAuth snapshots).</summary>
    public required ISecretStore SecretStore { get; init; }

    /// <summary>Claude Code's OAuth credential store (snapshot/restore).</summary>
    public required ICredentialStore CredentialStore { get; init; }
}

// ---------------------------------------------------------------------------
// Switcher
// ---------------------------------------------------------------------------

/// <summary>
/// Account switching engine. All public methods are static; callers hold
/// <see cref="AppConfig"/> and pass it in. See module doc for the precise
/// ordering and atomicity guarantees.
/// </summary>
public static class Switcher
{
    /// <summary>
    /// Make <paramref name="accountId"/> the active account, applying its env to
    /// <c>settings.json</c> and (for OAuth) restoring its credential snapshot.
    /// <para>
    /// On success <paramref name="config"/> is mutated in-place:
    /// <c>ManagedKeys</c> and <c>ActiveAccountId</c> are updated, then the
    /// updated config is persisted to disk.
    /// </para>
    /// </summary>
    /// <param name="config">The current app config (mutated in-place on success).</param>
    /// <param name="accountId">Target account id.</param>
    /// <param name="deps">I/O dependencies (stores + paths).</param>
    /// <exception cref="UnknownAccountException">
    /// The target account id was not found. No store was touched.
    /// </exception>
    /// <exception cref="MissingSecretException">
    /// The target is a token account but its secret is missing. No settings
    /// file was written.
    /// </exception>
    /// <exception cref="SwitchException">
    /// Any other error during the switch (settings load/write, config persist).
    /// </exception>
    public static void ApplyAccount(AppConfig config, string accountId, SwitchDeps deps)
    {
        // --- Step 1: validate target up front --------------------------------
        // An unknown id is a typed error and must not touch any store.
        var target = config.Accounts.Find(a => a.Id == accountId)
            ?? throw new UnknownAccountException(accountId);

        // --- Step 2: capture-on-switch-out -----------------------------------
        // If the currently-active account is OAuth AND is a different account
        // that still exists, re-snapshot its live credential blob AND its
        // oauthAccount section into the keyring so refreshed tokens and account
        // details are preserved. These keyring writes are intentional and are
        // never rolled back even if a later step fails.
        var activeId = config.ActiveAccountId;
        if (activeId != null)
        {
            var activeAccount = config.Accounts.Find(a => a.Id == activeId);
            if (activeAccount != null && activeAccount.AccountType == AccountType.AnthropicOauth)
            {
                // Re-snapshot live credential blob.
                var liveBlob = deps.CredentialStore.Read();
                if (liveBlob != null)
                    deps.SecretStore.Set(activeId, liveBlob);
                // Missing live blob → skip silently.

                // Also re-snapshot the live oauthAccount section.
                if (deps.UserConfigPath != null)
                {
                    try
                    {
                        var oauth = UserConfig.ReadOauthAccount(deps.UserConfigPath);
                        if (oauth != null)
                        {
                            var serialized = oauth.ToJsonString();
                            deps.SecretStore.Set(UserConfig.OauthAccountKey(activeId), serialized);
                        }
                    }
                    catch
                    {
                        // Best-effort: failure to capture oauthAccount does not abort.
                    }
                }
            }
        }

        // --- Step 3: load settings (invalid JSON aborts before any mutation) --
        JsonObject settings;
        try
        {
            settings = SettingsEnv.Load(deps.SettingsPath);
        }
        catch (SettingsEnvException ex)
        {
            throw new SwitchException($"failed to load settings.json: {ex.Message}", ex);
        }

        // --- Step 4: build target env (missing secret aborts before any write) --
        // For token accounts fetch the secret from the keyring; OAuth accounts
        // do not need a secret to build env.
        string? secret = target.AccountType == AccountType.Token
            ? deps.SecretStore.Get(accountId)
            : null;

        // May throw MissingSecretException — propagated directly (caller handles).
        var newEnv = EnvBuilder.Build(target, secret, config.Proxy);

        // --- Step 5: merge env -----------------------------------------------
        var (mergedSettings, newManagedKeys) =
            SettingsEnv.MergeEnv(settings, config.ManagedKeys, newEnv);

        // --- Step 6: backup + atomic write settings.json ---------------------
        var settingsBackupsDir = Path.Combine(
            Path.GetDirectoryName(deps.SettingsPath) ?? ".", "backups");
        try
        {
            AtomicFile.Backup(deps.SettingsPath, settingsBackupsDir);
        }
        catch (IOException ex)
        {
            throw new SwitchException($"failed to back up settings.json: {ex.Message}", ex);
        }

        var settingsJson = mergedSettings.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        try
        {
            AtomicFile.Write(deps.SettingsPath, settingsJson);
        }
        catch (IOException ex)
        {
            throw new SwitchException($"failed to write settings.json: {ex.Message}", ex);
        }

        // --- Step 7: restore OAuth credential snapshot (after settings write) --
        if (target.AccountType == AccountType.AnthropicOauth)
        {
            var snapshot = deps.SecretStore.Get(accountId);
            if (snapshot != null)
            {
                // This CAN throw (propagated — the switch fails if restore fails).
                deps.CredentialStore.Write(snapshot);
            }
            // No snapshot stored yet → switch still succeeds (freshly-imported account).

            // Restore oauthAccount section into ~/.claude.json (best-effort).
            if (deps.UserConfigPath != null)
            {
                try
                {
                    var stored = deps.SecretStore.Get(UserConfig.OauthAccountKey(accountId));
                    if (stored != null)
                    {
                        var oauthNode = JsonNode.Parse(stored);
                        if (oauthNode != null)
                            UserConfig.MergeOauthAccount(deps.UserConfigPath, oauthNode);
                    }
                }
                catch
                {
                    // Best-effort: failure to restore oauthAccount does not fail the switch.
                }
            }
        }

        // --- Step 8: persist config (managed_keys + active_account_id) -------
        config.ManagedKeys = newManagedKeys;
        config.ActiveAccountId = accountId;
        try
        {
            ConfigStore.Save(deps.ConfigDir, config);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            throw new SwitchException($"failed to persist config.json: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Clear <c>ActiveAccountId</c> when it refers to an account that no longer
    /// exists in <paramref name="config"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the active id was cleared (it was dangling);
    /// <see langword="false"/> when the active id exists or was already null.
    /// </returns>
    public static bool ClearActiveIfMissing(AppConfig config)
    {
        if (config.ActiveAccountId == null)
            return false;

        var exists = config.Accounts.Any(a => a.Id == config.ActiveAccountId);
        if (!exists)
        {
            config.ActiveAccountId = null;
            return true;
        }

        return false;
    }
}
