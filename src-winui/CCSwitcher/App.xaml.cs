using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Threading;

namespace CCSwitcher;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// Single-instance enforcement: if another instance is already running, signal it via
/// named pipe to focus its settings window, then exit silently.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "CCSwitcher_SingleInstance";
    private const string PipeName = "CCSwitcher_Focus";

    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// Serializes all mutating operations (switch, add, update, delete, proxy toggle,
    /// import) — same role as Arc&lt;Mutex&lt;AppConfig&gt;&gt; in the Tauri version.
    /// </summary>
    public static readonly SemaphoreSlim StateMutex = new SemaphoreSlim(1, 1);

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Attempt to acquire the single-instance mutex.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — signal it to show/focus the
            // settings window, then exit this second instance silently.
            SignalExistingInstance();
            this.Exit();
            return;
        }

        // We are the first (and only) instance — create the hidden main window.
        var window = new MainWindow();
        window.Activate();
    }

    /// <summary>
    /// Connect to the named pipe server running in the existing instance and send
    /// the "focus" command so that instance brings its settings window to the front.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.Out);

            // Use a short timeout — if the server isn't ready, we just exit.
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
