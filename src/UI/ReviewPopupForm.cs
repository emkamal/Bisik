namespace Bisik.UI;

public sealed class ReviewPopupForm : Form
{
    private readonly TextBox _textBox;
    private readonly Button _copyButton;
    private readonly Button _dismissButton;

    public ReviewPopupForm(string text)
    {
        Text = "Bisik Review";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(520, 320);
        MinimumSize = new Size(420, 240);
        TopMost = true;
        ShowInTaskbar = false;

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = text,
        };

        _copyButton = new Button
        {
            Text = "Copy",
            AutoSize = true,
        };
        _copyButton.Click += (_, _) => Clipboard.SetText(_textBox.Text);

        _dismissButton = new Button
        {
            Text = "Dismiss",
            AutoSize = true,
            DialogResult = DialogResult.OK,
        };
        _dismissButton.Click += (_, _) => Close();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        buttonPanel.Controls.Add(_dismissButton);
        buttonPanel.Controls.Add(_copyButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_textBox, 0, 0);
        layout.Controls.Add(buttonPanel, 0, 1);

        Controls.Add(layout);
        AcceptButton = _dismissButton;
    }
}
