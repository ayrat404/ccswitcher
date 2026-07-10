// Core data model for ccswitcher.
//
// These types describe the non-secret persisted state (config.json).
// Secrets (token strings, OAuth credential snapshots) never live here; they
// are stored in the OS keyring and referenced by account id.
//
// The JSON shapes deliberately match the documented config.json layout:
// - AccountType serializes as "anthropic_oauth" / "token".
// - AuthKind serializes as "auth_token" / "api_key".

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CCSwitcher.Core;

/// <summary>Kind of account ccswitcher manages.</summary>
[JsonConverter(typeof(AccountTypeConverter))]
public enum AccountType
{
    /// <summary>Native Anthropic OAuth login (subscription). Restores a credential
    /// snapshot and writes no env token (but may carry its own base_url).</summary>
    AnthropicOauth,
    /// <summary>Token account (API key / third-party provider). Writes an env token
    /// override (ANTHROPIC_AUTH_TOKEN / ANTHROPIC_API_KEY).</summary>
    Token,
}

/// <summary>Which env variable a token account writes its secret into.</summary>
[JsonConverter(typeof(AuthKindConverter))]
public enum AuthKind
{
    /// <summary>Write the secret into ANTHROPIC_AUTH_TOKEN.</summary>
    AuthToken,
    /// <summary>Write the secret into ANTHROPIC_API_KEY.</summary>
    ApiKey,
}

/// <summary>Custom converter for AccountType: serializes as "anthropic_oauth" / "token".</summary>
public sealed class AccountTypeConverter : JsonConverter<AccountType>
{
    public override AccountType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? throw new JsonException("Expected a string for AccountType.");
        return value switch
        {
            "anthropic_oauth" => AccountType.AnthropicOauth,
            "token" => AccountType.Token,
            _ => throw new JsonException($"Unknown AccountType value: '{value}'"),
        };
    }

    public override void Write(Utf8JsonWriter writer, AccountType value, JsonSerializerOptions options)
    {
        var str = value switch
        {
            AccountType.AnthropicOauth => "anthropic_oauth",
            AccountType.Token => "token",
            _ => throw new JsonException($"Unknown AccountType: {value}"),
        };
        writer.WriteStringValue(str);
    }
}

/// <summary>Custom converter for AuthKind: serializes as "auth_token" / "api_key".</summary>
public sealed class AuthKindConverter : JsonConverter<AuthKind>
{
    public override AuthKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? throw new JsonException("Expected a string for AuthKind.");
        return value switch
        {
            "auth_token" => AuthKind.AuthToken,
            "api_key" => AuthKind.ApiKey,
            _ => throw new JsonException($"Unknown AuthKind value: '{value}'"),
        };
    }

    public override void Write(Utf8JsonWriter writer, AuthKind value, JsonSerializerOptions options)
    {
        var str = value switch
        {
            AuthKind.AuthToken => "auth_token",
            AuthKind.ApiKey => "api_key",
            _ => throw new JsonException($"Unknown AuthKind: {value}"),
        };
        writer.WriteStringValue(str);
    }
}

/// <summary>A single managed account. Non-secret metadata only.</summary>
public sealed class Account
{
    /// <summary>Stable unique id (UUID). Also the keyring entry name suffix.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>User-facing display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Account kind. JSON field name is "type" (not "account_type").</summary>
    [JsonPropertyName("type")]
    public AccountType AccountType { get; init; }

    /// <summary>Optional ANTHROPIC_BASE_URL. Valid for BOTH account types.</summary>
    [JsonPropertyName("base_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; init; }

    /// <summary>Token-only: whether the secret is an auth token or an api key.</summary>
    [JsonPropertyName("auth_kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuthKind? AuthKind { get; init; }

    /// <summary>OAuth-only, optional: a stable identity (email / account id) used to
    /// deduplicate imports.</summary>
    [JsonPropertyName("identity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Identity { get; init; }

    /// <summary>Extra environment variables applied on switch. Values are written
    /// to <c>settings.json</c>'s env on switch-in and re-captured from the live
    /// env on switch-out, so manual edits to these keys persist into the account.
    /// Stored as null when empty so the JSON field is omitted (WhenWritingNull).
    /// Settable (not init-only) so the switch engine can capture live values back
    /// into the account. Use <see cref="ExtraEnv"/> to get a non-null view.</summary>
    [JsonPropertyName("extra_env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ExtraEnvNullable { get; set; }

    /// <summary>Non-null view of ExtraEnv; empty dict when not set.</summary>
    [JsonIgnore]
    public Dictionary<string, string> ExtraEnv => ExtraEnvNullable ?? new();

    /// <summary>Per-account snapshot of tracked top-level settings.json keys
    /// (e.g. <c>{"model":"claude-opus-4-8"}</c>), captured when switching away
    /// and restored when switching back. Keyed by settings key. Mutable so the
    /// switch engine can update it in place; omitted from JSON when null.
    /// See <see cref="AppConfig.TrackedSettingsKeys"/>.</summary>
    [JsonPropertyName("saved_settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? SavedSettings { get; set; }
}

/// <summary>Global single HTTP proxy toggle.</summary>
public sealed class ProxySettings
{
    /// <summary>Whether the proxy keys are written into the active account's env.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = false;

    /// <summary>Proxy URL used for HTTP_PROXY / HTTPS_PROXY.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = "http://127.0.0.1:8080";

    /// <summary>Value used for NO_PROXY.</summary>
    [JsonPropertyName("no_proxy")]
    public string NoProxy { get; init; } = "localhost,127.0.0.1";

    /// <summary>Returns the default ProxySettings matching the Rust defaults.</summary>
    public static ProxySettings Default => new();
}

/// <summary>Root persisted, non-secret application config.</summary>
public sealed class AppConfig
{
    /// <summary>Current config.json schema version.</summary>
    public const int SchemaVersionValue = 1;

    /// <summary>Schema version for forward/backward migration.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = SchemaVersionValue;

    /// <summary>Currently active account id, or null if no account is active.</summary>
    [JsonPropertyName("active_account_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveAccountId { get; set; }

    /// <summary>Global proxy settings.</summary>
    [JsonPropertyName("proxy")]
    public ProxySettings Proxy { get; set; } = ProxySettings.Default;

    /// <summary>The set of env keys last written by ccswitcher into settings.json's env.
    /// Used to robustly strip prior managed/extra keys on the next switch.</summary>
    [JsonPropertyName("managed_keys")]
    public List<string> ManagedKeys { get; set; } = new();

    /// <summary>Top-level <c>settings.json</c> keys (siblings of <c>env</c>) that
    /// ccswitcher captures from the outgoing account and restores for the
    /// incoming account on every switch. Each account's captured values live in
    /// <see cref="Account.SavedSettings"/>. Defaults to <c>["model"]</c>; an
    /// empty list disables the feature (those keys are never touched).</summary>
    [JsonPropertyName("tracked_settings_keys")]
    public List<string> TrackedSettingsKeys { get; set; } = new() { "model" };

    /// <summary>All known accounts.</summary>
    [JsonPropertyName("accounts")]
    public List<Account> Accounts { get; init; } = new();

    /// <summary>Returns the default AppConfig with sane starting values.</summary>
    public static AppConfig Default => new();

    /// <summary>Shared JsonSerializerOptions for AppConfig serialization.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}
