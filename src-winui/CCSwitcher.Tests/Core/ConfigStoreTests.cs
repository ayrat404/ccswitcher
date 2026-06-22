// Tests for ClaudePaths and ConfigStore.
//
// All tests that touch the filesystem use isolated temp directories
// (never the user's real %APPDATA% or %USERPROFILE%).

using System.Text.Json;
using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class ConfigStoreTests : IDisposable
{
    // One temp directory per test instance; cleaned up in Dispose.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ccswitcher-tests-{Guid.NewGuid():N}");

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // ClaudePaths tests
    // -----------------------------------------------------------------------

    [Fact]
    public void SettingsPath_EndsWithClaudeSettingsJson()
    {
        var path = ClaudePaths.SettingsPath;
        Assert.EndsWith(Path.Combine(".claude", "settings.json"), path);
    }

    [Fact]
    public void CredentialsPath_EndsWithClaudeCredentialsJson()
    {
        var path = ClaudePaths.CredentialsPath;
        Assert.EndsWith(Path.Combine(".claude", ".credentials.json"), path);
    }

    [Fact]
    public void AppConfigDir_MatchesTauriPath()
    {
        // The C# AppConfigDir MUST be identical to the Tauri app's %APPDATA%\ccswitcher\
        // so both builds share the same config.json without migration.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ccswitcher");

        Assert.Equal(expected, ClaudePaths.AppConfigDir);
    }

    [Fact]
    public void AppConfigDir_ContainsCcswitcherSegment()
    {
        // Basic sanity: the path must contain the literal "ccswitcher" segment.
        Assert.Contains("ccswitcher", ClaudePaths.AppConfigDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppConfigDir_ContainsAppDataSegment()
    {
        // Must sit under %APPDATA%.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.StartsWith(appData, ClaudePaths.AppConfigDir, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // ConfigStore.Load tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        // _dir exists but contains no config.json.
        var config = ConfigStore.Load(_dir);

        Assert.NotNull(config);
        Assert.Equal(AppConfig.Default.SchemaVersion, config.SchemaVersion);
        Assert.Null(config.ActiveAccountId);
        Assert.Empty(config.Accounts);
        Assert.Empty(config.ManagedKeys);
    }

    [Fact]
    public void Load_InvalidJson_ThrowsJsonException()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ not valid json }}}");

        Assert.Throws<JsonException>(() => ConfigStore.Load(_dir));
    }

    [Fact]
    public void Load_EmptyFile_ThrowsJsonException()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), "");

        // An empty file is invalid JSON.
        Assert.Throws<JsonException>(() => ConfigStore.Load(_dir));
    }

    // -----------------------------------------------------------------------
    // ConfigStore.Save + Load round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllFields()
    {
        var original = new AppConfig
        {
            SchemaVersion = 1,
            ActiveAccountId = "acc-123",
            Proxy = new ProxySettings
            {
                Enabled = true,
                Url = "http://10.0.0.1:3128",
                NoProxy = "*.local",
            },
            ManagedKeys = new List<string> { "ANTHROPIC_AUTH_TOKEN", "HTTP_PROXY" },
            Accounts = new List<Account>
            {
                new Account
                {
                    Id = "acc-123",
                    Name = "My Token Account",
                    AccountType = AccountType.Token,
                    AuthKind = AuthKind.AuthToken,
                    BaseUrl = "https://api.example.com",
                    ExtraEnvNullable = new Dictionary<string, string> { ["FOO"] = "bar" },
                },
                new Account
                {
                    Id = "acc-456",
                    Name = "Anthropic OAuth",
                    AccountType = AccountType.AnthropicOauth,
                    Identity = "user@example.com",
                },
            },
        };

        ConfigStore.Save(_dir, original);
        var loaded = ConfigStore.Load(_dir);

        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(original.ActiveAccountId, loaded.ActiveAccountId);
        Assert.Equal(original.Proxy.Enabled, loaded.Proxy.Enabled);
        Assert.Equal(original.Proxy.Url, loaded.Proxy.Url);
        Assert.Equal(original.Proxy.NoProxy, loaded.Proxy.NoProxy);
        Assert.Equal(original.ManagedKeys, loaded.ManagedKeys);
        Assert.Equal(2, loaded.Accounts.Count);

        var tokenAcc = loaded.Accounts[0];
        Assert.Equal("acc-123", tokenAcc.Id);
        Assert.Equal("My Token Account", tokenAcc.Name);
        Assert.Equal(AccountType.Token, tokenAcc.AccountType);
        Assert.Equal(AuthKind.AuthToken, tokenAcc.AuthKind);
        Assert.Equal("https://api.example.com", tokenAcc.BaseUrl);
        Assert.Equal("bar", tokenAcc.ExtraEnv["FOO"]);

        var oauthAcc = loaded.Accounts[1];
        Assert.Equal("acc-456", oauthAcc.Id);
        Assert.Equal("Anthropic OAuth", oauthAcc.Name);
        Assert.Equal(AccountType.AnthropicOauth, oauthAcc.AccountType);
        Assert.Equal("user@example.com", oauthAcc.Identity);
        Assert.Null(oauthAcc.BaseUrl);
        Assert.Null(oauthAcc.AuthKind);
    }

    [Fact]
    public void Save_ThenLoad_MinimalConfig_PreservesDefaults()
    {
        // Save the default config and verify defaults survive the round-trip.
        ConfigStore.Save(_dir, AppConfig.Default);
        var loaded = ConfigStore.Load(_dir);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Null(loaded.ActiveAccountId);
        Assert.False(loaded.Proxy.Enabled);
        Assert.Equal("http://127.0.0.1:8080", loaded.Proxy.Url);
        Assert.Equal("localhost,127.0.0.1", loaded.Proxy.NoProxy);
        Assert.Empty(loaded.ManagedKeys);
        Assert.Empty(loaded.Accounts);
    }

    // -----------------------------------------------------------------------
    // ConfigStore.Save backup tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Save_CreatesBackupWhenFileAlreadyExists()
    {
        // Write a first config, then overwrite with a second.
        ConfigStore.Save(_dir, AppConfig.Default);

        var config2 = new AppConfig { ActiveAccountId = "acc-backup-test" };
        ConfigStore.Save(_dir, config2);

        // After the second save, a backup of the first file must exist.
        var backupsDir = Path.Combine(_dir, "backups");
        Assert.True(Directory.Exists(backupsDir));

        var baks = Directory.GetFiles(backupsDir, "*.bak");
        Assert.NotEmpty(baks);
    }

    [Fact]
    public void Save_DoesNotCreateBackupWhenFileAbsent()
    {
        // First ever save — no existing file to back up.
        ConfigStore.Save(_dir, AppConfig.Default);

        var backupsDir = Path.Combine(_dir, "backups");

        // Either backups dir does not exist, or it has no .bak files.
        if (Directory.Exists(backupsDir))
        {
            var baks = Directory.GetFiles(backupsDir, "*.bak");
            Assert.Empty(baks);
        }
        // else: directory not created at all — also fine.
    }

    [Fact]
    public void Save_CreatesDirectoryIfAbsent()
    {
        // Point at a subdirectory that has not been created yet.
        var subDir = Path.Combine(_dir, "new-subdir");
        Assert.False(Directory.Exists(subDir));

        ConfigStore.Save(subDir, AppConfig.Default);

        Assert.True(Directory.Exists(subDir));
        Assert.True(File.Exists(Path.Combine(subDir, "config.json")));
    }

    [Fact]
    public void Save_ProducesValidConfigFile()
    {
        // The written JSON must be valid and loadable.
        var config = new AppConfig
        {
            ActiveAccountId = "x",
            Accounts = new List<Account>
            {
                new Account { Id = "x", Name = "X", AccountType = AccountType.Token, AuthKind = AuthKind.ApiKey },
            },
        };

        ConfigStore.Save(_dir, config);

        // File must exist and be non-empty.
        var path = Path.Combine(_dir, "config.json");
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        // Must reload cleanly.
        var loaded = ConfigStore.Load(_dir);
        Assert.Equal("x", loaded.ActiveAccountId);
    }
}
