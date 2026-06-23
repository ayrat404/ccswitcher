// System tray icon + context menu for ccswitcher.
//
// Uses H.NotifyIcon.WinUI to create a TaskbarIcon with a WinUI MenuFlyout
// built in code-behind. The menu is torn down and rebuilt on every state
// change (accounts added/removed, active account switched, proxy toggled).
//
// Menu structure:
//   [account name]  (✓ prefix when active)  — per account in config.Accounts
//   ---
//   Proxy           (shows checked state)
//   ---
//   Settings...
//   Import current login
//   Launch at startup  (shows checked state)
//   ---
//   Exit

using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using CCSwitcher.Core;

namespace CCSwitcher;

/// <summary>
/// Callbacks wired by <see cref="App"/> into the tray menu items.
/// All actions are invoked on the UI thread (WinUI MenuFlyout handles that).
/// </summary>
public sealed class TrayCallbacks
{
    /// <summary>Called when the user clicks an account item. Parameter is the account id.</summary>
    public Action<string>? OnSwitchAccount { get; init; }

    /// <summary>Called when the user clicks the Proxy toggle item. Parameter is the desired new state.</summary>
    public Action<bool>? OnToggleProxy { get; init; }

    /// <summary>Called when the user clicks "Settings…".</summary>
    public Action? OnOpenSettings { get; init; }

    /// <summary>Called when the user clicks "Import current login".</summary>
    public Action? OnImport { get; init; }

    /// <summary>Called when the user clicks the "Launch at startup" toggle. Parameter is the desired new state.</summary>
    public Action<bool>? OnToggleStartup { get; init; }

    /// <summary>Called when the user clicks "Exit".</summary>
    public Action? OnExit { get; init; }
}

/// <summary>
/// Manages the system tray icon and its context menu.
/// Call <see cref="Build"/> once on startup, then <see cref="Rebuild"/> after
/// any state change to reflect the updated config.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private TaskbarIcon? _icon;

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Create the tray icon and populate its context menu from
    /// <paramref name="config"/>.
    /// </summary>
    public void Build(AppConfig config, TrayCallbacks callbacks)
    {
        _icon?.Dispose();
        _icon = CreateIcon(config, callbacks);
    }

    /// <summary>
    /// Tear down the current context menu and rebuild it to reflect the
    /// updated <paramref name="config"/>.
    /// </summary>
    public void Rebuild(AppConfig config, TrayCallbacks callbacks)
    {
        if (_icon == null)
        {
            Build(config, callbacks);
            return;
        }

        // Replace only the context menu; keep the same icon instance so the
        // tray icon itself does not flicker.
        _icon.ContextFlyout = BuildMenu(config, callbacks);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static TaskbarIcon CreateIcon(AppConfig config, TrayCallbacks callbacks)
    {
        var icon = new TaskbarIcon
        {
            ToolTipText = "CCSwitcher",
            ContextFlyout = BuildMenu(config, callbacks),
        };

        // Try to load the app icon from the application package or resources.
        // Fall back gracefully if the icon file is not found — the tray will
        // show a default icon rather than crashing.
        try
        {
            // LoadIconFromFile requires the icon to be embedded in the package or
            // accessible as a file. In the self-contained unpackaged scenario, we
            // look for a file next to the executable.
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            var iconPath = Path.Combine(exeDir, "Assets", "appicon.ico");
            if (File.Exists(iconPath))
            {
                icon.Icon = new System.Drawing.Icon(iconPath);
            }
        }
        catch
        {
            // Best-effort: icon is cosmetic; failure must not crash startup.
        }

        return icon;
    }

    private static MenuFlyout BuildMenu(AppConfig config, TrayCallbacks callbacks)
    {
        var flyout = new MenuFlyout();

        // --- Accounts -------------------------------------------------------
        foreach (var account in config.Accounts)
        {
            var isActive = account.Id == config.ActiveAccountId;
            var displayName = isActive ? $"✓ {account.Name}" : account.Name;

            var accountId = account.Id; // capture for closure
            var item = new MenuFlyoutItem { Text = displayName };
            item.Click += (_, _) => callbacks.OnSwitchAccount?.Invoke(accountId);
            flyout.Items.Add(item);
        }

        if (config.Accounts.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        // --- Proxy toggle ---------------------------------------------------
        var proxyItem = new ToggleMenuFlyoutItem
        {
            Text = "Proxy",
            IsChecked = config.Proxy.Enabled,
        };
        proxyItem.Click += (_, _) =>
            callbacks.OnToggleProxy?.Invoke(!config.Proxy.Enabled);
        flyout.Items.Add(proxyItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // --- Settings -------------------------------------------------------
        var settingsItem = new MenuFlyoutItem { Text = "Settings…" };
        settingsItem.Click += (_, _) => callbacks.OnOpenSettings?.Invoke();
        flyout.Items.Add(settingsItem);

        // --- Import ---------------------------------------------------------
        var importItem = new MenuFlyoutItem { Text = "Import current login" };
        importItem.Click += (_, _) => callbacks.OnImport?.Invoke();
        flyout.Items.Add(importItem);

        // --- Launch at startup ----------------------------------------------
        var startupEnabled = StartupManager.IsEnabled();
        var startupItem = new ToggleMenuFlyoutItem
        {
            Text = "Launch at startup",
            IsChecked = startupEnabled,
        };
        startupItem.Click += (_, _) =>
            callbacks.OnToggleStartup?.Invoke(!startupEnabled);
        flyout.Items.Add(startupItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // --- Exit -----------------------------------------------------------
        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => callbacks.OnExit?.Invoke();
        flyout.Items.Add(exitItem);

        return flyout;
    }
}
