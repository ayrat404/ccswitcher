// The global HTTP proxy toggle.
//
// Port of src-tauri/src/core/proxy.rs.
//
// Flipping the proxy on or off is a *lighter* operation than a full account
// switch: it re-writes only the active account's env in settings.json so the
// proxy keys (HTTP_PROXY / HTTPS_PROXY / NO_PROXY) appear or disappear, while
// leaving the rest of the switching machinery alone. In particular it:
//
//  - does NOT touch the OAuth credential store, and
//  - does NOT perform capture-on-switch-out.
//
// This is enforced BY CONSTRUCTION: ProxyDeps has no ICredentialStore field,
// so SetEnabled cannot read or write one even by accident.
//
// Flow
// ----
//  1. Update config.Proxy.Enabled.
//  2. If there is no active account → persist config only (no settings write).
//  3. If active account id is dangling (deleted) → same: persist config only.
//  4. Load settings.json (invalid JSON aborts before any mutation).
//  5. Rebuild active account's env via EnvBuilder.Build with the updated proxy.
//     Token accounts fetch their secret from the keyring; OAuth accounts do not.
//  6. Merge env via SettingsEnv.MergeEnv (backup + atomic write).
//  7. Persist config (managed_keys may have changed; proxy.Enabled already set).

using System.Text.Json;

namespace CCSwitcher.Core;

// ---------------------------------------------------------------------------
// ProxyDeps
// ---------------------------------------------------------------------------

/// <summary>
/// I/O dependencies for a proxy toggle.
/// <para>
/// Deliberately <em>smaller</em> than <see cref="SwitchDeps"/>: there is no
/// <c>ICredentialStore</c>, because a proxy toggle never touches OAuth
/// credentials. This makes the "no credential-store I/O" guarantee structural
/// rather than a matter of convention.
/// </para>
/// </summary>
public sealed class ProxyDeps
{
    /// <summary>Path to Claude Code's <c>settings.json</c> (the file whose env is edited).</summary>
    public required string SettingsPath { get; init; }

    /// <summary>Directory holding ccswitcher's own <c>config.json</c>.</summary>
    public required string ConfigDir { get; init; }

    /// <summary>
    /// OS keyring for per-account secrets (needed to rebuild a token account's env).
    /// </summary>
    /// <remarks>
    /// Note: there is deliberately no <c>ICredentialStore</c> here. A proxy
    /// toggle must never touch the OAuth credential store.
    /// </remarks>
    public required ISecretStore SecretStore { get; init; }

    // NO ICredentialStore — by design.
}

// ---------------------------------------------------------------------------
// ProxyException
// ---------------------------------------------------------------------------

/// <summary>Raised when the proxy toggle encounters an unrecoverable error.</summary>
public sealed class ProxyException : Exception
{
    public ProxyException(string message) : base(message) { }
    public ProxyException(string message, Exception inner) : base(message, inner) { }
}

// ---------------------------------------------------------------------------
// Proxy
// ---------------------------------------------------------------------------

/// <summary>
/// Global HTTP proxy toggle.
/// All public methods are static; callers hold <see cref="AppConfig"/> and
/// pass it in.
/// </summary>
public static class Proxy
{
    /// <summary>
    /// Set whether the global HTTP proxy is enabled and re-apply it to the
    /// active account's env.
    /// <para>
    /// Mutates <paramref name="config"/> in place (<c>Proxy.Enabled</c>, and
    /// on a settings write also <c>ManagedKeys</c>) and persists to disk.
    /// </para>
    /// <para>
    /// When no account is active (or the active id is dangling), only the flag
    /// is persisted; <c>settings.json</c> is never created or modified.
    /// </para>
    /// </summary>
    /// <param name="config">Current app config (mutated in-place on success).</param>
    /// <param name="enabled">New proxy enabled state.</param>
    /// <param name="deps">I/O dependencies (no credential store — by design).</param>
    /// <exception cref="ProxyException">
    /// Settings load or write failed, or the active token account is missing its
    /// secret.
    /// </exception>
    public static void SetEnabled(AppConfig config, bool enabled, ProxyDeps deps)
    {
        // Step 1: mutate config flag immediately.
        // ProxySettings uses init-only properties, so we create a new instance.
        config.Proxy = new ProxySettings
        {
            Enabled = enabled,
            Url     = config.Proxy.Url,
            NoProxy = config.Proxy.NoProxy,
        };

        // Step 2: no active account → persist flag only, never touch settings.json.
        var activeId = config.ActiveAccountId;
        if (activeId is null)
        {
            ConfigStore.Save(deps.ConfigDir, config);
            return;
        }

        // Step 3: dangling id (account deleted) → same: persist flag only.
        var active = config.Accounts.Find(a => a.Id == activeId);
        if (active is null)
        {
            ConfigStore.Save(deps.ConfigDir, config);
            return;
        }

        // Step 4: load settings (invalid JSON aborts before any mutation).
        System.Text.Json.Nodes.JsonObject settings;
        try
        {
            settings = SettingsEnv.Load(deps.SettingsPath);
        }
        catch (SettingsEnvException ex)
        {
            throw new ProxyException($"failed to load settings.json: {ex.Message}", ex);
        }

        // Step 5: rebuild active account's env with the *updated* proxy.
        // Token accounts need their secret; OAuth accounts do not.
        string? secret = active.AccountType == AccountType.Token
            ? deps.SecretStore.Get(activeId)
            : null;

        Dictionary<string, string> newEnv;
        try
        {
            newEnv = EnvBuilder.Build(active, secret, config.Proxy);
        }
        catch (MissingSecretException ex)
        {
            throw new ProxyException($"active token account is missing its secret: {ex.Message}", ex);
        }

        // Step 6: merge (strip union of MANAGED_KEYS + stored keys, insert new env).
        var (mergedSettings, newManagedKeys) =
            SettingsEnv.MergeEnv(settings, config.ManagedKeys, newEnv);

        // Timestamped backup + atomic write.
        var settingsBackupsDir = Path.Combine(
            Path.GetDirectoryName(deps.SettingsPath) ?? ".", "backups");

        try
        {
            AtomicFile.Backup(deps.SettingsPath, settingsBackupsDir);
        }
        catch (IOException ex)
        {
            throw new ProxyException($"failed to back up settings.json: {ex.Message}", ex);
        }

        var settingsJson = mergedSettings.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true });
        try
        {
            AtomicFile.Write(deps.SettingsPath, settingsJson);
        }
        catch (IOException ex)
        {
            throw new ProxyException($"failed to write settings.json: {ex.Message}", ex);
        }

        // Step 7: persist config (managed_keys may have changed).
        config.ManagedKeys = newManagedKeys;
        ConfigStore.Save(deps.ConfigDir, config);
    }
}
