namespace Bisik.UI;

// Shared native help with a lifetime bounded by its owning window.
internal sealed class FieldHelp : IDisposable
{
    private readonly Control _owner;
    private readonly ToolTip _tooltip = new() { OwnerDraw = true, InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 15000 };
    private readonly System.Windows.Forms.Timer _expiration = new() { Interval = 15000 };
    private Control? _shown;
    private bool _disposed;
    private const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private sealed record Content(string Title, string Description);

    public FieldHelp(Control owner)
    {
        _owner = owner; _tooltip.Popup += Popup; _tooltip.Draw += Draw;
        _expiration.Tick += (_, _) => Hide();
        owner.VisibleChanged += OwnerVisibleChanged;
        if (owner is Form form) { form.Deactivate += Deactivate; form.Activated += Activate; form.FormClosing += Closing; }
    }
    public Control CreateLabel(string title, string description)
    {
        var label = new Label { Text = title, AutoSize = true, MaximumSize = new(140, 0), Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        var info = new InfoButton { Text = "ⓘ", Size = new(28, 28), Anchor = AnchorStyles.Left, Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat, ForeColor = SystemColors.HotTrack, Cursor = Cursors.Help,
            AccessibleName = $"{title} help", AccessibleDescription = description, Tag = new Content(title, description) };
        info.FlatAppearance.BorderSize = 0; _tooltip.SetToolTip(info, description);
        void Show()
        {
            if (_disposed || !info.Visible || !_tooltip.Active) return;
            Hide(); _shown = info;
            _tooltip.Show(description, info, 0, info.Height, _tooltip.AutoPopDelay);
            _expiration.Start();
        }
        info.Click += (_, _) => { if (_shown == info) Hide(); else Show(); };
        // Mouse focus precedes Click; showing here would immediately toggle the popup off.
        info.Enter += (_, _) => { if (Control.MouseButtons == MouseButtons.None) Show(); };
        info.Leave += (_, _) => Hide(); info.MouseLeave += (_, _) => Hide();
        info.VisibleChanged += (_, _) => { if (!info.Visible) Hide(); };
        info.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.SuppressKeyPress = true; } };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new(3, 5, 3, 3) };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100)); panel.ColumnStyles.Add(new(SizeType.AutoSize));
        panel.Controls.Add(label, 0, 0); panel.Controls.Add(info, 1, 0); return panel;
    }
    private void Popup(object? sender, PopupEventArgs e)
    {
        if (!_owner.Visible || !_tooltip.Active) { e.Cancel = true; return; }
        if (e.AssociatedControl?.Tag is not Content help) return;
        var padding = _owner.LogicalToDeviceUnits(12);
        var width = Math.Min(_owner.LogicalToDeviceUnits(360), Screen.FromControl(e.AssociatedControl).WorkingArea.Width - padding * 2);
        using var titleFont = new Font(_owner.Font, FontStyle.Bold);
        var title = TextRenderer.MeasureText(help.Title, titleFont, new Size(width, int.MaxValue), TextFlags);
        var body = TextRenderer.MeasureText(help.Description, _owner.Font, new Size(width, int.MaxValue), TextFlags);
        e.ToolTipSize = new(width + padding * 2, title.Height + body.Height + padding * 2 + padding / 2);
    }
    private void Draw(object? sender, DrawToolTipEventArgs e)
    {
        if (e.AssociatedControl?.Tag is not Content help) return;
        e.Graphics.FillRectangle(SystemBrushes.Info, e.Bounds); e.DrawBorder();
        var padding = _owner.LogicalToDeviceUnits(12); var width = e.Bounds.Width - padding * 2;
        using var titleFont = new Font(_owner.Font, FontStyle.Bold);
        var title = TextRenderer.MeasureText(help.Title, titleFont, new Size(width, int.MaxValue), TextFlags);
        TextRenderer.DrawText(e.Graphics, help.Title, titleFont, new Rectangle(padding, padding, width, title.Height), SystemColors.InfoText, TextFlags);
        var bodyTop = padding + title.Height + padding / 2;
        TextRenderer.DrawText(e.Graphics, help.Description, _owner.Font, new Rectangle(padding, bodyTop, width, e.Bounds.Height - bodyTop - padding), SystemColors.InfoText, TextFlags);
    }
    internal bool IsVisible => _shown is not null;
    private void Hide() { if (_disposed) return; _expiration.Stop(); if (_shown is { } shown) _tooltip.Hide(shown); _shown = null; }
    private void Deactivate(object? sender, EventArgs e) { Hide(); _tooltip.Active = false; }
    private void Activate(object? sender, EventArgs e) { if (!_disposed) _tooltip.Active = true; }
    private void Closing(object? sender, FormClosingEventArgs e) => Deactivate(sender, e);
    private void OwnerVisibleChanged(object? sender, EventArgs e) { if (!_owner.Visible) Deactivate(sender, e); else Activate(sender, e); }
    public void Dispose()
    {
        if (_disposed) return; Hide(); _disposed = true;
        _owner.VisibleChanged -= OwnerVisibleChanged;
        if (_owner is Form form) { form.Deactivate -= Deactivate; form.Activated -= Activate; form.FormClosing -= Closing; }
        _expiration.Dispose(); _tooltip.Dispose();
    }

    private sealed class InfoButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            // Draw the icon independently of the system font's glyph fallback and DPI metrics.
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var diameter = Math.Min(ClientSize.Width, ClientSize.Height) * 0.6f;
            var x = (ClientSize.Width - diameter) / 2; var y = (ClientSize.Height - diameter) / 2;
            using var pen = new Pen(ForeColor, Math.Max(1, DeviceDpi / 96f));
            using var brush = new SolidBrush(ForeColor);
            e.Graphics.DrawEllipse(pen, x, y, diameter, diameter);
            var center = ClientSize.Width / 2f;
            e.Graphics.FillEllipse(brush, center - pen.Width, y + diameter * 0.22f, pen.Width * 2, pen.Width * 2);
            e.Graphics.DrawLine(pen, center, y + diameter * 0.45f, center, y + diameter * 0.78f);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }
}
