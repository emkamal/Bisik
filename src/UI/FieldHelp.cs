namespace Bisik.UI;

// Shared native info buttons retain the Settings tooltip's wrapping and keyboard behavior.
internal sealed class FieldHelp : IDisposable
{
    private readonly Control _owner;
    private readonly ToolTip _tooltip = new() { OwnerDraw = true, ShowAlways = true, InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 30000 };
    private bool _disposed;
    private const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private sealed record Content(string Title, string Description);

    public FieldHelp(Control owner) { _owner = owner; _tooltip.Popup += Popup; _tooltip.Draw += Draw; }
    public Control CreateLabel(string title, string description)
    {
        var label = new Label { Text = title, AutoSize = true, MaximumSize = new(140, 0), Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        var info = new Button { Text = "ⓘ", Size = new(24, 24), Anchor = AnchorStyles.Left, Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat, ForeColor = SystemColors.HotTrack, Cursor = Cursors.Help,
            AccessibleName = $"{title} help", AccessibleDescription = description, Tag = new Content(title, description) };
        info.FlatAppearance.BorderSize = 0; _tooltip.SetToolTip(info, description);
        void Show() { if (!_disposed) _tooltip.Show(description, info, 0, info.Height, _tooltip.AutoPopDelay); }
        void Hide() { if (!_disposed) _tooltip.Hide(info); }
        info.Click += (_, _) => Show(); info.Enter += (_, _) => Show(); info.Leave += (_, _) => Hide();
        info.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new(3, 5, 3, 3) };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100)); panel.ColumnStyles.Add(new(SizeType.Absolute, 24));
        panel.Controls.Add(label, 0, 0); panel.Controls.Add(info, 1, 0); return panel;
    }
    private void Popup(object? sender, PopupEventArgs e)
    {
        if (e.AssociatedControl?.Tag is not Content help) return;
        var padding = _owner.LogicalToDeviceUnits(12);
        var width = Math.Min(_owner.LogicalToDeviceUnits(420), Screen.FromControl(e.AssociatedControl).WorkingArea.Width - padding * 2);
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
    public void Dispose() { if (_disposed) return; _disposed = true; _tooltip.Dispose(); }
}
