using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Threading;
using WinRT.Interop;

namespace CCSwitcher;

/// <summary>
/// Invisible lifecycle host window required by WinUI 3. Hides itself immediately
/// on first activation so it never appears in the taskbar or on-screen.
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

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;

        // Shrink to 0×0 and remove from the taskbar so this host window is invisible.
        HideWindow();

        // Start the pipe listener that wakes up when a second instance signals us.
        _pipeListenerCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPipeListenerAsync(_pipeListenerCts.Token));
    }

    /// <summary>
    /// Hides this host window: resize to 0×0 and remove from taskbar via Win32.
    /// </summary>
    private void HideWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);

        // Remove from taskbar by clearing the WS_EX_APPWINDOW style.
        const int GWL_EXSTYLE = -20;
        const int WS_EX_APPWINDOW = 0x00040000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        int exStyle = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle = (exStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Resize to 0×0 and move off-screen.
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);

        // Hide the window entirely.
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
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
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Minimal Win32 P/Invoke surface needed to hide the host window.
/// </summary>
internal static class NativeMethods
{
    internal const int SW_HIDE = 0;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOZORDER = 0x0004;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
