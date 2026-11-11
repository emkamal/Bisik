using System.Collections.Immutable;
using Bisik.Models;

namespace Bisik.UI;

/// <summary>Structured editing of the optional cleanup vocabulary without delimiter syntax.</summary>
public sealed class CleanupVocabularyEditor : UserControl
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, PlaceholderText = "e.g. My project", MaxLength = 80, AccessibleName = "Vocabulary profile name" };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true,
        SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = true,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, RowHeadersWidth = 36,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
        AccessibleName = "Canonical vocabulary table",
    };
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Fill };
    private bool _loading;

    public CleanupVocabularyEditor()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
        var profile = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
        profile.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        profile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profile.Controls.Add(new Label { Text = "Profile name", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) });
        profile.Controls.Add(_name);
        layout.Controls.Add(profile, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Help cleanup recognize project names and technical terms. Give the correct spelling, when it applies, and possible mishearings.\nExample: HAPPI  •  ABB History API; preserve ordinary ‘happy’  •  happi, happy\nOptional: leave the name and table empty to omit vocabulary guidance. Used when post-processing is enabled.",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12),
        }, 0, 1);

        foreach (var (title, width, max) in new[] { ("Canonical spelling", 28f, 256), ("Context / when to use", 42f, 512), ("Variants (comma separated)", 30f, 2600) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = title, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = width,
                MinimumWidth = 160, MaxInputLength = max, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True },
            });
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        layout.Controls.Add(_grid, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
        AddButton("Add term", () =>
        {
            _grid.EndEdit();
            var index = _grid.Rows.Add();
            _grid.CurrentCell = _grid.Rows[index].Cells[0];
            _grid.Focus();
            _grid.BeginEdit(true);
        });
        AddButton("Remove selected", () =>
        {
            _grid.EndEdit();
            var indices = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex)
                .Concat(_grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)).Distinct().OrderDescending().ToArray();
            foreach (var index in indices) if (!_grid.Rows[index].IsNewRow) _grid.Rows.RemoveAt(index);
        });
        AddButton("Load ABB template", () => LoadVocabulary(CleanupVocabulary.AbbTemplate));
        AddButton("Clear vocabulary", () => LoadVocabulary(null));
        layout.Controls.Add(buttons, 0, 3);
        layout.Controls.Add(_summary, 0, 4);
        Controls.Add(layout);
        _name.TextChanged += (_, _) => UpdateSummary();
        _grid.CellValueChanged += (_, _) => UpdateSummary();
        _grid.RowsAdded += (_, _) => UpdateSummary();
        _grid.RowsRemoved += (_, _) => UpdateSummary();
        UpdateSummary();

        void AddButton(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) => action();
            buttons.Controls.Add(button);
        }
    }

    public void LoadVocabulary(CleanupVocabulary? vocabulary)
    {
        _loading = true;
        try
        {
            _grid.CancelEdit();
            _grid.Rows.Clear();
            _name.Text = vocabulary?.Name ?? "";
            foreach (var entry in vocabulary?.Entries ?? [])
                _grid.Rows.Add(entry.Canonical, entry.Context, string.Join(", ", entry.Variants));
        }
        finally { _loading = false; }
        UpdateSummary();
    }

    public CleanupVocabulary? GetVocabulary()
    {
        _grid.EndEdit();
        return ReadVocabulary();
    }

    private CleanupVocabulary? ReadVocabulary()
    {
        var entries = ImmutableArray.CreateBuilder<CleanupVocabularyEntry>();
        foreach (DataGridViewRow row in _grid.Rows) row.ErrorText = "";
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            var cells = row.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString()?.Trim() ?? "").ToArray();
            if (cells.All(string.IsNullOrEmpty)) continue;
            var variants = cells[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
            if (cells[0].Length is < 1 or > 256 || cells[1].Length > 512 || variants.Length > 20 || variants.Any(v => v.Length > 128))
            {
                row.ErrorText = $"Row {row.Index + 1}: enter a canonical spelling (up to 256 characters), context up to 512 characters, and at most 20 variants of up to 128 characters each.";
                throw new ArgumentException(row.ErrorText);
            }
            entries.Add(new(cells[0], cells[1], variants));
        }
        if (entries.Count == 0 && string.IsNullOrWhiteSpace(_name.Text)) return null;
        if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException("Give this vocabulary a profile name.");
        if (entries.Count is < 1 or > 32) throw new ArgumentException("Add between 1 and 32 terms, or clear the profile name to omit vocabulary.");
        return new CleanupVocabulary(_name.Text.Trim(), entries.ToImmutable()).Snapshot();
    }

    private void UpdateSummary()
    {
        if (_loading) return;
        // Validate committed cells as the table changes; never open dialogs during editing.
        try
        {
            var vocabulary = ReadVocabulary();
            _summary.Text = vocabulary is null ? "No vocabulary configured." : $"{vocabulary.Entries.Length} / 32 terms · Saved with Apply or OK.";
            _summary.ForeColor = SystemColors.GrayText;
        }
        catch (ArgumentException error)
        {
            _summary.Text = error.Message;
            _summary.ForeColor = Color.Firebrick;
        }
    }
}
