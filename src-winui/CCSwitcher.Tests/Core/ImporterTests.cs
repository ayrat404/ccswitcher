// Tests for Importer — mirrors the Rust test suite in import.rs tests.
//
// All tests use temp files and in-memory mocks so no real OS stores are touched.

using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class ImporterTests : IDisposable
{
    // Each test gets an isolated temp directory for settings / user-config files.
    private readonly string _tmpDir;

    public ImporterTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"ccsw-imp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string WriteSettings(string json)
    {
        var path = Path.Combine(_tmpDir, $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        return path;
    }

    private string WriteUserConfig(string json)
    {
        var path = Path.Combine(_tmpDir, $"userconfig-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        return path;
    }

    private static Account OauthAccount(string id, string? identity = null) => new()
    {
        Id          = id,
        Name        = $"oauth-{id}",
        AccountType = AccountType.AnthropicOauth,
        Identity    = identity,
    };

    private static Account TokenAccount(
        string id,
        AuthKind authKind = AuthKind.AuthToken,
        string? baseUrl   = null) => new()
    {
        Id          = id,
        Name        = $"token-{id}",
        AccountType = AccountType.Token,
        AuthKind    = authKind,
        BaseUrl     = baseUrl,
    };

    // -----------------------------------------------------------------------
    // Detect tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Detect_ReturnsToken_WhenAuthTokenPresent()
    {
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-123"}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        Assert.IsType<ImportCandidate.Token>(result);
    }

    [Fact]
    public void Detect_ReturnsToken_WhenApiKeyPresent()
    {
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_API_KEY":"sk-456"}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        var tok = Assert.IsType<ImportCandidate.Token>(result);
        Assert.Equal(AuthKind.ApiKey, tok.AuthKind);
    }

    [Fact]
    public void Detect_ExtractsBaseUrl_FromToken()
    {
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-abc","ANTHROPIC_BASE_URL":"https://api.example.com"}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        var tok = Assert.IsType<ImportCandidate.Token>(result);
        Assert.Equal("https://api.example.com", tok.BaseUrl);
    }

    [Fact]
    public void Detect_ReturnsOauth_WhenCredentialsNonEmpty()
    {
        var sp    = WriteSettings("""{"env":{}}""");
        var creds = new InMemoryCredentialStore();
        creds.Write("""{"claudeAiOauth":{"accessToken":"a"}}""");

        var result = Importer.Detect(sp, null, creds);

        Assert.IsType<ImportCandidate.Oauth>(result);
    }

    [Fact]
    public void Detect_ReturnsNull_WhenNeitherExists()
    {
        var sp    = WriteSettings("""{"env":{}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        Assert.Null(result);
    }

    [Fact]
    public void Detect_ReturnsToken_RegardlessOfManagedHistory()
    {
        // Detection is value-based: a live AUTH_TOKEN is a candidate no matter
        // whether ccswitcher ever wrote that key. "Already ours" is decided later,
        // value-based, by FindDuplicate — not by a key-name list here. (This is the
        // regression guard for an externally-swapped token, e.g. a different
        // provider, that was previously blocked by the sticky managed_keys list.)
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-123"}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        Assert.IsType<ImportCandidate.Token>(result);
    }

    [Fact]
    public void Detect_WithLiveTokenAndOauthBlob_ReturnsTokenNotOauth()
    {
        // A live env token always wins over the OAuth credential blob (Claude Code
        // precedence), so the candidate is the Token — never the (possibly stale)
        // OAuth blob, and never null.
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-123"}}""");
        var creds = new InMemoryCredentialStore();
        creds.Write("""{"claudeAiOauth":{"accessToken":"a"}}""");

        var result = Importer.Detect(sp, null, creds);

        Assert.IsType<ImportCandidate.Token>(result);
    }

    [Fact]
    public void Detect_PrefersAuthToken_OverApiKey()
    {
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-auth","ANTHROPIC_API_KEY":"sk-api"}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        var tok = Assert.IsType<ImportCandidate.Token>(result);
        Assert.Equal(AuthKind.AuthToken, tok.AuthKind);
        Assert.Equal("sk-auth", tok.Secret);
    }

    [Fact]
    public void Detect_UsesUserConfig_ForOauthIdentity()
    {
        // Credential blob has no identity fields, but ~/.claude.json has oauthAccount.
        var sp    = WriteSettings("""{"env":{}}""");
        var ucp   = WriteUserConfig("""{"oauthAccount":{"accountUuid":"acc-uuid","emailAddress":"u@x.com"}}""");
        var creds = new InMemoryCredentialStore();
        creds.Write("""{"claudeAiOauth":{"accessToken":"a"}}""");

        var result = Importer.Detect(sp, ucp, creds);

        var oauth = Assert.IsType<ImportCandidate.Oauth>(result);
        Assert.Equal("acc-uuid", oauth.Identity);
    }

    [Fact]
    public void Detect_ReturnsToken_WithBaseUrl()
    {
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-manual","ANTHROPIC_BASE_URL":"https://api.anthropic.com"}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        var tok = Assert.IsType<ImportCandidate.Token>(result);
        Assert.Equal("https://api.anthropic.com", tok.BaseUrl);
    }

    [Fact]
    public void Detect_ReturnsToken_ManagedKeysNoLongerConsulted()
    {
        // managed_keys is no longer a Detect parameter: a live AUTH_TOKEN is always
        // detected. (Replaces the old key-name-based ignore, which broke importing
        // a token swapped in out-of-band.)
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-manual"}}""");
        var creds = new InMemoryCredentialStore();

        var result = Importer.Detect(sp, null, creds);

        Assert.IsType<ImportCandidate.Token>(result);
    }

    // -----------------------------------------------------------------------
    // DefaultName tests
    // -----------------------------------------------------------------------

    [Fact]
    public void DefaultName_Token_WithBaseUrl_ExtractsHost()
    {
        var c = new ImportCandidate.Token
        {
            Secret   = "sk-123",
            AuthKind = AuthKind.AuthToken,
            BaseUrl  = "https://api.anthropic.com/v1",
        };
        Assert.Equal("api.anthropic.com", Importer.DefaultName(c));
    }

    [Fact]
    public void DefaultName_Token_WithoutBaseUrl_IsGeneric()
    {
        var c = new ImportCandidate.Token
        {
            Secret   = "sk-123",
            AuthKind = AuthKind.AuthToken,
        };
        Assert.Equal("Token Account", Importer.DefaultName(c));
    }

    [Fact]
    public void DefaultName_Oauth_WithEmailIdentity()
    {
        var c = new ImportCandidate.Oauth
        {
            Blob     = "{}",
            Identity = "user@example.com",
        };
        Assert.Equal("user@example.com", Importer.DefaultName(c));
    }

    [Fact]
    public void DefaultName_Oauth_WithNonEmailIdentity_IsAnthropic()
    {
        var c = new ImportCandidate.Oauth
        {
            Blob     = "{}",
            Identity = "account-123",
        };
        Assert.Equal("Anthropic", Importer.DefaultName(c));
    }

    [Fact]
    public void DefaultName_Oauth_WithoutIdentity_IsAnthropic()
    {
        var c = new ImportCandidate.Oauth { Blob = "{}" };
        Assert.Equal("Anthropic", Importer.DefaultName(c));
    }

    // -----------------------------------------------------------------------
    // Import tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Import_CreatesTokenAccount_AndStoresSecret()
    {
        var candidate = new ImportCandidate.Token
        {
            Secret   = "sk-secret",
            AuthKind = AuthKind.AuthToken,
            BaseUrl  = "https://api.anthropic.com",
        };
        var store = new InMemorySecretStore();

        var result = Importer.Import(candidate, "Work", [], store);

        var created = Assert.IsType<ImportResult.Created>(result);
        Assert.Equal("Work", created.Account.Name);
        Assert.Equal(AccountType.Token, created.Account.AccountType);
        Assert.Equal("https://api.anthropic.com", created.Account.BaseUrl);
        Assert.Equal(AuthKind.AuthToken, created.Account.AuthKind);
        Assert.Equal("sk-secret", store.Get(created.Account.Id));
    }

    [Fact]
    public void Import_CreatesOauthAccount_AndStoresBlob()
    {
        var blob      = """{"claudeAiOauth":{"accessToken":"tok"}}""";
        var candidate = new ImportCandidate.Oauth { Blob = blob, Identity = "user@example.com" };
        var store     = new InMemorySecretStore();

        var result = Importer.Import(candidate, "Personal", [], store);

        var created = Assert.IsType<ImportResult.Created>(result);
        Assert.Equal("Personal", created.Account.Name);
        Assert.Equal(AccountType.AnthropicOauth, created.Account.AccountType);
        Assert.Equal("user@example.com", created.Account.Identity);
        Assert.Equal(blob, store.Get(created.Account.Id));
    }

    [Fact]
    public void Import_WithExtraEnv_SetsExtraEnvOnTokenAccount()
    {
        var candidate = new ImportCandidate.Token
        {
            Secret   = "sk-secret",
            AuthKind = AuthKind.AuthToken,
        };
        var store    = new InMemorySecretStore();
        var extraEnv = new Dictionary<string, string> { ["CUSTOM_VAR"] = "value" };

        var result = Importer.Import(candidate, "Work", [], store, extraEnv);

        var created = Assert.IsType<ImportResult.Created>(result);
        Assert.Equal(extraEnv, created.Account.ExtraEnv);
    }

    [Fact]
    public void Import_WithExtraEnv_SetsExtraEnvOnOauthAccount()
    {
        var candidate = new ImportCandidate.Oauth { Blob = "{}", Identity = "user@example.com" };
        var store     = new InMemorySecretStore();
        var extraEnv  = new Dictionary<string, string> { ["FOO"] = "bar" };

        var result = Importer.Import(candidate, "Personal", [], store, extraEnv);

        var created = Assert.IsType<ImportResult.Created>(result);
        Assert.Equal(extraEnv, created.Account.ExtraEnv);
    }

    [Fact]
    public void Import_WithoutExtraEnv_LeavesExtraEnvNull()
    {
        var candidate = new ImportCandidate.Token { Secret = "sk", AuthKind = AuthKind.AuthToken };
        var store     = new InMemorySecretStore();

        var result = Importer.Import(candidate, "Work", [], store);

        var created = Assert.IsType<ImportResult.Created>(result);
        Assert.Null(created.Account.ExtraEnvNullable);
    }

    // -----------------------------------------------------------------------
    // CurrentExtraEnv tests
    // -----------------------------------------------------------------------

    [Fact]
    public void CurrentExtraEnv_ReturnsOnlyNonManagedKeys()
    {
        var path = WriteSettings("""
        {
          "env": {
            "ANTHROPIC_AUTH_TOKEN": "sk-live",
            "ANTHROPIC_BASE_URL": "https://api.anthropic.com",
            "HTTP_PROXY": "http://127.0.0.1:8080",
            "CUSTOM_VAR": "value",
            "ANOTHER": "42"
          }
        }
        """);

        var env = Importer.CurrentExtraEnv(path);

        Assert.Equal(2, env.Count);
        Assert.Equal("value", env["CUSTOM_VAR"]);
        Assert.Equal("42", env["ANOTHER"]);
        Assert.DoesNotContain("ANTHROPIC_AUTH_TOKEN", env.Keys);
        Assert.DoesNotContain("ANTHROPIC_BASE_URL", env.Keys);
        Assert.DoesNotContain("HTTP_PROXY", env.Keys);
    }

    [Fact]
    public void CurrentExtraEnv_ReturnsEmpty_WhenSettingsMissing()
    {
        var path = Path.Combine(_tmpDir, "does-not-exist.json");

        Assert.Empty(Importer.CurrentExtraEnv(path));
    }

    [Fact]
    public void CurrentExtraEnv_ReturnsEmpty_WhenNoEnvObject()
    {
        var path = WriteSettings("""{ "permissions": {} }""");

        Assert.Empty(Importer.CurrentExtraEnv(path));
    }

    [Fact]
    public void CurrentExtraEnv_SkipsNonStringAndEmptyValues()
    {
        var path = WriteSettings("""
        {
          "env": {
            "GOOD": "yes",
            "EMPTY": "",
            "NUMBER": 5,
            "NULLED": null
          }
        }
        """);

        var env = Importer.CurrentExtraEnv(path);

        Assert.Single(env);
        Assert.Equal("yes", env["GOOD"]);
    }

    [Fact]
    public void Import_Token_DuplicateBySameProviderAndKey_ReturnsWarning()
    {
        var candidate = new ImportCandidate.Token
        {
            Secret   = "sk-new",
            AuthKind = AuthKind.AuthToken,
            BaseUrl  = "https://api.anthropic.com",
        };
        var existing = new List<Account>
        {
            TokenAccount("old-id", AuthKind.AuthToken, "https://api.anthropic.com"),
        };
        var store = new InMemorySecretStore();
        // Same provider AND same key (secret) → duplicate.
        store.Set("old-id", "sk-new");

        var result = Importer.Import(candidate, "New Work", existing, store);

        var warn = Assert.IsType<ImportResult.CreatedWithWarning>(result);
        Assert.Equal("New Work", warn.Account.Name);
        Assert.Contains("token-old-id", warn.Warning);
        Assert.Contains("already exists", warn.Warning);
    }

    [Fact]
    public void Import_Token_SameProviderDifferentKey_NoWarning()
    {
        // Same base_url + auth_kind but a different key (secret) → NOT a duplicate.
        var candidate = new ImportCandidate.Token
        {
            Secret   = "sk-new",
            AuthKind = AuthKind.AuthToken,
            BaseUrl  = "https://api.anthropic.com",
        };
        var existing = new List<Account>
        {
            TokenAccount("old-id", AuthKind.AuthToken, "https://api.anthropic.com"),
        };
        var store = new InMemorySecretStore();
        store.Set("old-id", "sk-different");

        var result = Importer.Import(candidate, "Another Key", existing, store);

        Assert.IsType<ImportResult.Created>(result);
    }

    [Fact]
    public void Import_Token_DifferentAuthKind_NoWarning()
    {
        var candidate = new ImportCandidate.Token
        {
            Secret   = "sk-new",
            AuthKind = AuthKind.ApiKey,
            BaseUrl  = "https://api.anthropic.com",
        };
        var existing = new List<Account>
        {
            TokenAccount("old-id", AuthKind.AuthToken, "https://api.anthropic.com"),
        };
        var store = new InMemorySecretStore();

        var result = Importer.Import(candidate, "API Key Account", existing, store);

        Assert.IsType<ImportResult.Created>(result);
    }

    [Fact]
    public void Import_Oauth_DuplicateByIdentity_ReturnsWarning()
    {
        var candidate = new ImportCandidate.Oauth
        {
            Blob     = """{"claudeAiOauth":{"accessToken":"new"}}""",
            Identity = "user@example.com",
        };
        var existing = new List<Account>
        {
            OauthAccount("old-id", "user@example.com"),
        };
        var store = new InMemorySecretStore();

        var result = Importer.Import(candidate, "New Personal", existing, store);

        var warn = Assert.IsType<ImportResult.CreatedWithWarning>(result);
        Assert.Equal("New Personal", warn.Account.Name);
        Assert.Contains("oauth-old-id", warn.Warning);
    }

    [Fact]
    public void Import_Oauth_NoIdentity_SkipsIdentityDedup()
    {
        var candidate = new ImportCandidate.Oauth
        {
            Blob     = """{"claudeAiOauth":{"accessToken":"new"}}""",
            Identity = null,
        };
        // Existing account also has no identity and no stored blob in keyring.
        var existing = new List<Account> { OauthAccount("old-id") };
        var store    = new InMemorySecretStore();

        var result = Importer.Import(candidate, "Another OAuth", existing, store);

        // No warning: identity is None AND existing account has no stored blob
        // to compare against (empty keyring).
        Assert.IsType<ImportResult.Created>(result);
    }

    [Fact]
    public void Import_Oauth_SameBlobNoIdentity_NotDuplicate()
    {
        // Without a stable identity we do NOT dedup on the blob fingerprint
        // (after stripping volatile tokens the blob is not account-unique), so
        // this is treated as a new account.
        var candidate = new ImportCandidate.Oauth
        {
            Blob     = """{"claudeAiOauth":{"accessToken":"fresh","refreshToken":"r","expiresAt":999}}""",
            Identity = null,
        };
        var existing = new List<Account> { OauthAccount("old-id") };
        var store    = new InMemorySecretStore();
        store.Set("old-id", """{"claudeAiOauth":{"accessToken":"stale","expiresAt":1}}""");

        var result = Importer.Import(candidate, "Another OAuth", existing, store);

        Assert.IsType<ImportResult.Created>(result);
    }

    // -----------------------------------------------------------------------
    // FindDuplicate tests
    // -----------------------------------------------------------------------

    [Fact]
    public void FindDuplicate_Token_SameProviderAndKey_ReturnsAccount()
    {
        var candidate = new ImportCandidate.Token
        {
            Secret   = "sk-key",
            AuthKind = AuthKind.AuthToken,
            BaseUrl  = "https://api.anthropic.com",
        };
        var existing = new List<Account>
        {
            TokenAccount("old-id", AuthKind.AuthToken, "https://api.anthropic.com"),
        };
        var store = new InMemorySecretStore();
        store.Set("old-id", "sk-key");

        var dup = Importer.FindDuplicate(candidate, existing, store);

        Assert.NotNull(dup);
        Assert.Equal("old-id", dup!.Id);
    }

    [Fact]
    public void FindDuplicate_Token_DifferentKey_ReturnsNull()
    {
        var candidate = new ImportCandidate.Token
        {
            Secret   = "sk-key",
            AuthKind = AuthKind.AuthToken,
            BaseUrl  = "https://api.anthropic.com",
        };
        var existing = new List<Account>
        {
            TokenAccount("old-id", AuthKind.AuthToken, "https://api.anthropic.com"),
        };
        var store = new InMemorySecretStore();
        store.Set("old-id", "sk-other");

        Assert.Null(Importer.FindDuplicate(candidate, existing, store));
    }

    [Fact]
    public void FindDuplicate_Oauth_SameIdentity_ReturnsAccount()
    {
        var candidate = new ImportCandidate.Oauth
        {
            Blob     = """{"claudeAiOauth":{"accessToken":"x"}}""",
            Identity = "acc-uuid",
        };
        var existing = new List<Account> { OauthAccount("old-id", "acc-uuid") };
        var store    = new InMemorySecretStore();

        var dup = Importer.FindDuplicate(candidate, existing, store);

        Assert.NotNull(dup);
        Assert.Equal("old-id", dup!.Id);
    }

    [Fact]
    public void FindDuplicate_Oauth_DifferentIdentity_ReturnsNull()
    {
        var candidate = new ImportCandidate.Oauth
        {
            Blob     = """{"claudeAiOauth":{"accessToken":"x"}}""",
            Identity = "acc-A",
        };
        var existing = new List<Account> { OauthAccount("old-id", "acc-B") };
        var store    = new InMemorySecretStore();

        Assert.Null(Importer.FindDuplicate(candidate, existing, store));
    }

    [Fact]
    public void FindDuplicate_Oauth_NullIdentity_ReturnsNull()
    {
        var candidate = new ImportCandidate.Oauth
        {
            Blob     = """{"claudeAiOauth":{"accessToken":"x"}}""",
            Identity = null,
        };
        var existing = new List<Account> { OauthAccount("old-id", "acc-B") };
        var store    = new InMemorySecretStore();

        Assert.Null(Importer.FindDuplicate(candidate, existing, store));
    }

    // -----------------------------------------------------------------------
    // FindCurrentManagedAccount tests
    // -----------------------------------------------------------------------

    [Fact]
    public void FindCurrentManagedAccount_ReturnsActiveToken_WhenItsManagedTokenIsLive()
    {
        // Active token account whose managed AUTH_TOKEN is present in env and
        // matches the secret in the keyring → it is the current login.
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-live","ANTHROPIC_BASE_URL":"https://api.z.ai/api/anthropic"}}""");
        var store = new InMemorySecretStore();
        store.Set("zai", "sk-live");

        var accounts = new List<Account>
        {
            TokenAccount("zai", AuthKind.AuthToken, "https://api.z.ai/api/anthropic"),
        };

        var result = Importer.FindCurrentManagedAccount(
            accounts, "zai", ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"], sp, store);

        Assert.NotNull(result);
        Assert.Equal("zai", result!.Id);
    }

    [Fact]
    public void FindCurrentManagedAccount_ReturnsNull_WhenNoActiveAccount()
    {
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-live"}}""");
        var store = new InMemorySecretStore();

        var result = Importer.FindCurrentManagedAccount(
            [], null, ["ANTHROPIC_AUTH_TOKEN"], sp, store);

        Assert.Null(result);
    }

    [Fact]
    public void FindCurrentManagedAccount_ReturnsNull_WhenActiveIsOauth()
    {
        // OAuth-active is handled by the normal Detect + FindDuplicate path.
        var sp    = WriteSettings("""{"env":{}}""");
        var store = new InMemorySecretStore();
        var accounts = new List<Account> { OauthAccount("anthropic", "acc-uuid") };

        var result = Importer.FindCurrentManagedAccount(
            accounts, "anthropic", [], sp, store);

        Assert.Null(result);
    }

    [Fact]
    public void FindCurrentManagedAccount_ReturnsNull_WhenKeyNotManaged()
    {
        // Active token account but its auth key is not in managed_keys (ccswitcher
        // is not driving it) → don't claim it as the current managed login.
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-live"}}""");
        var store = new InMemorySecretStore();
        store.Set("zai", "sk-live");
        var accounts = new List<Account> { TokenAccount("zai", AuthKind.AuthToken, "https://api.z.ai") };

        var result = Importer.FindCurrentManagedAccount(accounts, "zai", [], sp, store);

        Assert.Null(result);
    }

    [Fact]
    public void FindCurrentManagedAccount_ReturnsNull_WhenLiveTokenDiffersFromStored()
    {
        // The env token was swapped manually and no longer matches the stored
        // secret → it's not the active account's login; fall through to Detect.
        var sp    = WriteSettings("""{"env":{"ANTHROPIC_AUTH_TOKEN":"sk-different"}}""");
        var store = new InMemorySecretStore();
        store.Set("zai", "sk-live");
        var accounts = new List<Account> { TokenAccount("zai", AuthKind.AuthToken, "https://api.z.ai") };

        var result = Importer.FindCurrentManagedAccount(
            accounts, "zai", ["ANTHROPIC_AUTH_TOKEN"], sp, store);

        Assert.Null(result);
    }

    // -----------------------------------------------------------------------
    // ExtractIdentityFromBlob tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractIdentityFromBlob_ParsesEmail()
    {
        var blob = """{"claudeAiOauth":{"email":"user@example.com","accessToken":"tok"}}""";
        Assert.Equal("user@example.com", Importer.ExtractIdentityFromBlob(blob));
    }

    [Fact]
    public void ExtractIdentityFromBlob_ParsesAccountId()
    {
        var blob = """{"claudeAiOauth":{"account_id":"acc-123","accessToken":"tok"}}""";
        Assert.Equal("acc-123", Importer.ExtractIdentityFromBlob(blob));
    }

    [Fact]
    public void ExtractIdentityFromBlob_ReturnsNull_ForMalformedBlob()
    {
        Assert.Null(Importer.ExtractIdentityFromBlob("not json"));
        Assert.Null(Importer.ExtractIdentityFromBlob("""{"wrongKey":{}}"""));
        Assert.Null(Importer.ExtractIdentityFromBlob("""{"claudeAiOauth":{}}"""));
    }

    // -----------------------------------------------------------------------
    // ExtractIdentityFromUserConfig tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractIdentityFromUserConfig_PrefersAccountUuid()
    {
        var ucp = WriteUserConfig("""{"userID":"u","oauthAccount":{"accountUuid":"acc-uuid","emailAddress":"u@x.com"}}""");
        Assert.Equal("acc-uuid", Importer.ExtractIdentityFromUserConfig(ucp));
    }

    [Fact]
    public void ExtractIdentityFromUserConfig_FallsBackToEmail()
    {
        var ucp = WriteUserConfig("""{"oauthAccount":{"emailAddress":"u@x.com"}}""");
        Assert.Equal("u@x.com", Importer.ExtractIdentityFromUserConfig(ucp));
    }

    [Fact]
    public void ExtractIdentityFromUserConfig_ReturnsNull_WhenNoOauthAccount()
    {
        var ucp = WriteUserConfig("""{"userID":"u"}""");
        Assert.Null(Importer.ExtractIdentityFromUserConfig(ucp));
    }

    [Fact]
    public void ExtractIdentityFromUserConfig_ReturnsNull_WhenFileMissing()
    {
        var missingPath = Path.Combine(_tmpDir, "nonexistent.json");
        Assert.Null(Importer.ExtractIdentityFromUserConfig(missingPath));
    }
}
