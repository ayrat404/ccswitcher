// Registry-based launch-at-startup toggle.
//
// Reads and writes HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
// The value name is "CCSwitcher" and the value is the path to the current exe.
// Mirrors the Tauri version's behaviour (src-tauri/src/main.rs startup toggle).

namespace CCSwitcher;

/// <summary>
/// Manages the "launch at Windows startup" registry key for this application.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CCSwitcher";

    /// <summary>
    /// Returns <see langword="true"/> when the "CCSwitcher" value exists under
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Add or remove the startup registry value.
    /// <para>
    /// When <paramref name="enabled"/> is <see langword="true"/>, writes the
    /// full path to the running exe as the value.  When <see langword="false"/>,
    /// deletes the value (no-op if it does not exist).
    /// </para>
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;

            if (enabled)
                key.SetValue(AppName, Environment.ProcessPath ?? "");
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort: registry failure must not crash the app.
        }
    }
}
