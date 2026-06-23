using System.Text.Json;
using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public class ModelsTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static Account TokenAccount() => new()
    {
        Id = "tok-id",
        Name = "Work",
        AccountType = AccountType.Token,
        BaseUrl = "https://proxy.example.com",
        AuthKind = AuthKind.AuthToken,
        Identity = null,
        ExtraEnvNullable = new Dictionary<string, string> { ["FOO"] = "bar" },
    };

    private static Account OauthAccount() => new()
    {
        Id = "oauth-id",
        Name = "Personal",
        AccountType = AccountType.AnthropicOauth,
        BaseUrl = "https://api.anthropic.com",
        AuthKind = null,
        Identity = "user@example.com",
        ExtraEnvNullable = null,   // empty → null → omitted from JSON
    };

    // ── AppConfig defaults ──────────────────────────────────────────────────────

    [Fact]
    public void DefaultAppConfig_HasCorrectDefaults()
    {
        var cfg = AppConfig.Default;
        Assert.Equal(AppConfig.SchemaVersionValue, cfg.SchemaVersion);
        Assert.Null(cfg.ActiveAccountId);
        Assert.False(cfg.Proxy.Enabled);
        Assert.Equal("http://127.0.0.1:8080", cfg.Proxy.Url);
        Assert.Equal("localhost,127.0.0.1", cfg.Proxy.NoProxy);
        Assert.Empty(cfg.ManagedKeys);
        Assert.Empty(cfg.Accounts);
    }

    // ── AccountType enum serialization ─────────────────────────────────────────

    [Fact]
    public void AccountType_AnthropicOauth_SerializesAsSnakeCase()
    {
        var json = JsonSerializer.Serialize(AccountType.AnthropicOauth);
        Assert.Equal("\"anthropic_oauth\"", json);
    }

    [Fact]
    public void AccountType_Token_SerializesAsSnakeCase()
    {
        var json = JsonSerializer.Serialize(AccountType.Token);
        Assert.Equal("\"token\"", json);
    }

    [Fact]
    public void AccountType_AnthropicOauth_DeserializesFromSnakeCase()
    {
        var val = JsonSerializer.Deserialize<AccountType>("\"anthropic_oauth\"");
        Assert.Equal(AccountType.AnthropicOauth, val);
    }

    [Fact]
    public void AccountType_Token_DeserializesFromSnakeCase()
    {
        var val = JsonSerializer.Deserialize<AccountType>("\"token\"");
        Assert.Equal(AccountType.Token, val);
    }

    [Fact]
    public void AccountType_UnknownValue_Throws()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AccountType>("\"unknown\""));
    }

    // ── AuthKind enum serialization ────────────────────────────────────────────

    [Fact]
    public void AuthKind_AuthToken_SerializesAsSnakeCase()
    {
        var json = JsonSerializer.Serialize(AuthKind.AuthToken);
        Assert.Equal("\"auth_token\"", json);
    }

    [Fact]
    public void AuthKind_ApiKey_SerializesAsSnakeCase()
    {
        var json = JsonSerializer.Serialize(AuthKind.ApiKey);
        Assert.Equal("\"api_key\"", json);
    }

    [Fact]
    public void AuthKind_AuthToken_DeserializesFromSnakeCase()
    {
        var val = JsonSerializer.Deserialize<AuthKind>("\"auth_token\"");
        Assert.Equal(AuthKind.AuthToken, val);
    }

    [Fact]
    public void AuthKind_ApiKey_DeserializesFromSnakeCase()
    {
        var val = JsonSerializer.Deserialize<AuthKind>("\"api_key\"");
        Assert.Equal(AuthKind.ApiKey, val);
    }

    // ── Account field names in JSON ────────────────────────────────────────────

    [Fact]
    public void Account_UsesTypeFieldName_NotAccountType()
    {
        var acc = TokenAccount();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(acc));
        var root = doc.RootElement;

        // "type" must be present with the serialized AccountType value
        Assert.True(root.TryGetProperty("type", out var typeEl));
        Assert.Equal("token", typeEl.GetString());

        // "account_type" must NOT appear in JSON
        Assert.False(root.TryGetProperty("account_type", out _));
    }

    [Fact]
    public void Account_AuthKind_SerializesAsSnakeCase()
    {
        var acc = TokenAccount();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(acc));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("auth_kind", out var kindEl));
        Assert.Equal("auth_token", kindEl.GetString());
    }

    // ── Optional field omission ────────────────────────────────────────────────

    [Fact]
    public void Account_NullOptionals_OmittedFromJson()
    {
        var acc = new Account
        {
            Id = "x",
            Name = "X",
            AccountType = AccountType.Token,
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(acc));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("base_url", out _));
        Assert.False(root.TryGetProperty("auth_kind", out _));
        Assert.False(root.TryGetProperty("identity", out _));
    }

    [Fact]
    public void Account_EmptyExtraEnv_OmittedFromJson()
    {
        // OauthAccount has ExtraEnvNullable = null, which means empty extra env.
        // The JSON field "extra_env" must be omitted entirely.
        var acc = OauthAccount();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(acc));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("extra_env", out _));
    }

    [Fact]
    public void Account_ExtraEnv_PublicPropertyReturnsEmptyDictWhenNull()
    {
        var acc = new Account { Id = "x", Name = "X", AccountType = AccountType.Token };
        // ExtraEnvNullable is null but ExtraEnv should return empty dict
        Assert.Null(acc.ExtraEnvNullable);
        Assert.Empty(acc.ExtraEnv);
    }

    // ── Round-trip with both account types ────────────────────────────────────

    [Fact]
    public void AppConfig_RoundTrip_WithBothAccountTypes()
    {
        var cfg = new AppConfig
        {
            SchemaVersion = AppConfig.SchemaVersionValue,
            ActiveAccountId = "oauth-id",
            Proxy = new ProxySettings
            {
                Enabled = true,
                Url = "http://localhost:9000",
                NoProxy = "localhost",
            },
            ManagedKeys = ["ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN"],
            Accounts = [TokenAccount(), OauthAccount()],
        };

        var json = JsonSerializer.Serialize(cfg, AppConfig.JsonOptions);
        var back = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.JsonOptions);

        Assert.NotNull(back);
        Assert.Equal(cfg.SchemaVersion, back.SchemaVersion);
        Assert.Equal(cfg.ActiveAccountId, back.ActiveAccountId);
        Assert.Equal(cfg.Proxy.Enabled, back.Proxy.Enabled);
        Assert.Equal(cfg.Proxy.Url, back.Proxy.Url);
        Assert.Equal(cfg.Proxy.NoProxy, back.Proxy.NoProxy);
        Assert.Equal(cfg.ManagedKeys, back.ManagedKeys);

        Assert.Equal(2, back.Accounts.Count);

        var tok = back.Accounts[0];
        Assert.Equal("tok-id", tok.Id);
        Assert.Equal(AccountType.Token, tok.AccountType);
        Assert.Equal("https://proxy.example.com", tok.BaseUrl);
        Assert.Equal(AuthKind.AuthToken, tok.AuthKind);
        Assert.Null(tok.Identity);
        Assert.Equal("bar", tok.ExtraEnv["FOO"]);

        var oauth = back.Accounts[1];
        Assert.Equal("oauth-id", oauth.Id);
        Assert.Equal(AccountType.AnthropicOauth, oauth.AccountType);
        Assert.Equal("https://api.anthropic.com", oauth.BaseUrl);
        Assert.Null(oauth.AuthKind);
        Assert.Equal("user@example.com", oauth.Identity);
        Assert.Empty(oauth.ExtraEnv);
    }

    // ── Deserialize from documented JSON shape ─────────────────────────────────

    [Fact]
    public void AppConfig_DeserializesFromDocumentedShape()
    {
        const string raw = """
            {
                "schema_version": 1,
                "active_account_id": "uuid-1",
                "proxy": {
                    "enabled": false,
                    "url": "http://127.0.0.1:8080",
                    "no_proxy": "localhost,127.0.0.1"
                },
                "managed_keys": ["ANTHROPIC_BASE_URL"],
                "accounts": [
                    {
                        "id": "uuid-1",
                        "name": "Work",
                        "type": "anthropic_oauth",
                        "base_url": "https://api.anthropic.com",
                        "identity": "user@example.com",
                        "extra_env": { "FOO": "bar" }
                    },
                    {
                        "id": "uuid-2",
                        "name": "Provider",
                        "type": "token",
                        "auth_kind": "api_key"
                    }
                ]
            }
            """;

        var cfg = JsonSerializer.Deserialize<AppConfig>(raw);
        Assert.NotNull(cfg);
        Assert.Equal(2, cfg.Accounts.Count);

        var oauth = cfg.Accounts[0];
        Assert.Equal(AccountType.AnthropicOauth, oauth.AccountType);
        Assert.Equal("https://api.anthropic.com", oauth.BaseUrl);
        Assert.Equal("user@example.com", oauth.Identity);
        Assert.Null(oauth.AuthKind);
        Assert.Equal("bar", oauth.ExtraEnv["FOO"]);

        var token = cfg.Accounts[1];
        Assert.Equal(AccountType.Token, token.AccountType);
        Assert.Equal(AuthKind.ApiKey, token.AuthKind);
        Assert.Null(token.BaseUrl);
        Assert.Null(token.Identity);
        Assert.Empty(token.ExtraEnv);
    }

    // ── Missing optional fields use defaults ───────────────────────────────────

    [Fact]
    public void AppConfig_MissingOptionalFields_UseDefaults()
    {
        const string raw = """{ "accounts": [] }""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(raw);
        Assert.NotNull(cfg);
        Assert.Equal(AppConfig.SchemaVersionValue, cfg.SchemaVersion);
        Assert.Null(cfg.ActiveAccountId);
        Assert.False(cfg.Proxy.Enabled);
        Assert.Empty(cfg.ManagedKeys);
    }

    [Fact]
    public void AppConfig_EmptyJson_UsesDefaults()
    {
        const string raw = """{}""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(raw);
        Assert.NotNull(cfg);
        Assert.Equal(AppConfig.SchemaVersionValue, cfg.SchemaVersion);
        Assert.Null(cfg.ActiveAccountId);
        Assert.NotNull(cfg.Proxy);
        Assert.False(cfg.Proxy.Enabled);
        Assert.Equal("http://127.0.0.1:8080", cfg.Proxy.Url);
        Assert.Equal("localhost,127.0.0.1", cfg.Proxy.NoProxy);
        Assert.Empty(cfg.ManagedKeys);
        Assert.Empty(cfg.Accounts);
    }

    // ── ProxySettings defaults ─────────────────────────────────────────────────

    [Fact]
    public void ProxySettings_Default_HasCorrectValues()
    {
        var proxy = ProxySettings.Default;
        Assert.False(proxy.Enabled);
        Assert.Equal("http://127.0.0.1:8080", proxy.Url);
        Assert.Equal("localhost,127.0.0.1", proxy.NoProxy);
    }

    // ── ActiveAccountId omitted when null ─────────────────────────────────────

    [Fact]
    public void AppConfig_NullActiveAccountId_OmittedFromJson()
    {
        var cfg = AppConfig.Default;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(cfg, AppConfig.JsonOptions));
        Assert.False(doc.RootElement.TryGetProperty("active_account_id", out _));
    }

    [Fact]
    public void AppConfig_SetActiveAccountId_PresentInJson()
    {
        var cfg = new AppConfig { ActiveAccountId = "some-id" };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(cfg, AppConfig.JsonOptions));
        Assert.True(doc.RootElement.TryGetProperty("active_account_id", out var el));
        Assert.Equal("some-id", el.GetString());
    }
}
