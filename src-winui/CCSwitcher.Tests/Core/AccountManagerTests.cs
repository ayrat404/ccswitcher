// Tests for AccountManager — add, update, delete account CRUD.
//
// All tests use a temp directory for config.json and an InMemorySecretStore
// so no real filesystem or OS keyring is touched.

using System.Text.Json.Nodes;
using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class AccountManagerTests : IDisposable
{
    private readonly string _configDir;
    private readonly InMemorySecretStore _secrets;

    public AccountManagerTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"ccsw-am-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configDir);
        _secrets = new InMemorySecretStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AppConfig EmptyConfig() => new();

    private static Account MakeTokenAccount(string id, string name = "Test") => new()
    {
        Id          = id,
        Name        = name,
        AccountType = AccountType.Token,
        AuthKind    = AuthKind.AuthToken,
    };

    // -----------------------------------------------------------------------
    // AddTokenAccount
    // -----------------------------------------------------------------------

    [Fact]
    public void AddTokenAccount_CreatesAccountAndStoresSecret()
    {
        var config = EmptyConfig();

        var account = AccountManager.AddTokenAccount(
            config, "Work", null, AuthKind.AuthToken, "sk-ant-secret", null, _secrets, _configDir);

        // Account was appended to config.
        Assert.Single(config.Accounts);
        Assert.Equal("Work", account.Name);
        Assert.Equal(AccountType.Token, account.AccountType);
        Assert.Equal(AuthKind.AuthToken, account.AuthKind);
        Assert.Null(account.BaseUrl);

        // Secret was stored in keyring.
        Assert.Equal("sk-ant-secret", _secrets.Get(account.Id));
    }

    [Fact]
    public void AddTokenAccount_StoresBaseUrl()
    {
        var config = EmptyConfig();

        var account = AccountManager.AddTokenAccount(
            config, "Custom", "https://proxy.example.com", AuthKind.ApiKey, "key", null, _secrets, _configDir);

        Assert.Equal("https://proxy.example.com", account.BaseUrl);
        Assert.Equal(AuthKind.ApiKey, account.AuthKind);
    }

    [Fact]
    public void AddTokenAccount_PersistsConfig()
    {
        var config = EmptyConfig();
        AccountManager.AddTokenAccount(config, "Work", null, AuthKind.AuthToken, "s", null, _secrets, _configDir);

        // Reload from disk and verify the account survived.
        var reloaded = ConfigStore.Load(_configDir);
        Assert.Single(reloaded.Accounts);
        Assert.Equal("Work", reloaded.Accounts[0].Name);
    }

    [Fact]
    public void AddTokenAccount_ReturnsFreshUuid()
    {
        var config = EmptyConfig();

        var a1 = AccountManager.AddTokenAccount(config, "A", null, AuthKind.AuthToken, "s1", null, _secrets, _configDir);
        var a2 = AccountManager.AddTokenAccount(config, "B", null, AuthKind.AuthToken, "s2", null, _secrets, _configDir);

        Assert.NotEmpty(a1.Id);
        Assert.NotEmpty(a2.Id);
        Assert.NotEqual(a1.Id, a2.Id);

        // Ids must be valid GUIDs.
        Assert.True(Guid.TryParse(a1.Id, out _));
        Assert.True(Guid.TryParse(a2.Id, out _));
    }

    // -----------------------------------------------------------------------
    // UpdateAccount
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateAccount_RenamesAccount()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1", "Old Name"));

        AccountManager.UpdateAccount(config, "id-1", "New Name", null, null, null, null, _secrets, _configDir);

        Assert.Equal("New Name", config.Accounts[0].Name);
    }

    [Fact]
    public void UpdateAccount_UpdatesSecretWhenProvided()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        _secrets.Set("id-1", "old-secret");

        AccountManager.UpdateAccount(config, "id-1", "Test", null, null, "new-secret", null, _secrets, _configDir);

        Assert.Equal("new-secret", _secrets.Get("id-1"));
    }

    [Fact]
    public void UpdateAccount_DoesNotTouchKeyringWhenNewSecretIsNull()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        _secrets.Set("id-1", "original-secret");

        AccountManager.UpdateAccount(config, "id-1", "Renamed", null, null, null, null, _secrets, _configDir);

        // Secret must be unchanged.
        Assert.Equal("original-secret", _secrets.Get("id-1"));
    }

    [Fact]
    public void UpdateAccount_DoesNotTouchKeyringWhenNewSecretIsEmpty()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        _secrets.Set("id-1", "original-secret");

        AccountManager.UpdateAccount(config, "id-1", "Renamed", null, null, "", null, _secrets, _configDir);

        Assert.Equal("original-secret", _secrets.Get("id-1"));
    }

    [Fact]
    public void UpdateAccount_ThrowsForUnknownId()
    {
        var config = EmptyConfig();

        Assert.Throws<AccountNotFoundException>(() =>
            AccountManager.UpdateAccount(config, "no-such-id", "Name", null, null, null, null, _secrets, _configDir));
    }

    [Fact]
    public void UpdateAccount_PersistsConfig()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1", "Old"));
        ConfigStore.Save(_configDir, config);  // initial save

        AccountManager.UpdateAccount(config, "id-1", "Updated", null, null, null, null, _secrets, _configDir);

        var reloaded = ConfigStore.Load(_configDir);
        Assert.Equal("Updated", reloaded.Accounts[0].Name);
    }

    // -----------------------------------------------------------------------
    // DeleteAccount
    // -----------------------------------------------------------------------

    [Fact]
    public void DeleteAccount_RemovesAccountFromList()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        config.Accounts.Add(MakeTokenAccount("id-2", "Other"));

        AccountManager.DeleteAccount(config, "id-1", _secrets, _configDir);

        Assert.Single(config.Accounts);
        Assert.Equal("id-2", config.Accounts[0].Id);
    }

    [Fact]
    public void DeleteAccount_ClearsActiveAccountIdWhenItMatches()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        config.ActiveAccountId = "id-1";

        AccountManager.DeleteAccount(config, "id-1", _secrets, _configDir);

        Assert.Null(config.ActiveAccountId);
    }

    [Fact]
    public void DeleteAccount_DoesNotClearActiveAccountIdForDifferentAccount()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        config.Accounts.Add(MakeTokenAccount("id-2"));
        config.ActiveAccountId = "id-2";

        AccountManager.DeleteAccount(config, "id-1", _secrets, _configDir);

        // Active id for the surviving account must be unchanged.
        Assert.Equal("id-2", config.ActiveAccountId);
    }

    [Fact]
    public void DeleteAccount_RemovesKeyringSecret()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        _secrets.Set("id-1", "token");

        AccountManager.DeleteAccount(config, "id-1", _secrets, _configDir);

        Assert.Null(_secrets.Get("id-1"));
    }

    [Fact]
    public void DeleteAccount_RemovesOauthAccountKeyringEntry()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        _secrets.Set(UserConfig.OauthAccountKey("id-1"), "oauth-blob");

        AccountManager.DeleteAccount(config, "id-1", _secrets, _configDir);

        Assert.Null(_secrets.Get(UserConfig.OauthAccountKey("id-1")));
    }

    [Fact]
    public void DeleteAccount_ThrowsForUnknownId()
    {
        var config = EmptyConfig();

        Assert.Throws<AccountNotFoundException>(() =>
            AccountManager.DeleteAccount(config, "no-such-id", _secrets, _configDir));
    }

    [Fact]
    public void DeleteAccount_PersistsConfig()
    {
        var config = EmptyConfig();
        config.Accounts.Add(MakeTokenAccount("id-1"));
        config.Accounts.Add(MakeTokenAccount("id-2", "Survivor"));
        ConfigStore.Save(_configDir, config);

        AccountManager.DeleteAccount(config, "id-1", _secrets, _configDir);

        var reloaded = ConfigStore.Load(_configDir);
        Assert.Single(reloaded.Accounts);
        Assert.Equal("id-2", reloaded.Accounts[0].Id);
    }

    // -----------------------------------------------------------------------
    // Extra environment variables
    // -----------------------------------------------------------------------

    [Fact]
    public void AddTokenAccount_StoresExtraEnv()
    {
        var config = EmptyConfig();
        var env = new Dictionary<string, string> { ["ANTHROPIC_LOG"] = "debug", ["MY_FLAG"] = "1" };

        var account = AccountManager.AddTokenAccount(
            config, "Work", null, AuthKind.AuthToken, "s", env, _secrets, _configDir);

        Assert.NotNull(account.ExtraEnvNullable);
        Assert.Equal("debug", account.ExtraEnvNullable!["ANTHROPIC_LOG"]);
        Assert.Equal("1", account.ExtraEnvNullable!["MY_FLAG"]);

        // Persists to disk under the documented "extra_env" field.
        var reloaded = ConfigStore.Load(_configDir);
        Assert.Equal("debug", reloaded.Accounts[0].ExtraEnv["ANTHROPIC_LOG"]);
    }

    [Fact]
    public void AddTokenAccount_NullExtraEnv_OmittedFromJson()
    {
        var config = EmptyConfig();

        AccountManager.AddTokenAccount(
            config, "Work", null, AuthKind.AuthToken, "s", null, _secrets, _configDir);

        var json = File.ReadAllText(Path.Combine(_configDir, "config.json"));
        Assert.DoesNotContain("extra_env", json);
    }

    [Fact]
    public void UpdateAccount_ReplacesExtraEnv()
    {
        var config = EmptyConfig();
        config.Accounts.Add(new Account
        {
            Id               = "id-1",
            Name             = "Test",
            AccountType      = AccountType.Token,
            AuthKind         = AuthKind.AuthToken,
            ExtraEnvNullable = new Dictionary<string, string> { ["OLD"] = "x" },
        });

        var replacement = new Dictionary<string, string> { ["NEW"] = "y" };
        AccountManager.UpdateAccount(config, "id-1", "Test", null, null, null, replacement, _secrets, _configDir);

        Assert.NotNull(config.Accounts[0].ExtraEnvNullable);
        Assert.False(config.Accounts[0].ExtraEnvNullable!.ContainsKey("OLD"));
        Assert.Equal("y", config.Accounts[0].ExtraEnvNullable!["NEW"]);
    }

    [Fact]
    public void UpdateAccount_EmptyExtraEnv_ClearsExisting()
    {
        var config = EmptyConfig();
        config.Accounts.Add(new Account
        {
            Id               = "id-1",
            Name             = "Test",
            AccountType      = AccountType.Token,
            AuthKind         = AuthKind.AuthToken,
            ExtraEnvNullable = new Dictionary<string, string> { ["OLD"] = "x" },
        });

        AccountManager.UpdateAccount(
            config, "id-1", "Test", null, null, null, new Dictionary<string, string>(), _secrets, _configDir);

        Assert.Null(config.Accounts[0].ExtraEnvNullable);
    }

    [Fact]
    public void UpdateAccount_PreservesSavedSettings()
    {
        var config = EmptyConfig();
        config.Accounts.Add(new Account
        {
            Id            = "id-1",
            Name          = "Test",
            AccountType   = AccountType.Token,
            AuthKind      = AuthKind.AuthToken,
            SavedSettings = new JsonObject { ["model"] = "claude-sonnet-4-6" },
        });

        AccountManager.UpdateAccount(config, "id-1", "Renamed", null, null, null, null, _secrets, _configDir);

        Assert.NotNull(config.Accounts[0].SavedSettings);
        Assert.Equal("claude-sonnet-4-6", config.Accounts[0].SavedSettings!["model"]?.GetValue<string>());
    }
}
