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
    /// Attach to the XAML-declared <paramref name="icon"/> hosted by the hidden
    /// MainWindow: load its image, populate its context menu, and make sure it
    /// is registered with the shell.
    /// </summary>
    public void Attach(TaskbarIcon icon, AppConfig config, TrayCallbacks callbacks)
    {
        _icon = icon;

        // Load the app icon from the file copied next to the executable.
        // Fall back gracefully if it is missing — the tray still registers
        // (a blank icon) rather than crashing.
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (File.Exists(iconPath))
                icon.Icon = new System.Drawing.Icon(iconPath);
        }
        catch
        {
            // Best-effort: icon is cosmetic; failure must not crash startup.
        }

        icon.ContextFlyout = BuildMenu(config, callbacks);

        // Native-looking tray menu: PopupMenu emulates the menu with a native
        // Win32 popup (matches the OS look) and routes selections to each item's
        // Command. Our menu items wire Command (not Click) precisely for this
        // mode. It also works without a visible host window.
        icon.ContextMenuMode = ContextMenuMode.PopupMenu;

        // The XAML TaskbarIcon normally self-registers on Loaded, but the host
        // window is hidden right after activation; force creation here so the
        // icon is guaranteed to appear. enablesEfficiencyMode:false keeps the
        // background process responsive to tray clicks.
        if (!icon.IsCreated)
            icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// Rebuild the context menu to reflect the updated <paramref name="config"/>.
    /// Keeps the same icon instance so the tray icon does not flicker.
    /// </summary>
    public void Rebuild(AppConfig config, TrayCallbacks callbacks)
    {
        if (_icon == null)
            return;

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

    // NOTE: items use Command, not Click. In PopupMenu mode (native Win32 menu)
    // H.NotifyIcon routes a selection to the item's Command — the Click event
    // never fires. Wiring Command keeps the menu working in the native mode.
    private static MenuFlyout BuildMenu(AppConfig config, TrayCallbacks callbacks)
    {
        var flyout = new MenuFlyout();

        // --- Accounts -------------------------------------------------------
        foreach (var account in config.Accounts)
        {
            var isActive = account.Id == config.ActiveAccountId;
            var displayName = isActive ? $"✓ {account.Name}" : account.Name;

            var accountId = account.Id; // capture for closure
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = displayName,
                Command = new DelegateCommand(() => callbacks.OnSwitchAccount?.Invoke(accountId)),
            });
        }

        if (config.Accounts.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        // --- Proxy toggle ---------------------------------------------------
        flyout.Items.Add(new ToggleMenuFlyoutItem
        {
            Text = "Proxy",
            IsChecked = config.Proxy.Enabled,
            Command = new DelegateCommand(() => callbacks.OnToggleProxy?.Invoke(!config.Proxy.Enabled)),
        });

        flyout.Items.Add(new MenuFlyoutSeparator());

        // --- Settings -------------------------------------------------------
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Settings…",
            Command = new DelegateCommand(() => callbacks.OnOpenSettings?.Invoke()),
        });

        // --- Import ---------------------------------------------------------
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Import current login",
            Command = new DelegateCommand(() => callbacks.OnImport?.Invoke()),
        });

        // --- Launch at startup ----------------------------------------------
        var startupEnabled = StartupManager.IsEnabled();
        flyout.Items.Add(new ToggleMenuFlyoutItem
        {
            Text = "Launch at startup",
            IsChecked = startupEnabled,
            Command = new DelegateCommand(() => callbacks.OnToggleStartup?.Invoke(!startupEnabled)),
        });

        flyout.Items.Add(new MenuFlyoutSeparator());

        // --- Exit -----------------------------------------------------------
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Exit",
            Command = new DelegateCommand(() => callbacks.OnExit?.Invoke()),
        });

        return flyout;
    }
}

/// <summary>
/// Minimal <see cref="System.Windows.Input.ICommand"/> that always executes a
/// fixed action — used to wire tray menu items so they work in the native
/// PopupMenu context-menu mode (which invokes Command, not Click).
/// </summary>
internal sealed class DelegateCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public DelegateCommand(Action execute) => _execute = execute;

    // CanExecute is always true; the event is required by ICommand but never
    // raised (empty accessors avoid an unused-field warning).
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
