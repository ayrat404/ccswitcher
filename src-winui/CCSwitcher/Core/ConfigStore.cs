// Load and save ccswitcher's own config.json.
//
// Port of src-tauri/src/core/config_store.rs.
//
// ConfigStore.Load:  deserialize config.json from the given directory.
//                    Returns AppConfig.Default if the file is absent.
//                    Throws JsonException if the file exists but is invalid JSON.
// ConfigStore.Save:  backup existing file, write to .tmp, rename atomically.
//                    Creates the directory if it does not exist.

using System.Text.Json;

namespace CCSwitcher.Core;

/// <summary>
/// Static helpers for loading and saving ccswitcher's <c>config.json</c>.
/// </summary>
public static class ConfigStore
{
    private const string FileName = "config.json";

    /// <summary>
    /// Load <c>config.json</c> from <paramref name="dir"/>.
    /// <para>
    /// Returns <see cref="AppConfig.Default"/> when the file does not exist so
    /// first-run behaviour is sane without requiring the caller to handle the
    /// missing-file case separately.
    /// </para>
    /// </summary>
    /// <param name="dir">Directory that contains (or will contain) config.json.</param>
    /// <returns>Deserialized <see cref="AppConfig"/>, or the default value.</returns>
    /// <exception cref="JsonException">
    /// Thrown when the file exists but contains invalid JSON or does not match
    /// the expected schema.
    /// </exception>
    public static AppConfig Load(string dir)
    {
        var path = Path.Combine(dir, FileName);

        if (!File.Exists(path))
            return AppConfig.Default;

        var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        // JsonSerializer.Deserialize throws JsonException on invalid JSON.
        var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.JsonOptions);
        return config ?? AppConfig.Default;
    }

    /// <summary>
    /// Save <paramref name="config"/> to <c>config.json</c> inside
    /// <paramref name="dir"/>, using atomic write (temp + rename) with a
    /// timestamped backup of the existing file.
    /// <para>
    /// Creates <paramref name="dir"/> (and the <c>backups/</c> sub-directory)
    /// if they do not already exist.
    /// </para>
    /// </summary>
    /// <param name="dir">Directory in which to write config.json.</param>
    /// <param name="config">Config to persist.</param>
    /// <exception cref="IOException">Thrown if the write or rename fails.</exception>
    public static void Save(string dir, AppConfig config)
    {
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, FileName);
        var backupsDir = Path.Combine(dir, "backups");

        // Backup before writing so we never lose the previous good config.
        AtomicFile.Backup(path, backupsDir);

        var json = JsonSerializer.Serialize(config, AppConfig.JsonOptions);
        AtomicFile.Write(path, json);
    }
}
