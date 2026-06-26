// Tests for Switcher — mirrors the Rust test suite in switcher.rs.
//
// Every test uses temp directories for settings_path and config_dir, plus
// InMemorySecretStore and InMemoryCredentialStore so no real OS stores are
// touched.

using System.Text.Json.Nodes;
using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

/// <summary>
/// A failing credential store: <see cref="Read"/> returns whatever was
/// last force-set, but <see cref="Write"/> always throws.
/// Used to simulate a cross-store post-abort scenario (settings written,
/// credential restore fails).
/// </summary>
file sealed class FailingWriteCredentialStore : ICredentialStore
{
    private string? _blob;

    /// <summary>Seed the readable blob without going through the failing Write.</summary>
    public void ForceSet(string blob) => _blob = blob;

    public string? Read() => string.IsNullOrEmpty(_blob) ? null : _blob;

    public void Write(string blob) =>
        throw new InvalidOperationException("FailingWriteCredentialStore: write not allowed");
}

// ---------------------------------------------------------------------------
// Test fixture
// ---------------------------------------------------------------------------

public sealed class SwitcherTests : IDisposable
{
    // Each test gets its own isolated directories.
    private readonly string _settingsDir;
    private readonly string _configDir;
    private readonly string _settingsPath;
    private readonly InMemorySecretStore _secrets;
    private readonly InMemoryCredentialStore _creds;

    public SwitcherTests()
    {
        _settingsDir = Path.Combine(Path.GetTempPath(), $"ccsw-sw-s-{Guid.NewGuid():N}");
        _configDir   = Path.Combine(Path.GetTempPath(), $"ccsw-sw-c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_settingsDir);
        Directory.CreateDirectory(_configDir);
        _settingsPath = Path.Combine(_settingsDir, "settings.json");
        _secrets = new InMemorySecretStore();
        _creds   = new InMemoryCredentialStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_settingsDir, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_configDir,   recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private SwitchDeps Deps(string? userConfigPath = null) => new()
    {
        SettingsPath    = _settingsPath,
        ConfigDir       = _configDir,
        UserConfigPath  = userConfigPath,
        SecretStore     = _secrets,
        CredentialStore = _creds,
    };

    /// <summary>
    /// Read settings.json and return the "env" object (or null if absent).
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

    private static Account TokenAccount(string id, string? baseUrl = null) => new()
    {
        Id          = id,
        Name        = $"token-{id}",
        AccountType = AccountType.Token,
        AuthKind    = CCSwitcher.Core.AuthKind.AuthToken,
        BaseUrl     = baseUrl,
    };

    private static Account OauthAccount(string id, string? baseUrl = null) => new()
    {
        Id          = id,
        Name        = $"oauth-{id}",
        AccountType = AccountType.AnthropicOauth,
        Identity    = $"{id}@example.com",
        BaseUrl     = baseUrl,
    };

    private static AppConfig ConfigWith(params Account[] accounts) => new()
    {
        Accounts = new List<Account>(accounts),
    };

    // -----------------------------------------------------------------------
    // 1. unknown_target_returns_typed_error
    // -----------------------------------------------------------------------

    [Fact]
    public void UnknownTarget_ReturnsTypedError_AndTouchesNoStore()
    {
        var cfg = ConfigWith(TokenAccount("a"));

        var ex = Assert.Throws<UnknownAccountException>(
            () => Switcher.ApplyAccount(cfg, "does-not-exist", Deps()));

        Assert.Equal("does-not-exist", ex.AccountId);
        // Settings file must not have been created.
        Assert.False(File.Exists(_settingsPath));
    }

    // -----------------------------------------------------------------------
    // 2. switch_to_token_writes_env_override
    // -----------------------------------------------------------------------

    [Fact]
    public void SwitchToToken_WritesEnvAndPersistsManagedKeys()
    {
        _secrets.Set("tok", "sk-secret");
        var cfg = ConfigWith(TokenAccount("tok", baseUrl: "https://proxy.example.com"));

        Switcher.ApplyAccount(cfg, "tok", Deps());

        var env = SettingsEnvObj();
        Assert.NotNull(env);
        Assert.Equal("sk-secret",                    env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.Equal("https://proxy.example.com",    env["ANTHROPIC_BASE_URL"]?.GetValue<string>());

        Assert.Equal("tok", cfg.ActiveAccountId);

        // Config was persisted with managed keys.
        var reloaded = ConfigStore.Load(_configDir);
        Assert.Equal("tok", reloaded.ActiveAccountId);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN", reloaded.ManagedKeys);
    }

    // -----------------------------------------------------------------------
    // 3. switch_to_oauth_restores_snapshot_and_writes_no_token_key
    // -----------------------------------------------------------------------

    [Fact]
    public void SwitchToOauth_RestoresSnapshot_AndWritesNoTokenKey()
    {
        const string blob = """{"claudeAiOauth":{"accessToken":"a"}}""";
        _secrets.Set("oa", blob);
        var cfg = ConfigWith(OauthAccount("oa", baseUrl: "https://api.anthropic.com"));

        Switcher.ApplyAccount(cfg, "oa", Deps());

        var env = SettingsEnvObj();
        Assert.NotNull(env);
        Assert.Null(env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Null(env["ANTHROPIC_API_KEY"]);
        Assert.Equal("https://api.anthropic.com", env["ANTHROPIC_BASE_URL"]?.GetValue<string>());

        // Snapshot restored to credential store.
        Assert.Equal(blob, _creds.Read());
    }

    // -----------------------------------------------------------------------
    // 4. token_oauth_token_leaves_no_stale_keys
    // -----------------------------------------------------------------------

    [Fact]
    public void TokenOauthToken_LeavesNoStaleKeys()
    {
        _secrets.Set("t1", "sk-1");
        _secrets.Set("oa", """{"claudeAiOauth":{"accessToken":"a"}}""");
        _secrets.Set("t2", "sk-2");

        var cfg = ConfigWith(TokenAccount("t1"), OauthAccount("oa"), TokenAccount("t2"));

        Switcher.ApplyAccount(cfg, "t1", Deps());
        Switcher.ApplyAccount(cfg, "oa", Deps());

        // After switching to OAuth (no token key), prior token must be gone.
        Assert.Null(SettingsEnvObj()?["ANTHROPIC_AUTH_TOKEN"]);

        Switcher.ApplyAccount(cfg, "t2", Deps());

        var env = SettingsEnvObj();
        Assert.NotNull(env);
        Assert.Equal("sk-2", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.Null(env["ANTHROPIC_API_KEY"]);
    }

    // -----------------------------------------------------------------------
    // 5. a_oauth_b_a_preserves_latest_blob
    // -----------------------------------------------------------------------

    [Fact]
    public void OauthCycle_AOauthBOauthA_PreservesRefreshedBlob()
    {
        const string importBlob    = """{"claudeAiOauth":{"accessToken":"import","expiresAt":1}}""";
        const string refreshedBlob = """{"claudeAiOauth":{"accessToken":"refreshed","expiresAt":2}}""";

        _secrets.Set("a", importBlob);
        _secrets.Set("b", "sk-b");

        var cfg = ConfigWith(OauthAccount("a"), TokenAccount("b"));

        // Switch to A: restores the import-time snapshot into the live store.
        Switcher.ApplyAccount(cfg, "a", Deps());
        Assert.Equal(importBlob, _creds.Read());

        // Simulate Claude Code refreshing the live credential store in place.
        _creds.Write(refreshedBlob);

        // Switch to B: capture-on-switch-out must re-snapshot A's *refreshed* blob.
        Switcher.ApplyAccount(cfg, "b", Deps());
        Assert.Equal(refreshedBlob, _secrets.Get("a"));

        // Switch back to A: restore must use the refreshed blob, not the import-time one.
        Switcher.ApplyAccount(cfg, "a", Deps());
        Assert.Equal(refreshedBlob, _creds.Read());
    }

    // -----------------------------------------------------------------------
    // 5b. skip-capture (import current login) does not clobber the active blob
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyAccount_SkipCapture_DoesNotOverwriteActiveOauthBlob()
    {
        const string aBlob   = """{"claudeAiOauth":{"accessToken":"a-stored"}}""";
        const string newBlob = """{"claudeAiOauth":{"accessToken":"new-login"}}""";

        _secrets.Set("a", aBlob);
        _secrets.Set("b", newBlob);

        var cfg = ConfigWith(OauthAccount("a"), OauthAccount("b"));
        cfg.ActiveAccountId = "a";

        // The live store now holds a *different* login — as it does right after
        // importing the current login (the new account "b").
        _creds.Write(newBlob);

        // Mark "b" active without capture-on-switch-out.
        Switcher.ApplyAccount(cfg, "b", Deps(), captureOnSwitchOut: false);

        // "a"'s stored blob must be untouched (NOT re-snapshotted from the live
        // store, which would corrupt it with "b"'s credentials).
        Assert.Equal(aBlob, _secrets.Get("a"));
        Assert.Equal("b", cfg.ActiveAccountId);
    }

    // -----------------------------------------------------------------------
    // 6. oauth_account_with_base_url_keeps_it_after_switch
    // -----------------------------------------------------------------------

    [Fact]
    public void OauthWithBaseUrl_KeepsBaseUrlAfterSecondSwitch()
    {
        _secrets.Set("oa", """{"claudeAiOauth":{"accessToken":"a"}}""");
        var cfg = ConfigWith(OauthAccount("oa", baseUrl: "https://api.anthropic.com"));

        Switcher.ApplyAccount(cfg, "oa", Deps());
        // A second switch (to itself) must still keep the base_url, not strip it.
        Switcher.ApplyAccount(cfg, "oa", Deps());

        Assert.Equal(
            "https://api.anthropic.com",
            SettingsEnvObj()?["ANTHROPIC_BASE_URL"]?.GetValue<string>());
    }

    // -----------------------------------------------------------------------
    // 7. missing_secret_token_aborts_before_any_settings_write
    // -----------------------------------------------------------------------

    [Fact]
    public void MissingSecret_TokenAccount_AbortsBeforeSettingsWrite()
    {
        // No secret stored for the token account.
        var cfg = ConfigWith(TokenAccount("tok"));

        Assert.Throws<MissingSecretException>(
            () => Switcher.ApplyAccount(cfg, "tok", Deps()));

        // No settings file was created.
        Assert.False(File.Exists(_settingsPath));
        // Active id unchanged.
        Assert.Null(cfg.ActiveAccountId);
    }

    // -----------------------------------------------------------------------
    // 8. cross_store_post_abort_is_recoverable_idempotently
    // -----------------------------------------------------------------------

    [Fact]
    public void CrossStorePostAbort_IsRecoverableIdempotently()
    {
        // Seed an existing settings.json so the write produces a backup.
        File.WriteAllText(_settingsPath, """{"env":{"MY_OWN":"keep"}}""");

        const string blob = """{"claudeAiOauth":{"accessToken":"a"}}""";
        _secrets.Set("oa", blob);

        var failing = new FailingWriteCredentialStore();
        var cfg = ConfigWith(OauthAccount("oa"));

        var depsWithFailing = new SwitchDeps
        {
            SettingsPath    = _settingsPath,
            ConfigDir       = _configDir,
            UserConfigPath  = null,
            SecretStore     = _secrets,
            CredentialStore = failing,
        };

        // First run: settings write succeeds, but credential restore fails.
        Assert.ThrowsAny<Exception>(
            () => Switcher.ApplyAccount(cfg, "oa", depsWithFailing));

        // settings.json was written (valid JSON) and the user key preserved.
        var written = SettingsEnv.Load(_settingsPath);
        Assert.Equal("keep", written["env"]?["MY_OWN"]?.GetValue<string>());

        // A timestamped backup of the prior settings was taken.
        var backupsDir = Path.Combine(_settingsDir, "backups");
        Assert.True(Directory.Exists(backupsDir));
        var bakCount = Directory.EnumerateFiles(backupsDir, "*.bak").Count();
        Assert.Equal(1, bakCount);

        // The keyring capture for the OAuth account is retained.
        Assert.Equal(blob, _secrets.Get("oa"));

        // Re-run with a working credential store: switch heals to consistent state.
        var working = new InMemoryCredentialStore();
        var depsWithWorking = new SwitchDeps
        {
            SettingsPath    = _settingsPath,
            ConfigDir       = _configDir,
            UserConfigPath  = null,
            SecretStore     = _secrets,
            CredentialStore = working,
        };

        Switcher.ApplyAccount(cfg, "oa", depsWithWorking);

        Assert.Equal(blob, working.Read());
        Assert.Equal("oa", cfg.ActiveAccountId);
        var reloaded = ConfigStore.Load(_configDir);
        Assert.Equal("oa", reloaded.ActiveAccountId);
    }

    // -----------------------------------------------------------------------
    // 9. oauth_switch_captures_and_restores_user_config_oauth_account
    // -----------------------------------------------------------------------

    [Fact]
    public void OauthSwitch_CapturesAndRestoresUserConfigOauthAccount()
    {
        // Two OAuth accounts. Live ~/.claude.json currently holds account A's
        // oauthAccount; account B's snapshot is in the keyring.
        var userConfigPath = Path.Combine(_settingsDir, ".claude.json");
        File.WriteAllText(userConfigPath,
            """{"userID":"keep-me","oauthAccount":{"accountUuid":"A","emailAddress":"a@x"}}""");

        _secrets.Set(
            UserConfig.OauthAccountKey("b"),
            """{"accountUuid":"B","emailAddress":"b@x"}""");

        var cfg = ConfigWith(OauthAccount("a"), OauthAccount("b"));
        cfg.ActiveAccountId = "a";

        var deps = new SwitchDeps
        {
            SettingsPath    = _settingsPath,
            ConfigDir       = _configDir,
            UserConfigPath  = userConfigPath,
            SecretStore     = _secrets,
            CredentialStore = _creds,
        };

        // Switch A → B.
        Switcher.ApplyAccount(cfg, "b", deps);

        // Capture-on-switch-out: A's live oauthAccount was snapshotted to keyring.
        var capturedA = _secrets.Get(UserConfig.OauthAccountKey("a"));
        Assert.NotNull(capturedA);
        Assert.Contains(@"""accountUuid"":""A""", capturedA);

        // Restore-on-switch-in: B's oauthAccount merged into ~/.claude.json.
        var afterText = File.ReadAllText(userConfigPath);
        var afterNode = JsonNode.Parse(afterText)!.AsObject();
        Assert.Equal("B", afterNode["oauthAccount"]?["accountUuid"]?.GetValue<string>());
        // Other fields preserved.
        Assert.Equal("keep-me", afterNode["userID"]?.GetValue<string>());
    }

    // -----------------------------------------------------------------------
    // 10-12. ClearActiveIfMissing
    // -----------------------------------------------------------------------

    [Fact]
    public void ClearActiveIfMissing_ClearsDanglingId()
    {
        var cfg = ConfigWith(TokenAccount("a"));
        cfg.ActiveAccountId = "ghost";

        var cleared = Switcher.ClearActiveIfMissing(cfg);

        Assert.True(cleared);
        Assert.Null(cfg.ActiveAccountId);
    }

    [Fact]
    public void ClearActiveIfMissing_KeepsExistingId()
    {
        var cfg = ConfigWith(TokenAccount("a"));
        cfg.ActiveAccountId = "a";

        var cleared = Switcher.ClearActiveIfMissing(cfg);

        Assert.False(cleared);
        Assert.Equal("a", cfg.ActiveAccountId);
    }

    [Fact]
    public void ClearActiveIfMissing_NoopWhenNone()
    {
        var cfg = ConfigWith(TokenAccount("a"));
        // ActiveAccountId is null by default.

        var cleared = Switcher.ClearActiveIfMissing(cfg);

        Assert.False(cleared);
        Assert.Null(cfg.ActiveAccountId);
    }
}
