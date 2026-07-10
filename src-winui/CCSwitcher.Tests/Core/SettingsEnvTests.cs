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

    // -----------------------------------------------------------------------
    // CaptureSettings / RestoreSettings (tracked top-level keys)
    // -----------------------------------------------------------------------

    [Fact]
    public void CaptureSettings_CopiesPresentTrackedKeys()
    {
        var settings = JsonNode.Parse("""{"env":{},"model":"opus","editorMode":"vim"}""")!.AsObject();
        var into = new JsonObject();

        SettingsEnv.CaptureSettings(into, settings, new[] { "model" });

        Assert.Equal("opus", into["model"]?.GetValue<string>());
        // Untracked key not captured.
        Assert.False(into.ContainsKey("editorMode"));
    }

    [Fact]
    public void CaptureSettings_RecordsNull_WhenKeyAbsent()
    {
        // Claude Code omits "model" entirely when the model is "default". Capture
        // must record that as JSON null (the "default" state), overwriting any
        // previously-saved value.
        var settings = JsonNode.Parse("""{"env":{}}""")!.AsObject(); // no "model"
        var into = new JsonObject { ["model"] = "previously-saved" };

        SettingsEnv.CaptureSettings(into, settings, new[] { "model" });

        Assert.True(into.ContainsKey("model"));
        Assert.Null(into["model"]);
    }

    [Fact]
    public void RestoreSettings_WritesSavedValues()
    {
        var settings = JsonNode.Parse("""{"env":{},"model":"opus"}""")!.AsObject();
        var saved = new JsonObject { ["model"] = "glm" };

        SettingsEnv.RestoreSettings(settings, saved, new[] { "model" });

        Assert.Equal("glm", settings["model"]?.GetValue<string>());
    }

    [Fact]
    public void RestoreSettings_RemovesKey_WhenSavedIsNull()
    {
        // Captured "default" → the key must be dropped from settings (Claude Code
        // represents "default" by the key's absence).
        var settings = JsonNode.Parse("""{"env":{},"model":"opus"}""")!.AsObject();
        var saved = new JsonObject { ["model"] = null };

        SettingsEnv.RestoreSettings(settings, saved, new[] { "model" });

        Assert.False(settings.ContainsKey("model"));
    }

    [Fact]
    public void RestoreSettings_LeavesKeyUntouched_WhenNeverCaptured()
    {
        var settings = JsonNode.Parse("""{"env":{},"model":"opus"}""")!.AsObject();

        // Null snapshot → no-op.
        SettingsEnv.RestoreSettings(settings, null, new[] { "model" });
        Assert.Equal("opus", settings["model"]?.GetValue<string>());

        // Snapshot without the tracked key (never captured) → no-op.
        SettingsEnv.RestoreSettings(settings, new JsonObject(), new[] { "model" });
        Assert.Equal("opus", settings["model"]?.GetValue<string>());
    }

    // -----------------------------------------------------------------------
    // CaptureExtraEnv (live extra_env values read back from settings env)
    // -----------------------------------------------------------------------

    [Fact]
    public void CaptureExtraEnv_ReadsLiveValuesForRequestedKeys()
    {
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_MODEL":"opus","ANTHROPIC_SMALL_FAST_MODEL":"haiku","OTHER":"x"}}""")!.AsObject();

        var captured = SettingsEnv.CaptureExtraEnv(
            settings, new[] { "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL" });

        Assert.Equal(2, captured.Count);
        Assert.Equal("opus", captured["ANTHROPIC_MODEL"]);
        Assert.Equal("haiku", captured["ANTHROPIC_SMALL_FAST_MODEL"]);
        // Keys not requested are ignored.
        Assert.DoesNotContain("OTHER", captured.Keys);
    }

    [Fact]
    public void CaptureExtraEnv_DropsAbsentKey()
    {
        // A manually-deleted key (absent from env) is dropped, not kept stale.
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_MODEL":"opus"}}""")!.AsObject();

        var captured = SettingsEnv.CaptureExtraEnv(
            settings, new[] { "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL" });

        Assert.Single(captured);
        Assert.Equal("opus", captured["ANTHROPIC_MODEL"]);
        Assert.DoesNotContain("ANTHROPIC_SMALL_FAST_MODEL", captured.Keys);
    }

    [Fact]
    public void CaptureExtraEnv_SkipsEmptyAndNonString()
    {
        var settings = JsonNode.Parse(
            """{"env":{"KEEP":"v","EMPTY":"","NUM":5,"NULLED":null}}""")!.AsObject();

        var captured = SettingsEnv.CaptureExtraEnv(
            settings, new[] { "KEEP", "EMPTY", "NUM", "NULLED", "MISSING" });

        Assert.Single(captured);
        Assert.Equal("v", captured["KEEP"]);
    }

    [Fact]
    public void CaptureExtraEnv_NoEnvObject_ReturnsEmpty()
    {
        var settings = JsonNode.Parse("""{"permissions":{}}""")!.AsObject();

        var captured = SettingsEnv.CaptureExtraEnv(settings, new[] { "ANTHROPIC_MODEL" });

        Assert.Empty(captured);
    }

    // -----------------------------------------------------------------------
    // ClassifyEnv (env split into managed / account-extra / shared buckets)
    // -----------------------------------------------------------------------

    private static Account AccountWithExtra(params (string k, string v)[] pairs)
        => new()
        {
            Id = "acc-1",
            Name = "Test",
            AccountType = AccountType.Token,
            ExtraEnvNullable = pairs.Length == 0
                ? null
                : pairs.ToDictionary(p => p.k, p => p.v),
        };

    [Fact]
    public void ClassifyEnv_MissingEnv_AllBucketsEmpty()
    {
        var settings = JsonNode.Parse("""{"permissions":{}}""")!.AsObject();

        var buckets = SettingsEnv.ClassifyEnv(settings, active: null);

        Assert.Empty(buckets.Managed);
        Assert.Empty(buckets.AccountExtra);
        Assert.Empty(buckets.Shared);
        Assert.Empty(buckets.SharedReadOnlyKeys);
    }

    [Fact]
    public void ClassifyEnv_EmptyEnvObject_AllBucketsEmpty()
    {
        var settings = JsonNode.Parse("""{"env":{}}""")!.AsObject();

        var buckets = SettingsEnv.ClassifyEnv(settings, active: null);

        Assert.Empty(buckets.Managed);
        Assert.Empty(buckets.AccountExtra);
        Assert.Empty(buckets.Shared);
        Assert.Empty(buckets.SharedReadOnlyKeys);
    }

    [Fact]
    public void ClassifyEnv_OnlySharedKeys_NoActive_AllInShared()
    {
        var settings = JsonNode.Parse(
            """{"env":{"FOO":"1","BAR":"2"}}""")!.AsObject();

        var buckets = SettingsEnv.ClassifyEnv(settings, active: null);

        Assert.Empty(buckets.Managed);
        Assert.Empty(buckets.AccountExtra);
        Assert.Equal(2, buckets.Shared.Count);
        Assert.Contains(new KeyValuePair<string, string>("FOO", "1"), buckets.Shared);
        Assert.Contains(new KeyValuePair<string, string>("BAR", "2"), buckets.Shared);
    }

    [Fact]
    public void ClassifyEnv_PreservesFileOrderForShared()
    {
        var settings = JsonNode.Parse(
            """{"env":{"Z":"1","A":"2","M":"3"}}""")!.AsObject();

        var buckets = SettingsEnv.ClassifyEnv(settings, active: null);

        Assert.Equal(new[] { "Z", "A", "M" }, buckets.Shared.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void ClassifyEnv_ManagedKeysGoToManagedBucket()
    {
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_BASE_URL":"https://x","ANTHROPIC_AUTH_TOKEN":"tok","FOO":"bar"}}""")!.AsObject();

        var buckets = SettingsEnv.ClassifyEnv(settings, active: null);

        Assert.Contains(new KeyValuePair<string, string>("ANTHROPIC_BASE_URL", "https://x"), buckets.Managed);
        Assert.Contains(new KeyValuePair<string, string>("ANTHROPIC_AUTH_TOKEN", "tok"), buckets.Managed);
        Assert.Single(buckets.Shared);
        Assert.Contains(new KeyValuePair<string, string>("FOO", "bar"), buckets.Shared);
    }

    [Fact]
    public void ClassifyEnv_ExtraEnvKeys_GoToAccountExtra_NotShared()
    {
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_MODEL":"opus","FOO":"bar"}}""")!.AsObject();
        var active = AccountWithExtra(("ANTHROPIC_MODEL", "opus"));

        var buckets = SettingsEnv.ClassifyEnv(settings, active);

        Assert.Contains(new KeyValuePair<string, string>("ANTHROPIC_MODEL", "opus"), buckets.AccountExtra);
        Assert.DoesNotContain(buckets.Shared, kv => kv.Key == "ANTHROPIC_MODEL");
        Assert.Contains(new KeyValuePair<string, string>("FOO", "bar"), buckets.Shared);
    }

    [Fact]
    public void ClassifyEnv_ManagedNameCollidesWithExtraEnv_ManagedWins()
    {
        // Active account declares ANTHROPIC_BASE_URL in extra_env, but it is a
        // managed key → it must land in Managed, not AccountExtra.
        var settings = JsonNode.Parse(
            """{"env":{"ANTHROPIC_BASE_URL":"https://x"}}""")!.AsObject();
        var active = AccountWithExtra(("ANTHROPIC_BASE_URL", "https://x"));

        var buckets = SettingsEnv.ClassifyEnv(settings, active);

        Assert.Contains(new KeyValuePair<string, string>("ANTHROPIC_BASE_URL", "https://x"), buckets.Managed);
        Assert.Empty(buckets.AccountExtra);
        Assert.Empty(buckets.Shared);
    }

    [Fact]
    public void ClassifyEnv_NonStringSharedValue_GoesToReadOnlyKeys()
    {
        var settings = JsonNode.Parse(
            """{"env":{"NUM":5,"ARR":[1,2],"OBJ":{"a":1},"NULLED":null,"STR":"ok"}}""")!.AsObject();

        var buckets = SettingsEnv.ClassifyEnv(settings, active: null);

        Assert.Single(buckets.Shared);
        Assert.Contains(new KeyValuePair<string, string>("STR", "ok"), buckets.Shared);
        Assert.Equal(new[] { "NUM", "ARR", "OBJ", "NULLED" }, buckets.SharedReadOnlyKeys.ToArray());
    }

    [Fact]
    public void ClassifyEnv_ExtraEnvKeyAbsentFromEnv_NotEmitted()
    {
        // extra_env declares a key that is not present in the live env → it is
        // simply not classified (ClassifyEnv reads only the env object).
        var settings = JsonNode.Parse(
            """{"env":{"FOO":"bar"}}""")!.AsObject();
        var active = AccountWithExtra(("ANTHROPIC_MODEL", "opus"));

        var buckets = SettingsEnv.ClassifyEnv(settings, active);

        Assert.Empty(buckets.AccountExtra);
        Assert.Single(buckets.Shared);
    }
}
