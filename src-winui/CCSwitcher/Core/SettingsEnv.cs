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
    /// The constant managed env keys in canonical, deterministic order. This is
    /// the single source of truth for both <see cref="ManagedKeys"/> (membership)
    /// and the fixed order the Managed bucket is emitted in by
    /// <see cref="ClassifyEnv"/>, so the order never depends on
    /// <see cref="HashSet{T}"/> enumeration.
    /// </summary>
    private static readonly string[] ManagedKeyOrder =
    {
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "NO_PROXY",
    };

    /// <summary>
    /// The constant set of env keys ccswitcher always owns inside
    /// <c>settings.json</c>.  On top of these, an account's
    /// <c>extra_env</c> keys are also managed; those are passed via
    /// <c>oldManagedKeys</c> / <c>newEnv</c> rather than living here.
    /// </summary>
    public static readonly IReadOnlySet<string> ManagedKeys =
        new HashSet<string>(ManagedKeyOrder, StringComparer.Ordinal);

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
    /// Classify the live <c>env</c> block of <paramref name="settings"/> into three
    /// disjoint buckets so a UI can render the whole block while only allowing the
    /// safe-to-edit keys to be changed:
    /// <list type="bullet">
    ///   <item><b>Managed</b> — keys in the constant <see cref="ManagedKeys"/> set
    ///   (base url, tokens, proxy). Read-only; edited via account/proxy dialogs.
    ///   Emitted in the fixed <see cref="ManagedKeys"/> order.</item>
    ///   <item><b>AccountExtra</b> — keys of the active account's
    ///   <c>extra_env</c> that are present in <c>env</c> and not managed
    ///   (managed wins on a name collision). Editable.</item>
    ///   <item><b>Shared</b> — every other <c>env</c> key with a string value.
    ///   Editable.</item>
    /// </list>
    /// Non-managed, non-extra keys whose value is <b>not</b> a string (number,
    /// array, object, null) cannot be edited in a text field, so their names are
    /// returned separately in <see cref="EnvBuckets.SharedReadOnlyKeys"/> and are
    /// excluded from <see cref="EnvBuckets.Shared"/>. Managed and extra_env values
    /// are always strings; a non-string value is coerced to its JSON text.
    /// AccountExtra and Shared preserve the order the keys appear in the file.
    /// </summary>
    /// <param name="settings">The full settings object loaded by <see cref="Load"/>.</param>
    /// <param name="active">The currently active account, or <c>null</c> when none
    /// is active (then AccountExtra is always empty).</param>
    public static EnvBuckets ClassifyEnv(JsonObject settings, Account? active)
    {
        JsonObject? envObj = TryGetEnvObject(settings);

        var managed = new List<KeyValuePair<string, string>>();
        var accountExtra = new List<KeyValuePair<string, string>>();
        var shared = new List<KeyValuePair<string, string>>();
        var sharedReadOnly = new List<string>();

        if (envObj is null)
            return new EnvBuckets(managed, accountExtra, shared, sharedReadOnly);

        // Managed first, in the fixed canonical order (not HashSet enumeration).
        foreach (var key in ManagedKeyOrder)
        {
            if (envObj.TryGetPropertyValue(key, out var node))
                managed.Add(new KeyValuePair<string, string>(key, NodeToString(node)));
        }

        var extraKeys = active is not null
            ? new HashSet<string>(active.ExtraEnv.Keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        // Everything else, in file order.
        foreach (var kvp in envObj)
        {
            var key = kvp.Key;
            if (ManagedKeys.Contains(key))
                continue; // managed wins on a name collision

            if (extraKeys.Contains(key))
            {
                accountExtra.Add(new KeyValuePair<string, string>(key, NodeToString(kvp.Value)));
                continue;
            }

            if (kvp.Value is JsonValue val && val.TryGetValue<string>(out var s))
                shared.Add(new KeyValuePair<string, string>(key, s));
            else
                sharedReadOnly.Add(key); // non-string: not editable in a text field
        }

        return new EnvBuckets(managed, accountExtra, shared, sharedReadOnly);
    }

    /// <summary>
    /// Coerce a JSON node to a string: a JSON string yields its raw value; any
    /// other node (number/bool/array/object) yields its JSON text; <c>null</c>
    /// yields the empty string.
    /// </summary>
    private static string NodeToString(JsonNode? node)
    {
        if (node is JsonValue val && val.TryGetValue<string>(out var s))
            return s;
        return node?.ToJsonString() ?? string.Empty;
    }

    /// <summary>
    /// Return the <c>env</c> child object of <paramref name="settings"/> when it is
    /// present and a JSON object, otherwise <c>null</c>. Read-only: nothing is
    /// created or written back. Shared by the read-only classify/capture paths.
    /// </summary>
    private static JsonObject? TryGetEnvObject(JsonObject settings)
        => settings.TryGetPropertyValue("env", out var envNode) && envNode is JsonObject eo
            ? eo
            : null;

    /// <summary>
    /// Return the existing <c>env</c> child object of <paramref name="settings"/>,
    /// or, when it is absent or not a JSON object, assign a fresh empty
    /// <see cref="JsonObject"/> into <c>settings["env"]</c> and return that. Shared
    /// by the mutating merge/apply paths.
    /// </summary>
    private static JsonObject GetOrCreateEnvObject(JsonObject settings)
    {
        if (settings.TryGetPropertyValue("env", out JsonNode? envNode) &&
            envNode is JsonObject existing)
        {
            return existing;
        }

        var fresh = new JsonObject();
        settings["env"] = fresh;
        return fresh;
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
        // Get or create the "env" child object (absent/non-object → fresh empty).
        JsonObject envObj = GetOrCreateEnvObject(settings);

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
    /// Apply user edits to the <b>shared</b> (non-managed, non-extra_env) portion
    /// of the <c>env</c> block — the targeted, touched-only counterpart of
    /// <see cref="MergeEnv"/>. Unlike <see cref="MergeEnv"/>, this does <b>not</b>
    /// strip the managed union: it only removes the shared keys the user deleted
    /// and writes back the ones they kept/added, leaving managed keys, the active
    /// account's <c>extra_env</c> keys, and any read-only non-string shared keys
    /// (those not in <paramref name="oldSharedKeys"/>) completely untouched.
    /// </summary>
    /// <param name="settings">
    /// The full settings object loaded by <see cref="Load"/>.
    /// </param>
    /// <param name="oldSharedKeys">
    /// The string-valued shared keys that were present when the editor was opened.
    /// Any of these missing from <paramref name="newShared"/> is treated as a user
    /// deletion and removed. Keys outside this set are never removed, so read-only
    /// non-string shared values survive.
    /// </param>
    /// <param name="newShared">
    /// The shared key/value pairs from the editor to write back.
    /// </param>
    /// <returns>The updated <paramref name="settings"/>.</returns>
    public static JsonObject ApplySharedEnv(
        JsonObject settings,
        IEnumerable<string> oldSharedKeys,
        IReadOnlyDictionary<string, string> newShared)
    {
        // Get or create the "env" child object. An absent or non-object value is
        // reset to a fresh empty object (there are no shared keys to preserve in
        // that case, and MergeEnv treats a non-object env the same way).
        JsonObject envObj = GetOrCreateEnvObject(settings);

        // Remove only the previously-present shared keys the user dropped.
        foreach (var key in oldSharedKeys)
        {
            if (!newShared.ContainsKey(key))
                envObj.Remove(key);
        }

        // Write back the kept/added shared entries.
        foreach (var (key, value) in newShared)
            envObj[key] = JsonValue.Create(value);

        return settings;
    }

    /// <summary>
    /// Load <c>settings.json</c>, apply a shared-env edit via
    /// <see cref="ApplySharedEnv"/>, then back up and atomically write it back —
    /// the encapsulated, on-disk counterpart of <see cref="ApplySharedEnv"/>.
    /// Mirrors the backup + <c>WriteIndented</c> + atomic-rename sequence used by
    /// <c>Proxy.SetEnabled</c> / <c>Switcher</c> so the code-behind never
    /// hand-rolls the write dance.
    /// <para>
    /// The caller computes <paramref name="removalKeys"/> (the shared keys the
    /// user dropped, already filtered of managed / still-active extra_env keys);
    /// this method neither strips the managed union nor touches any key outside
    /// <paramref name="removalKeys"/> ∪ <paramref name="newShared"/>, preserving
    /// Invariant 1.
    /// </para>
    /// </summary>
    /// <param name="settingsPath">Absolute path to <c>settings.json</c>.</param>
    /// <param name="removalKeys">Shared keys to remove (the final removal set).</param>
    /// <param name="newShared">The shared key/value pairs to write back.</param>
    /// <exception cref="SettingsEnvException">Loading, backing up, or writing failed.</exception>
    public static void SaveShared(
        string settingsPath,
        IEnumerable<string> removalKeys,
        IReadOnlyDictionary<string, string> newShared)
    {
        var settings = Load(settingsPath);
        ApplySharedEnv(settings, removalKeys, newShared);

        var backupsDir = Path.Combine(
            Path.GetDirectoryName(settingsPath) ?? ".", "backups");

        try
        {
            AtomicFile.Backup(settingsPath, backupsDir);

            var json = settings.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.Write(settingsPath, json);
        }
        catch (IOException ex)
        {
            throw new SettingsEnvException($"settings.json write failed: {ex.Message}", ex);
        }
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
    /// Re-read the live values of <paramref name="keys"/> from the <c>env</c>
    /// object of <paramref name="settings"/> and return them as a fresh map — the
    /// <see cref="ExtraEnv"/> analogue of <see cref="CaptureSettings"/>. This lets
    /// manual edits the user made to an account's env keys (e.g.
    /// <c>ANTHROPIC_*_MODEL</c> values) be saved back into the account on
    /// switch-out, instead of being silently overwritten on the next switch.
    /// <para>
    /// Per key: present &amp; non-empty string → included with the live value;
    /// absent, empty, or non-string → dropped (so a manual deletion of the key is
    /// respected rather than re-instated from a stale stored value). Only the
    /// <c>env</c> object is read; nothing else in <paramref name="settings"/> is
    /// touched.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> CaptureExtraEnv(
        JsonObject settings,
        IEnumerable<string> keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        JsonObject? envObj = TryGetEnvObject(settings);

        foreach (var key in keys)
        {
            if (envObj is null
                || !envObj.TryGetPropertyValue(key, out var node))
                continue;
            if (node is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                result[key] = s;
        }

        return result;
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
/// The live <c>settings.json</c> <c>env</c> block split into disjoint buckets by
/// <see cref="SettingsEnv.ClassifyEnv"/>. See that method for the classification
/// rules and ordering guarantees.
/// </summary>
/// <param name="Managed">App-owned keys (base url / tokens / proxy). Read-only.
/// In fixed <see cref="SettingsEnv.ManagedKeys"/> order.</param>
/// <param name="AccountExtra">Active account's <c>extra_env</c> keys present in
/// <c>env</c>. Editable. File order.</param>
/// <param name="Shared">All other string-valued <c>env</c> keys. Editable.
/// File order.</param>
/// <param name="SharedReadOnlyKeys">Non-managed, non-extra keys whose value is not
/// a string (number/array/object/null); shown read-only and never edited.</param>
public sealed record EnvBuckets(
    IReadOnlyList<KeyValuePair<string, string>> Managed,
    IReadOnlyList<KeyValuePair<string, string>> AccountExtra,
    IReadOnlyList<KeyValuePair<string, string>> Shared,
    IReadOnlyList<string> SharedReadOnlyKeys);

/// <summary>
/// Exception raised while loading or merging <c>settings.json</c>.
/// </summary>
public sealed class SettingsEnvException : Exception
{
    public SettingsEnvException(string message) : base(message) { }
    public SettingsEnvException(string message, Exception inner) : base(message, inner) { }
}
