// SettingsWindow.xaml.cs — Account management UI for ccswitcher.
//
// Responsibilities:
//   - Display the list of accounts with active-account highlight.
//   - Add / edit token accounts via ContentDialog.
//   - Delete account via confirmation ContentDialog.
//   - Import current Claude Code login via ContentDialog.
//   - Proxy settings (enable toggle, URL, No-Proxy) with Save button.
//   - Launch-at-startup ToggleSwitch backed by StartupManager.
//
// All mutating operations acquire App.StateMutex before touching state.
// Errors are sanitized via Secrets.Sanitize before display.
// After successful mutation, App.RebuildTray() is called.
//
// Note: account rows are built in code-behind (RebuildAccountList) rather than
// via a DataTemplate, so the XAML is minimal and the XAML compiler does not
// need to resolve any custom view-model types.

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI.Controls;
using System.Text.Json.Nodes;
using CCSwitcher.Core;

namespace CCSwitcher;

/// <summary>
/// Account management settings window.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    // Stored reference to App so we can reach StateMutex, config, stores, etc.
    private readonly App _app;

    // Guards the startup toggle's Toggled handler from firing programmatically.
    private bool _suppressStartupToggle;

    // Guards the proxy toggle's Toggled handler from firing programmatically.
    private bool _suppressProxyToggle;

    // Guards the model-tracking toggle's Toggled handler from firing programmatically.
    private bool _suppressModelTrackingToggle;

    // The settings.json top-level key tracked by the "remember model" toggle.
    private const string ModelSettingKey = "model";

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    public SettingsWindow(App app)
    {
        _app = app;
        this.InitializeComponent();

        // Native Windows 11 look: Mica backdrop + content extended into a
        // custom, draggable title bar (the AppTitleBar grid defined in XAML).
        this.SystemBackdrop = new MicaBackdrop();
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);

        // Title-bar icon.
        var appWindow = this.AppWindow;
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (System.IO.File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }
        catch
        {
            // Icon is cosmetic; ignore failures.
        }

        SizeWindow(widthDip: 680, heightDip: 760);

        this.Activated += OnFirstActivated;
        this.Closed += OnClosed;
    }

    /// <summary>
    /// Resize the window to a DPI-aware size. <see cref="Microsoft.UI.Windowing.AppWindow.Resize"/>
    /// takes physical pixels, so the requested device-independent size is scaled
    /// by the monitor's DPI (e.g. ×1.5 at 150% scaling) and then clamped to the
    /// monitor's work area so it never exceeds the visible screen.
    /// </summary>
    private void SizeWindow(int widthDip, int heightDip)
    {
        var appWindow = this.AppWindow;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;

        int width = (int)(widthDip * scale);
        int height = (int)(heightDip * scale);

        // Clamp to the monitor's work area (also physical pixels) with a margin.
        var work = Microsoft.UI.Windowing.DisplayArea
            .GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)
            .WorkArea;
        width = Math.Min(width, work.Width - (int)(40 * scale));
        height = Math.Min(height, work.Height - (int)(40 * scale));

        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        // Center within the monitor's work area.
        int x = work.X + (work.Width - width) / 2;
        int y = work.Y + (work.Height - height) / 2;
        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private bool _initialized;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        Refresh();
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        // Tell App that the settings window is gone so it can null-out the ref.
        _app.OnSettingsWindowClosed();
    }

    // -----------------------------------------------------------------------
    // Public API called by App
    // -----------------------------------------------------------------------

    /// <summary>
    /// Open the settings window and immediately show the import dialog.
    /// Called by App when the user picks "Import current login" from the tray.
    /// </summary>
    public void TriggerImport()
    {
        this.Activate();
        // Defer so the window has time to render before the dialog opens.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            async () => await RunImportFlowAsync());
    }

    /// <summary>
    /// Re-populate all controls from the current config. Called by <see cref="App"/>
    /// after an external (tray-driven) state change — switch, proxy toggle, startup
    /// toggle — so this window never shows stale state. Must run on the UI thread.
    /// </summary>
    public void RefreshFromExternal() => Refresh();

    // -----------------------------------------------------------------------
    // Refresh: populate all controls from current config
    // -----------------------------------------------------------------------

    private void Refresh()
    {
        var config = _app.GetConfig();

        // Accounts list (built in code-behind).
        RebuildAccountList(config);

        // Proxy controls — suppress the Toggled handler while we set values.
        _suppressProxyToggle = true;
        ProxyEnabledToggle.IsOn = config.Proxy.Enabled;
        _suppressProxyToggle = false;

        ProxyUrlBox.Text = config.Proxy.Url;
        NoProxyBox.Text  = config.Proxy.NoProxy;

        // Startup toggle
        _suppressStartupToggle = true;
        StartupToggle.IsOn = StartupManager.IsEnabled();
        _suppressStartupToggle = false;

        // Model-tracking toggle
        _suppressModelTrackingToggle = true;
        ModelTrackingToggle.IsOn = config.TrackedSettingsKeys.Contains(ModelSettingKey);
        _suppressModelTrackingToggle = false;
    }

    /// <summary>
    /// Build the account row list in code-behind as native <see cref="SettingsCard"/>s
    /// (the same control PowerToys uses). Building them here rather than via a
    /// DataTemplate keeps the XAML free of a view-model type and avoids the XAML
    /// compiler crashing on x:DataType for a type in this partial class.
    /// </summary>
    private void RebuildAccountList(AppConfig config)
    {
        AccountsPanel.Children.Clear();

        foreach (var account in config.Accounts)
        {
            var isActive = account.Id == config.ActiveAccountId;
            var isOAuth  = account.AccountType == AccountType.AnthropicOauth;
            var accountId = account.Id; // capture for closures

            // Description: account type, plus the base URL when present.
            var typeText = isOAuth ? "Anthropic OAuth" : "Token";
            var description = string.IsNullOrEmpty(account.BaseUrl)
                ? typeText
                : $"{typeText} · {account.BaseUrl}";

            var card = new SettingsCard
            {
                Header      = account.Name,
                Description = description,
            };

            // Header icon: a filled accent check for the active account,
            // a neutral contact glyph otherwise.
            card.HeaderIcon = new FontIcon
            {
                Glyph = isActive ? "" : "", // CheckMark : Contact
                Foreground = isActive
                    ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                    : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };

            // Right-aligned content: type badge + Edit / Delete icon buttons.
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var badge = new Border
            {
                Background = new SolidColorBrush(isOAuth
                    ? Windows.UI.Color.FromArgb(255, 0, 120, 215)    // blue for OAuth
                    : Windows.UI.Color.FromArgb(255, 100, 100, 100)), // grey for Token
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = isOAuth ? "OAuth" : "Token",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.White),
                },
            };

            var editBtn = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 }, // Edit
                Tag = accountId,
            };
            ToolTipService.SetToolTip(editBtn, "Edit");
            editBtn.Click += EditAccountBtn_Click;

            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 }, // Delete
                Tag = accountId,
            };
            ToolTipService.SetToolTip(deleteBtn, "Delete");
            deleteBtn.Click += DeleteAccountBtn_Click;

            content.Children.Add(badge);
            content.Children.Add(editBtn);
            content.Children.Add(deleteBtn);

            card.Content = content;
            AccountsPanel.Children.Add(card);
        }

        NoAccountsCard.Visibility = config.Accounts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // -----------------------------------------------------------------------
    // Dialog sizing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Widen a <see cref="ContentDialog"/> beyond the default ~548 dip cap so the
    /// env-var key/value rows have comfortable room, while staying inside the
    /// settings window (680 dip). The content is stretched to fill the dialog and
    /// sized just under the window width minus the dialog's own chrome/padding, so
    /// the right-hand "remove variable" button never clips off the edge.
    /// </summary>
    private static void WidenDialog(ContentDialog dialog, FrameworkElement content)
    {
        // ContentDialogMaxWidth only caps the width; without an explicit content
        // width the panel shrinks to its content's desired size (≈ the default
        // ~500 dip), so the dialog looks unchanged. Pin an explicit content width
        // to actually widen it, and raise the cap above it so it isn't clamped.
        // A ContentDialog can never exceed its host window (680 dip), and the
        // dialog adds ~40 dip of its own padding, so 600 is the widest content
        // that fits without clipping the right-hand "remove variable" button.
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.Width = 520;
    }

    // -----------------------------------------------------------------------
    // Status bar helpers
    // -----------------------------------------------------------------------

    private void ShowSuccess(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message  = message;
        StatusBar.IsOpen   = true;
    }

    private void ShowError(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message  = Secrets.Sanitize(message);
        StatusBar.IsOpen   = true;
    }

    private void ShowWarning(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Warning;
        StatusBar.Message  = Secrets.Sanitize(message);
        StatusBar.IsOpen   = true;
    }

    // -----------------------------------------------------------------------
    // Add Token Account
    // -----------------------------------------------------------------------

    private async void AddTokenBtn_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddTokenDialogAsync(editingAccount: null);
    }

    // -----------------------------------------------------------------------
    // Edit Account
    // -----------------------------------------------------------------------

    private async void EditAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string accountId)
        {
            var config  = _app.GetConfig();
            var account = config.Accounts.Find(a => a.Id == accountId);
            if (account == null) return;

            // OAuth accounts have an auto-managed credential blob; only the
            // display name and (optional) base URL are meaningful to edit.
            if (account.AccountType == AccountType.AnthropicOauth)
                await ShowEditOauthDialogAsync(account);
            else
                await ShowAddTokenDialogAsync(editingAccount: account);
        }
    }

    // -----------------------------------------------------------------------
    // Add / Edit token account dialog
    // -----------------------------------------------------------------------

    private async Task ShowAddTokenDialogAsync(Account? editingAccount)
    {
        var isEdit = editingAccount != null;

        // --- Build dialog content ---
        var nameBox = new TextBox
        {
            Header      = "Name",
            PlaceholderText = "My Account",
            Text        = editingAccount?.Name ?? string.Empty,
        };

        var baseUrlBox = new TextBox
        {
            Header          = "Base URL (optional)",
            PlaceholderText = "https://api.anthropic.com",
            Text            = editingAccount?.BaseUrl ?? string.Empty,
        };

        var authKindCombo = new ComboBox { Header = "Auth Kind", MinWidth = 160 };
        authKindCombo.Items.Add(new ComboBoxItem { Content = "Auth Token", Tag = AuthKind.AuthToken });
        authKindCombo.Items.Add(new ComboBoxItem { Content = "API Key",    Tag = AuthKind.ApiKey });
        authKindCombo.SelectedIndex = editingAccount?.AuthKind == AuthKind.ApiKey ? 1 : 0;

        var tokenBox = new PasswordBox
        {
            Header          = isEdit ? "New Token (leave blank to keep existing)" : "Token",
            PlaceholderText = isEdit ? "(unchanged)" : "sk-ant-...",
        };

        var envEditor = new EnvVarEditor(editingAccount?.ExtraEnvNullable);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(authKindCombo);
        panel.Children.Add(tokenBox);
        panel.Children.Add(envEditor.Root);

        var dialog = new ContentDialog
        {
            Title             = isEdit ? "Edit Account" : "Add Token Account",
            Content           = panel,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = this.Content.XamlRoot,
        };
        WidenDialog(dialog, panel);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name    = nameBox.Text.Trim();
        var baseUrl = baseUrlBox.Text.Trim();
        var secret  = tokenBox.Password;
        var authKind = authKindCombo.SelectedItem is ComboBoxItem { Tag: AuthKind ak }
            ? ak
            : AuthKind.AuthToken;

        var extraEnv = envEditor.Collect();

        if (string.IsNullOrEmpty(name))
        {
            ShowError("Name is required.");
            return;
        }

        if (!isEdit && string.IsNullOrEmpty(secret))
        {
            ShowError("Token is required for a new account.");
            return;
        }

        await App.StateMutex.WaitAsync();
        try
        {
            var config = _app.GetConfig();
            if (isEdit)
            {
                AccountManager.UpdateAccount(
                    config,
                    editingAccount!.Id,
                    name,
                    string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
                    authKind,
                    string.IsNullOrEmpty(secret) ? null : secret,
                    extraEnv,
                    _app.GetSecretStore(),
                    ClaudePaths.AppConfigDir);
            }
            else
            {
                AccountManager.AddTokenAccount(
                    config,
                    name,
                    string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
                    authKind,
                    secret,
                    extraEnv,
                    _app.GetSecretStore(),
                    ClaudePaths.AppConfigDir);
            }

            // If an EXISTING account was edited and it is the active one,
            // re-apply its env to settings.json so the edit takes effect
            // immediately. (Skipped on add — a brand-new account isn't active.)
            if (isEdit)
                ReapplyActiveEnvIfActive(config, editingAccount!.Id);

            _app.RebuildTray();
            Refresh();
            ShowSuccess(isEdit ? "Account updated." : "Account added.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            App.StateMutex.Release();
        }
    }

    /// <summary>
    /// Re-apply the given account's env to settings.json if it is the currently
    /// active account, so an in-app edit (name, base_url, auth_kind, secret, or
    /// extra_env) takes effect immediately instead of only on the next switch.
    /// No-op when the account is not active (e.g. a freshly-added account, or
    /// editing a non-active account).
    /// </summary>
    private void ReapplyActiveEnvIfActive(AppConfig config, string accountId)
    {
        if (config.ActiveAccountId != accountId)
            return;

        Switcher.ReapplyActiveAccountEnv(
            config,
            accountId,
            new ProxyDeps
            {
                SettingsPath = ClaudePaths.SettingsPath,
                ConfigDir    = ClaudePaths.AppConfigDir,
                SecretStore  = _app.GetSecretStore(),
            });
    }

    // -----------------------------------------------------------------------
    // Edit OAuth account (name + base URL only)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Edit an Anthropic OAuth account. Only the display name and optional base
    /// URL are editable — the credential blob is auto-managed (captured on
    /// switch-out) and auth_kind/token do not apply, so they are not shown.
    /// </summary>
    private async Task ShowEditOauthDialogAsync(Account account)
    {
        var nameBox = new TextBox
        {
            Header          = "Name",
            PlaceholderText = "Anthropic",
            Text            = account.Name,
        };

        var baseUrlBox = new TextBox
        {
            Header          = "Base URL (optional)",
            PlaceholderText = "https://api.anthropic.com",
            Text            = account.BaseUrl ?? string.Empty,
        };

        var envEditor = new EnvVarEditor(account.ExtraEnvNullable);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(envEditor.Root);

        var dialog = new ContentDialog
        {
            Title             = "Edit Account",
            Content           = panel,
            PrimaryButtonText = "Save",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = this.Content.XamlRoot,
        };
        WidenDialog(dialog, panel);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name    = nameBox.Text.Trim();
        var baseUrl = baseUrlBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            ShowError("Name is required.");
            return;
        }

        await App.StateMutex.WaitAsync();
        try
        {
            var config = _app.GetConfig();

            var extraEnv = envEditor.Collect();

            // Pass the existing auth_kind (null for OAuth) and no new secret so
            // the credential blob and identity are preserved untouched.
            AccountManager.UpdateAccount(
                config,
                account.Id,
                name,
                string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
                account.AuthKind,
                null,        // newSecret: keep the existing keyring secret
                extraEnv,
                _app.GetSecretStore(),
                ClaudePaths.AppConfigDir);

            // If the edited account is the active one, re-apply its env to
            // settings.json so the edit (base_url, extra_env, …) takes effect
            // immediately instead of only on the next switch. No-op otherwise.
            ReapplyActiveEnvIfActive(config, account.Id);

            _app.RebuildTray();
            Refresh();
            ShowSuccess("Account updated.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            App.StateMutex.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Delete Account
    // -----------------------------------------------------------------------

    private async void DeleteAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string accountId) return;

        var config  = _app.GetConfig();
        var account = config.Accounts.Find(a => a.Id == accountId);
        if (account == null) return;

        // Confirmation dialog
        var confirmDialog = new ContentDialog
        {
            Title             = "Delete Account",
            Content           = $"Delete \"{account.Name}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = this.Content.XamlRoot,
        };

        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await App.StateMutex.WaitAsync();
        try
        {
            AccountManager.DeleteAccount(
                config,
                accountId,
                _app.GetSecretStore(),
                ClaudePaths.AppConfigDir);

            _app.RebuildTray();
            Refresh();
            ShowSuccess($"Account \"{account.Name}\" deleted.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            App.StateMutex.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Import current login
    // -----------------------------------------------------------------------

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunImportFlowAsync();
    }

    private async Task RunImportFlowAsync()
    {
        // Step 0: is the current login already a managed account?
        // When a ccswitcher token account is active and its token is still live in
        // settings.json, importing would just re-import it. Detect this up front
        // for a precise message — and so we never surface a stale OAuth credential
        // blob left on disk by a *different* (previously-active) account.
        var initialConfig = _app.GetConfig();
        var alreadyManaged = Importer.FindCurrentManagedAccount(
            initialConfig.Accounts,
            initialConfig.ActiveAccountId,
            initialConfig.ManagedKeys,
            ClaudePaths.SettingsPath,
            _app.GetSecretStore());
        if (alreadyManaged != null)
        {
            var alreadyDialog = new ContentDialog
            {
                Title           = "Import Current Login",
                Content         = $"This login is already imported as \"{alreadyManaged.Name}\".",
                CloseButtonText = "OK",
                XamlRoot        = this.Content.XamlRoot,
            };
            await alreadyDialog.ShowAsync();
            return;
        }

        // Step 1: detect
        ImportCandidate? candidate;
        try
        {
            candidate = Importer.Detect(
                ClaudePaths.SettingsPath,
                ClaudePaths.FindUserConfig(),
                _app.GetCredentialStore());
        }
        catch (Exception ex)
        {
            ShowError($"Detection failed: {ex.Message}");
            return;
        }

        if (candidate == null)
        {
            var noLoginDialog = new ContentDialog
            {
                Title           = "Import Current Login",
                Content         = "No importable Claude Code login was detected. Make sure Claude Code is logged in.",
                CloseButtonText = "OK",
                XamlRoot        = this.Content.XamlRoot,
            };
            await noLoginDialog.ShowAsync();
            return;
        }

        // Step 1b: block if this login is already imported.
        var duplicate = Importer.FindDuplicate(
            candidate,
            _app.GetConfig().Accounts,
            _app.GetSecretStore());
        if (duplicate != null)
        {
            var dupDialog = new ContentDialog
            {
                Title           = "Import Current Login",
                Content         = $"This login is already imported as \"{duplicate.Name}\".",
                CloseButtonText = "OK",
                XamlRoot        = this.Content.XamlRoot,
            };
            await dupDialog.ShowAsync();
            return;
        }

        // Step 2: show name prompt, pre-filling the current login's model-selector
        // env vars (ANTHROPIC_*_MODEL) so they are adopted as the new account's
        // extra_env. Other env vars are left untouched (shared across logins); the
        // user can still add any of them by hand before confirming.
        var defaultName = Importer.DefaultName(candidate);

        var nameBox = new TextBox
        {
            Header          = "Account Name",
            Text            = defaultName,
            PlaceholderText = "Anthropic",
        };

        var currentEnv = Importer.CurrentModelEnv(ClaudePaths.SettingsPath);
        var envEditor  = new EnvVarEditor(currentEnv.Count > 0 ? currentEnv : null);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(envEditor.Root);

        var importDialog = new ContentDialog
        {
            Title             = "Import Current Login",
            Content           = panel,
            PrimaryButtonText = "Import",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = this.Content.XamlRoot,
        };
        WidenDialog(importDialog, panel);

        var result = await importDialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();
        var importExtraEnv = envEditor.Collect();

        // Step 3: import
        await App.StateMutex.WaitAsync();
        try
        {
            var config = _app.GetConfig();

            var importResult = Importer.Import(
                candidate,
                name,
                config.Accounts,
                _app.GetSecretStore(),
                importExtraEnv);

            Account newAccount;
            string? warning = null;

            switch (importResult)
            {
                case ImportResult.Created c:
                    newAccount = c.Account;
                    break;
                case ImportResult.CreatedWithWarning w:
                    newAccount = w.Account;
                    warning    = w.Warning;
                    break;
                default:
                    throw new InvalidOperationException("Unknown ImportResult type.");
            }

            config.Accounts.Add(newAccount);

            // Mark the freshly-imported current login active. Import must NOT
            // touch Claude Code's files (settings.json / .credentials.json /
            // ~/.claude.json) — the login is already live; ccswitcher merely
            // adopts it. So we only update our own config.json: record the
            // account active and set managed_keys to the keys this account owns
            // (already present in settings.json) so a later switch strips them.
            try
            {
                string? secret = newAccount.AccountType == AccountType.Token
                    ? _app.GetSecretStore().Get(newAccount.Id)
                    : null;
                config.ManagedKeys =
                    EnvBuilder.Build(newAccount, secret, config.Proxy).Keys.ToList();
                config.ActiveAccountId = newAccount.Id;
            }
            catch (Exception ex)
            {
                // Best-effort: a failure here must not fail the import; the
                // account is still saved below (just not marked active).
                System.Diagnostics.Debug.WriteLine(
                    $"[CCSwitcher] Mark-active after import failed: {Secrets.Sanitize(ex.Message)}");
            }

            ConfigStore.Save(ClaudePaths.AppConfigDir, config);

            _app.RebuildTray();
            Refresh();

            if (warning != null)
                ShowWarning($"Imported \"{name}\". Note: {warning}");
            else
                ShowSuccess($"Imported \"{name}\" successfully.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            App.StateMutex.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Proxy settings
    // -----------------------------------------------------------------------

    private void ProxyEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // Suppress programmatic changes during Refresh().
        // The actual proxy save happens when the user clicks "Save Proxy Settings".
        if (_suppressProxyToggle) return;
    }

    private async void SaveProxyBtn_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ProxyEnabledToggle.IsOn;
        var url     = ProxyUrlBox.Text.Trim();
        var noProxy = NoProxyBox.Text.Trim();

        await App.StateMutex.WaitAsync();
        try
        {
            var config = _app.GetConfig();

            // Update proxy URL and no-proxy before calling Proxy.SetEnabled so
            // the correct values are used when rebuilding the env.
            config.Proxy = new ProxySettings
            {
                Enabled = config.Proxy.Enabled, // SetEnabled will update this
                Url     = string.IsNullOrEmpty(url)     ? config.Proxy.Url     : url,
                NoProxy = string.IsNullOrEmpty(noProxy) ? config.Proxy.NoProxy : noProxy,
            };

            var deps = new ProxyDeps
            {
                SettingsPath = ClaudePaths.SettingsPath,
                ConfigDir    = ClaudePaths.AppConfigDir,
                SecretStore  = _app.GetSecretStore(),
            };

            Proxy.SetEnabled(config, enabled, deps);

            _app.RebuildTray();
            Refresh();
            ShowSuccess("Proxy settings saved.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            App.StateMutex.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Environment editor (settings.json env block)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Open the environment editor: a snapshot of Claude Code's <c>settings.json</c>
    /// <c>env</c> block, classified into three buckets — Managed (read-only),
    /// the active account's <c>extra_env</c> (editable), and Shared (editable) —
    /// plus a read-only list of non-string shared values. The dialog is a snapshot:
    /// the env is read once on open and applied only on Save (routing lives in the
    /// Save handler). A corrupt <c>settings.json</c> aborts the open with an error.
    /// </summary>
    private async void ManageEnvBtn_Click(object sender, RoutedEventArgs e)
    {
        var config = _app.GetConfig();
        var active = config.ActiveAccountId is null
            ? null
            : config.Accounts.Find(a => a.Id == config.ActiveAccountId);

        // Snapshot the live env on open. A corrupt settings.json must not open the
        // editor (and nothing is written): surface a sanitized error and bail.
        JsonObject settings;
        EnvBuckets buckets;
        try
        {
            settings = SettingsEnv.Load(ClaudePaths.SettingsPath);
            buckets  = SettingsEnv.ClassifyEnv(settings, active);
        }
        catch (SettingsEnvException ex)
        {
            ShowError($"Cannot open environment editor: {ex.Message}");
            return;
        }

        var panel = new StackPanel { Spacing = 18 };

        // --- Group 1: Managed by ccswitcher (read-only, tokens masked) ---
        // Hidden when there is no active account or nothing managed is present.
        if (active != null && buckets.Managed.Count > 0)
            panel.Children.Add(BuildManagedGroup(buckets.Managed));

        // --- Group 2: Active account variables (extra_env, editable) ---
        // Hidden when no account is active.
        EnvVarEditor? extraEditor = null;
        if (active != null)
        {
            extraEditor = new EnvVarEditor(
                active.ExtraEnvNullable,
                header: "Active account variables (extra_env)",
                subtitle: $"Applied to Claude Code while \"{active.Name}\" is active. Saved to the account.");
            panel.Children.Add(extraEditor.Root);
        }

        // --- Group 3: Shared variables (editable) + read-only non-string keys ---
        var sharedInitial = buckets.Shared.Count > 0
            ? buckets.Shared.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal)
            : null;
        var sharedEditor = new EnvVarEditor(
            sharedInitial,
            header: "Shared variables",
            subtitle: "Shared across all logins in settings.json. Only touched when you edit here — never rewritten on account switch.");

        var sharedGroup = new StackPanel { Spacing = 6 };
        sharedGroup.Children.Add(sharedEditor.Root);
        if (buckets.SharedReadOnlyKeys.Count > 0)
            sharedGroup.Children.Add(BuildSharedReadOnlyNote(buckets.SharedReadOnlyKeys));
        panel.Children.Add(sharedGroup);

        // Snapshot data the Save routing (Task 4) needs, captured in this closure.
        var oldSharedKeys = buckets.Shared.Select(k => k.Key).ToList();

        // Outer scroll so the three groups fit inside the ~680 dip window.
        var scroll = new ScrollViewer
        {
            Content                       = panel,
            MaxHeight                     = 460,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dialog = new ContentDialog
        {
            Title             = "Environment Variables",
            Content           = scroll,
            PrimaryButtonText = "Save",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = this.Content.XamlRoot,
        };
        WidenDialog(dialog, scroll);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // TODO(Task 4): implement the Save routing here. Under App.StateMutex
        // (released in a finally): re-read the active account from a fresh config
        // (guards against a tray switch between open and Save); validate the
        // collected Shared keys don't collide with SettingsEnv.ManagedKeys or the
        // active account's extra_env keys (else ShowError, don't save/close);
        // route AccountExtra via active.ExtraEnvNullable + ReapplyActiveEnvIfActive;
        // route Shared via SettingsEnv.Load -> ApplySharedEnv(settings,
        // oldSharedKeys, newShared) -> AtomicFile backup + atomic write; then
        // App.RebuildTray() + Refresh() + ShowSuccess. The snapshot captured above
        // (active?.Id, extraEditor, sharedEditor, oldSharedKeys) feeds that flow.
        _ = (active, extraEditor, sharedEditor, oldSharedKeys);
    }

    /// <summary>
    /// Build the read-only "Managed by ccswitcher" group: one row per managed env
    /// key. Token keys (<c>ANTHROPIC_AUTH_TOKEN</c> / <c>ANTHROPIC_API_KEY</c>) are
    /// masked — the secret value is never rendered.
    /// </summary>
    private static FrameworkElement BuildManagedGroup(
        IReadOnlyList<KeyValuePair<string, string>> managed)
    {
        var group = new StackPanel { Spacing = 6 };
        group.Children.Add(new TextBlock
        {
            Text  = "Managed by ccswitcher",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        group.Children.Add(new TextBlock
        {
            Text         = "Controlled by the active account and Proxy settings — edit those to change these.",
            Opacity      = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var (key, value) in managed)
        {
            var display = IsSecretKey(key)
                ? (string.IsNullOrEmpty(value) ? "(unset)" : "••••••")
                : value;

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,   GridUnitType.Star) });

            var keyText = new TextBlock
            {
                Text              = key,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var valText = new TextBlock
            {
                Text                   = display,
                Opacity                = 0.7,
                TextTrimming           = TextTrimming.CharacterEllipsis,
                VerticalAlignment      = VerticalAlignment.Center,
                IsTextSelectionEnabled = !IsSecretKey(key),
            };
            Grid.SetColumn(keyText, 0);
            Grid.SetColumn(valText, 1);
            row.Children.Add(keyText);
            row.Children.Add(valText);
            group.Children.Add(row);
        }

        return group;
    }

    /// <summary>True for the two managed env keys whose value is a secret token.</summary>
    private static bool IsSecretKey(string key) =>
        key is "ANTHROPIC_AUTH_TOKEN" or "ANTHROPIC_API_KEY";

    /// <summary>
    /// Build the read-only note listing shared env keys whose value is not a string
    /// (number/array/object/null). They can't be edited in a text field, so they are
    /// shown for visibility only and are never modified on Save.
    /// </summary>
    private static FrameworkElement BuildSharedReadOnlyNote(IReadOnlyList<string> keys)
    {
        var group = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        group.Children.Add(new TextBlock
        {
            Text         = "Read-only (non-text values, left untouched):",
            Opacity      = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var key in keys)
        {
            group.Children.Add(new TextBlock
            {
                Text       = key,
                Opacity    = 0.7,
                FontFamily = new FontFamily("Consolas"),
            });
        }
        return group;
    }

    // -----------------------------------------------------------------------
    // Launch at startup
    // -----------------------------------------------------------------------

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle) return;

        StartupManager.SetEnabled(StartupToggle.IsOn);
        _app.RebuildTray();
    }

    // -----------------------------------------------------------------------
    // On switch — remember selected model per account
    // -----------------------------------------------------------------------

    private async void ModelTrackingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressModelTrackingToggle) return;

        var enabled = ModelTrackingToggle.IsOn;

        await App.StateMutex.WaitAsync();
        try
        {
            var config = _app.GetConfig();

            var has = config.TrackedSettingsKeys.Contains(ModelSettingKey);
            if (enabled && !has)
                config.TrackedSettingsKeys.Add(ModelSettingKey);
            else if (!enabled && has)
                config.TrackedSettingsKeys.Remove(ModelSettingKey);

            ConfigStore.Save(ClaudePaths.AppConfigDir, config);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            App.StateMutex.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Per-account environment-variable editor (used inside the add/edit dialogs)
    // -----------------------------------------------------------------------

    /// <summary>
    /// A repeatable key/value row editor for an account's <c>extra_env</c>.
    /// Built imperatively, like the rest of the dialogs, and returns the
    /// collected dictionary via <see cref="Collect"/>. Empty rows and rows
    /// with a blank key are dropped; an all-empty editor collects to null so
    /// the field is omitted from config.json.
    /// </summary>
    private sealed class EnvVarEditor
    {
        private readonly StackPanel _rows = new() { Spacing = 6 };

        /// <summary>The control to append to a dialog's content panel.</summary>
        public FrameworkElement Root { get; }

        public EnvVarEditor(
            Dictionary<string, string>? initial,
            string header = "Environment variables (optional)",
            string subtitle = "Applied to Claude Code when this account is active.")
        {
            var addBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon { Glyph = "" },  // Add
                        new TextBlock { Text = "Add variable", VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            addBtn.Click += (_, _) => AddRow("", "");

            // Bound so a long list scrolls inside the dialog instead of overflowing.
            var rowsScroll = new ScrollViewer
            {
                Content                       = _rows,
                MaxHeight                     = 220,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            };

            Root = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text  = header,
                        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    },
                    new TextBlock
                    {
                        Text         = subtitle,
                        Opacity      = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    rowsScroll,
                    addBtn,
                },
            };

            if (initial != null)
                foreach (var (key, value) in initial)
                    AddRow(key, value);
        }

        private void AddRow(string key, string value)
        {
            var keyBox = new TextBox { PlaceholderText = "Variable", Text = key };
            var valBox = new TextBox { PlaceholderText = "Value",     Text = value };

            var removeBtn = new Button
            {
                Content = new FontIcon { Glyph = "" },  // Clear (✕)
                Padding = new Thickness(8, 4, 8, 4),
            };

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,   GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(keyBox, 0);
            Grid.SetColumn(valBox, 1);
            Grid.SetColumn(removeBtn, 2);
            row.Children.Add(keyBox);
            row.Children.Add(valBox);
            row.Children.Add(removeBtn);

            removeBtn.Click += (_, _) => _rows.Children.Remove(row);

            _rows.Children.Add(row);
        }

        /// <summary>
        /// Collects rows with a non-empty key (trimming key and value) into a
        /// dictionary. Returns null when empty.
        /// </summary>
        public Dictionary<string, string>? Collect()
        {
            var dict = new Dictionary<string, string>();

            foreach (var row in _rows.Children.OfType<Grid>())
            {
                var boxes = row.Children.OfType<TextBox>().ToList();
                if (boxes.Count == 0) continue;

                var key = boxes[0].Text.Trim();
                if (string.IsNullOrEmpty(key)) continue;

                dict[key] = boxes.Count > 1 ? boxes[1].Text.Trim() : string.Empty;
            }

            return dict.Count > 0 ? dict : null;
        }
    }
}
