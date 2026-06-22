// Path resolution for Claude Code's config files.
//
// Port of src-tauri/src/core/claude_paths.rs.
//
// All paths are derived from environment-variable-backed folders so they
// work correctly for every user profile without any hard-coded strings.

namespace CCSwitcher.Core;

/// <summary>
/// Static helpers that resolve well-known paths used by Claude Code and by
/// ccswitcher itself.  No I/O is performed here except in
/// <see cref="FindUserConfig"/> which checks whether candidate files exist.
/// </summary>
public static class ClaudePaths
{
    /// <summary>
    /// Path to Claude Code's settings file: <c>%USERPROFILE%\.claude\settings.json</c>.
    /// </summary>
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

    /// <summary>
    /// Path to Claude Code's OAuth credential store:
    /// <c>%USERPROFILE%\.claude\.credentials.json</c>.
    /// </summary>
    public static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    /// <summary>
    /// Returns the first of the two candidate user-config paths that exists on
    /// disk, or <c>null</c> if neither exists.
    /// <para>
    /// Candidate order (priority high to low):
    /// <list type="number">
    ///   <item><c>%USERPROFILE%\.claude\.claude.json</c></item>
    ///   <item><c>%USERPROFILE%\.claude.json</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public static string? FindUserConfig()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidate1 = Path.Combine(profile, ".claude", ".claude.json");
        if (File.Exists(candidate1))
            return candidate1;

        var candidate2 = Path.Combine(profile, ".claude.json");
        if (File.Exists(candidate2))
            return candidate2;

        return null;
    }

    /// <summary>
    /// Directory where ccswitcher stores its own <c>config.json</c>:
    /// <c>%APPDATA%\ccswitcher\</c>.
    /// <para>
    /// This path must match the Tauri app's data directory exactly so that
    /// both builds read and write the same <c>config.json</c>.
    /// </para>
    /// </summary>
    public static string AppConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ccswitcher");
}
