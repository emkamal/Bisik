using Bisik.Models;
using Bisik.Core;

namespace Bisik.UI;

public sealed class SettingsForm : Form
{
    /// <summary>Raised when settings are saved (via Apply or OK).</summary>
    public event Action<AppSettings>? SettingsApplied;

    private readonly TabControl _tabs;
    private readonly Dictionary<ProviderType, Panel> _providerPanels = [];
    private readonly Dictionary<ProviderType, Dictionary<string, TextBox>> _providerFieldInputs = [];
    private readonly SettingsManager _settingsManager;
    private readonly AudioRecorder? _audioRecorder;
    private readonly TranscriptionService? _transcriptionService;
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
    private CheckBox? _historyEnabledCheckBox;
    private ComboBox? _postProcessorProviderComboBox;
    private CheckBox? _postProcessorEnabledCheckBox;
    private ComboBox? _postProcessorPresetComboBox;
    private TextBox? _postProcessorEndpointTextBox;
    private TextBox? _postProcessorApiKeyTextBox;
    private TextBox? _postProcessorDeploymentNameTextBox;
    private TextBox? _postProcessorModelTextBox;
    private TextBox? _postProcessorTargetLanguageTextBox;
    private TextBox? _postProcessorSystemPromptTextBox;
    private Button? _testConnectionButton;
    private Button? _applyButton;
    private Button? _okButton;
    private Button? _cancelButton;

    public SettingsForm()
        : this(audioRecorder: null, transcriptionService: null, settingsManager: null)
    {
    }

    public SettingsForm(AudioRecorder? audioRecorder, TranscriptionService? transcriptionService, SettingsManager? settingsManager = null)
    {
        _settingsManager = settingsManager ?? new SettingsManager();
        _audioRecorder = audioRecorder;
        _transcriptionService = transcriptionService;
        Text = "Bisik Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(860, 640);

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
            RowCount = 6,
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
        layout.Controls.Add(CreateFieldLabel("Launch at startup"), 0, 0);
        layout.Controls.Add(_launchAtStartupCheckBox, 1, 0);

        _triggerModeComboBox = CreateEnumComboBox<TriggerMode>(
            new Dictionary<TriggerMode, string>
            {
                [TriggerMode.PushToTalk] = "Push-to-talk",
                [TriggerMode.Toggle] = "Toggle",
                [TriggerMode.Vad] = "Voice Activity Detection",
            },
            new AppSettings().TriggerMode);
        layout.Controls.Add(CreateFieldLabel("Trigger mode"), 0, 1);
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
        var hotkeyHintLabel = new Label
        {
            Text = "Click the field, then press the hotkey combination you want Bisik to use.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 3, 3),
        };
        var hotkeyPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        hotkeyPanel.Controls.Add(_hotkeyTextBox);
        hotkeyPanel.Controls.Add(hotkeyHintLabel);
        layout.Controls.Add(CreateFieldLabel("Hotkey"), 0, 2);
        layout.Controls.Add(hotkeyPanel, 1, 2);

        _outputModeComboBox = CreateEnumComboBox<OutputMode>(
            new Dictionary<OutputMode, string>
            {
                [OutputMode.AutoType] = "Auto-type",
                [OutputMode.Clipboard] = "Clipboard",
                [OutputMode.Popup] = "Popup",
            },
            new AppSettings().OutputMode);
        layout.Controls.Add(CreateFieldLabel("Output mode"), 0, 3);
        layout.Controls.Add(_outputModeComboBox, 1, 3);

        _indicatorStyleComboBox = CreateEnumComboBox<IndicatorStyle>(
            new Dictionary<IndicatorStyle, string>
            {
                [IndicatorStyle.DotNearCursor] = "Dot near cursor",
                [IndicatorStyle.BarOverlay] = "Bar overlay",
                [IndicatorStyle.TrayIconOnly] = "Tray icon only",
            },
            new AppSettings().IndicatorStyle);
        layout.Controls.Add(CreateFieldLabel("Recording indicator"), 0, 4);
        layout.Controls.Add(_indicatorStyleComboBox, 1, 4);

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
        root.Controls.Add(CreateFieldLabel("Provider"), 0, 0);
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
            ("Extra Headers", "key:value"));

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

        root.Controls.Add(CreateFieldLabel("Provider fields"), 0, 1);
        root.Controls.Add(fieldsHost, 1, 1);

        var providerHint = new Label
        {
            Text = "Provider-specific validation and live field binding are added in later steps.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 3),
        };
        root.Controls.Add(CreateFieldLabel("Status"), 0, 2);
        root.Controls.Add(providerHint, 1, 2);

        _testConnectionButton = new Button
        {
            Text = "Test Connection",
            AutoSize = true,
            Enabled = _audioRecorder is not null && _transcriptionService is not null,
        };
        _testConnectionButton.Click += HandleTestConnectionClickAsync;
        var testHintLabel = new Label
        {
            Text = _testConnectionButton.Enabled
                ? "Records 3 seconds and runs the selected provider through the transcription pipeline."
                : "Connect audio/transcription services when wiring the app to enable live test transcription.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 3, 3),
        };
        var testPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        testPanel.Controls.Add(_testConnectionButton);
        testPanel.Controls.Add(testHintLabel);
        root.Controls.Add(CreateFieldLabel("Connection test"), 0, 3);
        root.Controls.Add(testPanel, 1, 3);

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

        root.Controls.Add(CreateFieldLabel("Microphone"), 0, 0);
        root.Controls.Add(_microphoneSelectorComboBox, 1, 0);

        _audioFormatComboBox = CreateEnumComboBox<AudioFormat>(
            new Dictionary<AudioFormat, string>
            {
                [AudioFormat.Wav] = "WAV",
                [AudioFormat.Mp3] = "MP3",
                [AudioFormat.Ogg] = "OGG",
            },
            settings.Format);
        root.Controls.Add(CreateFieldLabel("Upload format"), 0, 1);
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
        root.Controls.Add(CreateFieldLabel("Sample rate"), 0, 2);
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
        root.Controls.Add(CreateFieldLabel("VAD silence threshold"), 0, 3);
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
        root.Controls.Add(CreateFieldLabel("Min speech duration"), 0, 4);
        root.Controls.Add(minSpeechPanel, 1, 4);

        var audioHint = new Label
        {
            Text = "Device refresh, format validation, and live VAD tuning are added in later steps.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 3),
        };
        root.Controls.Add(CreateFieldLabel("Status"), 0, 5);
        root.Controls.Add(audioHint, 1, 5);

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

        root.Controls.Add(CreateFieldLabel("Default language"), 0, 0);
        root.Controls.Add(_defaultLanguageComboBox, 1, 0);

        _quickLanguagesTextBox = new TextBox
        {
            Multiline = true,
            Width = 280,
            Height = 120,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, settings.QuickLanguages),
        };
        var quickLanguagesHint = new Label
        {
            Text = "One language code per line for the tray quick-switch list.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 3, 3),
        };
        var quickLanguagesPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        quickLanguagesPanel.Controls.Add(_quickLanguagesTextBox);
        quickLanguagesPanel.Controls.Add(quickLanguagesHint);
        root.Controls.Add(CreateFieldLabel("Quick-switch languages"), 0, 1);
        root.Controls.Add(quickLanguagesPanel, 1, 1);

        var languageHint = new Label
        {
            Text = "Validation and custom language entry are added in later steps.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 3),
        };
        root.Controls.Add(CreateFieldLabel("Status"), 0, 2);
        root.Controls.Add(languageHint, 1, 2);

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
        root.Controls.Add(CreateFieldLabel("History logging"), 0, 0);
        root.Controls.Add(_historyEnabledCheckBox, 1, 0);

        var historyPathTextBox = new TextBox
        {
            ReadOnly = true,
            Width = 360,
            Dock = DockStyle.Left,
            Text = historyPath,
        };
        root.Controls.Add(CreateFieldLabel("History file"), 0, 1);
        root.Controls.Add(historyPathTextBox, 1, 1);

        var openLocationButton = new Button
        {
            Text = "Open File Location",
            AutoSize = true,
            Enabled = false,
        };
        var openLocationHint = new Label
        {
            Text = "File-location launch is wired in a later step.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 3, 3),
        };
        var openLocationPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        openLocationPanel.Controls.Add(openLocationButton);
        openLocationPanel.Controls.Add(openLocationHint);
        root.Controls.Add(CreateFieldLabel("Open location"), 0, 2);
        root.Controls.Add(openLocationPanel, 1, 2);

        var clearHistoryButton = new Button
        {
            Text = "Clear History",
            AutoSize = true,
            Enabled = false,
        };
        var clearHistoryHint = new Label
        {
            Text = "History clearing is wired in a later step.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 3, 3),
        };
        var clearHistoryPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        clearHistoryPanel.Controls.Add(clearHistoryButton);
        clearHistoryPanel.Controls.Add(clearHistoryHint);
        root.Controls.Add(CreateFieldLabel("Maintenance"), 0, 3);
        root.Controls.Add(clearHistoryPanel, 1, 3);

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
        root.Controls.Add(CreateFieldLabel("Enabled"), 0, 0);
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
        root.Controls.Add(CreateFieldLabel("Provider"), 0, 1);
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
        _postProcessorApiKeyTextBox = new TextBox { Dock = DockStyle.Left, Width = 280, Text = settings.Provider.ApiKey };
        _postProcessorDeploymentNameTextBox = new TextBox { Dock = DockStyle.Left, Width = 280, Text = settings.Provider.DeploymentName };
        _postProcessorModelTextBox = new TextBox { Dock = DockStyle.Left, Width = 280, Text = settings.Model };

        providerLayout.Controls.Add(CreateFieldLabel("Endpoint / Base URL"), 0, 0);
        providerLayout.Controls.Add(_postProcessorEndpointTextBox, 1, 0);
        providerLayout.Controls.Add(CreateFieldLabel("API Key"), 0, 1);
        providerLayout.Controls.Add(_postProcessorApiKeyTextBox, 1, 1);
        providerLayout.Controls.Add(CreateFieldLabel("Deployment Name"), 0, 2);
        providerLayout.Controls.Add(_postProcessorDeploymentNameTextBox, 1, 2);
        providerLayout.Controls.Add(CreateFieldLabel("Model"), 0, 3);
        providerLayout.Controls.Add(_postProcessorModelTextBox, 1, 3);
        providerPanel.Controls.Add(providerLayout);
        root.Controls.Add(CreateFieldLabel("Provider fields"), 0, 2);
        root.Controls.Add(providerPanel, 1, 2);

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
        root.Controls.Add(CreateFieldLabel("Preset"), 0, 3);
        root.Controls.Add(_postProcessorPresetComboBox, 1, 3);

        _postProcessorTargetLanguageTextBox = new TextBox
        {
            Dock = DockStyle.Left,
            Width = 220,
            Text = string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "English" : settings.TargetLanguage,
        };
        root.Controls.Add(CreateFieldLabel("Target language"), 0, 4);
        root.Controls.Add(_postProcessorTargetLanguageTextBox, 1, 4);

        _postProcessorSystemPromptTextBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 360,
            Height = 150,
            Text = settings.SystemPrompt,
        };
        root.Controls.Add(CreateFieldLabel("System prompt"), 0, 5);
        root.Controls.Add(_postProcessorSystemPromptTextBox, 1, 5);

        var hintLabel = new Label
        {
            Text = "Preset-driven prompt updates and provider binding are added in later steps.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 3),
        };
        root.Controls.Add(CreateFieldLabel("Status"), 0, 6);
        root.Controls.Add(hintLabel, 1, 6);

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
            };
            fieldInputs[field.Label] = textBox;

            layout.Controls.Add(CreateFieldLabel(field.Label), 0, index);
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

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 11, 3, 3),
        };
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
        if (_audioRecorder is null || _transcriptionService is null || _testConnectionButton is null)
        {
            return;
        }

        _testConnectionButton.Enabled = false;

        try
        {
            _audioRecorder.UpdateSettings(new AudioSettings());
            _audioRecorder.StartRecording();
            await Task.Delay(TimeSpan.FromSeconds(3));
            var recordedAudio = await _audioRecorder.StopRecordingAsync(CancellationToken.None);
            var settings = BuildProviderTestSettings();
            var result = await _transcriptionService.TranscribeAsync(
                recordedAudio.FilePath,
                settings.DefaultLanguage,
                settings,
                CancellationToken.None);

            ShowModelessStatus(
                title: result.Succeeded ? "Test Connection Succeeded" : "Test Connection Failed",
                message: result.Succeeded ? result.OutputText : result.ErrorMessage);
        }
        catch (Exception exception)
        {
            ShowModelessStatus("Test Connection Failed", exception.Message);
        }
        finally
        {
            _testConnectionButton.Enabled = true;
        }
    }

    private AppSettings BuildProviderTestSettings()
    {
        var selectedProvider = (_providerSelectorComboBox?.SelectedItem as ComboBoxItem<ProviderType>)?.Value
            ?? ProviderType.AzureOpenAi;

        var provider = new ProviderSettings
        {
            Type = selectedProvider,
        };

        if (_providerFieldInputs.TryGetValue(selectedProvider, out var fields))
        {
            fields.TryGetValue("Endpoint URL", out var endpointTextBox);
            fields.TryGetValue("Base URL", out var baseUrlTextBox);
            fields.TryGetValue("API Key", out var apiKeyTextBox);
            fields.TryGetValue("API Key (optional)", out var optionalApiKeyTextBox);
            fields.TryGetValue("Deployment Name", out var deploymentTextBox);
            fields.TryGetValue("API Version", out var apiVersionTextBox);
            fields.TryGetValue("Model", out var modelTextBox);
            fields.TryGetValue("Model (optional)", out var optionalModelTextBox);

            provider = provider with
            {
                Endpoint = endpointTextBox?.Text ?? baseUrlTextBox?.Text ?? string.Empty,
                ApiKey = apiKeyTextBox?.Text ?? optionalApiKeyTextBox?.Text ?? string.Empty,
                DeploymentName = deploymentTextBox?.Text ?? string.Empty,
                ApiVersion = apiVersionTextBox?.Text ?? "2024-06-01",
                Model = modelTextBox?.Text ?? optionalModelTextBox?.Text,
                ExtraHeaders = BuildAzureProviderExtraHeaders(fields),
            };
        }

        return new AppSettings
        {
            DefaultLanguage = "auto",
            Provider = provider,
            PostProcessor = new PostProcessorSettings
            {
                Enabled = false,
            },
        };
    }

    private void LoadSettingsIntoForm(AppSettings settings)
    {
        if (_launchAtStartupCheckBox is not null)
        {
            _launchAtStartupCheckBox.Checked = settings.LaunchAtStartup;
        }

        SetComboSelection(_triggerModeComboBox, settings.TriggerMode);
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

        if (_historyEnabledCheckBox is not null)
        {
            _historyEnabledCheckBox.Checked = settings.HistoryEnabled;
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
        }
    }

    private AppSettings BuildSettingsFromForm()
    {
        var defaults = new AppSettings();
        var providerType = GetSelectedComboValue(_providerSelectorComboBox, defaults.Provider.Type);
        var provider = BuildProviderSettings(providerType);
        var postProcessorProviderType = GetSelectedComboValue(_postProcessorProviderComboBox, defaults.PostProcessor.Provider.Type);
        var postProcessorProvider = new ProviderSettings
        {
            Type = postProcessorProviderType,
            Endpoint = _postProcessorEndpointTextBox?.Text ?? string.Empty,
            ApiKey = _postProcessorApiKeyTextBox?.Text ?? string.Empty,
            DeploymentName = _postProcessorDeploymentNameTextBox?.Text ?? string.Empty,
            ApiVersion = "2024-06-01",
            Model = _postProcessorModelTextBox?.Text ?? string.Empty,
        };

        var quickLanguages = (_quickLanguagesTextBox?.Text ?? string.Empty)
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new AppSettings
        {
            LaunchAtStartup = _launchAtStartupCheckBox?.Checked ?? defaults.LaunchAtStartup,
            TriggerMode = GetSelectedComboValue(_triggerModeComboBox, defaults.TriggerMode),
            Hotkey = string.IsNullOrWhiteSpace(_hotkeyTextBox?.Text) ? defaults.Hotkey : _hotkeyTextBox.Text,
            OutputMode = GetSelectedComboValue(_outputModeComboBox, defaults.OutputMode),
            IndicatorStyle = GetSelectedComboValue(_indicatorStyleComboBox, defaults.IndicatorStyle),
            DefaultLanguage = GetSelectedComboValue(_defaultLanguageComboBox, defaults.DefaultLanguage),
            QuickLanguages = quickLanguages.Length > 0 ? quickLanguages : defaults.QuickLanguages,
            HistoryEnabled = _historyEnabledCheckBox?.Checked ?? defaults.HistoryEnabled,
            Provider = provider,
            Audio = new AudioSettings
            {
                DeviceId = ResolveSelectedDeviceId(),
                Format = GetSelectedComboValue(_audioFormatComboBox, defaults.Audio.Format),
                SampleRate = (int?)_sampleRateInput?.Value ?? defaults.Audio.SampleRate,
                VadSilenceThreshold = (double?)_silenceThresholdInput?.Value ?? defaults.Audio.VadSilenceThreshold,
                VadMinSpeechMs = (int?)_minSpeechInput?.Value ?? defaults.Audio.VadMinSpeechMs,
            },
            PostProcessor = new PostProcessorSettings
            {
                Enabled = _postProcessorEnabledCheckBox?.Checked ?? defaults.PostProcessor.Enabled,
                Provider = postProcessorProvider,
                Model = _postProcessorModelTextBox?.Text ?? string.Empty,
                TargetLanguage = _postProcessorTargetLanguageTextBox?.Text ?? defaults.PostProcessor.TargetLanguage,
                PresetName = GetSelectedComboValue(_postProcessorPresetComboBox, defaults.PostProcessor.PresetName),
                SystemPrompt = _postProcessorSystemPromptTextBox?.Text ?? defaults.PostProcessor.SystemPrompt,
            },
        };
    }

    private void SaveWithoutClosing()
    {
        var settings = BuildSettingsFromForm();
        _settingsManager.Save(settings);
        LoadSettingsIntoForm(_settingsManager.Load());
        SettingsApplied?.Invoke(settings);
    }

    private void SaveAndClose()
    {
        var settings = BuildSettingsFromForm();
        _settingsManager.Save(settings);
        LoadSettingsIntoForm(_settingsManager.Load());
        SettingsApplied?.Invoke(settings);
        DialogResult = DialogResult.OK;
        Close();
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
        SetTextBox(fields, "Realtime Calls Endpoint (optional)", GetExtraHeaderValue(provider, "Bisik-Realtime-Calls-Endpoint"));
    }

    private void ApplyPostProcessorProviderFields(PostProcessorSettings settings)
    {
        if (_postProcessorEndpointTextBox is not null) _postProcessorEndpointTextBox.Text = settings.Provider.Endpoint;
        if (_postProcessorApiKeyTextBox is not null) _postProcessorApiKeyTextBox.Text = settings.Provider.ApiKey;
        if (_postProcessorDeploymentNameTextBox is not null) _postProcessorDeploymentNameTextBox.Text = settings.Provider.DeploymentName;
        if (_postProcessorModelTextBox is not null) _postProcessorModelTextBox.Text = settings.Model;
    }

    private ProviderSettings BuildProviderSettings(ProviderType providerType, string endpointLabel = "Endpoint URL")
    {
        var provider = new ProviderSettings
        {
            Type = providerType,
        };

        if (_providerFieldInputs.TryGetValue(providerType, out var fields))
        {
            provider = provider with
            {
                Endpoint = GetFieldText(fields, endpointLabel, GetFieldText(fields, "Base URL")),
                ApiKey = GetFieldText(fields, "API Key", GetFieldText(fields, "API Key (optional)")),
                DeploymentName = GetFieldText(fields, "Deployment Name"),
                ApiVersion = GetFieldText(fields, "API Version", "2024-06-01"),
                Model = GetFieldText(fields, "Model", GetFieldText(fields, "Model (optional)")),
                ExtraHeaders = BuildAzureProviderExtraHeaders(fields),
            };
        }

        return provider;
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

    private static Dictionary<string, string>? BuildAzureProviderExtraHeaders(Dictionary<string, TextBox> fields)
    {
        var realtimeCallsEndpoint = GetFieldText(fields, "Realtime Calls Endpoint (optional)");
        if (string.IsNullOrWhiteSpace(realtimeCallsEndpoint))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bisik-Realtime-Calls-Endpoint"] = realtimeCallsEndpoint,
        };
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
