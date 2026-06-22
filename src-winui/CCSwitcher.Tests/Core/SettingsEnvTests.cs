// Tests for SettingsEnv — mirrors the Rust test suite in settings_env.rs.
//
// Tests never touch real Claude Code files; they operate on isolated
// temp directories and in-memory JsonObject values.

using System.Text.Json.Nodes;
using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class SettingsEnvTests : IDisposable
{
    // One isolated temp directory per test instance.
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"ccswitcher-settings-tests-{Guid.NewGuid():N}");

    public SettingsEnvTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string SettingsPath(string name = "settings.json")
        => Path.Combine(_dir, name);

    private static Dictionary<string, string> Env(params (string k, string v)[] pairs)
        => pairs.ToDictionary(p => p.k, p => p.v);

    /// <summary>
    /// Extract the "env" child object from a settings root.
    /// Asserts that it exists and is a JsonObject.
    /// </summary>
    private static JsonObject EnvOf(JsonObject settings)
    {
        var node = settings["env"];
        Assert.NotNull(node);
        var obj = Assert.IsType<JsonObject>(node);
        return obj;
    }

    // -----------------------------------------------------------------------
    // Load tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Load_MissingFile_ReturnsEmptyObject()
    {
        var path = SettingsPath();
        // Do not create the file.
        var result = SettingsEnv.Load(path);
        Assert.NotNull(result);
        Assert.Empty(result); // JsonObject has no properties
        // Loading must NOT create the file.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_InvalidJson_ThrowsSettingsEnvException()
    {
        var path = SettingsPath();
        var original = "{ not valid json";
        File.WriteAllText(path, original);

        var ex = Assert.Throws<SettingsEnvException>(() => SettingsEnv.Load(path));
        Assert.Contains("not valid JSON", ex.Message);

        // The file must be left untouched.
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Load_ValidJsonArrayTopLevel_ThrowsSettingsEnvException()
    {
        var path = SettingsPath();
        File.WriteAllText(path, "[1,2,3]");

        var ex = Assert.Throws<SettingsEnvException>(() => SettingsEnv.Load(path));
        Assert.Contains("not a JSON object", ex.Message);
    }

    [Fact]
    public void Load_ValidJsonStringTopLevel_ThrowsSettingsEnvException()
    {
        var path = SettingsPath();
        File.WriteAllText(path, "\"hello\"");

        var ex = Assert.Throws<SettingsEnvException>(() => SettingsEnv.Load(path));
        Assert.Contains("not a JSON object", ex.Message);
    }

    [Fact]
    public void Load_ValidObjectFile_ReturnsSettings()
    {
        var path = SettingsPath();
        File.WriteAllText(path, """{"env":{"FOO":"bar"},"permissions":{}}""");

        var settings = SettingsEnv.Load(path);
        Assert.Equal("bar", settings["env"]?["FOO"]?.GetValue<string>());
        Assert.NotNull(settings["permissions"]);
    }

    // -----------------------------------------------------------------------
    // MergeEnv tests
    // -----------------------------------------------------------------------

    [Fact]
    public void MergeEnv_ManagedKeysAreReplaced()
    {
        // "env" already has an old ANTHROPIC_AUTH_TOKEN; it must be replaced
        // with the new value, not duplicated.
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_AUTH_TOKEN":"old-tok"}}""")!.AsObject();
        var newEnv = Env(("ANTHROPIC_AUTH_TOKEN", "new-tok"));

        var (merged, _) = SettingsEnv.MergeEnv(settings, [], newEnv);
        var env = EnvOf(merged);

        Assert.Equal("new-tok", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
    }

    [Fact]
    public void MergeEnv_UserKeysSurvive()
    {
        // A key that is NOT in MANAGED_KEYS must not be touched.
        var settings = JsonNode.Parse(
            """{"env":{"MY_OWN_KEY":"keep-me"}}""")!.AsObject();
        var newEnv = Env(("ANTHROPIC_AUTH_TOKEN", "tok"));

        var (merged, _) = SettingsEnv.MergeEnv(settings, [], newEnv);
        var env = EnvOf(merged);

        Assert.Equal("keep-me", env["MY_OWN_KEY"]?.GetValue<string>());
        Assert.Equal("tok", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
    }

    [Fact]
    public void MergeEnv_UnionOfOldAndNewManagedKeysStripped()
    {
        // Previous switch wrote CUSTOM_PROXY_VAR (tracked in old managed keys).
        // The new switch doesn't include it → it must be removed.
        var settings = JsonNode.Parse(
            """{"env":{"CUSTOM_PROXY_VAR":"old","MY_OWN_KEY":"keep"}}""")!.AsObject();
        var oldManaged = new[] { "CUSTOM_PROXY_VAR" };
        var newEnv = Env(("ANTHROPIC_AUTH_TOKEN", "tok"));

        var (merged, newKeys) = SettingsEnv.MergeEnv(settings, oldManaged, newEnv);
        var env = EnvOf(merged);

        Assert.False(env.ContainsKey("CUSTOM_PROXY_VAR"),
            "Previously-managed key must be removed.");
        Assert.Equal("keep", env["MY_OWN_KEY"]?.GetValue<string>());
        Assert.Contains("ANTHROPIC_AUTH_TOKEN", newKeys);
    }

    [Fact]
    public void MergeEnv_StaleManagedKeyRemovedEvenWithEmptyOldKeys()
    {
        // Leftover ANTHROPIC_API_KEY must be stripped by the constant MANAGED_KEYS
        // set alone, even when oldManagedKeys is empty.
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_API_KEY":"stale","MY_OWN_KEY":"keep"}}""")!.AsObject();
        var newEnv = Env(("ANTHROPIC_AUTH_TOKEN", "tok"));

        var (merged, _) = SettingsEnv.MergeEnv(settings, [], newEnv);
        var env = EnvOf(merged);

        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("tok", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.Equal("keep", env["MY_OWN_KEY"]?.GetValue<string>());
    }

    [Fact]
    public void MergeEnv_MissingEnvKey_TreatedAsEmptyDict()
    {
        // Settings object has no "env" key at all → must not throw.
        var settings = JsonNode.Parse("""{"permissions":{}}""")!.AsObject();
        var newEnv = Env(("ANTHROPIC_BASE_URL", "https://api.anthropic.com"));

        var (merged, newKeys) = SettingsEnv.MergeEnv(settings, [], newEnv);
        var env = EnvOf(merged);

        Assert.Equal("https://api.anthropic.com",
            env["ANTHROPIC_BASE_URL"]?.GetValue<string>());
        // Non-env settings must be preserved.
        Assert.NotNull(merged["permissions"]);
        Assert.Contains("ANTHROPIC_BASE_URL", newKeys);
    }

    [Fact]
    public void MergeEnv_NonObjectEnvValue_TreatedAsEmptyDict()
    {
        // "env" exists but is a string (malformed) → reset to {} and proceed.
        var settings = JsonNode.Parse("""{"env":"bad-value"}""")!.AsObject();
        var newEnv = Env(("ANTHROPIC_AUTH_TOKEN", "tok"));

        // Must not throw.
        var (merged, _) = SettingsEnv.MergeEnv(settings, [], newEnv);
        var env = EnvOf(merged);

        Assert.Equal("tok", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
    }

    [Fact]
    public void MergeEnv_ReturnedNewManagedKeys_ContainsExactlyNewEnvKeys()
    {
        var settings = new JsonObject();
        var newEnv = Env(
            ("ANTHROPIC_AUTH_TOKEN", "tok"),
            ("FOO", "bar"));

        var (_, newKeys) = SettingsEnv.MergeEnv(settings, [], newEnv);

        Assert.Equal(2, newKeys.Count);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN", newKeys);
        Assert.Contains("FOO", newKeys);
    }

    [Fact]
    public void MergeEnv_NonEnvSettingsAreUntouched()
    {
        var settings = JsonNode.Parse("""
            {
                "env": {"ANTHROPIC_API_KEY": "old"},
                "permissions": {"allow": ["Bash"]},
                "mcpServers": {"foo": {"command": "bar"}}
            }
            """)!.AsObject();

        var newEnv = Env(("ANTHROPIC_AUTH_TOKEN", "tok"));
        var (merged, _) = SettingsEnv.MergeEnv(settings, [], newEnv);

        Assert.Equal("Bash",
            merged["permissions"]?["allow"]?[0]?.GetValue<string>());
        Assert.Equal("bar",
            merged["mcpServers"]?["foo"]?["command"]?.GetValue<string>());
    }

    [Fact]
    public void MergeEnv_EmptyNewEnv_StripsManagedKeysOnly()
    {
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_AUTH_TOKEN":"tok","USER_KEY":"keep"}}""")!.AsObject();

        // Switch to an account that writes no env keys (unusual but valid).
        var (merged, newKeys) = SettingsEnv.MergeEnv(settings, [], new Dictionary<string, string>());
        var env = EnvOf(merged);

        Assert.False(env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.Equal("keep", env["USER_KEY"]?.GetValue<string>());
        Assert.Empty(newKeys);
    }
}
