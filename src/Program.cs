using Bisik.UI;
using Bisik.Core;

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
        private readonly SettingsManager _settingsManager;
        private readonly HistoryLogger _historyLogger;
        private SettingsForm? _settingsForm;
        private HistoryForm? _historyForm;

        public BisikApplicationContext()
        {
            _settingsManager = new SettingsManager();
            _historyLogger = new HistoryLogger();
            _trayIcon = new TrayIcon(
                onOpenSettings: ShowSettings,
                onOpenHistory: ShowHistory,
                onShowAbout: static () => MessageBox.Show(
                    "Bisik",
                    "About Bisik",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information),
                onExit: ExitThread,
                onLanguageSelected: static _ => { },
                onTogglePostProcessor: static _ => { });
        }

        private void ShowSettings()
        {
            if (_settingsForm is null || _settingsForm.IsDisposed)
            {
                _settingsForm = new SettingsForm(audioRecorder: null, transcriptionService: null, settingsManager: _settingsManager);
                _settingsForm.FormClosed += (_, _) => _settingsForm = null;
                _settingsForm.Show();
                return;
            }

            if (!_settingsForm.Visible)
            {
                _settingsForm.Show();
            }

            if (_settingsForm.WindowState == FormWindowState.Minimized)
            {
                _settingsForm.WindowState = FormWindowState.Normal;
            }

                _settingsForm.BringToFront();
                _settingsForm.Activate();
        }

        private void ShowHistory()
        {
            if (_historyForm is null || _historyForm.IsDisposed)
            {
                _historyForm = new HistoryForm(_historyLogger);
                _historyForm.FormClosed += (_, _) => _historyForm = null;
                _historyForm.Show();
                return;
            }

            if (!_historyForm.Visible)
            {
                _historyForm.Show();
            }

            if (_historyForm.WindowState == FormWindowState.Minimized)
            {
                _historyForm.WindowState = FormWindowState.Normal;
            }

            _historyForm.BringToFront();
            _historyForm.Activate();
        }

        protected override void ExitThreadCore()
        {
            _settingsForm?.Close();
            _historyForm?.Close();
            _trayIcon.Dispose();
            base.ExitThreadCore();
        }
    }
}
