// Parse and merge the env block inside Claude Code's settings.json.
//
// Port of src-tauri/src/core/settings_env.rs.
//
// ccswitcher edits ONLY the "env" object inside settings.json, and ONLY a
// known set of managed keys within it.  All other env keys and every non-env
// setting (permissions, mcp, …) are left untouched.
//
// Merge semantics
// ---------------
// On every switch the engine strips the UNION of the constant MANAGED_KEYS
// set and the caller-supplied oldManagedKeys (the latter covers prior
// extra_env keys and survives a stale / empty stored list), then inserts the
// freshly-built newEnv.  The returned newManagedKeys is the exact set of keys
// this call wrote, to be persisted for the next switch.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCSwitcher.Core;

/// <summary>
/// Parse and merge the env block inside Claude Code's <c>settings.json</c>.
/// </summary>
public static class SettingsEnv
{
    /// <summary>
    /// The constant set of env keys ccswitcher always owns inside
    /// <c>settings.json</c>.  On top of these, an account's
    /// <c>extra_env</c> keys are also managed; those are passed via
    /// <c>oldManagedKeys</c> / <c>newEnv</c> rather than living here.
    /// </summary>
    public static readonly IReadOnlySet<string> ManagedKeys = new HashSet<string>
    {
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "NO_PROXY",
    };

    /// <summary>
    /// Load Claude Code's <c>settings.json</c> as a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="path">Absolute path to <c>settings.json</c>.</param>
    /// <returns>
    /// An empty <see cref="JsonObject"/> when the file is missing (a first run
    /// starts clean).
    /// </returns>
    /// <exception cref="SettingsEnvException">
    /// The file exists but contains invalid JSON, or its top-level value is
    /// not a JSON object.  The file is never modified.
    /// </exception>
    public static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        string text;
        try
        {
            text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsEnvException($"settings.json I/O error: {ex.Message}", ex);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new SettingsEnvException($"settings.json is not valid JSON: {ex.Message}", ex);
        }

        if (node is not JsonObject obj)
            throw new SettingsEnvException("settings.json top level is not a JSON object");

        return obj;
    }

    /// <summary>
    /// Merge app-managed env keys into <paramref name="settings"/>.
    /// </summary>
    /// <param name="settings">
    /// The full settings object loaded by <see cref="Load"/>.
    /// </param>
    /// <param name="oldManagedKeys">
    /// The managed-key list that was persisted after the previous switch.
    /// Allows stale extra_env keys to be cleaned up even when not present in
    /// the constant <see cref="ManagedKeys"/> set.
    /// </param>
    /// <param name="newEnv">
    /// The env entries for the target account, produced by
    /// <c>EnvBuilder.Build</c>.
    /// </param>
    /// <returns>
    /// A tuple of the updated <paramref name="settings"/> and the list of
    /// keys that were written (exactly the keys of <paramref name="newEnv"/>),
    /// for the caller to persist as the new managed-key list.
    /// </returns>
    public static (JsonObject settings, List<string> newManagedKeys) MergeEnv(
        JsonObject settings,
        IEnumerable<string> oldManagedKeys,
        IReadOnlyDictionary<string, string> newEnv)
    {
        // Get or create the "env" child object.
        JsonObject envObj;
        if (settings.TryGetPropertyValue("env", out JsonNode? envNode) &&
            envNode is JsonObject existingEnvObj)
        {
            envObj = existingEnvObj;
        }
        else
        {
            // Absent or non-object env value → reset to a fresh empty object.
            envObj = new JsonObject();
            settings["env"] = envObj;
        }

        // Strip the union of the constant managed set and the previously-
        // written managed keys so stale keys are always cleaned up.
        foreach (var key in ManagedKeys)
            envObj.Remove(key);

        foreach (var key in oldManagedKeys)
            envObj.Remove(key);

        // Insert the freshly-built env entries.
        foreach (var (key, value) in newEnv)
            envObj[key] = JsonValue.Create(value);

        var newManagedKeys = new List<string>(newEnv.Keys);
        return (settings, newManagedKeys);
    }

    /// <summary>
    /// Capture the current values of the tracked top-level <paramref name="keys"/>
    /// from <paramref name="settings"/> into <paramref name="into"/> (a per-account
    /// snapshot). Each tracked key is always recorded so the snapshot reflects the
    /// account's current state:
    /// <list type="bullet">
    ///   <item>present &amp; non-null in <paramref name="settings"/> → stored as a
    ///   deep clone of the value;</item>
    ///   <item>absent (or JSON null) in <paramref name="settings"/> → stored as
    ///   JSON null, which represents the "default" state (Claude Code omits the
    ///   key entirely when, e.g., the model is "default").</item>
    /// </list>
    /// Entries in <paramref name="into"/> for keys outside
    /// <paramref name="keys"/> are left untouched (merge across the key set).
    /// </summary>
    public static void CaptureSettings(
        JsonObject into,
        JsonObject settings,
        IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            into[key] = settings.TryGetPropertyValue(key, out var node) && node is not null
                ? node.DeepClone()
                : null; // absent/null in settings == "default"
        }
    }

    /// <summary>
    /// Restore the tracked top-level <paramref name="keys"/> into
    /// <paramref name="settings"/> from a per-account snapshot
    /// <paramref name="saved"/>. Tri-state per key:
    /// <list type="bullet">
    ///   <item><paramref name="saved"/> is null, or the key is <b>absent</b> from
    ///   it (never captured) → the key in <paramref name="settings"/> is left
    ///   exactly as-is (first switch keeps the current value);</item>
    ///   <item>the key is present with a <b>null</b> value (captured "default") →
    ///   the key is <b>removed</b> from <paramref name="settings"/>;</item>
    ///   <item>the key is present with a value → it is written (deep-cloned).</item>
    /// </list>
    /// </summary>
    public static void RestoreSettings(
        JsonObject settings,
        JsonObject? saved,
        IEnumerable<string> keys)
    {
        if (saved is null)
            return;

        foreach (var key in keys)
        {
            if (!saved.ContainsKey(key))
                continue; // never captured → leave as-is

            var node = saved[key];
            if (node is null)
                settings.Remove(key); // captured "default" → drop the key
            else
                settings[key] = node.DeepClone();
        }
    }
}

/// <summary>
/// Exception raised while loading or merging <c>settings.json</c>.
/// </summary>
public sealed class SettingsEnvException : Exception
{
    public SettingsEnvException(string message) : base(message) { }
    public SettingsEnvException(string message, Exception inner) : base(message, inner) { }
}
