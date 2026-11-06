using System.Drawing.Drawing2D;

namespace Bisik.UI;

public enum TrayIconState
{
    Idle,
    Recording,
    Transcribing,
    PostProcessing,
    Ready,
}

public sealed class TrayIcon : IDisposable
{
    private static readonly (string Code, string Label)[] DefaultLanguages =
    [
        ("auto", "Auto-detect"),
        ("en", "English"),
        ("id", "Indonesian"),
        ("ja", "Japanese"),
        ("zh", "Chinese"),
        ("es", "Spanish"),
        ("fr", "French"),
        ("de", "German"),
        ("other", "Other"),
    ];

    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<TrayIconState, Icon> _icons;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _languageMenuItem;
    private readonly ToolStripMenuItem _postProcessorMenuItem;
    private readonly Action<string> _onLanguageSelected;
    private readonly Dictionary<string, ToolStripMenuItem> _languageItems;
    private ToolStripMenuItem? _insertResultItem;

    public TrayIcon()
        : this(
            onOpenSettings: static () => { },
            onOpenHistory: static () => { },
            onShowAbout: static () => { },
            onExit: static () => { },
            onLanguageSelected: static _ => { },
            onTogglePostProcessor: static _ => { })
    {
    }

    public TrayIcon(
        Action onOpenSettings,
        Action onOpenHistory,
        Action onShowAbout,
        Action onExit,
        Action<string> onLanguageSelected,
        Action<bool> onTogglePostProcessor)
    {
        _onLanguageSelected = onLanguageSelected ?? throw new ArgumentNullException(nameof(onLanguageSelected));
        _icons = new Dictionary<TrayIconState, Icon>
        {
            [TrayIconState.Idle] = CreateStateIcon(Color.FromArgb(68, 116, 167)),
            [TrayIconState.Recording] = CreateStateIcon(Color.FromArgb(196, 62, 47)),
            [TrayIconState.Transcribing] = CreateStateIcon(Color.FromArgb(208, 140, 36)),
            [TrayIconState.PostProcessing] = CreateStateIcon(Color.FromArgb(78, 154, 168)),
            [TrayIconState.Ready] = CreateStateIcon(Color.FromArgb(78, 154, 168)),
        };
        _languageItems = new Dictionary<string, ToolStripMenuItem>(StringComparer.OrdinalIgnoreCase);
        _languageMenuItem = CreateLanguageMenuItem();
        _postProcessorMenuItem = new ToolStripMenuItem("Post-Processor Enabled")
        {
            CheckOnClick = true,
        };
        _postProcessorMenuItem.CheckedChanged += (_, _) => onTogglePostProcessor(_postProcessorMenuItem.Checked);
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(new ToolStripMenuItem("Settings", image: null, (_, _) => onOpenSettings()));
        _contextMenu.Items.Add(new ToolStripMenuItem("History", image: null, (_, _) => onOpenHistory()));
        _contextMenu.Items.Add(_languageMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_postProcessorMenuItem);
        _contextMenu.Items.Add(new ToolStripMenuItem("About", image: null, (_, _) => onShowAbout()));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Exit", image: null, (_, _) => onExit()));

        _notifyIcon = new NotifyIcon
        {
            Text = "Bisik",
            Visible = true,
            Icon = _icons[TrayIconState.Idle],
            ContextMenuStrip = _contextMenu,
        };
        _notifyIcon.DoubleClick += (_, _) => onOpenSettings();
    }

    public void AddExperimentsCommand(Action open) => _contextMenu.Items.Insert(2,
        new ToolStripMenuItem("Experiments", null, (_, _) => open()));

    public void AddResultCommands(Action insert, Action dismiss)
    {
        _insertResultItem = new ToolStripMenuItem("Insert latest result here", null, (_, _) => insert());
        _contextMenu.Items.Insert(0, _insertResultItem);
        _contextMenu.Items.Insert(1, new ToolStripMenuItem("Dismiss latest result", null, (_, _) => dismiss()));
    }

    public void AddRawRecoveryCommand(Action review) => _contextMenu.Items.Insert(2,
        new ToolStripMenuItem("Review recovered raw text", null, (_, _) => review()));
    public void SetRecoveryAvailable(bool available)
    {
        if (_insertResultItem is not null) _insertResultItem.Text = available ? "Insert recovered raw text here (cleanup failed)" : "Insert latest result here";
    }

    public NotifyIcon NotifyIcon => _notifyIcon;

    public TrayIconState State { get; private set; } = TrayIconState.Idle;

    public string SelectedLanguage { get; private set; } = "auto";

    public void SetState(TrayIconState state)
    {
        State = state;
        _notifyIcon.Icon = _icons[state];
        _notifyIcon.Text = state == TrayIconState.Ready ? "Bisik — latest result ready for manual insertion" : "Bisik";
    }

    public void SetPostProcessorEnabled(bool enabled)
    {
        _postProcessorMenuItem.Checked = enabled;
    }

    public void SetSelectedLanguage(string languageCode)
    {
        SelectedLanguage = string.IsNullOrWhiteSpace(languageCode) ? "auto" : languageCode;

        foreach (var item in _languageItems.Values)
        {
            item.Checked = string.Equals(
                item.Tag as string,
                SelectedLanguage,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();

        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private static Icon CreateStateIcon(Color accentColor)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var outerBrush = new SolidBrush(Color.FromArgb(34, 38, 43));
        using var accentBrush = new SolidBrush(accentColor);
        using var highlightBrush = new SolidBrush(Color.FromArgb(235, 239, 243));

        graphics.FillEllipse(outerBrush, 1, 1, 14, 14);
        graphics.FillEllipse(accentBrush, 3, 3, 10, 10);
        graphics.FillEllipse(highlightBrush, 5, 5, 3, 3);

        var handle = bitmap.GetHicon();
        try
        {
            return Icon.FromHandle(handle).Clone() as Icon ??
                throw new InvalidOperationException("Failed to clone tray icon handle.");
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private ToolStripMenuItem CreateLanguageMenuItem()
    {
        var menuItem = new ToolStripMenuItem("Language");

        foreach (var (code, label) in DefaultLanguages)
        {
            var item = new ToolStripMenuItem(label)
            {
                CheckOnClick = true,
                Tag = code,
            };
            item.Click += (_, _) =>
            {
                SetSelectedLanguage(code);
                _onLanguageSelected(code);
            };

            _languageItems[code] = item;
            menuItem.DropDownItems.Add(item);
        }

        SetSelectedLanguage("auto");
        return menuItem;
    }
}
