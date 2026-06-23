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

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    public SettingsWindow(App app)
    {
        _app = app;
        this.InitializeComponent();

        this.Activated += OnFirstActivated;
        this.Closed += OnClosed;
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
    }

    /// <summary>
    /// Build the account row list in code-behind so we do not need a
    /// DataTemplate with x:DataType (which can crash the XAML compiler when
    /// the view-model type is defined in the same partial class).
    /// </summary>
    private void RebuildAccountList(AppConfig config)
    {
        AccountsPanel.Children.Clear();

        foreach (var account in config.Accounts)
        {
            var isActive = account.Id == config.ActiveAccountId;

            // Row container
            var row = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(0, 0, 0, 1),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Name
            var nameBlock = new TextBlock
            {
                Text = isActive ? $"✓  {account.Name}" : account.Name,
                FontWeight = isActive
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(nameBlock, 0);

            // Type badge
            var isOAuth = account.AccountType == AccountType.AnthropicOauth;
            var badge = new Border
            {
                Background = new SolidColorBrush(isOAuth
                    ? Windows.UI.Color.FromArgb(255, 0, 120, 215)    // blue for OAuth
                    : Windows.UI.Color.FromArgb(255, 100, 100, 100)), // grey for Token
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = isOAuth ? "OAuth" : "Token",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.White),
                },
            };
            Grid.SetColumn(badge, 1);

            // Edit button
            var accountId = account.Id; // capture for closure
            var editBtn = new Button
            {
                Content = "Edit",
                Margin = new Thickness(0, 0, 4, 0),
                Tag = accountId,
            };
            editBtn.Click += EditAccountBtn_Click;
            Grid.SetColumn(editBtn, 2);

            // Delete button
            var deleteBtn = new Button
            {
                Content = "Delete",
                Tag = accountId,
            };
            deleteBtn.Click += DeleteAccountBtn_Click;
            Grid.SetColumn(deleteBtn, 3);

            grid.Children.Add(nameBlock);
            grid.Children.Add(badge);
            grid.Children.Add(editBtn);
            grid.Children.Add(deleteBtn);

            row.Child = grid;
            AccountsPanel.Children.Add(row);
        }

        NoAccountsText.Visibility = config.Accounts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(authKindCombo);
        panel.Children.Add(tokenBox);

        var dialog = new ContentDialog
        {
            Title             = isEdit ? "Edit Account" : "Add Token Account",
            Content           = panel,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = this.Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name    = nameBox.Text.Trim();
        var baseUrl = baseUrlBox.Text.Trim();
        var secret  = tokenBox.Password;
        var authKind = authKindCombo.SelectedItem is ComboBoxItem { Tag: AuthKind ak }
            ? ak
            : AuthKind.AuthToken;

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
                    _app.GetSecretStore(),
                    ClaudePaths.AppConfigDir);
            }

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
        // Step 1: detect
        ImportCandidate? candidate;
        try
        {
            var config = _app.GetConfig();
            candidate = Importer.Detect(
                config.ManagedKeys,
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

        // Step 2: show name prompt
        var defaultName = Importer.DefaultName(candidate);

        var nameBox = new TextBox
        {
            Header          = "Account Name",
            Text            = defaultName,
            PlaceholderText = "Anthropic",
        };

        var importDialog = new ContentDialog
        {
            Title             = "Import Current Login",
            Content           = nameBox,
            PrimaryButtonText = "Import",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = this.Content.XamlRoot,
        };

        var result = await importDialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();

        // Step 3: import
        await App.StateMutex.WaitAsync();
        try
        {
            var config = _app.GetConfig();

            var importResult = Importer.Import(
                candidate,
                name,
                config.Accounts,
                _app.GetSecretStore());

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
    // Launch at startup
    // -----------------------------------------------------------------------

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle) return;

        StartupManager.SetEnabled(StartupToggle.IsOn);
        _app.RebuildTray();
    }
}
