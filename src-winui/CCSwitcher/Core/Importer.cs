// Detect and import the current Claude Code login as a new account.
//
// Two detection paths (value-based — "is this already one of our accounts?" is
// decided by secret value via FindDuplicate, NOT by a key-name list):
// 1. Token-based: settings.json env contains a non-empty ANTHROPIC_AUTH_TOKEN
//    (preferred) or ANTHROPIC_API_KEY.
// 2. OAuth-based: only when NO token key is live, the credential store's OAuth
//    blob (a live env token always overrides the blob, per Claude Code
//    precedence).
//
// managed_keys is intentionally NOT consulted by Detect: it is a sticky union
// across all switches, so key-name matching would wrongly block importing a
// token the user swapped in out-of-band (e.g. a different provider) even when no
// ccswitcher account matches its value. FindCurrentManagedAccount still uses it
// for the precise, value-checked active-account short-circuit.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCSwitcher.Core;

// ---------------------------------------------------------------------------
// Discriminated union: ImportCandidate
// ---------------------------------------------------------------------------

/// <summary>A detected current login that can be imported as a new account.</summary>
public abstract class ImportCandidate
{
    private ImportCandidate() { }

    /// <summary>A token-based login (API key or auth token with optional base URL).</summary>
    public sealed class Token : ImportCandidate
    {
        /// <summary>The token value to store in the keyring.</summary>
        public required string Secret { get; init; }
        /// <summary>Which env variable this token belongs to.</summary>
        public required AuthKind AuthKind { get; init; }
        /// <summary>Optional base URL from the env.</summary>
        public string? BaseUrl { get; init; }
    }

    /// <summary>An OAuth-based login (native Anthropic or compatible provider).</summary>
    public sealed class Oauth : ImportCandidate
    {
        /// <summary>The raw credential blob to store in the keyring.</summary>
        public required string Blob { get; init; }
        /// <summary>
        /// A stable identity (email/account id) extracted from the blob or
        /// user config if available.  Used for duplicate detection.
        /// <see langword="null"/> means we couldn't extract one.
        /// </summary>
        public string? Identity { get; init; }
    }
}

// ---------------------------------------------------------------------------
// Discriminated union: ImportResult
// ---------------------------------------------------------------------------

/// <summary>Result of an import operation indicating whether a duplicate was detected.</summary>
public abstract class ImportResult
{
    private ImportResult() { }

    /// <summary>Account was created successfully — no duplicate detected.</summary>
    public sealed class Created : ImportResult
    {
        public required Account Account { get; init; }
    }

    /// <summary>Account was created but a duplicate may exist; warning returned to caller.</summary>
    public sealed class CreatedWithWarning : ImportResult
    {
        public required Account Account { get; init; }
        public required string Warning { get; init; }
    }
}

// ---------------------------------------------------------------------------
// Importer
// ---------------------------------------------------------------------------

/// <summary>
/// Detect and import the current Claude Code login.
/// Static port of src-tauri/src/core/import.rs public functions.
/// </summary>
public static class Importer
{
    // -----------------------------------------------------------------------
    // Detect
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detect the current Claude Code login.
    /// </summary>
    /// <param name="settingsPath">Absolute path to Claude Code's settings.json.</param>
    /// <param name="userConfigPath">
    /// Optional path to <c>~/.claude.json</c> for stable OAuth identity.
    /// May be <see langword="null"/> to skip.
    /// </param>
    /// <param name="credentialStore">Credential store reader (for OAuth detection).</param>
    /// <returns>
    /// An <see cref="ImportCandidate"/> when a login is detected, or
    /// <see langword="null"/> if nothing importable is found.
    /// </returns>
    /// <exception cref="SettingsEnvException">
    /// settings.json exists but is invalid or not a JSON object.
    /// </exception>
    /// <remarks>
    /// Detection is <b>value-based</b>, not key-name-based: any non-empty
    /// <c>ANTHROPIC_AUTH_TOKEN</c> (then <c>ANTHROPIC_API_KEY</c>) in the env is
    /// returned as a token candidate, regardless of whether ccswitcher ever wrote
    /// that key. Whether the candidate is already one of our accounts is decided
    /// separately and value-based by <see cref="FindDuplicate"/> (and the precise
    /// active-account check <see cref="FindCurrentManagedAccount"/>). This matters
    /// because <c>managed_keys</c> is a sticky union across all switches and would
    /// otherwise block importing a token the user swapped in out-of-band (e.g. a
    /// different provider) even when no ccswitcher account matches its value.
    /// </remarks>
    public static ImportCandidate? Detect(
        string settingsPath,
        string? userConfigPath,
        ICredentialStore credentialStore)
    {
        var settings = SettingsEnv.Load(settingsPath);

        JsonObject? envObj = null;
        if (settings.TryGetPropertyValue("env", out var envNode) && envNode is JsonObject eo)
            envObj = eo;

        // Any live token key is a candidate (value-based dedup happens later).
        // Prefer AUTH_TOKEN over API_KEY.
        if (envObj is not null)
        {
            if (envObj.TryGetPropertyValue("ANTHROPIC_AUTH_TOKEN", out var authTokenNode))
            {
                var authToken = authTokenNode?.GetValue<string>();
                if (!string.IsNullOrEmpty(authToken))
                {
                    var baseUrl = TryGetString(envObj, "ANTHROPIC_BASE_URL");
                    return new ImportCandidate.Token
                    {
                        Secret   = authToken,
                        AuthKind = AuthKind.AuthToken,
                        BaseUrl  = baseUrl,
                    };
                }
            }

            if (envObj.TryGetPropertyValue("ANTHROPIC_API_KEY", out var apiKeyNode))
            {
                var apiKey = apiKeyNode?.GetValue<string>();
                if (!string.IsNullOrEmpty(apiKey))
                {
                    var baseUrl = TryGetString(envObj, "ANTHROPIC_BASE_URL");
                    return new ImportCandidate.Token
                    {
                        Secret   = apiKey,
                        AuthKind = AuthKind.ApiKey,
                        BaseUrl  = baseUrl,
                    };
                }
            }
        }

        // A live env token always wins over the OAuth credential blob (Claude Code
        // prefers env tokens over OAuth), so only fall back to the blob when NO
        // token key is live. This also avoids surfacing a stale blob left on disk
        // by a previously-active OAuth account.
        if (envObj is not null && EnvTokenLive(envObj))
            return null;

        // No live token key — fall back to OAuth.
        var blob = credentialStore.Read();
        if (blob is null)
            return null;

        // Identity: prefer stable accountUuid/emailAddress from ~/.claude.json
        // oauthAccount object; fall back to fields embedded in the blob itself.
        string? identity = null;
        if (userConfigPath is not null)
            identity = ExtractIdentityFromUserConfig(userConfigPath);
        identity ??= ExtractIdentityFromBlob(blob);

        return new ImportCandidate.Oauth { Blob = blob, Identity = identity };
    }

    // -----------------------------------------------------------------------
    // DefaultName
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generate a default display name for an import candidate.
    /// </summary>
    /// <param name="candidate">The detected candidate.</param>
    /// <returns>A suggested account name.</returns>
    public static string DefaultName(ImportCandidate candidate) =>
        candidate switch
        {
            ImportCandidate.Token t when t.BaseUrl is not null =>
                // Strip scheme and path; keep only host.
                t.BaseUrl
                    .TrimStart()
                    .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("http://",  string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Split('/')[0],

            ImportCandidate.Token =>
                "Token Account",

            ImportCandidate.Oauth { Identity: { } id } when id.Contains('@') =>
                id,

            ImportCandidate.Oauth =>
                "Anthropic",

            _ => throw new InvalidOperationException("Unknown ImportCandidate type."),
        };

    // -----------------------------------------------------------------------
    // Import
    // -----------------------------------------------------------------------

    /// <summary>
    /// Import a candidate as a new account.
    /// </summary>
    /// <param name="candidate">The detected candidate to import.</param>
    /// <param name="name">User-facing display name for the new account.</param>
    /// <param name="existingAccounts">All currently configured accounts (for dedup).</param>
    /// <param name="secretStore">Keyring writer for the new account's secret.</param>
    /// <returns>
    /// <see cref="ImportResult.Created"/> when no duplicate is detected, or
    /// <see cref="ImportResult.CreatedWithWarning"/> when one is.
    /// </returns>
    public static ImportResult Import(
        ImportCandidate candidate,
        string name,
        IReadOnlyList<Account> existingAccounts,
        ISecretStore secretStore,
        Dictionary<string, string>? extraEnv = null)
    {
        var id = Guid.NewGuid().ToString();

        // Duplicate detection is primarily enforced up-front by the UI (which
        // blocks the import). This stays as a safety net for races and keeps a
        // single source of truth in FindDuplicate.
        var dup = FindDuplicate(candidate, existingAccounts, secretStore);
        var warning = dup is not null
            ? $"An account with the same login ({dup.Name}) already exists."
            : null;

        Account account;
        string secretValue;

        switch (candidate)
        {
            case ImportCandidate.Token t:
                account = new Account
                {
                    Id               = id,
                    Name             = name,
                    AccountType      = AccountType.Token,
                    BaseUrl          = t.BaseUrl,
                    AuthKind         = t.AuthKind,
                    ExtraEnvNullable = extraEnv,
                };
                secretValue = t.Secret;
                break;

            case ImportCandidate.Oauth o:
                account = new Account
                {
                    Id               = id,
                    Name             = name,
                    AccountType      = AccountType.AnthropicOauth,
                    Identity         = o.Identity,
                    ExtraEnvNullable = extraEnv,
                };
                secretValue = o.Blob;
                break;

            default:
                throw new InvalidOperationException("Unknown ImportCandidate type.");
        }

        // Store the secret in the keyring.
        secretStore.Set(id, secretValue);

        return warning is not null
            ? (ImportResult)new ImportResult.CreatedWithWarning { Account = account, Warning = warning }
            : new ImportResult.Created { Account = account };
    }

    // -----------------------------------------------------------------------
    // FindDuplicate
    // -----------------------------------------------------------------------

    /// <summary>
    /// Find an existing account that represents the same login as
    /// <paramref name="candidate"/>, or <see langword="null"/> if none.
    /// <para>
    /// <b>Token:</b> same provider <em>and</em> key — matches on
    /// <c>base_url</c> + <c>auth_kind</c> + the actual secret value (read from
    /// the keyring). Two different keys for the same provider are <em>not</em>
    /// duplicates.
    /// </para>
    /// <para>
    /// <b>OAuth:</b> identity only — matches on the stable
    /// <see cref="Account.Identity"/> (accountUuid/email from
    /// <c>~/.claude.json</c>). When the candidate has no identity, no duplicate
    /// is reported. (A blob fingerprint is intentionally not used: after
    /// stripping volatile tokens the blob is not account-unique, so it could
    /// falsely match a genuinely different account.)
    /// </para>
    /// </summary>
    public static Account? FindDuplicate(
        ImportCandidate candidate,
        IReadOnlyList<Account> existingAccounts,
        ISecretStore secretStore)
    {
        switch (candidate)
        {
            case ImportCandidate.Token t:
                return existingAccounts.FirstOrDefault(a =>
                    a.AccountType == AccountType.Token &&
                    a.AuthKind    == t.AuthKind &&
                    string.Equals(a.BaseUrl, t.BaseUrl, StringComparison.Ordinal) &&
                    string.Equals(secretStore.Get(a.Id), t.Secret, StringComparison.Ordinal));

            case ImportCandidate.Oauth o:
                if (o.Identity is null)
                    return null;
                return existingAccounts.FirstOrDefault(a =>
                    a.AccountType == AccountType.AnthropicOauth &&
                    string.Equals(a.Identity, o.Identity, StringComparison.Ordinal));

            default:
                throw new InvalidOperationException("Unknown ImportCandidate type.");
        }
    }

    // -----------------------------------------------------------------------
    // FindCurrentManagedAccount
    // -----------------------------------------------------------------------

    /// <summary>
    /// If the current Claude Code login is already a ccswitcher-managed account,
    /// return that account; otherwise <see langword="null"/>.
    /// <para>
    /// This lets the import flow short-circuit with a precise
    /// "already imported as X" message instead of re-detecting — and, crucially,
    /// avoids surfacing a <em>stale</em> OAuth credential blob left on disk by a
    /// previously-active OAuth account when a token account is now active.
    /// </para>
    /// <para>
    /// Only the <em>active</em> token account is considered: when its auth key is
    /// managed, present and non-empty in settings.json, AND still matches the
    /// secret stored in the keyring, the live login is that account. OAuth
    /// accounts are already handled correctly by <see cref="Detect"/> +
    /// <see cref="FindDuplicate"/> (their live blob matches by identity) and are
    /// intentionally not re-checked here.
    /// </para>
    /// </summary>
    /// <param name="accounts">All currently configured accounts.</param>
    /// <param name="activeAccountId">The currently active account id, or <see langword="null"/>.</param>
    /// <param name="managedKeys">Keys ccswitcher is managing (config.managed_keys).</param>
    /// <param name="settingsPath">Absolute path to Claude Code's settings.json.</param>
    /// <param name="secretStore">Keyring reader, to confirm the live token still matches.</param>
    public static Account? FindCurrentManagedAccount(
        IReadOnlyList<Account> accounts,
        string? activeAccountId,
        IReadOnlyCollection<string> managedKeys,
        string settingsPath,
        ISecretStore secretStore)
    {
        if (string.IsNullOrEmpty(activeAccountId))
            return null;

        var active = accounts.FirstOrDefault(a => a.Id == activeAccountId);
        if (active is null
            || active.AccountType != AccountType.Token
            || active.AuthKind is null)
            return null;

        var key = active.AuthKind == AuthKind.AuthToken
            ? "ANTHROPIC_AUTH_TOKEN"
            : "ANTHROPIC_API_KEY";
        if (!managedKeys.Contains(key))
            return null;

        // The managed token key must be present and non-empty in the live env…
        string? live;
        try
        {
            var settings = SettingsEnv.Load(settingsPath);
            live = settings.TryGetPropertyValue("env", out var envNode) && envNode is JsonObject envObj
                ? TryGetString(envObj, key)
                : null;
        }
        catch (SettingsEnvException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(live))
            return null;

        // …and still match the stored secret, so a manually-swapped token is not
        // misreported as the active account.
        var stored = secretStore.Get(active.Id);
        return string.Equals(live, stored, StringComparison.Ordinal) ? active : null;
    }

    // -----------------------------------------------------------------------
    // CurrentModelEnv
    // -----------------------------------------------------------------------

    /// <summary>
    /// The current <c>settings.json</c> <c>env</c> entries that are <b>model
    /// selectors</b> — keys starting with <c>ANTHROPIC_</c> that contain
    /// <c>_MODEL</c> (e.g. <c>ANTHROPIC_MODEL</c>, <c>ANTHROPIC_SMALL_FAST_MODEL</c>,
    /// <c>ANTHROPIC_DEFAULT_*_MODEL</c>). Offered as pre-filled <c>extra_env</c>
    /// when importing the current login, because model choice is inherently
    /// per-account and should switch with the account.
    /// <para>
    /// All other non-managed env entries (proxy timeouts, API tuning, arbitrary
    /// user vars, …) are intentionally <b>not</b> adopted: they are typically
    /// shared across logins, and by staying out of <c>extra_env</c> they remain
    /// untouched user env in <c>settings.json</c> that survives every switch. The
    /// user can still add any of them to the account by hand in the import dialog.
    /// The token, base URL and proxy keys are excluded regardless because they are
    /// captured separately (as the account secret / <see cref="Account.BaseUrl"/>
    /// / global proxy).
    /// </para>
    /// </summary>
    /// <param name="settingsPath">Absolute path to Claude Code's settings.json.</param>
    /// <returns>
    /// A dictionary of the string-valued model-selector env entries. Empty when
    /// settings.json is missing, invalid, or has no matching entries.
    /// </returns>
    public static Dictionary<string, string> CurrentModelEnv(string settingsPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        JsonObject settings;
        try
        {
            settings = SettingsEnv.Load(settingsPath);
        }
        catch (SettingsEnvException)
        {
            return result;
        }

        if (!settings.TryGetPropertyValue("env", out var envNode) || envNode is not JsonObject envObj)
            return result;

        foreach (var (key, node) in envObj)
        {
            if (SettingsEnv.ManagedKeys.Contains(key) || !IsModelKey(key))
                continue;
            if (node is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                result[key] = s;
        }

        return result;
    }

    /// <summary>
    /// A model-selector env key: starts with <c>ANTHROPIC_</c> and contains
    /// <c>_MODEL</c>. Such variables are per-account (the account's model choice)
    /// rather than shared, so they are adopted into <c>extra_env</c> on import.
    /// </summary>
    private static bool IsModelKey(string key) =>
        key.StartsWith("ANTHROPIC_", StringComparison.Ordinal) &&
        key.Contains("_MODEL", StringComparison.Ordinal);

    // -----------------------------------------------------------------------
    // Internal helpers (internal so tests can exercise them)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extract a stable identity (email/account_id) from an OAuth credential blob.
    /// The blob shape is <c>{"claudeAiOauth":{...}}</c>.
    /// Returns <see langword="null"/> if parsing fails or no such field is found.
    /// </summary>
    internal static string? ExtractIdentityFromBlob(string blob)
    {
        try
        {
            var node = JsonNode.Parse(blob);
            if (node is not JsonObject root)
                return null;

            if (!root.TryGetPropertyValue("claudeAiOauth", out var oauthNode) ||
                oauthNode is not JsonObject oauthObj)
                return null;

            return TryGetString(oauthObj, "email")
                ?? TryGetString(oauthObj, "account_id")
                ?? TryGetString(oauthObj, "accountId");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Read <c>~/.claude.json</c> at <paramref name="path"/> and extract its
    /// OAuth identity (<c>oauthAccount.accountUuid</c> preferred over
    /// <c>emailAddress</c>), or <see langword="null"/> when the file is missing
    /// or has no recognizable identity field.
    /// </summary>
    internal static string? ExtractIdentityFromUserConfig(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var text    = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var node    = JsonNode.Parse(text);
            if (node is not JsonObject root)
                return null;

            if (!root.TryGetPropertyValue("oauthAccount", out var oauthNode) ||
                oauthNode is not JsonObject oauthObj)
                return null;

            return TryGetString(oauthObj, "accountUuid")
                ?? TryGetString(oauthObj, "emailAddress");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Utility
    // -----------------------------------------------------------------------

    private static string? TryGetString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
            return null;
        if (node is JsonValue val && val.TryGetValue<string>(out var s))
            return string.IsNullOrEmpty(s) ? null : s;
        return null;
    }

    /// <summary>
    /// True when <em>any</em> token key (<c>ANTHROPIC_AUTH_TOKEN</c> /
    /// <c>ANTHROPIC_API_KEY</c>) is present and non-empty in settings.json's env.
    /// A live env token always overrides the OAuth credential blob (Claude Code
    /// precedence), so in that state the credential store is stale and must not be
    /// offered as an import candidate. This is independent of
    /// <c>managed_keys</c>: even an externally-set token (different provider) is
    /// the live login, and its value-based dedup is handled by
    /// <see cref="FindDuplicate"/>.
    /// </summary>
    private static bool EnvTokenLive(JsonObject envObj) =>
        !string.IsNullOrEmpty(TryGetString(envObj, "ANTHROPIC_AUTH_TOKEN"))
        || !string.IsNullOrEmpty(TryGetString(envObj, "ANTHROPIC_API_KEY"));
}
