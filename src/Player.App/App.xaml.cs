using Microsoft.UI.Xaml;
using Player.App.Platform;
using System.Runtime.InteropServices;

namespace Player.App;

public partial class App : Application
{
    private MainWindow? _window;
    private SingleInstance? _instance;
    public App()
    {
        UnhandledException += (_, args) => WriteStartupFailure(args.Exception, args.Message);
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (arguments.Length == 1 && arguments[0] is "--register-file-associations" or "--unregister-file-associations")
            {
                if (arguments[0] == "--register-file-associations") ExplorerIntegration.Register();
                else ExplorerIntegration.Unregister();
                Exit(); return;
            }
            if (arguments.FirstOrDefault() == "--") arguments = arguments.Skip(1).ToArray();
            if (arguments.Length > 1) throw new ArgumentException("Open one local media file at a time.");
            string? file = arguments.Length == 0 ? null : Path.GetFullPath(arguments[0]);
            _instance = new SingleInstance();
            if (!_instance.IsPrimary)
            {
                await _instance.ForwardAsync(file);
                _instance.Dispose(); _instance = null;
                Exit();
                return;
            }
            _window = new MainWindow(file);
            _window.Closed += () => { _instance?.Dispose(); _instance = null; };
            _instance.Listen(path => !_window.IsClosing && _window.DispatcherQueue.TryEnqueue(() => _window.OpenFromExplorer(path)));
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteStartupFailure(ex, "mpv.win startup or activation failed");
            Environment.ExitCode = 1;
            _instance?.Dispose(); _instance = null;
            MessageBox(0, ex is OperationCanceledException ? "mpv.win didn't respond in time. Please try opening the file again." : ex.Message,
                "Couldn't open mpv.win", 0x10);
            Exit();
        }
    }

    // Opt-in local diagnostics. Preserve normal exception handling and never
    // report a failed startup as successful by marking its exception handled.
    internal static void WriteStartupFailure(Exception error, string message)
    {
        var path = Environment.GetEnvironmentVariable("PLAYER_STARTUP_DIAGNOSTICS_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}{error}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
