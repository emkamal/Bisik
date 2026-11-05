using Bisik.UI;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;

namespace Bisik;

static class Program
{
    private const string SingleInstanceMutexName = @"Global\Bisik.SingleInstance";
    private const int MainHotkeyId = 0x4249;
    private const int InsertLatestHotkeyId = 0x424A;
    private const int FastOutputHotkeyId = 0x424B;
    private static readonly uint ShowSettingsMessage = RegisterWindowMessage("Bisik.ShowSettings");
    private static readonly object GlobalExceptionHandlerLock = new();
    private static Mutex? _singleInstanceMutex;
    private static Action<Exception, string>? _runtimeExceptionHandler;

    [STAThread]
    static void Main(string[] args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            PostMessage(new IntPtr(0xffff), ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ApplicationExit += HandleApplicationExit;
        Application.ThreadException += (_, args) => HandleUnhandledException(args.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ??
                new InvalidOperationException("An unknown unhandled exception terminated the app domain.");
            HandleUnhandledException(exception, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            HandleUnhandledException(args.Exception, "TaskScheduler");
            args.SetObserved();
        };

        // Start the message loop from an application context so the tray shell can own app lifetime later.
        try
        {
            Application.Run(new BisikApplicationContext(args.Contains("--settings")));
        }
        catch (Exception exception)
        {
            HandleUnhandledException(exception, "Startup");
            MessageBox.Show("Bisik could not start. See %APPDATA%\\Bisik\\error.log.", "Bisik", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void HandleApplicationExit(object? sender, EventArgs eventArgs)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private static void HandleUnhandledException(Exception exception, string source)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Action<Exception, string>? runtimeHandler;
        lock (GlobalExceptionHandlerLock)
        {
            runtimeHandler = _runtimeExceptionHandler;
        }

        if (runtimeHandler is not null)
        {
            runtimeHandler(exception, source);
            return;
        }

        try
        {
            new ErrorLogger().Log(exception);
        }
        catch
        {
            // Logging should never be able to trigger a second fatal exception path.
        }
    }

    private sealed class BisikApplicationContext : ApplicationContext
    {
        private AppSettings _settings;
        private readonly HttpClient _httpClient;
        private readonly TrayIcon _trayIcon;
        private readonly SettingsManager _settingsManager;
        private readonly HistoryLogger _historyLogger;
        private readonly ErrorLogger _errorLogger;
        private readonly AudioRecorder _audioRecorder;
        private readonly HotkeyManager _hotkeyManager;
        private readonly IPostProcessor _postProcessor;
        private TranscriptionService _transcriptionService;
        private readonly OutputHandler _outputHandler;
        private SoundFeedback _soundFeedback;
        private readonly StartupRegistration _startupRegistration;
        private readonly OverlayWindow _overlayWindow;
        private readonly HiddenMessageWindow _messageWindow;
        private readonly System.Windows.Forms.Timer _pushToTalkReleaseTimer;
        private HotkeyGesture _hotkeyGesture;
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private VoiceActivityTracker? _vadTracker;
        private bool _vadMonitoringEnabled;
        private bool _isShuttingDown;
        private SettingsForm? _settingsForm;
        private HistoryForm? _historyForm;

        private readonly WindowsTextOutput _textOutput;
        private readonly LatestResultStore _latestResult = new();
        private Task _recoveryTask = Task.CompletedTask;
        private DictationTarget? _dictationTarget;
        private CancellationTokenSource? _sessionCancellation;
        private AppSettings? _sessionSettings;
        private TranscriptionSession? _pipelineSession;
        private EventHandler<AudioChunkEventArgs>? _audioSubscription;
        private Task _sessionDisposal = Task.CompletedTask;
        private Task? _processingTask;
        private bool _shutdownDrained;
        private SessionMetrics? _sessionMetrics;
        private readonly IMetricsSink? _metricsSink = Environment.GetEnvironmentVariable("BISIK_LATENCY_TRACE") == "1"
            ? new BoundedMetricsFileSink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "latency.jsonl")) : null;
        private bool _processing;
        private bool _fastNextRecording;
        private readonly System.Windows.Forms.Timer _recordingLimitTimer = new() { Interval = 5 * 60 * 1000 };

        public BisikApplicationContext(bool showSettings = false)
        {
            _settingsManager = new SettingsManager();
            _settings = _settingsManager.Load();
            _httpClient = new HttpClient();
            _historyLogger = new HistoryLogger();
            _errorLogger = new ErrorLogger();
            _textOutput = new WindowsTextOutput(_errorLogger.Log);
            _audioRecorder = new AudioRecorder(_settings.Audio);
            _overlayWindow = new OverlayWindow();
            _messageWindow = CreateMessageWindow();
            _hotkeyManager = new HotkeyManager(new Win32HotkeyRegistrar(), _messageWindow.Handle);
            _hotkeyGesture = HotkeyManager.ParseHotkey(new AppSettings().Hotkey);
            _postProcessor = new LLMPostProcessor(_httpClient);
            _soundFeedback = new SoundFeedback(_settings.SoundEnabled);
            _transcriptionService = new TranscriptionService(
                providers: BuildProviderMap(_httpClient, _settings),
                postProcessor: _postProcessor,
                historyLogger: _historyLogger,
                errorLogger: _errorLogger,
                playSuccess: _soundFeedback.PlaySuccess,
                playError: _soundFeedback.PlayError);
            _transcriptionService.PhaseChanged += OnTranscriptionPhaseChanged;
            _outputHandler = new OutputHandler(_textOutput, HandlePopupOutput);
            _startupRegistration = new StartupRegistration();
            _pushToTalkReleaseTimer = new System.Windows.Forms.Timer
            {
                Interval = 35,
            };
            _pushToTalkReleaseTimer.Tick += async (_, _) => await CheckPushToTalkReleaseAsync();
            _audioRecorder.AudioLevelChanged += (_, args) => OnUi(() =>
            {
                _overlayWindow.SetLevel(args.Level);
                if (_audioRecorder.State == RecordingState.Recording)
                    HandleVadLevelSample(args.Level, args.ElapsedMilliseconds);
            });
            _audioRecorder.RecordingFailed += (_, args) => OnUi(() => HandleCaptureFailure(args.Exception));
            _recordingLimitTimer.Tick += async (_, _) => await CompleteRecordingAndTranscriptionAsync();
            _messageWindow.HotkeyPressed += async (_, hotkeyId) =>
            {
                if (hotkeyId == MainHotkeyId)
                {
                    await HandleRegisteredHotkeyAsync();
                }
                else if (hotkeyId == InsertLatestHotkeyId) await InsertLatestAsync();
                else if (hotkeyId == FastOutputHotkeyId) await UseFastOutputAsync();
            };

            _trayIcon = new TrayIcon(
                onOpenSettings: ShowSettings,
                onOpenHistory: ShowHistory,
                onShowAbout: static () => MessageBox.Show(
                    "Bisik",
                    "About Bisik",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information),
                onExit: ExitThread,
                onLanguageSelected: language => SaveQuickSettings(_settings with { DefaultLanguage = language }),
                onTogglePostProcessor: enabled => SaveQuickSettings(_settings with
                {
                    PostProcessor = _settings.PostProcessor with { Enabled = enabled },
                }));
            _trayIcon.SetSelectedLanguage(_settings.DefaultLanguage);
            _trayIcon.AddResultCommands(() => _ = InsertLatestAsync(), () =>
            {
                if (_latestResult.Latest is { } pending) _latestResult.Dismiss(pending.Id);
                ShowPendingState();
            });
            _trayIcon.SetPostProcessorEnabled(_settings.PostProcessor.Enabled);
            RegisterConfiguredHotkey();
            _messageWindow.SettingsRequested += (_, _) => ShowSettings();
            if (showSettings) _messageWindow.BeginInvoke(ShowSettings);

            lock (GlobalExceptionHandlerLock)
            {
                _runtimeExceptionHandler = HandleRuntimeException;
            }
        }

        private void ShowSettings()
        {
            try
            {
                if (_sessionCancellation is not null || _vadMonitoringEnabled)
                {
                    _trayIcon.ShowBalloon("Bisik", "Stop or cancel dictation before opening Settings.");
                    return;
                }
                if (_settingsForm is null || _settingsForm.IsDisposed)
                {
                    _settingsForm = new SettingsForm(
                        audioRecorder: _audioRecorder,
                        transcriptionService: _transcriptionService,
                        settingsManager: _settingsManager,
                        serviceFactory: CreateTranscriptionService);
                    // React immediately to both Apply and OK so changes like deployment name,
                    // hotkey, endpoint, etc. take effect without requiring the form to close.
                    _settingsForm.SettingsApplied += ApplySettingsAndRebuildProviders;
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
            catch (Exception exception)
            {
                HandleRuntimeException(exception, "Settings window");
            }
        }

        private TranscriptionService CreateTranscriptionService(AppSettings settings)
        {
            return new TranscriptionService(BuildProviderMap(_httpClient, settings), _postProcessor,
                _historyLogger, _errorLogger, _soundFeedback.PlaySuccess, _soundFeedback.PlayError);
        }

        private void RegisterConfiguredHotkey()
        {
            try
            {
                _hotkeyGesture = HotkeyManager.ParseHotkey(_settings.Hotkey);
                if (!_hotkeyManager.Register(MainHotkeyId, _hotkeyGesture))
                    throw new InvalidOperationException("The hotkey is already registered by another application.");
                if (!_hotkeyManager.Register(InsertLatestHotkeyId, HotkeyManager.ParseHotkey(_settings.InsertLatestHotkey)))
                    throw new InvalidOperationException("Insert latest result hotkey is unavailable. Choose another binding in Settings.");
                if (!_hotkeyManager.Register(FastOutputHotkeyId, HotkeyManager.ParseHotkey(_settings.FastOutputHotkey)))
                    throw new InvalidOperationException("Fast output hotkey is unavailable. Choose another binding in Settings.");
            }
            catch (Exception exception)
            {
                _errorLogger.Log(exception);
                _trayIcon.ShowBalloon("Hotkey unavailable", "Open Settings and choose another hotkey. " + exception.Message, ToolTipIcon.Warning);
            }
        }

        private void ApplySettingsAndRebuildProviders(AppSettings fresh)
        {
            var gesture = HotkeyManager.ParseHotkey(fresh.Hotkey);
            _hotkeyManager.UnregisterAll();
            try
            {
                if (!_hotkeyManager.Register(MainHotkeyId, gesture))
                    throw new InvalidOperationException("The hotkey is already registered by another application.");
                if (!_hotkeyManager.Register(InsertLatestHotkeyId, HotkeyManager.ParseHotkey(fresh.InsertLatestHotkey)))
                    throw new InvalidOperationException("Insert latest result hotkey is already registered by another application.");
                if (!_hotkeyManager.Register(FastOutputHotkeyId, HotkeyManager.ParseHotkey(fresh.FastOutputHotkey)))
                    throw new InvalidOperationException("Fast output hotkey is already registered by another application.");
            }
            catch
            {
                _hotkeyManager.UnregisterAll();
                _hotkeyManager.Register(MainHotkeyId, _hotkeyGesture);
                _hotkeyManager.Register(InsertLatestHotkeyId, HotkeyManager.ParseHotkey(_settings.InsertLatestHotkey));
                _hotkeyManager.Register(FastOutputHotkeyId, HotkeyManager.ParseHotkey(_settings.FastOutputHotkey));
                throw;
            }
            // Only an explicit change in Settings touches startup registration.
            if (fresh.LaunchAtStartup != _settings.LaunchAtStartup)
                _startupRegistration.SetEnabled(fresh.LaunchAtStartup, Application.ExecutablePath);
            _settings = fresh;
            _hotkeyGesture = gesture;
            _soundFeedback.SetEnabled(fresh.SoundEnabled);
            _transcriptionService.PhaseChanged -= OnTranscriptionPhaseChanged;
            _transcriptionService = CreateTranscriptionService(fresh);
            _transcriptionService.PhaseChanged += OnTranscriptionPhaseChanged;
            _trayIcon.SetSelectedLanguage(fresh.DefaultLanguage);
            _trayIcon.SetPostProcessorEnabled(fresh.PostProcessor.Enabled);
        }

        private void SaveQuickSettings(AppSettings fresh)
        {
            if (_settings == fresh) return;
            try
            {
                _settingsManager.Save(fresh);
                _settings = fresh;
            }
            catch (Exception exception) { HandleRuntimeException(exception, "Settings"); }
        }

        private void ShowHistory()
        {
            try
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
            catch (Exception exception)
            {
                HandleRuntimeException(exception, "History window");
            }
        }

        protected override void ExitThreadCore()
        {
            if (!_shutdownDrained)
            {
                if (!_isShuttingDown) _ = DrainShutdownAsync();
                return;
            }
            _isShuttingDown = true;
            _shutdownCancellation.Cancel();
            _settingsForm?.Close();
            _historyForm?.Close();
            _pushToTalkReleaseTimer.Stop();
            _vadMonitoringEnabled = false;
            _vadTracker = null;
            _audioRecorder.StopVoiceActivityMonitoring();
            _sessionCancellation?.Cancel();
            _dictationTarget?.Dispose();
            _textOutput.Dispose();
            _recordingLimitTimer.Dispose();
            _pushToTalkReleaseTimer.Dispose();
            _hotkeyManager.UnregisterAll();
            _audioRecorder.Dispose();
            _overlayWindow.Dispose();
            _httpClient.Dispose();
            _shutdownCancellation.Dispose();
            _messageWindow.Dispose();
            _trayIcon.Dispose();
            lock (GlobalExceptionHandlerLock)
            {
                if (_runtimeExceptionHandler == HandleRuntimeException)
                {
                    _runtimeExceptionHandler = null;
                }
            }
            base.ExitThreadCore();
        }

        private async Task DrainShutdownAsync()
        {
            _isShuttingDown = true;
            _shutdownCancellation.Cancel();
            _sessionCancellation?.Cancel();
            _pipelineSession?.Cancel();
            try
            {
                if (_processing && _processingTask is not null) await _processingTask;
                else { _audioRecorder.AbortRecording(); ResetSession(); }
                await _sessionDisposal;
                await _recoveryTask;
                await _textOutput.DrainAsync();
                _latestResult.Clear();
                await _soundFeedback.DisposeAsync();
            }
            catch (Exception exception) { _errorLogger.Log(exception); }
            finally { _shutdownDrained = true; ExitThread(); }
        }

        private void HandleRuntimeException(Exception exception, string source)
        {
            if (_messageWindow.InvokeRequired)
            {
                OnUi(() => HandleRuntimeException(exception, source));
                return;
            }
            try
            {
                _errorLogger.Log(exception);
            }
            catch
            {
                // Avoid cascading failures if the error log path itself is unavailable.
            }

            try
            {
                _overlayWindow.HideOverlay();
                _trayIcon.SetState(TrayIconState.Idle);
                _trayIcon.ShowBalloon(
                    title: "Bisik Error",
                    message: string.IsNullOrWhiteSpace(exception.Message)
                        ? $"An unhandled {source} error occurred."
                        : exception.Message,
                    icon: ToolTipIcon.Error);
            }
            catch
            {
                // Best effort only; logging is the primary fallback.
            }
        }

        private async Task HandleRegisteredHotkeyAsync()
        {
            if (_isShuttingDown || _settingsForm is { Visible: true }) return;
            try
            {
                if (_processing)
                {
                    if (_settings.TriggerMode == TriggerMode.Vad) _vadMonitoringEnabled = false;
                    _sessionCancellation?.Cancel();
                    return;
                }
                var action = _hotkeyManager.HandleKeyDown(_settings.TriggerMode);
                switch (action)
                {
                    case HotkeyAction.StartRecording:
                        StartRecording();
                        break;
                    case HotkeyAction.StopRecordingAndTranscribe:
                        await CompleteRecordingAndTranscriptionAsync();
                        break;
                    case HotkeyAction.StartVoiceActivityDetection:
                        _vadMonitoringEnabled = true;
                        StartVadMonitoring();
                        _trayIcon.ShowBalloon("Bisik listening", "Speech starts dictation. Press the hotkey again to stop listening.");
                        break;
                    case HotkeyAction.StopVoiceActivityDetection:
                        _vadMonitoringEnabled = false;
                        _audioRecorder.StopVoiceActivityMonitoring();
                        if (_audioRecorder.State == RecordingState.Recording)
                            await CompleteRecordingAndTranscriptionAsync(false);
                        break;
                }
            }
            catch (Exception exception) { HandleCaptureFailure(exception); }
        }

        private void StartRecording()
        {
            if (_sessionCancellation is not null || _processing) return;
            // Capture the field before any overlay or asynchronous work can change focus.
            _dictationTarget = _textOutput.CaptureTarget();
            _sessionSettings = _settings.Snapshot();
            _sessionMetrics = new SessionMetrics(_settings.Provider.Type.ToString(), _metricsSink);
            _sessionMetrics.Mark(TimingStage.CaptureStart);
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
            using var scope = _sessionMetrics.Activate();
            _pipelineSession = _transcriptionService.CreateSession(_sessionSettings, _sessionCancellation.Token);
            if (_fastNextRecording) { _pipelineSession.SkipCleanup(); _fastNextRecording = false; }
            var session = _pipelineSession;
            _audioSubscription = (_, chunk) => session.PushAudio(chunk);
            _audioRecorder.AudioChunkAvailable += _audioSubscription;
            _audioRecorder.UpdateSettings(_sessionSettings.Audio);
            _audioRecorder.StartRecording();
            // Listening starts before any network setup, including when VAD pre-roll is emitted.
            _ = session.StartAsync(); // Owned/observed by CompleteAsync and DisposeAsync.
            ShowRecordingIndicator();
            _trayIcon.SetState(TrayIconState.Recording);
            _recordingLimitTimer.Start();
            if (_sessionSettings.TriggerMode == TriggerMode.PushToTalk) _pushToTalkReleaseTimer.Start();
        }

        private async Task CheckPushToTalkReleaseAsync()
        {
            if (_isShuttingDown || IsGestureStillPressed(_hotkeyGesture)) return;
            _pushToTalkReleaseTimer.Stop();
            if (_hotkeyManager.HandleKeyUp(_settings.TriggerMode) == HotkeyAction.StopRecordingAndTranscribe)
                await CompleteRecordingAndTranscriptionAsync();
        }

        private void HandleCaptureFailure(Exception exception)
        {
            if (_processing)
            {
                _errorLogger.Log(exception);
                _sessionCancellation?.Cancel();
                return;
            }
            _vadMonitoringEnabled = false;
            try
            {
                _audioRecorder.StopVoiceActivityMonitoring();
                _audioRecorder.AbortRecording();
            }
            catch (Exception cleanupException) { _errorLogger.Log(cleanupException); }
            finally { ResetSession(); }
            HandleRuntimeException(exception, "Microphone");
        }

        private void ResetSession()
        {
            _fastNextRecording = false;
            if (_audioSubscription is not null) _audioRecorder.AudioChunkAvailable -= _audioSubscription;
            _audioSubscription = null;
            if (_pipelineSession is { } session)
            {
                _pipelineSession = null;
                _sessionDisposal = DisposeSessionAsync(session, _sessionDisposal);
            }
            _recordingLimitTimer.Stop();
            _pushToTalkReleaseTimer.Stop();
            _dictationTarget?.Dispose();
            _dictationTarget = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _sessionSettings = null;
            _hotkeyManager.ResetState(_vadMonitoringEnabled);
            _processing = false;
            if (!_isShuttingDown)
            {
                ShowPendingState();
            }
        }

        private void ShowPendingState()
        {
            if (_latestResult.Latest is not null)
            {
                _trayIcon.SetState(TrayIconState.Ready);
                _overlayWindow.ShowProcessingOverlay(OverlayProcessingPhase.Ready);
            }
            else { _overlayWindow.HideOverlay(); _trayIcon.SetState(TrayIconState.Idle); }
        }

        private Task InsertLatestAsync()
        {
            if (_isShuttingDown || _settingsForm is { Visible: true }) return Task.CompletedTask;
            var claim = _latestResult.Claim();
            if (claim is null) return Task.CompletedTask;
            return _recoveryTask = InsertLatestCoreAsync(claim);
        }

        private async Task UseFastOutputAsync()
        {
            if (_isShuttingDown || _settingsForm is { Visible: true }) return;
            if (_processing)
            {
                if (_pipelineSession?.HasFinalTranscript == true) _pipelineSession.UseTranscriptNow();
                return;
            }
            if (_pipelineSession is null)
            {
                _fastNextRecording = true;
                await HandleRegisteredHotkeyAsync();
                return;
            }
            _pipelineSession.SkipCleanup();
            if (_audioRecorder.State == RecordingState.Recording) await CompleteRecordingAndTranscriptionAsync();
        }

        private async Task InsertLatestCoreAsync(PendingResult claim)
        {
            var dispatched = false;
            try
            {
                using var target = _textOutput.CaptureTarget();
                var output = await _outputHandler.DispatchAsync(claim.Result, OutputMode.AutoType,
                    _settings.AlwaysCopyToClipboard, target, _shutdownCancellation.Token);
                dispatched = output.Disposition == OutputDisposition.Inserted;
                if (!string.IsNullOrEmpty(output.Message)) _trayIcon.ShowBalloon("Bisik", output.Message);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _errorLogger.Log(exception); }
            finally
            {
                _latestResult.CompleteClaim(claim.Id, dispatched);
                if (!_isShuttingDown && !_processing) ShowPendingState();
            }
        }

        private async Task DisposeSessionAsync(TranscriptionSession session, Task previous)
        {
            try { await previous; await session.DisposeAsync(); }
            catch (Exception exception) { _errorLogger.Log(exception); }
        }

        private void OnUi(Action action)
        {
            if (_isShuttingDown || _messageWindow.IsDisposed) return;
            if (_messageWindow.InvokeRequired) _messageWindow.BeginInvoke(action);
            else action();
        }

        private void OnTranscriptionPhaseChanged(ProcessingPhase phase)
        {
            if (_isShuttingDown)
            {
                return;
            }

            switch (phase)
            {
                case ProcessingPhase.Transcribing:
                    _trayIcon.SetState(TrayIconState.Transcribing);
                    _overlayWindow.ShowProcessingOverlay(OverlayProcessingPhase.Transcribing);
                    break;

                case ProcessingPhase.PostProcessing:
                    _trayIcon.SetState(TrayIconState.PostProcessing);
                    _overlayWindow.ShowProcessingOverlay(OverlayProcessingPhase.PostProcessing);
                    break;

                case ProcessingPhase.None:
                    _overlayWindow.HideOverlay();
                    _trayIcon.SetState(TrayIconState.Idle);
                    break;
            }
        }

        private Task CompleteRecordingAndTranscriptionAsync(bool restartVadMonitoring = true)
        {
            if (_processing || _sessionCancellation is null || _audioRecorder.State != RecordingState.Recording) return Task.CompletedTask;
            _processing = true;
            return _processingTask = CompleteRecordingCoreAsync(restartVadMonitoring);
        }

        private async Task CompleteRecordingCoreAsync(bool restartVadMonitoring)
        {
            _recordingLimitTimer.Stop();
            _pushToTalkReleaseTimer.Stop();
            var settings = _sessionSettings!;
            var cancellation = _sessionCancellation!;
            var metrics = _sessionMetrics!;
            using var metricsScope = metrics.Activate();
            metrics.Mark(TimingStage.StopRequested);
            cancellation.CancelAfter(TimeSpan.FromMinutes(2));
            _pipelineSession!.RequestStop();
            RecordedAudio? audio = null;
            CapturedRecording? captured = null;
            Task<RecordedAudio>? persistence = null;
            var retainAudio = false;
            try
            {
                _overlayWindow.ShowProcessingOverlay(OverlayProcessingPhase.Transcribing);
                _trayIcon.SetState(TrayIconState.Transcribing);
                captured = await _audioRecorder.DrainCaptureAsync(cancellation.Token);
                persistence = captured.PersistAsync(cancellation.Token);
                using (var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(captured.WavData)))
                    metrics.AudioDurationMs = reader.TotalTime.TotalMilliseconds;
                var result = await _pipelineSession!.CompleteAsync(captured, persistence);
                retainAudio = settings.HistoryEnabled && result.Succeeded && !string.IsNullOrWhiteSpace(result.RawText);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!result.Succeeded)
                {
                    _trayIcon.ShowBalloon("Transcription failed", result.ErrorMessage, ToolTipIcon.Error);
                    return;
                }
                if (string.IsNullOrWhiteSpace(result.OutputText))
                {
                    _trayIcon.ShowBalloon("Bisik", "No speech was recognized.");
                    return;
                }
                var pending = _latestResult.Publish(result);
                var claim = _latestResult.Claim(pending.Id);
                if (claim is null) return;
                OutputDispatchResult output;
                var dispatched = false;
                try
                {
                    output = await _outputHandler.DispatchAsync(result, settings.OutputMode,
                        settings.AlwaysCopyToClipboard, _dictationTarget, cancellation.Token);
                    dispatched = output.Disposition == OutputDisposition.Inserted;
                }
                finally { _latestResult.CompleteClaim(claim.Id, dispatched); }
                _transcriptionService.NotifyDeliveryCompleted();
                if (!string.IsNullOrEmpty(output.Message)) _trayIcon.ShowBalloon("Bisik", output.Message);
                metrics.Publish(output.Disposition switch
                {
                    OutputDisposition.Inserted => SessionOutcome.PasteDispatched,
                    OutputDisposition.Copied => SessionOutcome.Copied,
                    _ => SessionOutcome.NeedsReview,
                });
            }
            catch (OperationCanceledException)
            {
                if (!_isShuttingDown) _trayIcon.ShowBalloon("Bisik", "Dictation cancelled or timed out. No text was inserted.");
            }
            catch (Exception exception) { HandleRuntimeException(exception, "Dictation"); }
            finally
            {
                if (persistence is not null)
                {
                    try { audio = await persistence; }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { _errorLogger.Log(exception); }
                }
                if (!retainAudio && captured is not null)
                {
                    try { File.Delete(captured.FilePath); }
                    catch (Exception exception) { _errorLogger.Log(exception); }
                }
                ResetSession();
                if (!_isShuttingDown && restartVadMonitoring && _vadMonitoringEnabled)
                {
                    try { StartVadMonitoring(); }
                    catch (Exception exception) { HandleCaptureFailure(exception); }
                }
            }
        }

        private void HandleVadLevelSample(double level, int elapsedMs)
        {
            if (!_vadMonitoringEnabled || _processing) return;
            _vadTracker ??= new VoiceActivityTracker(_settings.Audio);
            var action = _vadTracker.ProcessLevel(level, elapsedMs);
            if (action == VoiceActivityAction.StartRecording)
            {
                try
                {
                    _audioRecorder.StopVoiceActivityMonitoring(preserveAudio: true);
                    StartRecording();
                }
                catch (Exception exception) { HandleCaptureFailure(exception); }
            }
            else if (action == VoiceActivityAction.StopRecording)
                _ = CompleteRecordingAndTranscriptionAsync();
        }

        private void StartVadMonitoring()
        {
            if (!_vadMonitoringEnabled || _isShuttingDown) return;
            _vadTracker = new VoiceActivityTracker(_settings.Audio);
            _audioRecorder.UpdateSettings(_settings.Audio);
            _audioRecorder.StartVoiceActivityMonitoring((level, elapsed) => OnUi(() => HandleVadLevelSample(level, elapsed)));
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

        private void HandlePopupOutput(string text)
        {
            var id = _latestResult.ClaimedId;
            using var popup = new ReviewPopupForm(text);
            popup.ShowDialog();
            if (id is { } claimed) _latestResult.Dismiss(claimed);
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
            _ = form.Handle;
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

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class HiddenMessageWindow : Form
    {
        private const int WmHotkey = 0x0312;

        public event EventHandler<int>? HotkeyPressed;
        public event EventHandler? SettingsRequested;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey)
            {
                HotkeyPressed?.Invoke(this, message.WParam.ToInt32());
            }
            else if (message.Msg == ShowSettingsMessage)
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            }

            base.WndProc(ref message);
        }
    }
}
