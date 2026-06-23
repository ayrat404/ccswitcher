// Tests for Proxy.SetEnabled — mirrors the Rust test suite in proxy.rs tests.
//
// All tests use temp directories and InMemorySecretStore so no real OS stores
// are touched.

using System.Text.Json.Nodes;
using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class ProxyTests : IDisposable
{
    // Each test gets isolated temp directories.
    private readonly string _settingsDir;
    private readonly string _configDir;
    private readonly string _settingsPath;
    private readonly InMemorySecretStore _secrets;

    public ProxyTests()
    {
        _settingsDir  = Path.Combine(Path.GetTempPath(), $"ccsw-px-s-{Guid.NewGuid():N}");
        _configDir    = Path.Combine(Path.GetTempPath(), $"ccsw-px-c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_settingsDir);
        Directory.CreateDirectory(_configDir);
        _settingsPath = Path.Combine(_settingsDir, "settings.json");
        _secrets      = new InMemorySecretStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_settingsDir, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_configDir,   recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private ProxyDeps Deps() => new()
    {
        SettingsPath = _settingsPath,
        ConfigDir    = _configDir,
        SecretStore  = _secrets,
        // NO ICredentialStore — by design.
    };

    /// <summary>
    /// Read the current settings.json "env" value, or null if absent.
    /// </summary>
    private JsonObject? SettingsEnvObj()
    {
        if (!File.Exists(_settingsPath))
            return null;
        var settings = SettingsEnv.Load(_settingsPath);
        if (settings.TryGetPropertyValue("env", out var node) && node is JsonObject obj)
            return obj;
        return null;
    }

    private static Account TokenAccount(string id) => new()
    {
        Id          = id,
        Name        = $"token-{id}",
        AccountType = AccountType.Token,
        AuthKind    = CCSwitcher.Core.AuthKind.AuthToken,
    };

    private static AppConfig ConfigWithActive(Account[] accounts, string? activeId) => new()
    {
        ActiveAccountId = activeId,
        Proxy           = new ProxySettings
        {
            Enabled = false,
            Url     = "http://127.0.0.1:8080",
            NoProxy = "localhost,127.0.0.1",
        },
        Accounts        = new List<Account>(accounts),
    };

    // -----------------------------------------------------------------------
    // Test 1: enabling_proxy_adds_proxy_keys_to_active_env
    // -----------------------------------------------------------------------

    [Fact]
    public void EnablingProxy_AddsProxyKeysToActiveEnv()
    {
        _secrets.Set("tok", "sk-secret");
        var cfg = ConfigWithActive([TokenAccount("tok")], "tok");

        Proxy.SetEnabled(cfg, true, Deps());

        var env = SettingsEnvObj();
        Assert.NotNull(env);
        Assert.Equal("http://127.0.0.1:8080", env["HTTP_PROXY"]?.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8080", env["HTTPS_PROXY"]?.GetValue<string>());
        Assert.Equal("localhost,127.0.0.1",   env["NO_PROXY"]?.GetValue<string>());
        // The account's own env (token) is still present.
        Assert.Equal("sk-secret", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.True(cfg.Proxy.Enabled);

        // Config persisted with proxy keys in managed_keys.
        var reloaded = ConfigStore.Load(_configDir);
        Assert.True(reloaded.Proxy.Enabled);
        Assert.Contains("HTTP_PROXY", reloaded.ManagedKeys);
    }

    // -----------------------------------------------------------------------
    // Test 2: disabling_proxy_removes_proxy_keys_from_active_env
    // -----------------------------------------------------------------------

    [Fact]
    public void DisablingProxy_RemovesProxyKeysFromActiveEnv()
    {
        _secrets.Set("tok", "sk-secret");
        var cfg = ConfigWithActive([TokenAccount("tok")], "tok");

        // First enable, then disable.
        Proxy.SetEnabled(cfg, true, Deps());
        Assert.NotNull(SettingsEnvObj()?["HTTP_PROXY"]);

        Proxy.SetEnabled(cfg, false, Deps());

        var env = SettingsEnvObj();
        Assert.NotNull(env);
        Assert.Null(env["HTTP_PROXY"]);
        Assert.Null(env["HTTPS_PROXY"]);
        Assert.Null(env["NO_PROXY"]);
        // Account env survives the toggle.
        Assert.Equal("sk-secret", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.False(cfg.Proxy.Enabled);

        var reloaded = ConfigStore.Load(_configDir);
        Assert.False(reloaded.Proxy.Enabled);
        Assert.DoesNotContain("HTTP_PROXY", reloaded.ManagedKeys);
    }

    // -----------------------------------------------------------------------
    // Test 3: toggle_with_no_active_account_stores_flag_only
    // -----------------------------------------------------------------------

    [Fact]
    public void ToggleWithNoActiveAccount_StoresFlagOnly_NoSettingsWrite()
    {
        var cfg = ConfigWithActive([TokenAccount("tok")], activeId: null);

        Proxy.SetEnabled(cfg, true, Deps());

        // Flag stored in memory and on disk.
        Assert.True(cfg.Proxy.Enabled);
        var reloaded = ConfigStore.Load(_configDir);
        Assert.True(reloaded.Proxy.Enabled);

        // No settings.json was created or modified.
        Assert.False(File.Exists(_settingsPath));
    }

    // -----------------------------------------------------------------------
    // Test 4: toggle_with_dangling_active_id_stores_flag_only
    // -----------------------------------------------------------------------

    [Fact]
    public void ToggleWithDanglingActiveId_StoresFlagOnly_NoSettingsWrite()
    {
        // Active id points to a deleted / non-existent account.
        var cfg = ConfigWithActive([TokenAccount("tok")], activeId: "ghost");

        Proxy.SetEnabled(cfg, true, Deps());

        Assert.True(cfg.Proxy.Enabled);
        var reloaded = ConfigStore.Load(_configDir);
        Assert.True(reloaded.Proxy.Enabled);

        // No settings.json was created or modified.
        Assert.False(File.Exists(_settingsPath));
    }

    // -----------------------------------------------------------------------
    // Test 5: toggle_does_no_credential_store_io
    //
    // ProxyDeps has no ICredentialStore field — the no-credential-I/O guarantee
    // is structural, not behavioral. This test documents that guarantee: a full
    // enable+disable cycle on a token account succeeds entirely without a
    // credential store, because ProxyDeps simply has no field for one.
    // -----------------------------------------------------------------------

    [Fact]
    public void ToggleDoesNoCredentialStoreIo()
    {
        // ProxyDeps has no ICredentialStore field — the no-credential-I/O guarantee
        // is structural, not behavioral. Constructing ProxyDeps here is proof that
        // no credential store is required; there is no field to pass one through.
        _secrets.Set("tok", "sk-secret");
        var cfg = ConfigWithActive([TokenAccount("tok")], "tok");

        // A full enable+disable cycle: if any path tried credential I/O it would
        // need a store, which ProxyDeps simply does not provide.
        Proxy.SetEnabled(cfg, true, Deps());
        Proxy.SetEnabled(cfg, false, Deps());

        // Sanity: the toggle still did its real work without any credential store.
        var env = SettingsEnvObj();
        Assert.NotNull(env);
        Assert.Null(env["HTTP_PROXY"]);
        Assert.Equal("sk-secret", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.False(cfg.Proxy.Enabled);
    }
}
