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
    /// <summary>
    /// Credential-blob fields that change over a session and must NOT take
    /// part in duplicate comparison.  Removing them yields a stable
    /// "fingerprint" of the login itself.
    /// </summary>
    private static readonly HashSet<string> VolatileBlobFields = new(StringComparer.Ordinal)
    {
        "accessToken",
        "refreshToken",
        "expiresAt",
        "expiresAtTimestamp",
        "tokenResponse",
        "idToken",
    };

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

        Account account;
        string secretValue;
        string? warning;

        switch (candidate)
        {
            case ImportCandidate.Token t:
            {
                // Token duplicate: match on base_url + auth_kind.
                var dup = existingAccounts.FirstOrDefault(a =>
                    a.AccountType == AccountType.Token &&
                    a.AuthKind    == t.AuthKind &&
                    string.Equals(a.BaseUrl, t.BaseUrl, StringComparison.Ordinal));

                warning = dup is not null
                    ? $"An account with the same provider ({dup.Name}) already exists."
                    : null;

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
            }

            case ImportCandidate.Oauth o:
            {
                // 1. Identity-based dedup (only when a stable identity is known).
                warning = null;
                if (o.Identity is not null)
                {
                    var dup = existingAccounts.FirstOrDefault(a =>
                        a.AccountType == AccountType.AnthropicOauth &&
                        string.Equals(a.Identity, o.Identity, StringComparison.Ordinal));

                    if (dup is not null)
                        warning = $"An account with the same identity ({dup.Name}) already exists.";
                }

                // 2. Blob fingerprint dedup: normalize (strip volatile fields) and
                //    compare against each existing OAuth account's stored blob.
                if (warning is null)
                {
                    var norm = NormalizeBlob(o.Blob);
                    if (norm is not null)
                    {
                        foreach (var acc in existingAccounts.Where(
                            a => a.AccountType == AccountType.AnthropicOauth))
                        {
                            var stored = secretStore.Get(acc.Id);
                            if (stored is not null &&
                                NormalizeBlob(stored) == norm)
                            {
                                warning = $"An account with the same login ({acc.Name}) already exists.";
                                break;
                            }
                        }
                    }
                }

                account = new Account
                {
                    Id          = id,
                    Name        = name,
                    AccountType = AccountType.AnthropicOauth,
                    Identity    = o.Identity,
                };
                secretValue = o.Blob;
                break;
            }

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
    // Internal helpers (internal so tests can exercise them)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Normalize an OAuth credential blob into a canonical, comparable string.
    ///
    /// Strips the volatile fields so that two snapshots of the *same* login —
    /// taken before and after Claude Code refreshes its tokens in place —
    /// compare equal.  Returns <see langword="null"/> if the blob isn't valid JSON.
    /// Key order is canonical because JsonObject in System.Text.Json uses
    /// insertion order; we re-parse into a SortedDictionary to guarantee
    /// deterministic output.
    /// </summary>
    internal static string? NormalizeBlob(string blob)
    {
        JsonObject parsed;
        try
        {
            var node = JsonNode.Parse(blob);
            if (node is not JsonObject obj)
                return null;
            parsed = obj;
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed.TryGetPropertyValue("claudeAiOauth", out var oauthNode) &&
            oauthNode is JsonObject oauthObj)
        {
            foreach (var key in VolatileBlobFields)
                oauthObj.Remove(key);
        }

        // Re-serialize into a stable key-sorted form so two blobs with the
        // same stable content but different insertion order compare equal.
        return SerializeSorted(parsed);
    }

    /// <summary>
    /// Serialize a JsonNode to JSON with keys sorted alphabetically at every level.
    /// </summary>
    private static string SerializeSorted(JsonNode? node)
    {
        if (node is null)
            return "null";

        if (node is JsonObject obj)
        {
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in obj)
                sorted[key] = SerializeSorted(value);

            var pairs = sorted.Select(kv => $"\"{EscapeJson(kv.Key)}\":{kv.Value}");
            return "{" + string.Join(",", pairs) + "}";
        }

        if (node is JsonArray arr)
        {
            var items = arr.Select(SerializeSorted);
            return "[" + string.Join(",", items) + "]";
        }

        // Scalar: use the built-in serializer.
        return node.ToJsonString();
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\n", "\\n")
         .Replace("\r", "\\r")
         .Replace("\t", "\\t");

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
