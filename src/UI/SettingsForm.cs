using System.Collections.Immutable;
using Bisik.Models;
using Bisik.Core;

namespace Bisik.UI;

public sealed class SettingsForm : Form
{
    /// <summary>Raised when settings are saved (via Apply or OK).</summary>
    public event Action<AppSettings>? SettingsApplied;

    private readonly TabControl _tabs;
    private readonly ToolTip _helpToolTip = new()
    {
        OwnerDraw = true,
        ShowAlways = true,
        InitialDelay = 350,
        ReshowDelay = 100,
        AutoPopDelay = 30000,
    };
    private readonly Dictionary<ProviderType, Panel> _providerPanels = [];
    private readonly Dictionary<ProviderType, Dictionary<string, TextBox>> _providerFieldInputs = [];
    private readonly SettingsManager _settingsManager;
    private readonly AudioRecorder? _audioRecorder;
    private readonly TranscriptionService? _transcriptionService;
    private readonly Func<AppSettings, TranscriptionService>? _serviceFactory;
    private AppSettings _loadedSettings = new();
    private CancellationTokenSource? _connectionTestCancellation;
    private CheckBox? _azureRealtimeCheckBox;
    private CheckBox? _liveCaptureCheckBox;
    private ComboBox? _transcriptionDelayComboBox;
    private CheckBox? _pauseCommitsCheckBox;
    private ComboBox? _reasoningEffortComboBox;
    private TextBox? _insertLatestHotkeyTextBox;
    private TextBox? _fastOutputHotkeyTextBox;
    private CheckBox? _prewarmRealtimeCheckBox;
    private NumericUpDown? _prewarmIdleSecondsInput;
    private TextBox? _verifiedReasoningEffortsTextBox;
    private CheckBox? _speculativeCleanupCheckBox;
    private CheckBox? _launchAtStartupCheckBox;
    private ComboBox? _triggerModeComboBox;
    private TextBox? _hotkeyTextBox;
    private ComboBox? _outputModeComboBox;
    private ComboBox? _indicatorStyleComboBox;
    private ComboBox? _providerSelectorComboBox;
    private ComboBox? _microphoneSelectorComboBox;
    private ComboBox? _audioFormatComboBox;
    private NumericUpDown? _sampleRateInput;
    private NumericUpDown? _silenceThresholdInput;
    private NumericUpDown? _minSpeechInput;
    private ComboBox? _defaultLanguageComboBox;
    private TextBox? _quickLanguagesTextBox;
    private CheckBox? _alwaysCopyToClipboardCheckBox;
    private CheckBox? _historyEnabledCheckBox;
    private CheckBox? _historyRecordingDurationCheckBox;
    private CheckBox? _historyConfigurationCheckBox;
    private ComboBox? _postProcessorProviderComboBox;
    private CheckBox? _postProcessorEnabledCheckBox;
    private ComboBox? _postProcessorPresetComboBox;
    private TextBox? _postProcessorEndpointTextBox;
    private TextBox? _postProcessorApiKeyTextBox;
    private TextBox? _postProcessorDeploymentNameTextBox;
    private TextBox? _postProcessorModelTextBox;
    private TextBox? _postProcessorTargetLanguageTextBox;
    private TextBox? _postProcessorSystemPromptTextBox;
    private TextBox? _vocabularyName;
    private TextBox? _vocabularyLines;
    private TextBox? _recognitionPrompt;
    private TextBox? _recognitionKeywords;
    private TextBox? _recognitionLanguage;
    private TextBox? _recognitionFields;
    private Button? _testConnectionButton;
    private Button? _applyButton;
    private Button? _okButton;
    private Button? _cancelButton;

    public SettingsForm()
        : this(audioRecorder: null, transcriptionService: null, settingsManager: null)
    {
    }

    public SettingsForm(AudioRecorder? audioRecorder, TranscriptionService? transcriptionService, SettingsManager? settingsManager = null, Func<AppSettings, TranscriptionService>? serviceFactory = null)
    {
        _settingsManager = settingsManager ?? new SettingsManager();
        _audioRecorder = audioRecorder;
        _transcriptionService = transcriptionService;
        _serviceFactory = serviceFactory;
        FormClosed += (_, _) => _connectionTestCancellation?.Cancel();
        Text = "Bisik Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(860, 640);
        _helpToolTip.Popup += HandleHelpPopup;
        _helpToolTip.Draw += HandleHelpDraw;

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
        };

        _tabs.TabPages.Add(CreateGeneralTabPage());
        _tabs.TabPages.Add(CreateProviderTabPage());
        _tabs.TabPages.Add(CreateAudioTabPage());
        _tabs.TabPages.Add(CreateLanguageTabPage());
        _tabs.TabPages.Add(CreateHistoryTabPage());
        _tabs.TabPages.Add(CreatePostProcessorTabPage());
        var hints = CreateTabPage("Recognition context");
        var hintsLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        _recognitionPrompt = new TextBox { Multiline = true, Width = 360, Height = 90 };
        _recognitionKeywords = new TextBox { Width = 360 };
        _recognitionLanguage = new TextBox { Width = 200 };
        _recognitionFields = new TextBox { Width = 360 };
        var hintControls = new[] { _recognitionPrompt, _recognitionKeywords, _recognitionLanguage, _recognitionFields };
        var hintNames = new[] { "Optional domain prompt", "Keywords (comma separated)", "Expected language", "Verified fields (Prompt,Keywords,Language,Delay)" };
        for (var i = 0; i < hintControls.Length; i++) { hintsLayout.Controls.Add(CreateFieldLabel(hintNames[i], "Optional bounded domain hints for the selected recognition connection. Verified fields require exact model/API contract evidence. Defaults omit all hints. Never paste a full reference script. Realtime transport alone does not establish field support."), 0, i); hintsLayout.Controls.Add(hintControls[i], 1, i); }
        hints.Controls.Add(hintsLayout); _tabs.TabPages.Add(hints);

        Controls.Add(_tabs);
        Controls.Add(CreateBottomButtonPanel());
        LoadSettingsIntoForm(_settingsManager.Load());
    }

    public TabControl Tabs => _tabs;

    private static TabPage CreateTabPage(string title)
    {
        return new TabPage(title)
        {
            Padding = new Padding(12),
            AutoScroll = true,
        };
    }

    private TabPage CreateGeneralTabPage()
    {
        var page = CreateTabPage("General");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _launchAtStartupCheckBox = new CheckBox
        {
            Text = "Launch Bisik at Windows startup",
            AutoSize = true,
            Checked = new AppSettings().LaunchAtStartup,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8),
        };
        layout.Controls.Add(CreateFieldLabel("Launch at startup",
            "Start Bisik in the system tray when you sign in to Windows. Apply or OK saves this preference; turn it off to launch Bisik manually."), 0, 0);
        layout.Controls.Add(_launchAtStartupCheckBox, 1, 0);

        _triggerModeComboBox = CreateEnumComboBox<TriggerMode>(
            new Dictionary<TriggerMode, string>
            {
                [TriggerMode.PushToTalk] = "Push-to-talk",
                [TriggerMode.Toggle] = "Toggle",
                [TriggerMode.Vad] = "Voice Activity Detection",
            },
            new AppSettings().TriggerMode);
        layout.Controls.Add(CreateFieldLabel("Trigger mode",
            "Toggle: press the recording hotkey once to start and again to stop.\n\nPush-to-talk: hold the hotkey while speaking, then release it to stop.\n\nVoice Activity Detection (VAD): the hotkey enables listening; speech starts recording and silence stops it. Press again to stop listening. Tune detection in Audio."), 0, 1);
        layout.Controls.Add(_triggerModeComboBox, 1, 1);

        _hotkeyTextBox = new TextBox
        {
            Text = new AppSettings().Hotkey,
            Dock = DockStyle.Left,
            Width = 220,
            ReadOnly = true,
            ShortcutsEnabled = false,
        };
        _hotkeyTextBox.PreviewKeyDown += HandleHotkeyPreviewKeyDown;
        _hotkeyTextBox.KeyDown += HandleHotkeyKeyDown;
        _insertLatestHotkeyTextBox = new TextBox { Text = new AppSettings().InsertLatestHotkey, Width = 220, ReadOnly = true, ShortcutsEnabled = false };
        _insertLatestHotkeyTextBox.PreviewKeyDown += HandleHotkeyPreviewKeyDown;
        _insertLatestHotkeyTextBox.KeyDown += HandleHotkeyKeyDown;
        layout.Controls.Add(CreateFieldLabel("Insert latest hotkey",
            "Insert the latest completed result retained in memory into the field currently selected. Use this to recover a result when the original target changed or insertion could not be verified.\n\nClick the field and press your preferred key combination. The destination is checked before pasting; nothing is submitted automatically."), 0, 7);
        layout.Controls.Add(_insertLatestHotkeyTextBox, 1, 7);
        _fastOutputHotkeyTextBox = new TextBox { Text = new AppSettings().FastOutputHotkey, Width = 220, ReadOnly = true, ShortcutsEnabled = false };
        _fastOutputHotkeyTextBox.PreviewKeyDown += HandleHotkeyPreviewKeyDown;
        _fastOutputHotkeyTextBox.KeyDown += HandleHotkeyKeyDown;
        layout.Controls.Add(CreateFieldLabel("Fast output hotkey",
            "Start a recording that delivers the final raw transcript without waiting for optional LLM cleanup. During recording or processing, this shortcut selects fast output for that session. In VAD mode it applies to the next detected recording.\n\nClick the field and press your preferred key combination. This shortcut does not change your saved post-processing preference."), 0, 8);
        layout.Controls.Add(_fastOutputHotkeyTextBox, 1, 8);
        layout.Controls.Add(CreateFieldLabel("Hotkey",
            "Click the field, then press the key combination you want to use for recording. Apply or OK activates the binding and reports registration conflicts.\n\nThe default is Ctrl+Shift+Space. Windows may intercept combinations containing the Win key. While transcription is processing, the recording hotkey cancels it."), 0, 2);
        layout.Controls.Add(_hotkeyTextBox, 1, 2);

        _outputModeComboBox = CreateEnumComboBox<OutputMode>(
            new Dictionary<OutputMode, string>
            {
                [OutputMode.AutoType] = "Insert final text",
                [OutputMode.RealtimeAutoType] = "Insert final text (legacy realtime setting)",
                [OutputMode.Clipboard] = "Clipboard",
                [OutputMode.Popup] = "Popup",
            },
            new AppSettings().OutputMode);
        layout.Controls.Add(CreateFieldLabel("Output mode",
            "Insert final text: paste the completed result into the original field after checking focus, selection and released modifier keys. If the target cannot be verified, recover the result from Ready or copy it manually.\n\nClipboard: copy the result for manual paste.\n\nPopup: show the result in a review window.\n\nThe legacy realtime option also inserts only final text. Bisik never inserts partials or automatically submits text."), 0, 3);
        layout.Controls.Add(_outputModeComboBox, 1, 3);

        _indicatorStyleComboBox = CreateEnumComboBox<IndicatorStyle>(
            new Dictionary<IndicatorStyle, string>
            {
                [IndicatorStyle.DotNearCursor] = "Dot near cursor",
                [IndicatorStyle.BarOverlay] = "Bar overlay",
                [IndicatorStyle.TrayIconOnly] = "Tray icon only",
            },
            new AppSettings().IndicatorStyle);
        layout.Controls.Add(CreateFieldLabel("Recording indicator",
            "Choose how Bisik shows recording and processing activity.\n\nDot near cursor follows the mouse. Bar overlay displays a level indicator on the current screen. Tray icon only keeps the on-screen recording overlay hidden. Indicators do not take focus away from the field you are dictating into."), 0, 4);
        layout.Controls.Add(_indicatorStyleComboBox, 1, 4);

        _alwaysCopyToClipboardCheckBox = new CheckBox
        {
            Text = "Always copy transcription result to clipboard",
            AutoSize = true,
            Checked = new AppSettings().AlwaysCopyToClipboard,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8),
        };
        layout.Controls.Add(CreateFieldLabel("Clipboard",
            "Keep the completed result on the clipboard after insertion. When off, Bisik restores the previous clipboard after paste if it still owns the temporary clipboard contents.\n\nA newer copy is never overwritten by restoration. Clipboard output mode still copies the result for manual paste. A sent paste does not prove the destination accepted it; review the inserted text."), 0, 5);
        layout.Controls.Add(_alwaysCopyToClipboardCheckBox, 1, 5);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage CreateProviderTabPage()
    {
        var page = CreateTabPage("Provider");
        var settings = new AppSettings();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var providerSelector = CreateEnumComboBox<ProviderType>(
            new Dictionary<ProviderType, string>
            {
                [ProviderType.AzureOpenAi] = "Azure OpenAI",
                [ProviderType.OpenAi] = "OpenAI",
                [ProviderType.OpenRouter] = "OpenRouter",
                [ProviderType.LocalWhisper] = "Local Whisper",
                [ProviderType.CustomEndpoint] = "Custom Endpoint",
            },
            settings.Provider.Type);
        providerSelector.SelectedIndexChanged += (_, _) =>
        {
            if (providerSelector.SelectedItem is ComboBoxItem<ProviderType> selected)
            {
                ShowProviderPanel(selected.Value);
            }
        };
        _providerSelectorComboBox = providerSelector;
        root.Controls.Add(CreateFieldLabel("Provider",
            "Choose the service that converts microphone audio to text. Azure OpenAI uses your Azure resource and deployment; OpenAI and OpenRouter use their hosted services. Local Whisper uses a server you run, and Custom Endpoint uses a compatible transcription URL.\n\nConfigure the selected provider's fields below. Only the selected provider is used for transcription."), 0, 0);
        root.Controls.Add(providerSelector, 1, 0);

        var fieldsHost = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 8, 0, 0),
        };
        var azurePanel = CreateProviderFieldsPanel(
            ProviderType.AzureOpenAi,
            ("Endpoint URL", settings.Provider.Endpoint),
            ("API Key", settings.Provider.ApiKey),
            ("Deployment Name", settings.Provider.DeploymentName),
            ("API Version", settings.Provider.ApiVersion),
            ("Realtime Calls Endpoint (optional)", GetExtraHeaderValue(settings.Provider, "Bisik-Realtime-Calls-Endpoint")));
        var openAiPanel = CreateProviderFieldsPanel(
            ProviderType.OpenAi,
            ("API Key", string.Empty),
            ("Model", "whisper-1"));
        var openRouterPanel = CreateProviderFieldsPanel(
            ProviderType.OpenRouter,
            ("API Key", string.Empty),
            ("Model", string.Empty));
        var localPanel = CreateProviderFieldsPanel(
            ProviderType.LocalWhisper,
            ("Base URL", "http://localhost:8080"),
            ("Model", string.Empty),
            ("API Key (optional)", string.Empty));
        var customPanel = CreateProviderFieldsPanel(
            ProviderType.CustomEndpoint,
            ("Base URL", string.Empty),
            ("API Key (optional)", string.Empty),
            ("Model (optional)", string.Empty),
            ("Extra Headers", string.Empty));

        _providerPanels[ProviderType.AzureOpenAi] = azurePanel;
        _providerPanels[ProviderType.OpenAi] = openAiPanel;
        _providerPanels[ProviderType.OpenRouter] = openRouterPanel;
        _providerPanels[ProviderType.LocalWhisper] = localPanel;
        _providerPanels[ProviderType.CustomEndpoint] = customPanel;

        foreach (var panel in _providerPanels.Values)
        {
            panel.Visible = false;
            fieldsHost.Controls.Add(panel);
        }

        root.Controls.Add(fieldsHost, 0, 1);
        root.SetColumnSpan(fieldsHost, 2);

        _azureRealtimeCheckBox = new CheckBox
        {
            Text = "Use Azure realtime transcription",
            AutoSize = true,
            Margin = new Padding(3, 8, 3, 3),
        };
        root.Controls.Add(CreateFieldLabel("Azure protocol",
            "Use Azure's realtime transcription protocol instead of a multipart audio upload. Enable this when your deployment supports realtime transcription but its name does not contain 'realtime'. Bisik also detects realtime deployment/model names automatically.\n\nThis applies only to Azure OpenAI. It does not enable live microphone streaming by itself; use Live capture for that."), 0, 2);
        root.Controls.Add(_azureRealtimeCheckBox, 1, 2);

        _testConnectionButton = new Button
        {
            Text = "Test Connection",
            AutoSize = true,
            Enabled = _audioRecorder is not null && _transcriptionService is not null,
        };
        _testConnectionButton.Click += HandleTestConnectionClickAsync;
        root.Controls.Add(CreateFieldLabel("Connection test",
            "Record 3 seconds from the selected microphone and transcribe it using the provider fields currently shown, without needing to save them first. Speak during the recording, then review the displayed result.\n\nThe test sends audio to the selected service and may incur API charges. It does not insert text, run LLM cleanup or retain a history recording. The button is unavailable if audio/transcription services are not connected."), 0, 3);
        root.Controls.Add(_testConnectionButton, 1, 3);
        _liveCaptureCheckBox = new CheckBox { Text = "Stream microphone audio while recording", AutoSize = true };
        root.Controls.Add(CreateFieldLabel("Live capture",
            "Experimental, off by default. Send microphone audio to Azure realtime while you speak, so some upload and processing can happen before Stop. Requires an Azure realtime provider.\n\nOnly the completed final transcript is output. If streaming fails, Bisik can fall back to the complete recording within the same processing deadline. Turn this off to use the completed-file path."), 0, 4);
        root.Controls.Add(_liveCaptureCheckBox, 1, 4);
        _transcriptionDelayComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _transcriptionDelayComboBox.Items.AddRange(new object[] { "Provider default", "minimal", "low", "medium", "high", "xhigh" });
        _transcriptionDelayComboBox.SelectedIndex = 0;
        root.Controls.Add(CreateFieldLabel("Realtime delay",
            "Request a transcription delay level from supported Azure realtime transcription models: minimal, low, medium, high or xhigh. Lower levels favor quicker updates; higher levels allow more processing time. Support and results depend on the model/API.\n\nProvider default omits the parameter entirely; it does not mean medium. This setting is used for supported live realtime sessions."), 0, 5);
        root.Controls.Add(_transcriptionDelayComboBox, 1, 5);
        _pauseCommitsCheckBox = new CheckBox { Text = "Commit phrases during pauses", AutoSize = true };
        root.Controls.Add(CreateFieldLabel("Pause commits",
            "Experimental, off by default. During live capture, send an audio commit when a pause is detected so the provider can finalize completed phrases before Stop. Requires Live capture and Azure realtime.\n\nThe VAD silence threshold helps identify pauses; live phrase commits use a 600 ms pause. Phrases are combined into one final result, never typed separately."), 0, 6);
        root.Controls.Add(_pauseCommitsCheckBox, 1, 6);
        _speculativeCleanupCheckBox = new CheckBox { Text = "Prepare whole-text cleanup during pauses", AutoSize = true };
        root.Controls.Add(CreateFieldLabel("Prepare cleanup",
            "Experimental, off by default. Start LLM cleanup of completed whole-text snapshots during live capture. Requires Live capture, a realtime provider and enabled post-processing; phrase commits can provide earlier snapshots.\n\nThis can make extra paid requests (at most two speculative requests plus required final cleanup). A prepared result is reused only if its transcript and settings exactly match the final result."), 0, 7);
        root.Controls.Add(_speculativeCleanupCheckBox, 1, 7);
        _prewarmRealtimeCheckBox = new CheckBox { Text = "Prewarm a realtime connection", AutoSize = true };
        root.Controls.Add(CreateFieldLabel("Prewarm connection",
            "Experimental, off by default. Prepare one fresh unused Azure realtime connection before recording to reduce connection setup time. Requires Live capture and a streaming-capable provider.\n\nPrewarming contacts the provider but does not capture microphone audio or send it. Recording claims the connection; expiry, settings changes, disconnect or shutdown discard an unused connection."), 0, 8);
        root.Controls.Add(_prewarmRealtimeCheckBox, 1, 8);
        _prewarmIdleSecondsInput = new NumericUpDown { Minimum = 5, Maximum = 60, Value = 15, Width = 90 };
        root.Controls.Add(CreateFieldLabel("Prewarm idle seconds",
            "How long an unused prewarmed connection may wait before it is closed: 5 to 60 seconds, default 15. A connection claimed for recording is no longer subject to this idle expiry.\n\nThis setting has an effect only when Prewarm connection and Live capture are enabled."), 0, 9);
        root.Controls.Add(_prewarmIdleSecondsInput, 1, 9);

        page.Controls.Add(root);
        ShowProviderPanel(settings.Provider.Type);
        return page;
    }

    private TabPage CreateAudioTabPage()
    {
        var page = CreateTabPage("Audio");
        var settings = new AppSettings().Audio;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _microphoneSelectorComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 280,
            Dock = DockStyle.Left,
        };
        var devices = _audioRecorder?.GetInputDevices() ?? [];
        if (devices.Count == 0)
        {
            _microphoneSelectorComboBox.Items.Add("Default microphone");
            _microphoneSelectorComboBox.SelectedIndex = 0;
        }
        else
        {
            foreach (var device in devices)
            {
                _microphoneSelectorComboBox.Items.Add(device.Name);
            }

            _microphoneSelectorComboBox.SelectedIndex = 0;
        }

        root.Controls.Add(CreateFieldLabel("Microphone",
            "Choose the input device used for dictation, VAD listening and Test Connection. Devices are listed when Settings opens. If no devices are available, the list shows Default microphone.\n\nChoose the device that receives your voice, rather than a playback/loopback source."), 0, 0);
        root.Controls.Add(_microphoneSelectorComboBox, 1, 0);

        _audioFormatComboBox = CreateEnumComboBox<AudioFormat>(
            new Dictionary<AudioFormat, string>
            {
                [AudioFormat.Wav] = "WAV",
                [AudioFormat.Mp3] = "MP3",
                [AudioFormat.Ogg] = "OGG",
            },
            settings.Format);
        root.Controls.Add(CreateFieldLabel("Upload format",
            "Choose the format used for recorded files and multipart uploads. WAV is recommended and preserves uncompressed audio. MP3 uses Windows Media Foundation encoding. OGG is listed but encoding is not implemented and recordings in that format will fail.\n\nAzure realtime sends PCM16 mono audio at 24 kHz regardless of the selected file format."), 0, 1);
        root.Controls.Add(_audioFormatComboBox, 1, 1);

        _sampleRateInput = new NumericUpDown
        {
            Minimum = 8000,
            Maximum = 48000,
            Increment = 1000,
            Value = settings.SampleRate,
            Width = 120,
            ThousandsSeparator = true,
        };
        var sampleRatePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        sampleRatePanel.Controls.Add(_sampleRateInput);
        sampleRatePanel.Controls.Add(new Label
        {
            Text = "Hz",
            AutoSize = true,
            Margin = new Padding(8, 8, 3, 3),
        });
        root.Controls.Add(CreateFieldLabel("Sample rate",
            "Microphone capture samples per second, in Hz. The range is 8,000 to 48,000; the default is 16,000 Hz, mono, 16-bit PCM. Higher rates increase audio size and may not improve speech recognition. Your device must support the chosen rate.\n\nAzure realtime converts captured audio to 24,000 Hz before transmission."), 0, 2);
        root.Controls.Add(sampleRatePanel, 1, 2);

        _silenceThresholdInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 1,
            DecimalPlaces = 3,
            Increment = 0.005m,
            Value = (decimal)settings.VadSilenceThreshold,
            Width = 120,
        };
        root.Controls.Add(CreateFieldLabel("VAD silence threshold",
            "Audio level above which Bisik treats the input as speech, from 0 to 1 (default 0.010). Used by VAD recording and live pause detection.\n\nRaise it if background noise triggers recording or prevents pauses. Lower it if quiet speech is missed. Tune it with the microphone you actually use."), 0, 3);
        root.Controls.Add(_silenceThresholdInput, 1, 3);

        _minSpeechInput = new NumericUpDown
        {
            Minimum = 50,
            Maximum = 5000,
            Increment = 50,
            Value = settings.VadMinSpeechMs,
            Width = 120,
        };
        var minSpeechPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        minSpeechPanel.Controls.Add(_minSpeechInput);
        minSpeechPanel.Controls.Add(new Label
        {
            Text = "ms",
            AutoSize = true,
            Margin = new Padding(8, 8, 3, 3),
        });
        root.Controls.Add(CreateFieldLabel("Min speech duration",
            "Milliseconds of continuous speech required to start a VAD recording, from 50 to 5,000 (default 300). The same duration of continuous silence stops a VAD recording.\n\nIncrease it to ignore brief noises and tolerate longer pauses; decrease it for quicker starts and stops. Live phrase commits have a separate fixed 600 ms pause."), 0, 4);
        root.Controls.Add(minSpeechPanel, 1, 4);

        page.Controls.Add(root);
        return page;
    }

    private TabPage CreateLanguageTabPage()
    {
        var page = CreateTabPage("Language");
        var settings = new AppSettings();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _defaultLanguageComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 280,
            Dock = DockStyle.Left,
        };
        foreach (var option in GetLanguageOptions())
        {
            _defaultLanguageComboBox.Items.Add(option);
        }

        var selectedLanguage = _defaultLanguageComboBox.Items
            .OfType<ComboBoxItem<string>>()
            .FirstOrDefault(item => string.Equals(item.Value, settings.DefaultLanguage, StringComparison.OrdinalIgnoreCase));
        if (selectedLanguage is not null)
        {
            _defaultLanguageComboBox.SelectedItem = selectedLanguage;
        }
        else if (_defaultLanguageComboBox.Items.Count > 0)
        {
            _defaultLanguageComboBox.SelectedIndex = 0;
        }

        root.Controls.Add(CreateFieldLabel("Default language",
            "Hint about the spoken language sent to the transcription provider. Auto-detect lets the provider identify it; a specific language can help when recognition is ambiguous.\n\nThis does not translate the result. Use a Post-Processor translation preset to change the output language. 'Other' is a placeholder; custom language entry is not implemented in this selector."), 0, 0);
        root.Controls.Add(_defaultLanguageComboBox, 1, 0);

        _quickLanguagesTextBox = new TextBox
        {
            Multiline = true,
            Width = 280,
            Height = 120,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, settings.QuickLanguages),
        };
        root.Controls.Add(CreateFieldLabel("Quick-switch languages",
            "One language code per line for the tray menu's quick-switch list. For example:\n\nen\nid\nauto\n\nUse codes supported by your transcription service. Selecting a tray language changes the spoken-language hint for subsequent recordings. Blank lines are ignored; an empty list restores the default quick languages."), 0, 1);
        root.Controls.Add(_quickLanguagesTextBox, 1, 1);

        page.Controls.Add(root);
        return page;
    }

    private TabPage CreateHistoryTabPage()
    {
        var page = CreateTabPage("History");
        var settings = new AppSettings();
        var historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Bisik",
            "history.jsonl");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _historyEnabledCheckBox = new CheckBox
        {
            Text = "Enable local history logging",
            AutoSize = true,
            Checked = settings.HistoryEnabled,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8),
        };
        root.Controls.Add(CreateFieldLabel("History logging",
            "Keep successful, nonempty dictations in local history, including the raw transcript, processed text, provider, language, duration and recorded audio. These files contain what you dictated and are not encrypted like API credentials.\n\nTurn this off to avoid retaining future dictations and recordings. Existing history is not removed. Failed/empty recordings and Test Connection recordings are deleted."), 0, 0);
        root.Controls.Add(_historyEnabledCheckBox, 1, 0);

        root.RowCount = 6;
        _historyRecordingDurationCheckBox = new CheckBox
        {
            Text = "Include recording duration",
            AutoSize = true,
            Checked = settings.HistoryRecordingDurationEnabled,
            Margin = new Padding(3, 8, 3, 8),
        };
        root.Controls.Add(CreateFieldLabel("Recording duration",
            "Save the captured audio length in milliseconds with each new history entry. Includes pauses, silence and VAD pre-roll; this is not a speech-only estimate. On by default.\n\nCompare Recording, Transcription, Post-Proc and Stop to result in History. Turning this off leaves the recording-duration field absent in future entries."), 0, 1);
        root.Controls.Add(_historyRecordingDurationCheckBox, 1, 1);
        _historyConfigurationCheckBox = new CheckBox
        {
            Text = "Include configuration snapshot",
            AutoSize = true,
            Checked = settings.HistoryConfigurationEnabled,
            Margin = new Padding(3, 8, 3, 8),
        };
        root.Controls.Add(CreateFieldLabel("Configuration snapshot",
            "Save the settings used for each new dictation, including provider/model, audio, live capture, cleanup, reasoning effort and the effective system prompt. Off by default.\n\nAPI keys, endpoints and extra-header names/values are excluded; only connection-presence flags and the header count are included. Prompts are saved as text. Select rows in History and copy JSON to compare results with your expected script. Existing rows are not backfilled."), 0, 2);
        root.Controls.Add(_historyConfigurationCheckBox, 1, 2);
        _historyEnabledCheckBox.CheckedChanged += (_, _) =>
        {
            _historyRecordingDurationCheckBox.Enabled = _historyEnabledCheckBox.Checked;
            _historyConfigurationCheckBox.Enabled = _historyEnabledCheckBox.Checked;
        };

        var historyPathTextBox = new TextBox
        {
            ReadOnly = true,
            Width = 360,
            Dock = DockStyle.Left,
            Text = historyPath,
        };
        root.Controls.Add(CreateFieldLabel("History file",
            "Read-only location of the append-only history.jsonl file under your Windows roaming application-data folder, in Bisik. Each line represents a saved dictation.\n\nOpen History from the tray to review past results. Recorded audio is also retained locally when history is enabled."), 0, 3);
        root.Controls.Add(historyPathTextBox, 1, 3);

        var openLocationButton = new Button
        {
            Text = "Open File Location",
            AutoSize = true,
            Enabled = false,
        };
        root.Controls.Add(CreateFieldLabel("Open location",
            "Intended to open the history folder in File Explorer. This button is currently unavailable because its action is not implemented. The History file field above shows the location."), 0, 4);
        root.Controls.Add(openLocationButton, 1, 4);

        var clearHistoryButton = new Button
        {
            Text = "Clear History",
            AutoSize = true,
            Enabled = false,
        };
        root.Controls.Add(CreateFieldLabel("Maintenance",
            "Clear History is currently unavailable because its action is not implemented. Turning History logging off prevents new entries but does not delete existing history or recordings."), 0, 5);
        root.Controls.Add(clearHistoryButton, 1, 5);

        page.Controls.Add(root);
        return page;
    }

    private TabPage CreatePostProcessorTabPage()
    {
        var page = CreateTabPage("Post-Processor");
        var settings = new AppSettings().PostProcessor;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _postProcessorEnabledCheckBox = new CheckBox
        {
            Text = "Enable LLM post-processing",
            AutoSize = true,
            Checked = settings.Enabled,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8),
        };
        root.Controls.Add(CreateFieldLabel("Enabled",
            "Run an optional language-model step after speech recognition to clean up, rewrite or translate the final transcript. Off by default. Configure its provider and preset below.\n\nThe transcript is sent to that provider and may incur additional charges and delay. A failed or incomplete cleanup is reported without automatic insertion. Fast output can select the final raw transcript for one session."), 0, 0);
        root.Controls.Add(_postProcessorEnabledCheckBox, 1, 0);

        _postProcessorProviderComboBox = CreateEnumComboBox<ProviderType>(
            new Dictionary<ProviderType, string>
            {
                [ProviderType.AzureOpenAi] = "Azure OpenAI",
                [ProviderType.OpenAi] = "OpenAI",
                [ProviderType.OpenRouter] = "OpenRouter",
                [ProviderType.LocalWhisper] = "Local Whisper",
                [ProviderType.CustomEndpoint] = "Custom Endpoint",
            },
            settings.Provider.Type);
        root.Controls.Add(CreateFieldLabel("Provider",
            "Choose the service for LLM text processing, independently of the speech-to-text provider. The configured endpoint/model must support compatible chat completions; a transcription-only Whisper server cannot perform cleanup.\n\nSet the cleanup provider's own credentials and model below."), 0, 1);
        root.Controls.Add(_postProcessorProviderComboBox, 1, 1);

        var providerPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var providerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0),
        };
        providerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        providerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _postProcessorEndpointTextBox = new TextBox { Dock = DockStyle.Left, Width = 280, Text = settings.Provider.Endpoint };
        _postProcessorApiKeyTextBox = new TextBox { Dock = DockStyle.Left, Width = 280, Text = settings.Provider.ApiKey, UseSystemPasswordChar = true };
        _postProcessorDeploymentNameTextBox = new TextBox { Dock = DockStyle.Left, Width = 280, Text = settings.Provider.DeploymentName };
        _postProcessorModelTextBox = new TextBox { Dock = DockStyle.Left, Width = 280, Text = settings.Model };

        providerLayout.Controls.Add(CreateFieldLabel("Endpoint / Base URL",
            "Address of the cleanup provider's API. Azure uses your resource/gateway endpoint. Local Whisper appends chat/completions to the base URL. Custom Endpoint requires the complete chat-completions URL. OpenAI/OpenRouter use their hosted endpoints and ignore this field.\n\nThis is separate from the transcription endpoint and is encrypted when settings are saved."), 0, 0);
        providerLayout.Controls.Add(_postProcessorEndpointTextBox, 1, 0);
        providerLayout.Controls.Add(CreateFieldLabel("API Key",
            "Credential for the LLM cleanup provider, which may differ from your transcription key. Azure uses its API-key authentication; compatible hosted services normally use a bearer token. Leave it blank only if your local/custom server allows unauthenticated requests.\n\nThe field is masked and the saved key is encrypted for your Windows user with DPAPI."), 0, 1);
        providerLayout.Controls.Add(_postProcessorApiKeyTextBox, 1, 1);
        providerLayout.Controls.Add(CreateFieldLabel("Deployment Name",
            "Azure deployment serving the cleanup/chat model. Use the deployed name from your Azure resource, which may differ from the underlying model name. Non-Azure providers use Model instead.\n\nChanging the deployment clears the verified reasoning-effort list because the new deployment may support different values."), 0, 2);
        providerLayout.Controls.Add(_postProcessorDeploymentNameTextBox, 1, 2);
        providerLayout.Controls.Add(CreateFieldLabel("Model",
            "Identifier of the text/chat model used for cleanup, rewriting or translation. Choose a model supported by the selected provider and compatible chat-completions API. Azure can use its deployment name when Model is blank.\n\nChanging the model clears the verified reasoning-effort list. This setting does not change the speech-recognition model."), 0, 3);
        providerLayout.Controls.Add(_postProcessorModelTextBox, 1, 3);
        _reasoningEffortComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _reasoningEffortComboBox.Items.AddRange(new object[] { "Provider default", "none", "minimal", "low", "medium", "high", "xhigh", "max" });
        _reasoningEffortComboBox.SelectedIndex = 0;
        _verifiedReasoningEffortsTextBox = new TextBox { Width = 280 };
        providerLayout.Controls.Add(CreateFieldLabel("Reasoning effort",
            "Optional request for how much reasoning the cleanup model performs. Support, latency and cost vary by model/API; lower supported levels can be quicker. 'none' is an explicit value and is different from leaving the parameter out.\n\nProvider default omits the parameter. To choose another value, first list it under Verified supported efforts for this exact model/API; Apply rejects an unverified selection."), 0, 4);
        providerLayout.Controls.Add(_reasoningEffortComboBox, 1, 4);
        providerLayout.Controls.Add(CreateFieldLabel("Verified supported efforts",
            "Comma-separated effort values you have confirmed this exact cleanup model/API accepts. Allowed names: none, minimal, low, medium, high, xhigh, max. Enter only supported values; for example, low,medium,high if those were verified.\n\nThis field records your verification; it does not test support automatically. Changing provider, endpoint, deployment or model clears it. Leave it blank and choose Provider default when support is unknown."), 0, 5);
        providerLayout.Controls.Add(_verifiedReasoningEffortsTextBox, 1, 5);
        // A deployment alias or changed API cannot inherit another model's capability claim.
        foreach (var field in new[] { _postProcessorEndpointTextBox, _postProcessorDeploymentNameTextBox, _postProcessorModelTextBox })
            field.TextChanged += (_, _) => _verifiedReasoningEffortsTextBox.Text = string.Empty;
        _postProcessorProviderComboBox.SelectedIndexChanged += (_, _) => _verifiedReasoningEffortsTextBox.Text = string.Empty;
        providerPanel.Controls.Add(providerLayout);
        root.Controls.Add(providerPanel, 0, 2);
        root.SetColumnSpan(providerPanel, 2);

        _postProcessorPresetComboBox = CreateEnumComboBox<PostProcessorPreset>(
            new Dictionary<PostProcessorPreset, string>
            {
                [PostProcessorPreset.CleanUp] = "Clean up",
                [PostProcessorPreset.HomogenizeToEnglish] = "Homogenize to English",
                [PostProcessorPreset.HomogenizeToIndonesian] = "Homogenize to Indonesian",
                [PostProcessorPreset.TranslateToLanguage] = "Translate to language",
                [PostProcessorPreset.Custom] = "Custom",
            },
            settings.PresetName);
        root.Controls.Add(CreateFieldLabel("Preset",
            "Clean up fixes grammar/punctuation and removes filler words. Homogenize to English or Indonesian rewrites mixed-language speech into the selected language. Translate to language uses the Target language field.\n\nCustom uses your System prompt verbatim. Other presets generate their own instruction at processing time, so editing System prompt does not alter them."), 0, 3);
        root.Controls.Add(_postProcessorPresetComboBox, 1, 3);

        _postProcessorTargetLanguageTextBox = new TextBox
        {
            Dock = DockStyle.Left,
            Width = 220,
            Text = string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "English" : settings.TargetLanguage,
        };
        root.Controls.Add(CreateFieldLabel("Target language",
            "Output language requested by the Translate to language preset. Enter a language name, such as English or Indonesian. This field is used only by that preset.\n\nIt controls translation after transcription; Default language on the Language tab describes the language you speak."), 0, 4);
        root.Controls.Add(_postProcessorTargetLanguageTextBox, 1, 4);

        _postProcessorSystemPromptTextBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 360,
            Height = 150,
            Text = settings.SystemPrompt,
        };
        root.Controls.Add(CreateFieldLabel("System prompt",
            "Instructions sent to the cleanup model when Preset is Custom. Describe how to edit the transcript and ask it to return only the final text. Your custom prompt is used verbatim.\n\nBuilt-in presets generate their own instructions and ignore this field. The prompt is sent with the transcript to the cleanup provider when post-processing is enabled; avoid putting credentials in it."), 0, 5);
        root.Controls.Add(_postProcessorSystemPromptTextBox, 1, 5);
        _vocabularyName = new TextBox { Width = 360, AccessibleName = "Vocabulary profile name" };
        _vocabularyLines = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 360, Height = 150, AccessibleName = "Vocabulary entries" };
        root.Controls.Add(CreateFieldLabel("Vocabulary profile", "Optional project name. Leave both fields empty to preserve the existing prompt exactly."), 0, 6);
        root.Controls.Add(_vocabularyName, 1, 6);
        root.Controls.Add(CreateFieldLabel("Canonical vocabulary", "One canonical | context | comma-separated variants per line. Context distinguishes API/tool names and ordinary words. This is guidance, never global replacement or a full reference script."), 0, 7);
        root.Controls.Add(_vocabularyLines, 1, 7);
        var template = new Button { Text = "Load editable ABB template", AutoSize = true };
        template.Click += (_, _) => { _vocabularyName.Text = CleanupVocabulary.AbbTemplate.Name; _vocabularyLines.Text = CleanupVocabulary.AbbTemplate.Lines; };
        root.Controls.Add(template, 1, 8);

        page.Controls.Add(root);
        return page;
    }

    private void ShowProviderPanel(ProviderType providerType)
    {
        foreach (var panel in _providerPanels)
        {
            panel.Value.Visible = panel.Key == providerType;
        }
    }

    private Panel CreateProviderFieldsPanel(ProviderType providerType, params (string Label, string Value)[] fields)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var fieldInputs = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = fields.Length,
            Padding = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var textBox = new TextBox
            {
                Text = field.Value,
                Dock = DockStyle.Left,
                Width = 280,
                UseSystemPasswordChar = field.Label.Contains("API Key", StringComparison.OrdinalIgnoreCase),
                Multiline = field.Label == "Extra Headers",
                Height = field.Label == "Extra Headers" ? 70 : 23,
            };
            fieldInputs[field.Label] = textBox;

            layout.Controls.Add(CreateFieldLabel(field.Label, GetProviderFieldDescription(providerType, field.Label)), 0, index);
            layout.Controls.Add(textBox, 1, index);
        }

        panel.Controls.Add(layout);
        _providerFieldInputs[providerType] = fieldInputs;
        return panel;
    }

    private static ComboBox CreateEnumComboBox<TEnum>(
        IReadOnlyDictionary<TEnum, string> labels,
        TEnum selectedValue)
        where TEnum : struct, Enum
    {
        var comboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
            Dock = DockStyle.Left,
        };

        foreach (var option in labels)
        {
            comboBox.Items.Add(new ComboBoxItem<TEnum>(option.Key, option.Value));
        }

        var selectedItem = comboBox.Items
            .OfType<ComboBoxItem<TEnum>>()
            .FirstOrDefault(item => EqualityComparer<TEnum>.Default.Equals(item.Value, selectedValue));
        if (selectedItem is not null)
        {
            comboBox.SelectedItem = selectedItem;
        }

        return comboBox;
    }

    private static string GetProviderFieldDescription(ProviderType providerType, string label) => label switch
    {
        "Endpoint URL" => "Address of your Azure OpenAI resource or gateway, used to reach the selected deployment. Use the endpoint supplied by your Azure administrator. The endpoint is encrypted when saved.\n\nRealtime and multipart transcription use different routes; choose the matching Azure protocol below.",
        "Base URL" when providerType == ProviderType.LocalWhisper => "Base address of your running Whisper-compatible server, including its port and any required API prefix. Bisik appends audio/transcriptions to this address. The server must be running and reachable.\n\nThe address is encrypted when saved. A local server avoids sending audio to a hosted provider only if the server itself processes it locally.",
        "Base URL" => "Complete URL accepting an OpenAI-compatible multipart transcription request. Bisik sends directly to this URL; include the transcription route required by your server rather than only its host.\n\nThe address is encrypted when saved. Extra Headers can supply gateway-specific authentication.",
        "API Key" => "Credential for the selected transcription provider. Azure uses an API key; OpenAI/OpenRouter use a bearer token. This key is independent of the optional LLM cleanup key.\n\nThe field is masked and the saved credential is encrypted for your Windows user with DPAPI.",
        "API Key (optional)" => "Bearer token for a local/custom transcription server that requires authentication. Leave blank if the server does not require it. For other authentication schemes, a custom provider can use Extra Headers.\n\nThe field is masked and the saved credential is encrypted for your Windows user with DPAPI.",
        "Deployment Name" => "Name of the speech-recognition deployment in your Azure resource. Use the deployed name, which may differ from the underlying model name.\n\nNames containing 'realtime' select realtime transcription automatically. If a realtime deployment has another name, enable Use Azure realtime transcription below.",
        "API Version" => "Azure API version required by your multipart transcription deployment/gateway. Use a version supported by that resource. This controls the api-version query parameter for multipart requests; Azure realtime uses its own protocol routes.",
        "Realtime Calls Endpoint (optional)" => "Optional override for the Azure realtime calls fallback URL, for gateways that route calls separately from the main endpoint. Use the complete calls URL supplied by your administrator.\n\nLeave blank to derive the calls route from Endpoint URL. Used only for Azure realtime; the saved override is encrypted.",
        "Model" when providerType == ProviderType.LocalWhisper => "Model identifier accepted by your local transcription server. Leave blank if the server selects its own default. The model must support speech-to-text rather than only text chat.",
        "Model" => "Speech-to-text model identifier accepted by the selected hosted provider. OpenRouter requires a model. Choose a model that supports the provider's multipart audio-transcription API.\n\nThis is separate from the text/chat model configured on the Post-Processor tab.",
        "Model (optional)" => "Model identifier sent with a custom transcription request. Leave blank when the server selects its own model. Use the exact identifier required by your compatible endpoint.",
        "Extra Headers" => "Additional HTTP request headers for the custom transcription endpoint, one Name:Value per line. For example, a gateway can require its own authentication header. Split each line at its first colon.\n\nSensitive header values are encrypted when saved. When API Key is set, Bisik owns the Authorization bearer header and an extra Authorization header cannot replace it.",
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "A provider field needs a help description."),
    };

    private Control CreateFieldLabel(string text, string description)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(140, 0),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
        };
        var info = new Button
        {
            Text = "ⓘ",
            Size = new Size(24, 24),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            ForeColor = SystemColors.HotTrack,
            Cursor = Cursors.Help,
            AccessibleName = $"{text} help",
            AccessibleDescription = description,
            Tag = new HelpContent(text, description),
        };
        info.FlatAppearance.BorderSize = 0;
        _helpToolTip.SetToolTip(info, description);
        // Clicking or keyboard focus gives users another way to read hover help.
        info.Click += (_, _) => _helpToolTip.Show(description, info, 0, info.Height, _helpToolTip.AutoPopDelay);
        info.Enter += (_, _) => _helpToolTip.Show(description, info, 0, info.Height, _helpToolTip.AutoPopDelay);
        info.Leave += (_, _) => _helpToolTip.Hide(info);
        info.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) _helpToolTip.Hide(info); };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(3, 5, 3, 3),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(info, 1, 0);
        return panel;
    }

    private const TextFormatFlags HelpTextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private sealed record HelpContent(string Title, string Description);

    private void HandleHelpPopup(object? sender, PopupEventArgs e)
    {
        if (e.AssociatedControl?.Tag is not HelpContent help) return;
        var padding = LogicalToDeviceUnits(12);
        // Measure the complete text at a bounded width; native one-line sizing clips long help.
        var width = Math.Min(LogicalToDeviceUnits(420), Screen.FromControl(e.AssociatedControl).WorkingArea.Width - padding * 2);
        using var titleFont = new Font(Font, FontStyle.Bold);
        var title = TextRenderer.MeasureText(help.Title, titleFont, new Size(width, int.MaxValue), HelpTextFlags);
        var body = TextRenderer.MeasureText(help.Description, Font, new Size(width, int.MaxValue), HelpTextFlags);
        e.ToolTipSize = new Size(width + padding * 2, title.Height + body.Height + padding * 2 + padding / 2);
    }

    private void HandleHelpDraw(object? sender, DrawToolTipEventArgs e)
    {
        if (e.AssociatedControl?.Tag is not HelpContent help) return;
        e.Graphics.FillRectangle(SystemBrushes.Info, e.Bounds);
        e.DrawBorder();
        var padding = LogicalToDeviceUnits(12);
        var width = e.Bounds.Width - padding * 2;
        using var titleFont = new Font(Font, FontStyle.Bold);
        var title = TextRenderer.MeasureText(help.Title, titleFont, new Size(width, int.MaxValue), HelpTextFlags);
        TextRenderer.DrawText(e.Graphics, help.Title, titleFont,
            new Rectangle(padding, padding, width, title.Height), SystemColors.InfoText, HelpTextFlags);
        var bodyTop = padding + title.Height + padding / 2;
        TextRenderer.DrawText(e.Graphics, help.Description, Font,
            new Rectangle(padding, bodyTop, width, e.Bounds.Height - bodyTop - padding), SystemColors.InfoText, HelpTextFlags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _helpToolTip.Dispose();
        base.Dispose(disposing);
    }

    private static IEnumerable<ComboBoxItem<string>> GetLanguageOptions()
    {
        yield return new ComboBoxItem<string>("auto", "Auto-detect");
        yield return new ComboBoxItem<string>("en", "English");
        yield return new ComboBoxItem<string>("id", "Indonesian");
        yield return new ComboBoxItem<string>("ja", "Japanese");
        yield return new ComboBoxItem<string>("zh", "Chinese");
        yield return new ComboBoxItem<string>("es", "Spanish");
        yield return new ComboBoxItem<string>("fr", "French");
        yield return new ComboBoxItem<string>("de", "German");
        yield return new ComboBoxItem<string>("other", "Other (type manually)");
    }

    private sealed record ComboBoxItem<T>(T Value, string Label)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private Control CreateBottomButtonPanel()
    {
        _okButton = new Button
        {
            Text = "OK",
            AutoSize = true,
        };
        _okButton.Click += (_, _) => SaveAndClose();

        _applyButton = new Button
        {
            Text = "Apply",
            AutoSize = true,
        };
        _applyButton.Click += (_, _) => SaveWithoutClosing();

        _cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        _cancelButton.Click += (_, _) => Close();

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            WrapContents = false,
        };
        panel.Controls.Add(_cancelButton);
        panel.Controls.Add(_applyButton);
        panel.Controls.Add(_okButton);
        return panel;
    }

    private async void HandleTestConnectionClickAsync(object? sender, EventArgs eventArgs)
    {
        if (_transcriptionService is null || _testConnectionButton is null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _connectionTestCancellation = cancellation;
        _testConnectionButton.Enabled = false;
        _applyButton!.Enabled = _okButton!.Enabled = false;
        RecordedAudio? audio = null;
        try
        {
            var settings = BuildProviderTestSettings();
            // A separate recorder and fresh provider use the fields being tested, even before Apply.
            using var recorder = new AudioRecorder(settings.Audio);
            recorder.StartRecording();
            _testConnectionButton.Text = "Recording: speak for 3 seconds...";
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
            audio = await recorder.StopRecordingAsync(cancellation.Token);
            _testConnectionButton.Text = "Transcribing...";
            var service = _serviceFactory?.Invoke(settings) ?? _transcriptionService;
            var result = await service.TranscribeAsync(audio.FilePath, settings.DefaultLanguage, settings, cancellation.Token);
            if (!IsDisposed) ShowModelessStatus(result.Succeeded ? "Test Connection Succeeded" : "Test Connection Failed",
                result.Succeeded ? (string.IsNullOrWhiteSpace(result.OutputText) ? "No speech recognized." : result.OutputText) : result.ErrorMessage);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) ShowModelessStatus("Test Connection", "Cancelled or timed out.");
        }
        catch (Exception exception)
        {
            new ErrorLogger().Log(exception);
            if (!IsDisposed) ShowModelessStatus("Test Connection Failed", exception.Message);
        }
        finally
        {
            _connectionTestCancellation = null;
            if (audio is not null)
            {
                try { File.Delete(audio.FilePath); }
                catch (Exception exception) { new ErrorLogger().Log(exception); }
            }
            if (!IsDisposed)
            {
                _testConnectionButton.Text = "Test Connection";
                _testConnectionButton.Enabled = true;
                _applyButton!.Enabled = _okButton!.Enabled = true;
            }
        }
    }

    private AppSettings BuildProviderTestSettings()
    {
        var settings = BuildSettingsFromForm();
        return settings with { HistoryEnabled = false, PostProcessor = settings.PostProcessor with { Enabled = false } };
    }

    private void LoadSettingsIntoForm(AppSettings settings)
    {
        _loadedSettings = settings;
        if (_liveCaptureCheckBox is not null) _liveCaptureCheckBox.Checked = settings.LiveCapture;
        if (_transcriptionDelayComboBox is not null) _transcriptionDelayComboBox.SelectedItem = settings.Provider.TranscriptionDelay ?? "Provider default";
        if (_pauseCommitsCheckBox is not null) _pauseCommitsCheckBox.Checked = settings.PauseCommits;
        if (_speculativeCleanupCheckBox is not null) _speculativeCleanupCheckBox.Checked = settings.SpeculativeCleanup;
        if (_prewarmRealtimeCheckBox is not null) _prewarmRealtimeCheckBox.Checked = settings.PrewarmRealtime;
        if (_prewarmIdleSecondsInput is not null) _prewarmIdleSecondsInput.Value = Math.Clamp(settings.PrewarmIdleSeconds, 5, 60);
        if (_azureRealtimeCheckBox is not null) _azureRealtimeCheckBox.Checked = settings.Provider.AzureRealtime;
        if (_launchAtStartupCheckBox is not null)
        {
            _launchAtStartupCheckBox.Checked = settings.LaunchAtStartup;
        }

        SetComboSelection(_triggerModeComboBox, settings.TriggerMode);
        if (_insertLatestHotkeyTextBox is not null) _insertLatestHotkeyTextBox.Text = settings.InsertLatestHotkey;
        if (_fastOutputHotkeyTextBox is not null) _fastOutputHotkeyTextBox.Text = settings.FastOutputHotkey;
        _hotkeyTextBox?.SetBounds(_hotkeyTextBox.Left, _hotkeyTextBox.Top, _hotkeyTextBox.Width, _hotkeyTextBox.Height);
        if (_hotkeyTextBox is not null)
        {
            _hotkeyTextBox.Text = settings.Hotkey;
        }
        SetComboSelection(_outputModeComboBox, settings.OutputMode);
        SetComboSelection(_indicatorStyleComboBox, settings.IndicatorStyle);
        SetComboSelection(_providerSelectorComboBox, settings.Provider.Type);
        ShowProviderPanel(settings.Provider.Type);
        ApplyProviderFields(settings.Provider, settings.Provider.Type);

        if (_audioRecorder is not null && _microphoneSelectorComboBox is not null && settings.Audio.DeviceId is not null)
        {
            var devices = _audioRecorder.GetInputDevices();
            var matchIndex = devices.ToList().FindIndex(device => string.Equals(device.Id, settings.Audio.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (matchIndex >= 0 && matchIndex < _microphoneSelectorComboBox.Items.Count)
            {
                _microphoneSelectorComboBox.SelectedIndex = matchIndex;
            }
        }

        SetComboSelection(_audioFormatComboBox, settings.Audio.Format);
        if (_sampleRateInput is not null)
        {
            _sampleRateInput.Value = Math.Clamp(settings.Audio.SampleRate, (int)_sampleRateInput.Minimum, (int)_sampleRateInput.Maximum);
        }
        if (_silenceThresholdInput is not null)
        {
            _silenceThresholdInput.Value = ClampDecimal((decimal)settings.Audio.VadSilenceThreshold, _silenceThresholdInput.Minimum, _silenceThresholdInput.Maximum);
        }
        if (_minSpeechInput is not null)
        {
            _minSpeechInput.Value = Math.Clamp(settings.Audio.VadMinSpeechMs, (int)_minSpeechInput.Minimum, (int)_minSpeechInput.Maximum);
        }

        SetComboSelection(_defaultLanguageComboBox, settings.DefaultLanguage);
        if (_quickLanguagesTextBox is not null)
        {
            _quickLanguagesTextBox.Text = string.Join(Environment.NewLine, settings.QuickLanguages);
        }

        if (_alwaysCopyToClipboardCheckBox is not null)
        {
            _alwaysCopyToClipboardCheckBox.Checked = settings.AlwaysCopyToClipboard;
        }

        if (_historyEnabledCheckBox is not null)
        {
            _historyEnabledCheckBox.Checked = settings.HistoryEnabled;
        }
        if (_historyRecordingDurationCheckBox is not null)
        {
            _historyRecordingDurationCheckBox.Checked = settings.HistoryRecordingDurationEnabled;
            _historyRecordingDurationCheckBox.Enabled = settings.HistoryEnabled;
        }
        if (_historyConfigurationCheckBox is not null)
        {
            _historyConfigurationCheckBox.Checked = settings.HistoryConfigurationEnabled;
            _historyConfigurationCheckBox.Enabled = settings.HistoryEnabled;
        }

        if (_postProcessorEnabledCheckBox is not null)
        {
            _postProcessorEnabledCheckBox.Checked = settings.PostProcessor.Enabled;
        }
        SetComboSelection(_postProcessorProviderComboBox, settings.PostProcessor.Provider.Type);
        ApplyPostProcessorProviderFields(settings.PostProcessor);
        SetComboSelection(_postProcessorPresetComboBox, settings.PostProcessor.PresetName);
        if (_postProcessorTargetLanguageTextBox is not null)
        {
            _postProcessorTargetLanguageTextBox.Text = settings.PostProcessor.TargetLanguage;
        }
        if (_postProcessorSystemPromptTextBox is not null)
        {
            _postProcessorSystemPromptTextBox.Text = settings.PostProcessor.SystemPrompt;
            _recognitionPrompt!.Text = settings.Provider.RecognitionContext?.Prompt ?? "";
            _recognitionKeywords!.Text = string.Join(",", settings.Provider.RecognitionContext?.Keywords ?? []);
            _recognitionLanguage!.Text = settings.Provider.RecognitionContext?.ExpectedLanguage ?? "";
            _recognitionFields!.Text = string.Join(",", settings.Provider.RecognitionCapability?.Fields ?? []);
            _vocabularyName!.Text = settings.PostProcessor.Vocabulary?.Name ?? "";
            _vocabularyLines!.Text = settings.PostProcessor.Vocabulary?.Lines ?? "";
        }
    }

    private AppSettings BuildSettingsFromForm()
    {
        var defaults = new AppSettings();
        var providerType = GetSelectedComboValue(_providerSelectorComboBox, defaults.Provider.Type);
        var provider = BuildProviderSettings(providerType);
        var postProcessorProviderType = GetSelectedComboValue(_postProcessorProviderComboBox, defaults.PostProcessor.Provider.Type);
        var postProcessorProvider = _loadedSettings.PostProcessor.Provider with
        {
            Type = postProcessorProviderType,
            Endpoint = _postProcessorEndpointTextBox?.Text ?? string.Empty,
            ApiKey = _postProcessorApiKeyTextBox?.Text ?? string.Empty,
            DeploymentName = _postProcessorDeploymentNameTextBox?.Text ?? string.Empty,
            Model = _postProcessorModelTextBox?.Text ?? string.Empty,
        };

        var quickLanguages = (_quickLanguagesTextBox?.Text ?? string.Empty)
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var postProcessor = _loadedSettings.PostProcessor with
        {
            Enabled = _postProcessorEnabledCheckBox?.Checked ?? defaults.PostProcessor.Enabled,
            Provider = postProcessorProvider,
            Model = _postProcessorModelTextBox?.Text ?? string.Empty,
            TargetLanguage = _postProcessorTargetLanguageTextBox?.Text ?? defaults.PostProcessor.TargetLanguage,
            PresetName = GetSelectedComboValue(_postProcessorPresetComboBox, defaults.PostProcessor.PresetName),
            SystemPrompt = _postProcessorSystemPromptTextBox?.Text ?? defaults.PostProcessor.SystemPrompt,
            Vocabulary = CleanupVocabulary.ParseLines(_vocabularyName?.Text ?? "", _vocabularyLines?.Text ?? ""),
            ReasoningEffort = _reasoningEffortComboBox?.SelectedIndex > 0 ? _reasoningEffortComboBox.SelectedItem?.ToString() : null,
            ReasoningCapability = null,
        };
        var efforts = (_verifiedReasoningEffortsTextBox?.Text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (efforts.Any(e => e is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max")))
            throw new InvalidOperationException("Verified efforts must be comma-separated API-supported values.");
        if (efforts.Length > 0) postProcessor = postProcessor with { ReasoningCapability = new(ReasoningCapability.Fingerprint(postProcessor), efforts) };
        if (postProcessor.ReasoningEffort is { } selected && !efforts.Contains(selected, StringComparer.Ordinal))
            throw new InvalidOperationException("Verify that this model/API supports the selected effort, or choose Provider default.");

        return _loadedSettings with
        {
            LiveCapture = _liveCaptureCheckBox?.Checked ?? defaults.LiveCapture,
            PauseCommits = _pauseCommitsCheckBox?.Checked ?? defaults.PauseCommits,
            SpeculativeCleanup = _speculativeCleanupCheckBox?.Checked ?? defaults.SpeculativeCleanup,
            PrewarmRealtime = _prewarmRealtimeCheckBox?.Checked ?? defaults.PrewarmRealtime,
            PrewarmIdleSeconds = (int?)_prewarmIdleSecondsInput?.Value ?? defaults.PrewarmIdleSeconds,
            LaunchAtStartup = _launchAtStartupCheckBox?.Checked ?? defaults.LaunchAtStartup,
            TriggerMode = GetSelectedComboValue(_triggerModeComboBox, defaults.TriggerMode),
            Hotkey = string.IsNullOrWhiteSpace(_hotkeyTextBox?.Text) ? defaults.Hotkey : _hotkeyTextBox.Text,
            InsertLatestHotkey = _insertLatestHotkeyTextBox?.Text ?? defaults.InsertLatestHotkey,
            FastOutputHotkey = _fastOutputHotkeyTextBox?.Text ?? defaults.FastOutputHotkey,
            OutputMode = GetSelectedComboValue(_outputModeComboBox, defaults.OutputMode),
            IndicatorStyle = GetSelectedComboValue(_indicatorStyleComboBox, defaults.IndicatorStyle),
            DefaultLanguage = GetSelectedComboValue(_defaultLanguageComboBox, defaults.DefaultLanguage),
            QuickLanguages = quickLanguages.Length > 0 ? quickLanguages : defaults.QuickLanguages,
            AlwaysCopyToClipboard = _alwaysCopyToClipboardCheckBox?.Checked ?? defaults.AlwaysCopyToClipboard,
            HistoryEnabled = _historyEnabledCheckBox?.Checked ?? defaults.HistoryEnabled,
            HistoryRecordingDurationEnabled = _historyRecordingDurationCheckBox?.Checked ?? defaults.HistoryRecordingDurationEnabled,
            HistoryConfigurationEnabled = _historyConfigurationCheckBox?.Checked ?? defaults.HistoryConfigurationEnabled,
            Provider = provider,
            Audio = new AudioSettings
            {
                DeviceId = ResolveSelectedDeviceId(),
                Format = GetSelectedComboValue(_audioFormatComboBox, defaults.Audio.Format),
                SampleRate = (int?)_sampleRateInput?.Value ?? defaults.Audio.SampleRate,
                VadSilenceThreshold = (double?)_silenceThresholdInput?.Value ?? defaults.Audio.VadSilenceThreshold,
                VadMinSpeechMs = (int?)_minSpeechInput?.Value ?? defaults.Audio.VadMinSpeechMs,
            },
            PostProcessor = postProcessor,
        };
    }

    private void SaveWithoutClosing() => SaveSettings(close: false);

    private void SaveAndClose() => SaveSettings(close: true);

    private void SaveSettings(bool close)
    {
        try
        {
            var settings = BuildSettingsFromForm();
            _ = HotkeyManager.ParseHotkey(settings.Hotkey);
            SettingsApplied?.Invoke(settings);
            _settingsManager.Save(settings);
            LoadSettingsIntoForm(_settingsManager.Load());
            if (close)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (Exception exception)
        {
            new ErrorLogger().Log(exception);
            ShowModelessStatus("Settings not applied", exception.Message);
        }
    }

    private void ApplyProviderFields(ProviderSettings provider, ProviderType providerType)
    {
        if (!_providerFieldInputs.TryGetValue(providerType, out var fields))
        {
            return;
        }

        SetTextBox(fields, "Endpoint URL", provider.Endpoint);
        SetTextBox(fields, "Base URL", provider.Endpoint);
        SetTextBox(fields, "API Key", provider.ApiKey);
        SetTextBox(fields, "API Key (optional)", provider.ApiKey);
        SetTextBox(fields, "Deployment Name", provider.DeploymentName);
        SetTextBox(fields, "API Version", provider.ApiVersion);
        SetTextBox(fields, "Model", provider.Model ?? string.Empty);
        SetTextBox(fields, "Model (optional)", provider.Model ?? string.Empty);
        SetTextBox(fields, "Extra Headers", string.Join(Environment.NewLine,
            (provider.ExtraHeaders ?? []).Select(header => $"{header.Key}:{header.Value}")));
        SetTextBox(fields, "Realtime Calls Endpoint (optional)", GetExtraHeaderValue(provider, "Bisik-Realtime-Calls-Endpoint"));
    }

    private void ApplyPostProcessorProviderFields(PostProcessorSettings settings)
    {
        if (_postProcessorEndpointTextBox is not null) _postProcessorEndpointTextBox.Text = settings.Provider.Endpoint;
        if (_postProcessorApiKeyTextBox is not null) _postProcessorApiKeyTextBox.Text = settings.Provider.ApiKey;
        if (_postProcessorDeploymentNameTextBox is not null) _postProcessorDeploymentNameTextBox.Text = settings.Provider.DeploymentName;
        if (_postProcessorModelTextBox is not null) _postProcessorModelTextBox.Text = settings.Model;
        if (_reasoningEffortComboBox is not null) _reasoningEffortComboBox.SelectedItem = settings.ReasoningEffort ?? "Provider default";
        if (_verifiedReasoningEffortsTextBox is not null) _verifiedReasoningEffortsTextBox.Text =
            settings.ReasoningCapability is { } capability && capability.Matches(settings) ? string.Join(",", capability.SupportedEfforts) : string.Empty;
    }

    private ProviderSettings BuildProviderSettings(ProviderType providerType, string endpointLabel = "Endpoint URL")
    {
        var provider = _loadedSettings.Provider.Type == providerType
            ? _loadedSettings.Provider
            : new ProviderSettings { Type = providerType };

        if (_providerFieldInputs.TryGetValue(providerType, out var fields))
        {
            provider = provider with
            {
                Endpoint = GetFieldText(fields, endpointLabel, GetFieldText(fields, "Base URL")),
                ApiKey = GetFieldText(fields, "API Key", GetFieldText(fields, "API Key (optional)")),
                DeploymentName = GetFieldText(fields, "Deployment Name"),
                ApiVersion = GetFieldText(fields, "API Version", "2024-06-01"),
                Model = GetFieldText(fields, "Model", GetFieldText(fields, "Model (optional)")),
                AzureRealtime = providerType == ProviderType.AzureOpenAi && (_azureRealtimeCheckBox?.Checked ?? false),
                TranscriptionDelay = providerType == ProviderType.AzureOpenAi && _transcriptionDelayComboBox?.SelectedIndex > 0
                    ? _transcriptionDelayComboBox.SelectedItem?.ToString() : null,
                ExtraHeaders = BuildProviderExtraHeaders(provider, fields),
            };
        }

        var prompt = _recognitionPrompt?.Text;
        var keywords = (_recognitionKeywords?.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var language = _recognitionLanguage?.Text;
        var selectedFields = (_recognitionFields?.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Enum.GetNames<RecognitionField>().Contains(v) ? Enum.Parse<RecognitionField>(v) : throw new ArgumentException("Unknown recognition field.")).ToImmutableArray();
        var context = string.IsNullOrWhiteSpace(prompt) && keywords.Length == 0 && string.IsNullOrWhiteSpace(language) ? null
            : new RecognitionContext(string.IsNullOrWhiteSpace(prompt) ? null : prompt, keywords.ToImmutableArray(), string.IsNullOrWhiteSpace(language) ? null : language).Snapshot();
        var fingerprint = RecognitionCapability.Fingerprint(provider, provider.Type == ProviderType.AzureOpenAi && Bisik.Providers.AzureOpenAIProvider.UsesRealtime(provider));
        var old = _loadedSettings.Provider.RecognitionCapability;
        var unchangedClaim = old is not null && old.Fields.SequenceEqual(selectedFields);
        return provider with { RecognitionContext = context, RecognitionCapability = selectedFields.Length == 0 || unchangedClaim && old!.ApiFingerprint != fingerprint
            ? null : new(fingerprint, selectedFields) };
    }

    private string? ResolveSelectedDeviceId()
    {
        if (_audioRecorder is null || _microphoneSelectorComboBox is null)
        {
            return null;
        }

        var selectedIndex = _microphoneSelectorComboBox.SelectedIndex;
        var devices = _audioRecorder.GetInputDevices();
        if (selectedIndex >= 0 && selectedIndex < devices.Count)
        {
            return devices[selectedIndex].Id;
        }

        return null;
    }

    private static void SetTextBox(Dictionary<string, TextBox> fields, string label, string value)
    {
        if (fields.TryGetValue(label, out var textBox))
        {
            textBox.Text = value;
        }
    }

    private static string GetFieldText(Dictionary<string, TextBox> fields, string label, string? fallback = "")
    {
        return fields.TryGetValue(label, out var textBox)
            ? textBox.Text
            : fallback ?? string.Empty;
    }

    private static Dictionary<string, string>? BuildProviderExtraHeaders(ProviderSettings provider, Dictionary<string, TextBox> fields)
    {
        var headers = new Dictionary<string, string>(provider.ExtraHeaders ?? [], StringComparer.OrdinalIgnoreCase);
        if (provider.Type == ProviderType.CustomEndpoint)
        {
            headers.Clear();
            foreach (var line in GetFieldText(fields, "Extra Headers").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) throw new InvalidOperationException("Extra headers must use one Name:Value per line.");
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        if (provider.Type == ProviderType.AzureOpenAi)
        {
            var endpoint = GetFieldText(fields, "Realtime Calls Endpoint (optional)");
            if (provider.PersistedUndecryptableHeaderCiphertexts?.ContainsKey("bisikrealtimecallsendpoint") != true)
                headers.Remove("Bisik-Realtime-Calls-Endpoint");
            if (!string.IsNullOrWhiteSpace(endpoint)) headers["Bisik-Realtime-Calls-Endpoint"] = endpoint.Trim();
        }
        return headers.Count > 0 ? headers : null;
    }

    private static string GetExtraHeaderValue(ProviderSettings provider, string headerName)
    {
        return provider.ExtraHeaders is not null &&
            provider.ExtraHeaders.TryGetValue(headerName, out var value)
            ? value
            : string.Empty;
    }

    private static void SetComboSelection<T>(ComboBox? comboBox, T value)
    {
        if (comboBox is null)
        {
            return;
        }

        var selectedItem = comboBox.Items
            .OfType<ComboBoxItem<T>>()
            .FirstOrDefault(item => EqualityComparer<T>.Default.Equals(item.Value, value));
        if (selectedItem is not null)
        {
            comboBox.SelectedItem = selectedItem;
        }
    }

    private static T GetSelectedComboValue<T>(ComboBox? comboBox, T fallback)
    {
        return comboBox?.SelectedItem is ComboBoxItem<T> item
            ? item.Value
            : fallback;
    }

    private static decimal ClampDecimal(decimal value, decimal minimum, decimal maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }

    private void HandleHotkeyPreviewKeyDown(object? sender, PreviewKeyDownEventArgs eventArgs)
    {
        if (sender is not TextBox)
        {
            return;
        }

        // Space and Tab are commonly used in capture bindings and should reach KeyDown unchanged.
        if (eventArgs.KeyCode is Keys.Space or Keys.Tab)
        {
            eventArgs.IsInputKey = true;
        }
    }

    private void HandleHotkeyKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var captured = BuildCapturedHotkeyText(eventArgs.KeyData);
        if (captured is null)
        {
            eventArgs.SuppressKeyPress = true;
            eventArgs.Handled = true;
            return;
        }

        textBox.Text = captured;
        textBox.SelectionStart = textBox.TextLength;
        eventArgs.SuppressKeyPress = true;
        eventArgs.Handled = true;
    }

    private static string? BuildCapturedHotkeyText(Keys keyData)
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if ((keyData & Keys.Control) == Keys.Control)
        {
            modifiers |= HotkeyModifiers.Ctrl;
        }

        if ((keyData & Keys.Shift) == Keys.Shift)
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if ((keyData & Keys.Alt) == Keys.Alt)
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        var keyCode = keyData & Keys.KeyCode;

        // Win key (LWin/RWin) appears as a key code but should be treated as a modifier.
        // Detect it here and add the Win modifier flag so the display and registration
        // work correctly. We keep LWin/RWin as the keyCode because RegisterHotKey
        // supports using them as virtual keys alongside modifier flags.
        if (keyCode is Keys.LWin or Keys.RWin)
        {
            modifiers |= HotkeyModifiers.Win;
        }
        // Reject bare modifier keys (Ctrl, Shift, Alt, Win alone) - they are not valid hotkeys.
        else if (IsModifierOnlyKey(keyCode))
        {
            return null;
        }

        var combined = new HotkeyGesture(modifiers, keyCode);
        return HotkeyManager.IsSupportedHotkey(combined)
            ? HotkeyManager.FormatHotkey(combined)
            : null;
    }

    private static bool IsModifierOnlyKey(Keys keyCode)
    {
        return keyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
            Keys.Menu or Keys.LMenu or Keys.RMenu or
            Keys.LWin or Keys.RWin;
    }

    // Use a modeless form here so the test result doesn't block the Settings window workflow.
    private void ShowModelessStatus(string title, string message)
    {
        var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(520, 260),
            ShowInTaskbar = false,
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Text = string.IsNullOrWhiteSpace(message) ? "(no response)" : message,
            ScrollBars = ScrollBars.Vertical,
        };
        var closeButton = new Button
        {
            Text = "Close",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
        };
        closeButton.Click += (_, _) => dialog.Close();

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        buttonPanel.Controls.Add(closeButton);

        layout.Controls.Add(textBox, 0, 0);
        layout.Controls.Add(buttonPanel, 0, 1);
        dialog.Controls.Add(layout);
        dialog.Show(this);
    }
}
