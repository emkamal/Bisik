using System.ComponentModel;
using Bisik.Core;
using Bisik.Models;

namespace Bisik.UI;

public sealed class HistoryForm : Form
{
    private readonly HistoryLogger _historyLogger;
    private readonly BindingList<HistoryRow> _rows = [];
    private readonly DataGridView _grid;
    private readonly TextBox _searchTextBox;
    private readonly ComboBox _providerFilterComboBox;
    private readonly ComboBox _languageFilterComboBox;
    private readonly Button _copyButton;
    private readonly Button _deleteButton;
    private readonly Button _clearButton;
    private readonly Button _exportButton;
    private bool _suppressFilterRefresh;

    public HistoryForm()
        : this(historyLogger: null)
    {
    }

    public HistoryForm(HistoryLogger? historyLogger)
    {
        _historyLogger = historyLogger ?? new HistoryLogger();
        Text = "Bisik History";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 520);
        Size = new Size(1100, 640);

        _searchTextBox = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search history...",
            Margin = new Padding(0, 0, 0, 8),
        };
        _searchTextBox.TextChanged += async (_, _) => await RefreshRowsAsync();

        _providerFilterComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
        };
        _providerFilterComboBox.Items.Add("All providers");
        _providerFilterComboBox.SelectedIndex = 0;
        _providerFilterComboBox.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppressFilterRefresh)
            {
                return;
            }

            await RefreshRowsAsync();
        };

        _languageFilterComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 160,
        };
        _languageFilterComboBox.Items.Add("All languages");
        _languageFilterComboBox.SelectedIndex = 0;
        _languageFilterComboBox.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppressFilterRefresh)
            {
                return;
            }

            await RefreshRowsAsync();
        };

        _copyButton = new Button
        {
            Text = "Copy Entry",
            AutoSize = true,
            Enabled = false,
        };
        _copyButton.Click += (_, _) => CopySelectedEntry();

        _deleteButton = new Button
        {
            Text = "Delete Entry",
            AutoSize = true,
            Enabled = false,
        };
        _deleteButton.Click += async (_, _) => await DeleteSelectedEntryAsync();

        _clearButton = new Button
        {
            Text = "Clear All",
            AutoSize = true,
        };
        _clearButton.Click += async (_, _) => await ClearAllAsync();

        _exportButton = new Button
        {
            Text = "Export TXT",
            AutoSize = true,
        };
        _exportButton.Click += async (_, _) => await ExportVisibleEntriesAsync();

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            DataSource = _rows,
        };
        _grid.SelectionChanged += (_, _) =>
        {
            var hasSelection = _grid.CurrentRow?.DataBoundItem is HistoryRow;
            _copyButton.Enabled = hasSelection;
            _deleteButton.Enabled = hasSelection;
        };

        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Timestamp), "Timestamp", 180));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Provider), "Provider", 130));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Language), "Language", 110));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.DurationMs), "Duration (ms)", 110));
        _grid.Columns.Add(CreateFillColumn(nameof(HistoryRow.RawText), "Raw Text"));
        _grid.Columns.Add(CreateFillColumn(nameof(HistoryRow.Text), "Final Text"));

        var filterBar = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };
        filterBar.Controls.Add(_searchTextBox);
        filterBar.Controls.Add(_providerFilterComboBox);
        filterBar.Controls.Add(_languageFilterComboBox);
        filterBar.Controls.Add(_copyButton);
        filterBar.Controls.Add(_deleteButton);
        filterBar.Controls.Add(_clearButton);
        filterBar.Controls.Add(_exportButton);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(filterBar, 0, 0);
        root.Controls.Add(_grid, 0, 1);

        Controls.Add(root);
        Shown += async (_, _) => await RefreshRowsAsync();
    }

    private async Task RefreshRowsAsync()
    {
        var entries = string.IsNullOrWhiteSpace(_searchTextBox.Text)
            ? await _historyLogger.ReadAllAsync()
            : await _historyLogger.SearchAsync(_searchTextBox.Text);
        PopulateFilterItemsSafely(entries);

        var filteredEntries = entries
            .Where(entry =>
                IsAllFilter(_providerFilterComboBox) ||
                string.Equals(entry.Provider, _providerFilterComboBox.SelectedItem as string, StringComparison.OrdinalIgnoreCase))
            .Where(entry =>
                IsAllFilter(_languageFilterComboBox) ||
                string.Equals(entry.Language, _languageFilterComboBox.SelectedItem as string, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Timestamp)
            .ToArray();

        _rows.Clear();

        foreach (var entry in filteredEntries)
        {
            _rows.Add(new HistoryRow(
                TimestampValue: entry.Timestamp,
                Timestamp: entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                Provider: entry.Provider,
                Language: entry.Language,
                DurationMs: entry.DurationMs.ToString(),
                RawText: entry.RawText ?? string.Empty,
                Text: entry.Text));
        }
    }

    private void PopulateFilterItems(IReadOnlyList<TranscriptionEntry> entries)
    {
        PopulateFilterComboBox(
            _providerFilterComboBox,
            "All providers",
            entries.Select(entry => entry.Provider));
        PopulateFilterComboBox(
            _languageFilterComboBox,
            "All languages",
            entries.Select(entry => entry.Language));
    }

    private static void PopulateFilterComboBox(ComboBox comboBox, string allLabel, IEnumerable<string> values)
    {
        if (comboBox is null)
        {
            return;
        }

        var currentSelection = comboBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(currentSelection))
        {
            currentSelection = allLabel;
        }

        var normalizedValues = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        comboBox.BeginUpdate();
        comboBox.Items.Clear();
        comboBox.Items.Add(allLabel);
        foreach (var value in normalizedValues)
        {
            comboBox.Items.Add(value);
        }

        var targetSelection = comboBox.Items
            .Cast<object>()
            .Select(item => item?.ToString())
            .Any(item => string.Equals(item, currentSelection, StringComparison.OrdinalIgnoreCase))
            ? currentSelection
            : allLabel;
        comboBox.SelectedItem = targetSelection;
        comboBox.EndUpdate();
    }

    private void PopulateFilterItemsSafely(IReadOnlyList<TranscriptionEntry> entries)
    {
        _suppressFilterRefresh = true;
        try
        {
            PopulateFilterItems(entries);
        }
        finally
        {
            _suppressFilterRefresh = false;
        }
    }

    private static bool IsAllFilter(ComboBox comboBox)
    {
        return comboBox.SelectedIndex <= 0;
    }

    private static DataGridViewTextBoxColumn CreateTextColumn(string propertyName, string headerText, int width)
    {
        return new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = headerText,
            Width = width,
            SortMode = DataGridViewColumnSortMode.Automatic,
        };
    }

    private static DataGridViewTextBoxColumn CreateFillColumn(string propertyName, string headerText)
    {
        return new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = headerText,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.Automatic,
        };
    }

    private void CopySelectedEntry()
    {
        if (_grid.CurrentRow?.DataBoundItem is not HistoryRow row)
        {
            return;
        }

        var textToCopy = string.IsNullOrWhiteSpace(row.Text)
            ? row.RawText
            : row.Text;

        if (string.IsNullOrWhiteSpace(textToCopy))
        {
            return;
        }

        Clipboard.SetText(textToCopy);
    }

    private async Task DeleteSelectedEntryAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is not HistoryRow row)
        {
            return;
        }

        await _historyLogger.DeleteAsync(row.TimestampValue, row.Text);
        await RefreshRowsAsync();
    }

    private async Task ClearAllAsync()
    {
        var confirmation = MessageBox.Show(
            this,
            "Clear all transcription history?",
            "Clear History",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        await _historyLogger.ClearAsync();
        await RefreshRowsAsync();
    }

    private async Task ExportVisibleEntriesAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "txt",
            AddExtension = true,
            FileName = "bisik-history.txt",
            Title = "Export History",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        var visibleEntries = _rows
            .Select(row => new TranscriptionEntry(
                row.TimestampValue,
                row.Text,
                string.IsNullOrWhiteSpace(row.RawText) ? null : row.RawText,
                row.Provider,
                row.Language,
                long.TryParse(row.DurationMs, out var durationMs) ? durationMs : 0,
                AudioFilePath: null))
            .ToArray();

        await using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);

        foreach (var entry in visibleEntries)
        {
            await writer.WriteLineAsync($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz} | {entry.Text}");
        }
    }

    private sealed record HistoryRow(
        DateTimeOffset TimestampValue,
        string Timestamp,
        string Provider,
        string Language,
        string DurationMs,
        string RawText,
        string Text);
}
