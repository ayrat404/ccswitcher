// Read and merge the oauthAccount section of Claude Code's user-level config
// (~/.claude.json or ~/.claude/.claude.json).
//
// Port of src-tauri/src/core/user_config.rs.
//
// Only the oauthAccount key is swapped between accounts — everything else in
// the config (userID, projects, tips, settings, …) is the user's own data and
// must never be lost.
//
// The snapshot is stored in the keyring by the switcher/import code; this
// class is the pure file-level read/merge.

using System.Text.Json.Nodes;

namespace CCSwitcher.Core;

/// <summary>
/// Errors raised while reading or merging the user config.
/// </summary>
public sealed class UserConfigException : Exception
{
    public UserConfigException(string message) : base(message) { }
    public UserConfigException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Static helpers for reading and merging the <c>oauthAccount</c> section of
/// Claude Code's user-level config file (<c>~/.claude.json</c> or
/// <c>~/.claude/.claude.json</c>).
/// </summary>
public static class UserConfig
{
    /// <summary>
    /// Name of the <c>oauthAccount</c> key inside the user config.
    /// </summary>
    public const string OauthAccountField = "oauthAccount";

    /// <summary>
    /// Returns the keyring key under which an account's <c>oauthAccount</c>
    /// snapshot is stored: <c>"{accountId}#oauthAccount"</c>.
    /// <para>
    /// Separate from the credential-blob key (the bare account id) so the two
    /// never collide.
    /// </para>
    /// </summary>
    public static string OauthAccountKey(string accountId) => $"{accountId}#oauthAccount";

    /// <summary>
    /// Read the <c>oauthAccount</c> object from the config at <paramref name="path"/>.
    /// <para>
    /// Returns <see langword="null"/> if the file is missing or has no
    /// <c>oauthAccount</c> key.
    /// </para>
    /// </summary>
    /// <param name="path">Path to the user config file (<c>.claude.json</c>).</param>
    /// <returns>
    /// The <c>oauthAccount</c> <see cref="JsonNode"/>, or <see langword="null"/>
    /// when the file or key is absent.
    /// </returns>
    /// <exception cref="UserConfigException">
    /// Thrown when the file exists but contains invalid JSON.
    /// </exception>
    public static JsonNode? ReadOauthAccount(string path)
    {
        if (!File.Exists(path))
            return null;

        string content;
        try
        {
            content = File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (IOException ex)
        {
            throw new UserConfigException($"I/O error reading user config: {ex.Message}", ex);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (Exception ex)
        {
            throw new UserConfigException($"User config is not valid JSON: {ex.Message}", ex);
        }

        if (root is not JsonObject obj)
            return null;

        return obj[OauthAccountField];
    }

    /// <summary>
    /// Merge an <c>oauthAccount</c> object into the config at
    /// <paramref name="path"/>, replacing ONLY that one key and preserving
    /// every other field.
    /// <para>
    /// The file is backed up (best-effort) and written atomically.  If the file
    /// does not exist it is created with just the <c>oauthAccount</c> key.
    /// </para>
    /// </summary>
    /// <param name="path">Path to the user config file (<c>.claude.json</c>).</param>
    /// <param name="oauth">
    /// Must be a <see cref="JsonObject"/>; any other kind of <see cref="JsonNode"/>
    /// causes <see cref="UserConfigException"/> to be thrown without writing.
    /// </param>
    /// <exception cref="UserConfigException">
    /// Thrown when <paramref name="oauth"/> is not a JSON object, or when the
    /// existing file contains invalid JSON.
    /// </exception>
    public static void MergeOauthAccount(string path, JsonNode oauth)
    {
        // Validate oauth is a JSON object before touching any file.
        if (oauth is not JsonObject)
            throw new UserConfigException("oauthAccount must be a JSON object");

        // Load existing config, or start with an empty object if the file is absent.
        JsonObject config;
        if (File.Exists(path))
        {
            string content;
            try
            {
                content = File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            catch (IOException ex)
            {
                throw new UserConfigException($"I/O error reading user config: {ex.Message}", ex);
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(content);
            }
            catch (Exception ex)
            {
                throw new UserConfigException($"User config is not valid JSON: {ex.Message}", ex);
            }

            // If the top level is not an object (e.g. an array), reset to empty.
            config = root as JsonObject ?? new JsonObject();
        }
        else
        {
            config = new JsonObject();
        }

        // Replace ONLY the oauthAccount key; every other field is preserved.
        config[OauthAccountField] = oauth.DeepClone();

        var json = config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        // Best-effort backup before the atomic write (no-op if the file is new).
        var backupsDir = BackupsDir(path);
        AtomicFile.Backup(path, backupsDir);
        AtomicFile.Write(path, json);
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The <c>backups/</c> directory located next to the config file itself.
    /// </summary>
    private static string BackupsDir(string configPath)
    {
        var parent = Path.GetDirectoryName(configPath);
        return Path.Combine(string.IsNullOrEmpty(parent) ? "." : parent, "backups");
    }
}
