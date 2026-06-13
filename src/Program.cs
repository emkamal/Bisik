using Bisik.UI;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;
using WindowsInput;

namespace Bisik;

static class Program
{
    private const string SingleInstanceMutexName = @"Global\Bisik.SingleInstance";
    private const int MainHotkeyId = 0x4249;
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
        private readonly AppSettings _settings;
        private readonly HttpClient _httpClient;
        private readonly TrayIcon _trayIcon;
        private readonly SettingsManager _settingsManager;
        private readonly HistoryLogger _historyLogger;
        private readonly ErrorLogger _errorLogger;
        private readonly AudioRecorder _audioRecorder;
        private readonly HotkeyManager _hotkeyManager;
        private readonly IPostProcessor _postProcessor;
        private readonly TranscriptionService _transcriptionService;
        private readonly OutputHandler _outputHandler;
        private readonly SoundFeedback _soundFeedback;
        private readonly StartupRegistration _startupRegistration;
        private readonly OverlayWindow _overlayWindow;
        private readonly HiddenMessageWindow _messageWindow;
        private readonly System.Windows.Forms.Timer _pushToTalkReleaseTimer;
        private readonly HotkeyGesture _hotkeyGesture;
        private readonly InputSimulator _inputSimulator;
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private VoiceActivityTracker? _vadTracker;
        private bool _vadMonitoringEnabled;
        private bool _isShuttingDown;
        private SettingsForm? _settingsForm;
        private HistoryForm? _historyForm;

        public BisikApplicationContext()
        {
            _settingsManager = new SettingsManager();
            _settings = _settingsManager.Load();
            _httpClient = new HttpClient();
            _historyLogger = new HistoryLogger();
            _errorLogger = new ErrorLogger();
            _audioRecorder = new AudioRecorder(_settings.Audio);
            _overlayWindow = new OverlayWindow();
            _messageWindow = CreateMessageWindow();
            _hotkeyManager = new HotkeyManager(new Win32HotkeyRegistrar(), _messageWindow.Handle);
            _hotkeyGesture = HotkeyManager.ParseHotkey(_settings.Hotkey);
            _postProcessor = new LLMPostProcessor(_httpClient);
            _soundFeedback = new SoundFeedback(_settings.SoundEnabled);
            _inputSimulator = new InputSimulator();
            _transcriptionService = new TranscriptionService(
                providers: BuildProviderMap(_httpClient, _settings),
                postProcessor: _postProcessor,
                historyLogger: _historyLogger,
                errorLogger: _errorLogger,
                playSuccess: _soundFeedback.PlaySuccess,
                playError: _soundFeedback.PlayError);
            _outputHandler = new OutputHandler(
                autoTypeSink: HandleAutoTypeOutput,
                clipboardSink: HandleClipboardOutput,
                popupSink: HandlePopupOutput);
            _startupRegistration = new StartupRegistration();
            _pushToTalkReleaseTimer = new System.Windows.Forms.Timer
            {
                Interval = 35,
            };
            _pushToTalkReleaseTimer.Tick += async (_, _) => await CheckPushToTalkReleaseAsync();
            _audioRecorder.AudioLevelChanged += (_, args) => _overlayWindow.SetLevel(args.Level);
            _messageWindow.HotkeyPressed += async (_, hotkeyId) =>
            {
                if (hotkeyId == MainHotkeyId)
                {
                    await HandleRegisteredHotkeyAsync();
                }
            };

            if (_settings.LaunchAtStartup)
            {
                _startupRegistration.SetEnabled(true, Application.ExecutablePath);
            }

            _hotkeyManager.Register(MainHotkeyId, _hotkeyGesture);

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
                _settingsForm = new SettingsForm(
                    audioRecorder: _audioRecorder,
                    transcriptionService: _transcriptionService,
                    settingsManager: _settingsManager);
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
            _isShuttingDown = true;
            _shutdownCancellation.Cancel();
            _settingsForm?.Close();
            _historyForm?.Close();
            _pushToTalkReleaseTimer.Stop();
            _vadMonitoringEnabled = false;
            _vadTracker = null;
            _audioRecorder.StopVoiceActivityMonitoring();
            _hotkeyManager.UnregisterAll();
            _audioRecorder.Dispose();
            _overlayWindow.Dispose();
            _httpClient.Dispose();
            _shutdownCancellation.Dispose();
            _messageWindow.Dispose();
            _trayIcon.Dispose();
            base.ExitThreadCore();
        }

        private async Task HandleRegisteredHotkeyAsync()
        {
            if (_isShuttingDown)
            {
                return;
            }

            if (_settings.TriggerMode == TriggerMode.Vad)
            {
                await HandleVadHotkeyAsync();
                return;
            }

            if (_settings.TriggerMode == TriggerMode.Toggle)
            {
                await HandleToggleHotkeyAsync();
                return;
            }

            if (_settings.TriggerMode != TriggerMode.PushToTalk)
            {
                return;
            }

            var action = _hotkeyManager.HandleKeyDown(_settings.TriggerMode);
            if (action != HotkeyAction.StartRecording)
            {
                return;
            }

            try
            {
                _audioRecorder.UpdateSettings(_settings.Audio);
                _audioRecorder.StartRecording();
                ShowRecordingIndicator();
                _trayIcon.SetState(TrayIconState.Recording);
                _pushToTalkReleaseTimer.Start();
            }
            catch (Exception exception)
            {
                _errorLogger.Log(exception);
                _trayIcon.SetState(TrayIconState.Idle);
                _overlayWindow.HideOverlay();
            }
        }

        private async Task HandleVadHotkeyAsync()
        {
            if (_isShuttingDown)
            {
                return;
            }

            var action = _hotkeyManager.HandleKeyDown(_settings.TriggerMode);

            switch (action)
            {
                case HotkeyAction.StartVoiceActivityDetection:
                    try
                    {
                        _vadMonitoringEnabled = true;
                        _vadTracker = new VoiceActivityTracker(_settings.Audio);
                        _audioRecorder.UpdateSettings(_settings.Audio);
                        _audioRecorder.StartVoiceActivityMonitoring(HandleVadLevelSample);
                    }
                    catch (Exception exception)
                    {
                        _vadMonitoringEnabled = false;
                        _vadTracker = null;
                        _audioRecorder.StopVoiceActivityMonitoring();
                        _errorLogger.Log(exception);
                    }

                    break;

                case HotkeyAction.StopVoiceActivityDetection:
                    _vadMonitoringEnabled = false;
                    _vadTracker = null;
                    _audioRecorder.StopVoiceActivityMonitoring();

                    if (_audioRecorder.State == RecordingState.Recording)
                    {
                        await CompleteRecordingAndTranscriptionAsync(restartVadMonitoring: false);
                    }

                    break;
            }
        }

        private async Task HandleToggleHotkeyAsync()
        {
            if (_isShuttingDown)
            {
                return;
            }

            var action = _hotkeyManager.HandleKeyDown(_settings.TriggerMode);

            switch (action)
            {
                case HotkeyAction.StartRecording:
                    try
                    {
                        _audioRecorder.UpdateSettings(_settings.Audio);
                        _audioRecorder.StartRecording();
                        ShowRecordingIndicator();
                        _trayIcon.SetState(TrayIconState.Recording);
                    }
                    catch (Exception exception)
                    {
                        _errorLogger.Log(exception);
                        _trayIcon.SetState(TrayIconState.Idle);
                        _overlayWindow.HideOverlay();
                    }

                    break;

                case HotkeyAction.StopRecordingAndTranscribe:
                    await CompleteRecordingAndTranscriptionAsync();
                    break;
            }
        }

        private async Task CheckPushToTalkReleaseAsync()
        {
            if (_isShuttingDown)
            {
                return;
            }

            if (IsGestureStillPressed(_hotkeyGesture))
            {
                return;
            }

            _pushToTalkReleaseTimer.Stop();
            var action = _hotkeyManager.HandleKeyUp(_settings.TriggerMode);
            if (action != HotkeyAction.StopRecordingAndTranscribe)
            {
                return;
            }

            await CompleteRecordingAndTranscriptionAsync();
        }

        private async Task CompleteRecordingAndTranscriptionAsync(bool restartVadMonitoring = true)
        {
            try
            {
                _trayIcon.SetState(TrayIconState.Transcribing);
                _overlayWindow.HideOverlay();
                var recordedAudio = await _audioRecorder.StopRecordingAsync(_shutdownCancellation.Token);
                var result = await _transcriptionService.TranscribeAsync(
                    recordedAudio.FilePath,
                    _settings.DefaultLanguage,
                    _settings,
                    _shutdownCancellation.Token);

                if (!result.Succeeded)
                {
                    _trayIcon.ShowBalloon(
                        title: "Transcription Failed",
                        message: string.IsNullOrWhiteSpace(result.ErrorMessage) ? "The transcription request failed." : result.ErrorMessage,
                        icon: ToolTipIcon.Error);
                }

                await _outputHandler.DispatchAsync(result, _settings.OutputMode, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _errorLogger.Log(exception);
                _trayIcon.ShowBalloon(
                    title: "Transcription Failed",
                    message: exception.Message,
                    icon: ToolTipIcon.Error);
            }
            finally
            {
                _trayIcon.SetState(TrayIconState.Idle);

                if (restartVadMonitoring && _vadMonitoringEnabled)
                {
                    StartVadMonitoring();
                }
            }
        }

        private void HandleVadLevelSample(double level, int elapsedMs)
        {
            _vadTracker ??= new VoiceActivityTracker(_settings.Audio);
            var action = _vadTracker.ProcessLevel(level, elapsedMs);

            switch (action)
            {
                case VoiceActivityAction.StartRecording:
                    try
                    {
                        _audioRecorder.StopVoiceActivityMonitoring();
                        _audioRecorder.UpdateSettings(_settings.Audio);
                        _audioRecorder.StartRecording();
                        ShowRecordingIndicator();
                        _trayIcon.SetState(TrayIconState.Recording);
                    }
                    catch (Exception exception)
                    {
                        _errorLogger.Log(exception);
                        _trayIcon.SetState(TrayIconState.Idle);
                        _overlayWindow.HideOverlay();
                        if (_vadMonitoringEnabled)
                        {
                            StartVadMonitoring();
                        }
                    }

                    break;

                case VoiceActivityAction.StopRecording:
                    _ = CompleteRecordingAndTranscriptionAsync();
                    break;
            }
        }

        private void StartVadMonitoring()
        {
            if (!_vadMonitoringEnabled)
            {
                return;
            }

            _vadTracker = new VoiceActivityTracker(_settings.Audio);
            _audioRecorder.UpdateSettings(_settings.Audio);
            _audioRecorder.StartVoiceActivityMonitoring(HandleVadLevelSample);
        }

        private void ShowRecordingIndicator()
        {
            switch (_settings.IndicatorStyle)
            {
                case IndicatorStyle.DotNearCursor:
                    _overlayWindow.ShowDotNearCursor();
                    break;

                case IndicatorStyle.BarOverlay:
                    _overlayWindow.ShowBarOverlay();
                    break;

                case IndicatorStyle.TrayIconOnly:
                    _overlayWindow.SetMode(OverlayWindowMode.TrayIconOnly);
                    break;
            }
        }

        private void HandleAutoTypeOutput(string text)
        {
            Task.Delay(50).GetAwaiter().GetResult();
            _inputSimulator.Keyboard.TextEntry(text);
        }

        private static void HandleClipboardOutput(string text)
        {
            Clipboard.SetText(text);
        }

        private static void HandlePopupOutput(string text)
        {
            using var popup = new ReviewPopupForm(text);
            popup.ShowDialog();
        }

        private static HiddenMessageWindow CreateMessageWindow()
        {
            var form = new HiddenMessageWindow
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                Opacity = 0,
                Size = new Size(1, 1),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
            };
            form.Load += (_, _) => form.Hide();
            form.Show();
            return form;
        }

        private static bool IsGestureStillPressed(HotkeyGesture hotkey)
        {
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl) &&
                !IsVirtualKeyPressed((int)WindowsInput.Native.VirtualKeyCode.CONTROL))
            {
                return false;
            }

            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift) &&
                !IsVirtualKeyPressed((int)WindowsInput.Native.VirtualKeyCode.SHIFT))
            {
                return false;
            }

            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt) &&
                !IsVirtualKeyPressed((int)WindowsInput.Native.VirtualKeyCode.MENU))
            {
                return false;
            }

            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win) &&
                !IsEitherWindowsKeyPressed())
            {
                return false;
            }

            return hotkey.Key == Keys.None || IsVirtualKeyPressed((int)hotkey.Key);
        }

        private static bool IsEitherWindowsKeyPressed()
        {
            return IsVirtualKeyPressed((int)WindowsInput.Native.VirtualKeyCode.LWIN) ||
                IsVirtualKeyPressed((int)WindowsInput.Native.VirtualKeyCode.RWIN);
        }

        private static bool IsVirtualKeyPressed(int virtualKeyCode)
        {
            return (GetAsyncKeyState(virtualKeyCode) & 0x8000) != 0;
        }

        private static IReadOnlyDictionary<ProviderType, ITranscriptionProvider> BuildProviderMap(HttpClient httpClient, AppSettings settings)
        {
            var providerSettings = settings.Provider;
            return new Dictionary<ProviderType, ITranscriptionProvider>
            {
                [ProviderType.AzureOpenAi] = new AzureOpenAIProvider(httpClient, providerSettings with { Type = ProviderType.AzureOpenAi }),
                [ProviderType.OpenAi] = new OpenAIProvider(httpClient, providerSettings with { Type = ProviderType.OpenAi }),
                [ProviderType.OpenRouter] = new OpenRouterProvider(httpClient, providerSettings with { Type = ProviderType.OpenRouter }),
                [ProviderType.LocalWhisper] = new LocalWhisperProvider(httpClient, providerSettings with { Type = ProviderType.LocalWhisper }),
                [ProviderType.CustomEndpoint] = new CustomEndpointProvider(httpClient, providerSettings with { Type = ProviderType.CustomEndpoint }),
            };
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private sealed class HiddenMessageWindow : Form
    {
        private const int WmHotkey = 0x0312;

        public event EventHandler<int>? HotkeyPressed;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey)
            {
                HotkeyPressed?.Invoke(this, message.WParam.ToInt32());
            }

            base.WndProc(ref message);
        }
    }
}
