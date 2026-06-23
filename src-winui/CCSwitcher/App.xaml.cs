using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Threading;
using CCSwitcher.Core;

namespace CCSwitcher;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Single-instance enforcement via a named Mutex + named-pipe IPC.</item>
///   <item>Config loading and <see cref="Switcher.ClearActiveIfMissing"/> on startup.</item>
///   <item>Building and maintaining the system tray icon.</item>
///   <item>Serializing all mutating operations through <see cref="StateMutex"/>.</item>
/// </list>
/// </summary>
public partial class App : Application
{
    private const string MutexName = "CCSwitcher_SingleInstance";
    private const string PipeName = "CCSwitcher_Focus";

    private Mutex? _singleInstanceMutex;

    // -----------------------------------------------------------------------
    // Shared mutable state — all access must be serialized through StateMutex.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Serializes all mutating operations (switch, add, update, delete, proxy
    /// toggle, import) — same role as Arc&lt;Mutex&lt;AppConfig&gt;&gt; in the
    /// Tauri version.
    /// </summary>
    public static readonly SemaphoreSlim StateMutex = new SemaphoreSlim(1, 1);

    private AppConfig _config = AppConfig.Default;
    private readonly TrayIcon _trayIcon = new();
    private TrayCallbacks? _callbacks;

    // -----------------------------------------------------------------------
    // I/O adapters (real implementations; tests inject mocks via the core).
    // -----------------------------------------------------------------------
#if WINDOWS10_0_19041_0_OR_GREATER
    private readonly ISecretStore _secretStore = new PasswordVaultSecretStore();
#else
    private readonly ISecretStore _secretStore = new InMemorySecretStore();
#endif
    private readonly ICredentialStore _credentialStore =
        new FileCredentialStore(ClaudePaths.CredentialsPath);

    // -----------------------------------------------------------------------
    // Settings window (lazily created; null when closed/never opened).
    // -----------------------------------------------------------------------

    private SettingsWindow? _settingsWindow;

    // -----------------------------------------------------------------------
    // Application lifecycle
    // -----------------------------------------------------------------------

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Attempt to acquire the single-instance mutex.
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: MutexName,
            out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — signal it to show/focus the
            // settings window, then exit this second instance silently.
            SignalExistingInstance();
            this.Exit();
            return;
        }

        // We are the first (and only) instance.

        // Load config and heal any dangling active_account_id.
        InitializeConfig();

        // Build the tray icon with the current config state.
        _callbacks = BuildCallbacks();
        _trayIcon.Build(_config, _callbacks);

        // Create the hidden main window (WinUI 3 lifecycle requirement) which
        // also runs the named-pipe listener for focus signals from second instances.
        var window = new MainWindow(OnFocusSignalReceived);
        window.Activate();
    }

    // -----------------------------------------------------------------------
    // Config initialization
    // -----------------------------------------------------------------------

    private void InitializeConfig()
    {
        try
        {
            _config = ConfigStore.Load(ClaudePaths.AppConfigDir);
        }
        catch (Exception ex)
        {
            // Invalid config.json — start fresh so the app is still usable.
            System.Diagnostics.Debug.WriteLine(
                $"[CCSwitcher] Failed to load config, using defaults: {ex.Message}");
            _config = AppConfig.Default;
        }

        // Clear dangling active_account_id if the account no longer exists.
        if (Switcher.ClearActiveIfMissing(_config))
        {
            try
            {
                ConfigStore.Save(ClaudePaths.AppConfigDir, _config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CCSwitcher] Failed to save config after clearing dangling id: {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Tray callbacks
    // -----------------------------------------------------------------------

    private TrayCallbacks BuildCallbacks() => new TrayCallbacks
    {
        OnSwitchAccount = OnSwitchAccount,
        OnToggleProxy   = OnToggleProxy,
        OnOpenSettings  = OnOpenSettings,
        OnImport        = OnImport,
        OnToggleStartup = OnToggleStartup,
        OnExit          = OnExit,
    };

    private void OnSwitchAccount(string accountId)
    {
        _ = Task.Run(async () =>
        {
            await StateMutex.WaitAsync();
            try
            {
                var deps = new SwitchDeps
                {
                    SettingsPath    = ClaudePaths.SettingsPath,
                    ConfigDir       = ClaudePaths.AppConfigDir,
                    UserConfigPath  = ClaudePaths.FindUserConfig(),
                    SecretStore     = _secretStore,
                    CredentialStore = _credentialStore,
                };

                Switcher.ApplyAccount(_config, accountId, deps);

                // Rebuild tray on the UI thread after successful switch.
                DispatchToUI(() => _trayIcon.Rebuild(_config, _callbacks!));
            }
            catch (Exception ex)
            {
                var msg = Secrets.Sanitize(ex.Message);
                System.Diagnostics.Debug.WriteLine($"[CCSwitcher] Switch failed: {msg}");
                // TODO (Task 15): show error notification via SettingsWindow.
            }
            finally
            {
                StateMutex.Release();
            }
        });
    }

    private void OnToggleProxy(bool enabled)
    {
        _ = Task.Run(async () =>
        {
            await StateMutex.WaitAsync();
            try
            {
                var deps = new ProxyDeps
                {
                    SettingsPath = ClaudePaths.SettingsPath,
                    ConfigDir    = ClaudePaths.AppConfigDir,
                    SecretStore  = _secretStore,
                };

                Proxy.SetEnabled(_config, enabled, deps);

                DispatchToUI(() => _trayIcon.Rebuild(_config, _callbacks!));
            }
            catch (Exception ex)
            {
                var msg = Secrets.Sanitize(ex.Message);
                System.Diagnostics.Debug.WriteLine($"[CCSwitcher] Proxy toggle failed: {msg}");
            }
            finally
            {
                StateMutex.Release();
            }
        });
    }

    private void OnOpenSettings()
    {
        DispatchToUI(() => GetOrCreateSettingsWindow().Activate());
    }

    private void OnImport()
    {
        // Open settings window and trigger the import flow.
        DispatchToUI(() =>
        {
            var win = GetOrCreateSettingsWindow();
            win.TriggerImport();
        });
    }

    private void OnToggleStartup(bool enabled)
    {
        StartupManager.SetEnabled(enabled);
        // Rebuild tray so the toggle item reflects the new state.
        DispatchToUI(() => _trayIcon.Rebuild(_config, _callbacks!));
    }

    private void OnExit()
    {
        _trayIcon.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Application.Current.Exit();
    }

    // -----------------------------------------------------------------------
    // Settings window management
    // -----------------------------------------------------------------------

    private void ShowOrCreateSettingsWindow()
    {
        GetOrCreateSettingsWindow().Activate();
    }

    private SettingsWindow GetOrCreateSettingsWindow()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
        }
        _settingsWindow.Activate();
        return _settingsWindow;
    }

    /// <summary>
    /// Called by <see cref="SettingsWindow"/> when it is closed so we can
    /// null-out the reference and create a fresh window next time.
    /// </summary>
    public void OnSettingsWindowClosed()
    {
        _settingsWindow = null;
    }

    // -----------------------------------------------------------------------
    // Public accessors for SettingsWindow
    // -----------------------------------------------------------------------

    /// <summary>Returns the current app config (read-only snapshot for the UI).</summary>
    public AppConfig GetConfig() => _config;

    /// <summary>Returns the secret store used by the core.</summary>
    public ISecretStore GetSecretStore() => _secretStore;

    /// <summary>Returns the credential store used by the core.</summary>
    public ICredentialStore GetCredentialStore() => _credentialStore;

    /// <summary>
    /// Rebuild the tray menu to reflect the latest config state.
    /// Must be called on the UI thread (or will dispatch to it).
    /// </summary>
    public void RebuildTray()
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() != null)
            _trayIcon.Rebuild(_config, _callbacks!);
        else
            DispatchToUI(() => _trayIcon.Rebuild(_config, _callbacks!));
    }

    // -----------------------------------------------------------------------
    // Focus signal (from second instance via named pipe)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Called on the UI thread when a second instance sends the "focus" signal.
    /// </summary>
    internal void OnFocusSignalReceived()
    {
        ShowOrCreateSettingsWindow();
    }

    // -----------------------------------------------------------------------
    // UI thread dispatch helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Run <paramref name="action"/> on the UI thread.
    /// Safe to call from any thread.
    /// </summary>
    private static void DispatchToUI(Action action)
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() != null)
        {
            // Already on a UI thread.
            action();
        }
        else
        {
            // Post to the main window's dispatcher (created before any callbacks fire).
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue != null)
            {
                queue.TryEnqueue(() => action());
            }
            else
            {
                // Fallback: use the WinRT dispatcher if available.
                action();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Single-instance signalling
    // -----------------------------------------------------------------------

    /// <summary>
    /// Connect to the named pipe server running in the existing instance and
    /// send the "focus" command so that instance brings its settings window to
    /// the front.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.Out);

            // Short timeout — if the server isn't ready, we just exit.
            pipe.Connect(timeout: 2000);

            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("focus");
        }
        catch
        {
            // Best-effort: if the signal fails, the second instance still exits.
        }
    }
}
