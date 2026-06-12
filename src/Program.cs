using Bisik.UI;

namespace Bisik;

static class Program
{
    private const string SingleInstanceMutexName = @"Global\Bisik.SingleInstance";
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    static void Main()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.ApplicationExit += HandleApplicationExit;

        // Start the message loop from an application context so the tray shell can own app lifetime later.
        Application.Run(new BisikApplicationContext());
    }

    private static void HandleApplicationExit(object? sender, EventArgs eventArgs)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private sealed class BisikApplicationContext : ApplicationContext
    {
        private readonly TrayIcon _trayIcon;

        public BisikApplicationContext()
        {
            _trayIcon = new TrayIcon(
                onOpenSettings: static () => { },
                onOpenHistory: static () => { },
                onShowAbout: static () => MessageBox.Show(
                    "Bisik",
                    "About Bisik",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information),
                onExit: ExitThread,
                onLanguageSelected: static _ => { },
                onTogglePostProcessor: static _ => { });
        }

        protected override void ExitThreadCore()
        {
            _trayIcon.Dispose();
            base.ExitThreadCore();
        }
    }
}
