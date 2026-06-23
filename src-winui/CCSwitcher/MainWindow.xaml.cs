using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Threading;
using H.NotifyIcon;

namespace CCSwitcher;

/// <summary>
/// Invisible lifecycle host window required by WinUI 3. It hosts the
/// <see cref="TrayControl"/> TaskbarIcon (declared in XAML so H.NotifyIcon
/// registers it on Loaded) and is hidden by <see cref="App"/> via
/// <c>AppWindow.Hide()</c> right after activation — it never appears on-screen
/// or in the taskbar, yet stays "open" so the app keeps running.
///
/// Also runs a background named-pipe listener: when a second instance launches,
/// it sends a "focus" signal which triggers <see cref="_onFocusSignal"/> — wired
/// by <see cref="App"/> to show and focus the settings window.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string PipeName = "CCSwitcher_Focus";

    private readonly Action _onFocusSignal;
    private CancellationTokenSource? _pipeListenerCts;
    private bool _initialized;

    /// <summary>
    /// Construct the host window.
    /// </summary>
    /// <param name="onFocusSignal">
    /// Callback invoked on the UI thread when a "focus" pipe message arrives.
    /// Typically <c>app.OnFocusSignalReceived</c>.
    /// </param>
    public MainWindow(Action onFocusSignal)
    {
        _onFocusSignal = onFocusSignal;
        this.InitializeComponent();
        // WinUI 3 Window does not inherit FrameworkElement; use Activated for
        // the first-activation hook instead of Loaded.
        this.Activated += OnActivated;
    }

    /// <summary>
    /// The XAML-declared tray icon. <see cref="App"/> populates its menu/icon
    /// after the window is activated.
    /// </summary>
    public TaskbarIcon TrayIcon => TrayControl;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;

        // Start the pipe listener that wakes up when a second instance signals us.
        _pipeListenerCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPipeListenerAsync(_pipeListenerCts.Token));
    }

    /// <summary>
    /// Background loop: wait for a "focus" message from a second instance, then
    /// show/activate the settings window on the UI thread.
    /// </summary>
    private async Task RunPipeListenerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName: PipeName,
                    direction: PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var message = await reader.ReadLineAsync(ct);

                if (message?.Trim().Equals("focus", StringComparison.OrdinalIgnoreCase) == true)
                {
                    this.DispatcherQueue.TryEnqueue(() => _onFocusSignal());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient pipe error — restart the listener after a short pause.
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
