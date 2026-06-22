// Tests for UserConfig — mirrors the Rust test suite in user_config.rs.
//
// All tests use isolated temporary directories so they never touch real files.

using System.Text.Json.Nodes;
using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class UserConfigTests : IDisposable
{
    // One temp directory per test instance; cleaned up in Dispose.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ccswitcher-tests-{Guid.NewGuid():N}");

    public UserConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string ConfigPath() => Path.Combine(_dir, ".claude.json");

    // -----------------------------------------------------------------------
    // OauthAccountKey tests
    // -----------------------------------------------------------------------

    [Fact]
    public void OauthAccountKey_ReturnsExpectedFormat()
    {
        Assert.Equal("my-id#oauthAccount", UserConfig.OauthAccountKey("my-id"));
        Assert.Equal("abc-123#oauthAccount", UserConfig.OauthAccountKey("abc-123"));
    }

    // -----------------------------------------------------------------------
    // ReadOauthAccount tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ReadOauthAccount_ReturnsSectionWhenPresent()
    {
        var path = ConfigPath();
        File.WriteAllText(path, """{"userID":"u","oauthAccount":{"emailAddress":"a@x","accountUuid":"uuid-a"}}""");

        var result = UserConfig.ReadOauthAccount(path);

        Assert.NotNull(result);
        Assert.Equal("a@x", result!["emailAddress"]!.GetValue<string>());
        Assert.Equal("uuid-a", result["accountUuid"]!.GetValue<string>());
    }

    [Fact]
    public void ReadOauthAccount_ReturnsNullWhenFileMissing()
    {
        var path = Path.Combine(_dir, "does-not-exist.json");

        var result = UserConfig.ReadOauthAccount(path);

        Assert.Null(result);
    }

    [Fact]
    public void ReadOauthAccount_ReturnsNullWhenKeyAbsent()
    {
        var path = ConfigPath();
        File.WriteAllText(path, """{"userID":"u"}""");

        var result = UserConfig.ReadOauthAccount(path);

        Assert.Null(result);
    }

    [Fact]
    public void ReadOauthAccount_ThrowsUserConfigExceptionOnInvalidJson()
    {
        var path = ConfigPath();
        File.WriteAllText(path, "not json");

        Assert.Throws<UserConfigException>(() => UserConfig.ReadOauthAccount(path));
    }

    // -----------------------------------------------------------------------
    // MergeOauthAccount tests
    // -----------------------------------------------------------------------

    [Fact]
    public void MergeOauthAccount_ReplacesOnlyOauthAccountPreservingOtherFields()
    {
        var path = ConfigPath();
        File.WriteAllText(path, """{"userID":"keep","projects":{"x":1},"oauthAccount":{"emailAddress":"old@x"}}""");

        var newOauth = JsonNode.Parse("""{"emailAddress":"new@x","accountUuid":"uuid-new"}""")!;
        UserConfig.MergeOauthAccount(path, newOauth);

        var after = JsonNode.Parse(File.ReadAllText(path))!;
        // oauthAccount swapped.
        Assert.Equal("new@x", after["oauthAccount"]!["emailAddress"]!.GetValue<string>());
        Assert.Equal("uuid-new", after["oauthAccount"]!["accountUuid"]!.GetValue<string>());
        // Other fields untouched.
        Assert.Equal("keep", after["userID"]!.GetValue<string>());
        Assert.Equal(1, after["projects"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public void MergeOauthAccount_CreatesFileWhenAbsent()
    {
        var path = ConfigPath();
        Assert.False(File.Exists(path));

        var oauth = JsonNode.Parse("""{"emailAddress":"a@x"}""")!;
        UserConfig.MergeOauthAccount(path, oauth);

        Assert.True(File.Exists(path));
        var after = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("a@x", after["oauthAccount"]!["emailAddress"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""["not","an","object"]""")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void MergeOauthAccount_RejectsNonObjectOauth(string nonObjectJson)
    {
        var path = ConfigPath();
        // File must not be created on rejection.

        var nonObject = JsonNode.Parse(nonObjectJson)!;
        Assert.Throws<UserConfigException>(() => UserConfig.MergeOauthAccount(path, nonObject));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void MergeOauthAccount_CreatesBackupWhenFileAlreadyExists()
    {
        var path = ConfigPath();
        File.WriteAllText(path, """{"oauthAccount":{"emailAddress":"old@x"}}""");

        var oauth = JsonNode.Parse("""{"emailAddress":"new@x"}""")!;
        UserConfig.MergeOauthAccount(path, oauth);

        var backupsDir = Path.Combine(_dir, "backups");
        Assert.True(Directory.Exists(backupsDir), "backups dir should exist");

        var baks = Directory.GetFiles(backupsDir, "*.bak");
        Assert.NotEmpty(baks);
    }

    [Fact]
    public void MergeOauthAccount_NoBackupWhenFileAbsent()
    {
        var path = ConfigPath();
        Assert.False(File.Exists(path));

        var oauth = JsonNode.Parse("""{"emailAddress":"a@x"}""")!;
        UserConfig.MergeOauthAccount(path, oauth);

        var backupsDir = Path.Combine(_dir, "backups");
        // Either no backups dir, or dir exists but has no .bak files.
        if (Directory.Exists(backupsDir))
        {
            var baks = Directory.GetFiles(backupsDir, "*.bak");
            Assert.Empty(baks);
        }
    }

    [Fact]
    public void MergeOauthAccount_OauthNodeIsDeepCloned()
    {
        // Mutating the original node after the call must not affect what was written.
        var path = ConfigPath();
        var oauthObj = JsonNode.Parse("""{"emailAddress":"original@x"}""")!.AsObject();

        UserConfig.MergeOauthAccount(path, oauthObj);

        // Mutate the original node.
        oauthObj["emailAddress"] = JsonValue.Create("mutated@x");

        var after = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("original@x", after["oauthAccount"]!["emailAddress"]!.GetValue<string>());
    }

    [Fact]
    public void MergeOauthAccount_ResetsTopLevelNonObjectToEmptyObject()
    {
        // If the existing file has a JSON array at top level, reset to empty object.
        var path = ConfigPath();
        File.WriteAllText(path, """["an","array"]""");

        var oauth = JsonNode.Parse("""{"emailAddress":"a@x"}""")!;
        UserConfig.MergeOauthAccount(path, oauth);

        var after = JsonNode.Parse(File.ReadAllText(path))!;
        // Must be a JSON object containing only oauthAccount.
        Assert.NotNull(after.AsObject());
        Assert.Equal("a@x", after["oauthAccount"]!["emailAddress"]!.GetValue<string>());
    }
}
