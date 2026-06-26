// Detect and import the current Claude Code login as a new account.
//
// Port of src-tauri/src/core/import.rs.
//
// Two detection paths:
// 1. Token-based: settings.json env contains ANTHROPIC_AUTH_TOKEN or
//    ANTHROPIC_API_KEY that ccswitcher isn't already managing.
// 2. OAuth-based: the credential store has a non-empty OAuth blob.
//
// Detect() uses ONLY the caller-supplied managedKeys list for ignore logic —
// never the constant SettingsEnv.ManagedKeys.  The constant represents keys
// ccswitcher *may* write; managedKeys represents what it *has* written this
// session.

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
    /// <param name="managedKeys">
    /// Keys already managed by ccswitcher in this session (from
    /// <see cref="AppConfig.ManagedKeys"/>).  These are skipped so we never
    /// re-import our own injected values.
    /// <para><b>IMPORTANT:</b> only this list is used for ignore — never the
    /// constant <see cref="SettingsEnv.ManagedKeys"/>.</para>
    /// </param>
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
    public static ImportCandidate? Detect(
        IReadOnlyCollection<string> managedKeys,
        string settingsPath,
        string? userConfigPath,
        ICredentialStore credentialStore)
    {
        // Build the set of keys ccswitcher is already managing from config.managed_keys.
        // NOTE: We do NOT use the constant SettingsEnv.ManagedKeys here — that set
        // represents keys we *might* manage, not the ones we've *actually* written.
        var managedSet = new HashSet<string>(managedKeys, StringComparer.Ordinal);

        var settings = SettingsEnv.Load(settingsPath);

        // Check env for a non-managed token key.
        if (settings.TryGetPropertyValue("env", out var envNode) &&
            envNode is JsonObject envObj)
        {
            // Prefer AUTH_TOKEN over API_KEY.
            if (envObj.TryGetPropertyValue("ANTHROPIC_AUTH_TOKEN", out var authTokenNode))
            {
                var authToken = authTokenNode?.GetValue<string>();
                if (!string.IsNullOrEmpty(authToken) &&
                    !managedSet.Contains("ANTHROPIC_AUTH_TOKEN"))
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
                if (!string.IsNullOrEmpty(apiKey) &&
                    !managedSet.Contains("ANTHROPIC_API_KEY"))
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

        // No non-managed token key — fall back to OAuth credential store.
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
        ISecretStore secretStore)
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
                    Id          = id,
                    Name        = name,
                    AccountType = AccountType.Token,
                    BaseUrl     = t.BaseUrl,
                    AuthKind    = t.AuthKind,
                };
                secretValue = t.Secret;
                break;

            case ImportCandidate.Oauth o:
                account = new Account
                {
                    Id          = id,
                    Name        = name,
                    AccountType = AccountType.AnthropicOauth,
                    Identity    = o.Identity,
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
}
