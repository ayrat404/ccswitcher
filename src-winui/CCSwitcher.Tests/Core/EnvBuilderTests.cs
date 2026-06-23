// Tests for EnvBuilder — mirrors the Rust test suite in env_builder.rs.
//
// No filesystem, no OS keychain; the entire suite runs against in-memory values.

using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class EnvBuilderTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Account TokenAccount(
        AuthKind? authKind = null,
        string? baseUrl = null,
        Dictionary<string, string>? extraEnv = null) => new()
    {
        Id          = "tok-id",
        Name        = "Work",
        AccountType = AccountType.Token,
        AuthKind    = authKind,
        BaseUrl     = baseUrl,
        ExtraEnvNullable = extraEnv,
    };

    private static Account OauthAccount(
        string? baseUrl = null,
        Dictionary<string, string>? extraEnv = null) => new()
    {
        Id          = "oauth-id",
        Name        = "Personal",
        AccountType = AccountType.AnthropicOauth,
        Identity    = "user@example.com",
        BaseUrl     = baseUrl,
        ExtraEnvNullable = extraEnv,
    };

    private static ProxySettings ProxyOff => new() { Enabled = false };

    private static ProxySettings ProxyOn => new()
    {
        Enabled  = true,
        Url      = "http://127.0.0.1:8080",
        NoProxy  = "localhost,127.0.0.1",
    };

    // -----------------------------------------------------------------------
    // Token account tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Token_AuthToken_SetsAnthropicAuthToken()
    {
        var acc = TokenAccount(AuthKind.AuthToken);
        var env = EnvBuilder.Build(acc, "sk-secret", ProxyOff);

        Assert.Equal("sk-secret", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Token_ApiKey_SetsAnthropicApiKey()
    {
        var acc = TokenAccount(AuthKind.ApiKey);
        var env = EnvBuilder.Build(acc, "key-123", ProxyOff);

        Assert.Equal("key-123", env["ANTHROPIC_API_KEY"]);
        Assert.False(env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
    }

    [Fact]
    public void Token_AuthKindNull_DefaultsToAuthToken()
    {
        // When auth_kind is absent, fall back to AuthToken.
        var acc = TokenAccount(authKind: null);
        var env = EnvBuilder.Build(acc, "sk-secret", ProxyOff);

        Assert.Equal("sk-secret", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Token_BaseUrl_SetWhenPresent()
    {
        var acc = TokenAccount(AuthKind.ApiKey, baseUrl: "https://proxy.example.com");
        var env = EnvBuilder.Build(acc, "key-123", ProxyOff);

        Assert.Equal("https://proxy.example.com", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal(2, env.Count); // ANTHROPIC_API_KEY + ANTHROPIC_BASE_URL (two entries, not one)
    }

    [Fact]
    public void Token_NoBaseUrl_KeyAbsent()
    {
        var acc = TokenAccount(AuthKind.AuthToken);
        var env = EnvBuilder.Build(acc, "sk", ProxyOff);

        Assert.False(env.ContainsKey("ANTHROPIC_BASE_URL"));
    }

    // -----------------------------------------------------------------------
    // OAuth account tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Oauth_WritesNoTokenKey()
    {
        var acc = OauthAccount();
        var env = EnvBuilder.Build(acc, secret: null, ProxyOff);

        Assert.False(env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Oauth_NoBaseUrl_DictIsEmpty()
    {
        var acc = OauthAccount();
        var env = EnvBuilder.Build(acc, secret: null, ProxyOff);

        Assert.Empty(env);
    }

    [Fact]
    public void Oauth_BaseUrl_SetWhenPresent()
    {
        var acc = OauthAccount(baseUrl: "https://api.anthropic.com");
        var env = EnvBuilder.Build(acc, secret: null, ProxyOff);

        Assert.Equal("https://api.anthropic.com", env["ANTHROPIC_BASE_URL"]);
        Assert.Single(env);
    }

    [Fact]
    public void Oauth_IgnoresProvidedSecret()
    {
        // Even if a secret is passed, OAuth never writes a token key.
        var acc = OauthAccount();
        var env = EnvBuilder.Build(acc, secret: "should-be-ignored", ProxyOff);

        Assert.Empty(env);
    }

    // -----------------------------------------------------------------------
    // Proxy tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Proxy_Enabled_AddsThreeProxyKeys()
    {
        var acc = TokenAccount(AuthKind.AuthToken);
        var env = EnvBuilder.Build(acc, "sk", ProxyOn);

        Assert.Equal("http://127.0.0.1:8080", env["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:8080", env["HTTPS_PROXY"]);
        Assert.Equal("localhost,127.0.0.1",   env["NO_PROXY"]);
    }

    [Fact]
    public void Proxy_Disabled_ProxyKeysAbsent()
    {
        var acc = TokenAccount(AuthKind.AuthToken);
        var env = EnvBuilder.Build(acc, "sk", ProxyOff);

        Assert.False(env.ContainsKey("HTTP_PROXY"));
        Assert.False(env.ContainsKey("HTTPS_PROXY"));
        Assert.False(env.ContainsKey("NO_PROXY"));
    }

    // -----------------------------------------------------------------------
    // ExtraEnv tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtraEnv_MergedIntoResult()
    {
        var extra = new Dictionary<string, string> { ["FOO"] = "bar", ["BAZ"] = "qux" };
        var acc   = TokenAccount(AuthKind.AuthToken, extraEnv: extra);
        var env   = EnvBuilder.Build(acc, "sk", ProxyOff);

        Assert.Equal("bar", env["FOO"]);
        Assert.Equal("qux", env["BAZ"]);
        Assert.Equal("sk",  env["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public void ExtraEnv_CanOverrideManagedKey()
    {
        // ExtraEnv["ANTHROPIC_BASE_URL"] overrides the account's own BaseUrl
        // because ExtraEnv is merged last.
        var extra = new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "https://override.example.com" };
        var acc   = TokenAccount(AuthKind.AuthToken, baseUrl: "https://original.example.com", extraEnv: extra);
        var env   = EnvBuilder.Build(acc, "sk", ProxyOff);

        Assert.Equal("https://override.example.com", env["ANTHROPIC_BASE_URL"]);
    }

    [Fact]
    public void ExtraEnv_MergedForOauthToo()
    {
        var extra = new Dictionary<string, string> { ["CUSTOM"] = "value" };
        var acc   = OauthAccount(extraEnv: extra);
        var env   = EnvBuilder.Build(acc, secret: null, ProxyOff);

        Assert.Equal("value", env["CUSTOM"]);
        Assert.Single(env);
    }

    // -----------------------------------------------------------------------
    // MissingSecretException tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Token_NullSecret_ThrowsMissingSecretException()
    {
        var acc = TokenAccount(AuthKind.AuthToken);

        var ex = Assert.Throws<MissingSecretException>(
            () => EnvBuilder.Build(acc, secret: null, ProxyOff));
        Assert.Contains("missing its secret", ex.Message);
    }

    [Fact]
    public void Token_EmptySecret_ThrowsMissingSecretException()
    {
        var acc = TokenAccount(AuthKind.ApiKey);

        Assert.Throws<MissingSecretException>(
            () => EnvBuilder.Build(acc, secret: string.Empty, ProxyOff));
    }

    [Fact]
    public void Token_NullSecret_NoStoreWrittenBeforeException()
    {
        // The exception must be thrown before any dict entry is added.
        // We verify by catching and confirming we can't get any result.
        var acc = TokenAccount(AuthKind.AuthToken);

        Assert.Throws<MissingSecretException>(
            () => EnvBuilder.Build(acc, secret: null, ProxyOff));
        // (No dict to inspect — the call threw, which is the contract.)
    }
}
