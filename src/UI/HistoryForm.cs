using System.ComponentModel;
using Bisik.Core;
using Bisik.Models;
using NAudio.Wave;

namespace Bisik.UI;

public sealed class HistoryForm : Form
{
    private readonly HistoryLogger _historyLogger;
    private readonly Action<DataObject> _writeClipboard;
    private readonly BindingList<HistoryRow> _rows = [];
    private readonly DataGridView _grid;
    private readonly TextBox _searchTextBox;
    private readonly ComboBox _providerFilterComboBox;
    private readonly ComboBox _languageFilterComboBox;
    private readonly Button _copyButton;
    private readonly Button _deleteButton;
    private readonly Button _clearButton;
    private readonly Button _exportButton;
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ContextMenuStrip _copyMenu = new();
    private readonly ToolStripMenuItem _copyCellItem = new("Copy cell value");
    private readonly Label _refreshStatus = new() { AutoSize = true, Dock = DockStyle.Fill, Visible = false };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 250 };
    private readonly CancellationTokenSource _refreshCancellation = new();
    private FileSystemWatcher? _historyWatcher;
    private Task _refreshTask = Task.CompletedTask;
    private int _refreshVersion;
    private bool _refreshPending;
    private bool _rowsLoaded;
    private bool _resourcesDisposed;
    private DataGridViewCell? _contextCell;
    private bool _suppressFilterRefresh;
    private WaveOutEvent? _player;
    private AudioFileReader? _playbackAudio;
    private string? _playingPath;

    public HistoryForm()
        : this(historyLogger: null)
    {
    }

    public HistoryForm(HistoryLogger? historyLogger)
        : this(historyLogger, data => Clipboard.SetDataObject(data, true, 5, 50))
    {
    }

    internal HistoryForm(HistoryLogger? historyLogger, Action<DataObject> writeClipboard)
    {
        _historyLogger = historyLogger ?? new HistoryLogger();
        _writeClipboard = writeClipboard;
        Text = "Bisik History";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
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
            Text = "Copy Rows",
            AutoSize = true,
            Enabled = false,
        };
        _copyButton.Click += (_, _) => _copyMenu.Show(_copyButton, new Point(0, _copyButton.Height));

        _deleteButton = new Button
        {
            Text = "Delete Rows",
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
            MultiSelect = true,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable,
            DataSource = _rows,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        };
        _grid.SelectionChanged += (_, _) =>
        {
            var hasSelection = _grid.SelectedRows.Count > 0;
            _copyButton.Enabled = hasSelection;
            _deleteButton.Enabled = hasSelection;
        };

        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Timestamp), "Timestamp", 180));
        _grid.Columns.Add(CreateFillColumn(nameof(HistoryRow.RawText), "Raw Text"));
        _grid.Columns.Add(CreateFillColumn(nameof(HistoryRow.Text), "Final Text"));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.RecordingDurationMs), "Recording (ms)", 125));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.StopToResultDurationMs), "Stop to result (ms)", 145));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.CaptureToResultDurationMs), "Start to result (ms)", 145));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.DurationMs), "Processing (ms)", 125));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.TranscriptionDurationMs), "Transcription (ms)", 130));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.PostProcessingDurationMs), "Post-Proc (ms)", 120));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.PostProcessingApplied), "Cleanup applied", 125));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Provider), "Provider", 130));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Language), "Language", 110));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Configuration), "Configuration snapshot", 300));
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.AudioFilePath), "Audio file", 200));
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Playback", Width = 90,
            HeaderCell = new DataGridViewColumnHeaderCell { ToolTipText = "Play the original recording. Click Stop to end playback. Missing recordings cannot be played." },
            HeaderText = "Audio",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(CreateTextColumn(nameof(HistoryRow.Status), "Status", 120));
        _grid.Columns["Playback"].DisplayIndex = 1;
        _grid.Columns[_grid.Columns.Count - 1].DisplayIndex = 2;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Playback") return;
            var path = (_grid.Rows[e.RowIndex].DataBoundItem as HistoryRow)?.Entry.AudioFilePath;
            e.Value = path == _playingPath && _player is not null ? "Stop"
                : File.Exists(path) ? "Play" : "Unavailable";
            e.FormattingApplied = true;
        };
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "Playback"
                && _grid.Rows[e.RowIndex].DataBoundItem is HistoryRow row) TogglePlayback(row.Entry.AudioFilePath);
        };
        AddCopyItems(_copyMenu);
        AddCopyItems(_rowMenu);
        _rowMenu.Items.Add(new ToolStripSeparator());
        _rowMenu.Items.Add(_copyCellItem);
        _copyCellItem.Click += (_, _) => CopyCellValue();
        _rowMenu.Opening += (_, e) =>
        {
            e.Cancel = GetSelectedEntries().Length == 0;
            _copyCellItem.Enabled = _contextCell is not null;
        };
        _grid.CellMouseDown += HandleCellMouseDown;
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedRows(HistoryCopyFormat.Table);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10))
            {
                _contextCell = _grid.CurrentCell;
                _rowMenu.Show(Cursor.Position);
                e.SuppressKeyPress = true;
            }
        };

        var filterBar = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
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
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(filterBar, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(_refreshStatus, 0, 2);

        Controls.Add(root);
        Shown += async (_, _) =>
        {
            _historyLogger.Changed += HandleHistoryChanged;
            StartHistoryWatcher();
            await RefreshRowsAsync();
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            StartHistoryWatcher();
            await RefreshRowsAsync();
            if (!_refreshCancellation.IsCancellationRequested && _historyWatcher is null)
            {
                _refreshTimer.Interval = 2000;
                _refreshTimer.Start();
            }
        };
    }

    private void StartHistoryWatcher()
    {
        if (_historyWatcher is not null || _refreshCancellation.IsCancellationRequested) return;
        FileSystemWatcher? watcher = null;
        try
        {
            var directory = Path.GetDirectoryName(_historyLogger.HistoryPath)!;
            // Watch the directory so deleting or atomically replacing history.jsonl keeps working.
            Directory.CreateDirectory(directory);
            watcher = new FileSystemWatcher(directory, Path.GetFileName(_historyLogger.HistoryPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => QueueHistoryRefresh();
            watcher.Created += (_, _) => QueueHistoryRefresh();
            watcher.Deleted += (_, _) => QueueHistoryRefresh();
            watcher.Renamed += (_, _) => QueueHistoryRefresh();
            watcher.Error += (_, e) => QueueHistoryRefresh(e.GetException());
            watcher.EnableRaisingEvents = true;
            _historyWatcher = watcher;
            _refreshTimer.Interval = 250;
        }
        catch (Exception exception)
        {
            watcher?.Dispose();
            ShowRefreshError(exception, "Could not watch history. Bisik will retry automatically.");
            _refreshTimer.Interval = 2000;
            _refreshTimer.Start();
        }
    }

    private void HandleHistoryChanged() => QueueHistoryRefresh();

    private void QueueHistoryRefresh(Exception? watcherError = null)
    {
        // Watcher and logger callbacks only post work; persistence never waits for the grid.
        if (!IsHandleCreated || IsDisposed || _refreshCancellation.IsCancellationRequested) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (_refreshCancellation.IsCancellationRequested) return;
                if (watcherError is not null)
                {
                    _historyWatcher?.Dispose();
                    _historyWatcher = null;
                    ShowRefreshError(watcherError, "History monitoring was interrupted. Bisik will reconnect automatically.");
                }
                _refreshVersion++;
                _refreshPending = true;
                _refreshTimer.Stop();
                _refreshTimer.Interval = 250;
                _refreshTimer.Start();
            }));
        }
        catch (InvalidOperationException) when (Disposing || IsDisposed || !IsHandleCreated)
        {
            // The window can close between a file notification and BeginInvoke.
        }
    }

    private Task RefreshRowsAsync()
    {
        if (_refreshCancellation.IsCancellationRequested) return Task.CompletedTask;
        _refreshVersion++;
        _refreshPending = true;
        if (_refreshTask.IsCompleted) _refreshTask = RefreshPendingRowsAsync();
        return _refreshTask;
    }

    private async Task RefreshPendingRowsAsync()
    {
        var ct = _refreshCancellation.Token;
        while (_refreshPending && !ct.IsCancellationRequested)
        {
            if (_rowMenu.Visible || _copyMenu.Visible)
            {
                _refreshTimer.Start();
                return;
            }
            _refreshPending = false;
            var version = _refreshVersion;
            var query = _searchTextBox.Text;
            try
            {
                // JSON parsing and file I/O stay off the UI thread even for large histories.
                var entries = await Task.Run(() => _historyLogger.SearchAsync(query, ct), ct);
                if (ct.IsCancellationRequested) return;
                if (version != _refreshVersion) continue;
                if (_rowMenu.Visible || _copyMenu.Visible)
                {
                    _refreshPending = true;
                    continue;
                }
                PopulateFilterItemsSafely(entries);
                var filteredEntries = entries
                    .Where(entry => IsAllFilter(_providerFilterComboBox) ||
                        string.Equals(entry.Provider, _providerFilterComboBox.SelectedItem as string, StringComparison.OrdinalIgnoreCase))
                    .Where(entry => IsAllFilter(_languageFilterComboBox) ||
                        string.Equals(entry.Language, _languageFilterComboBox.SelectedItem as string, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(entry => entry.Timestamp).ToArray();
                PopulateRows(filteredEntries);
                if (_historyWatcher is not null)
                {
                    _refreshStatus.Visible = false;
                    _refreshTimer.Interval = 250;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                if (ct.IsCancellationRequested) return;
                ShowRefreshError(exception, "Could not refresh history. Displayed entries are unchanged; Bisik will retry automatically.");
                // An external writer can briefly hold an exclusive lock after its last notification.
                _refreshTimer.Interval = 2000;
                _refreshTimer.Start();
            }
        }
    }

    private void ShowRefreshError(Exception exception, string message)
    {
        if (_refreshStatus.Visible && _refreshStatus.Text == message) return;
        _refreshStatus.Text = message;
        _refreshStatus.Visible = true;
        try { new ErrorLogger(Path.GetDirectoryName(_historyLogger.HistoryPath)).Log(exception); }
        catch { /* Do not cascade when the history and error-log directory is unavailable. */ }
    }

    private void PopulateRows(IReadOnlyList<TranscriptionEntry> entries)
    {
        var previousKeys = GetRowKeys(_rows.Select(row => row.Entry));
        var selectedKeys = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(row => row.Index < previousKeys.Length).Select(row => previousKeys[row.Index]).ToHashSet();
        var currentIndex = _grid.CurrentCell?.RowIndex ?? -1;
        var currentColumn = _grid.CurrentCell?.ColumnIndex ?? 0;
        var topIndex = _grid.FirstDisplayedScrollingRowIndex;
        var horizontalOffset = _grid.HorizontalScrollingOffset;
        var newKeys = GetRowKeys(entries).Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index);

        _grid.SuspendLayout();
        _rows.RaiseListChangedEvents = false;
        try
        {
            _rows.Clear();
            foreach (var entry in entries) _rows.Add(new HistoryRow(entry));
        }
        finally { _rows.RaiseListChangedEvents = true; }
        _rows.ResetBindings();
        try
        {
            if (_rowsLoaded)
            {
                _grid.CurrentCell = currentIndex >= 0 && newKeys.TryGetValue(previousKeys[currentIndex], out var current)
                    ? _grid.Rows[current].Cells[currentColumn] : null;
                _grid.ClearSelection();
                foreach (var key in selectedKeys)
                    if (newKeys.TryGetValue(key, out var index)) _grid.Rows[index].Selected = true;
                if (_grid.Rows.Count > 0 && topIndex >= 0)
                {
                    _grid.FirstDisplayedScrollingRowIndex = newKeys.TryGetValue(previousKeys[topIndex], out var top)
                        ? top : Math.Min(topIndex, _grid.Rows.Count - 1);
                    _grid.HorizontalScrollingOffset = horizontalOffset;
                }
            }
        }
        finally { _grid.ResumeLayout(); }
        _rowsLoaded = true;
        _contextCell = null;
        _copyButton.Enabled = _deleteButton.Enabled = _grid.SelectedRows.Count > 0;
    }

    private readonly record struct HistoryRowKey(DateTimeOffset Timestamp, string Text, int Occurrence, Guid? Id);

    private static HistoryRowKey[] GetRowKeys(IEnumerable<TranscriptionEntry> entries)
    {
        var occurrences = new Dictionary<(DateTimeOffset, string), int>();
        return entries.Select(entry =>
        {
            // Occurrences preserve separate rows even when timestamp and final text are identical.
            var key = (entry.Timestamp, entry.Id is null ? entry.Text : "");
            var occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            return new HistoryRowKey(entry.Timestamp, key.Item2, entry.Id is null ? occurrence : 0, entry.Id);
        }).ToArray();
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

        // Keep an active filter even when an external edit or search removes all its matches.
        var normalizedValues = values.Concat(currentSelection == allLabel ? [] : new[] { currentSelection })
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
            .FirstOrDefault(item => string.Equals(item, currentSelection, StringComparison.OrdinalIgnoreCase)) ?? allLabel;
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
            HeaderCell = new DataGridViewColumnHeaderCell { ToolTipText = GetColumnDescription(propertyName) },
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
            HeaderCell = new DataGridViewColumnHeaderCell { ToolTipText = GetColumnDescription(propertyName) },
            DataPropertyName = propertyName,
            HeaderText = headerText,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220,
            SortMode = DataGridViewColumnSortMode.Automatic,
        };
    }

    private static string GetColumnDescription(string propertyName) => propertyName switch
    {
        nameof(HistoryRow.Timestamp) => "When transcription processing began, displayed with the saved time-zone offset.",
        nameof(HistoryRow.RawText) => "Speech-recognition output before optional cleanup.",
        nameof(HistoryRow.Text) => "The chosen output after cleanup, or raw text when cleanup was disabled or skipped.",
        nameof(HistoryRow.RecordingDurationMs) => "Captured audio length in milliseconds, including pauses, silence and VAD pre-roll. Blank when disabled or unavailable.",
        nameof(HistoryRow.StopToResultDurationMs) => "Milliseconds from Stop, including capture drain, until the chosen result exists. File-only calls use processing elapsed time. Excludes history, sound and output dispatch.",
        nameof(HistoryRow.CaptureToResultDurationMs) => "Milliseconds from recording activation until the chosen result exists. Blank when unavailable. Excludes history, sound and output dispatch.",
        nameof(HistoryRow.DurationMs) => "Sum of the measured transcription and post-processing stages in milliseconds. Excludes recording, history, sound and output dispatch; live work can overlap recording.",
        nameof(HistoryRow.TranscriptionDurationMs) => "Milliseconds spent completing recognition, including applicable retries and fallback. Live recognition can overlap recording.",
        nameof(HistoryRow.PostProcessingDurationMs) => "Milliseconds spent completing or selecting optional cleanup. Speculative cleanup can overlap recording; time can be spent even when raw output is selected.",
        nameof(HistoryRow.PostProcessingApplied) => "True when cleanup was selected for the final result; the wording may remain unchanged. False when disabled or skipped. Blank when unavailable in older entries.",
        nameof(HistoryRow.Provider) => "The speech-recognition provider, rather than the cleanup model. Model and deployment details are in the optional configuration snapshot.",
        nameof(HistoryRow.Language) => "The requested recognition language. 'auto' means automatic detection was requested, rather than a detected-language result.",
        nameof(HistoryRow.Configuration) => "Optional settings captured for this dictation, including models, audio, cleanup and prompts. Excludes keys, endpoint URLs and extra-header names/values. Blank when disabled or unavailable.",
        nameof(HistoryRow.AudioFilePath) => "Stored recording path. The file may subsequently have been moved or removed; older entries may have no path.",
        nameof(HistoryRow.Status) => "Updates as recognition and cleanup finish. Failed, cancelled and no-speech dictations retain their recorded audio when History is enabled.",
        _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "A history column needs a help description."),
    };

    private void AddCopyItems(ContextMenuStrip menu)
    {
        foreach (var (label, format) in new[]
        {
            ("Copy rows as JSON", HistoryCopyFormat.Json),
            ("Copy rows as CSV", HistoryCopyFormat.Csv),
            ("Copy rows as table (Excel)", HistoryCopyFormat.Table),
        })
        {
            var item = menu.Items.Add(label);
            item.Click += (_, _) => CopySelectedRows(format);
        }
    }

    private void HandleCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        // Preserve the multi-selection when right-clicking inside it.
        if (!row.Selected)
        {
            _grid.ClearSelection();
            row.Selected = true;
        }
        _contextCell = e.ColumnIndex >= 0 ? row.Cells[e.ColumnIndex] : null;
        _rowMenu.Show(Cursor.Position);
    }

    private TranscriptionEntry[] GetSelectedEntries() => _grid.SelectedRows.Cast<DataGridViewRow>()
        .OrderBy(row => row.Index).Select(row => row.DataBoundItem).OfType<HistoryRow>()
        .Select(row => row.Entry).ToArray();

    private void CopySelectedRows(HistoryCopyFormat format)
    {
        var entries = GetSelectedEntries();
        if (entries.Length == 0) return;
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, HistoryExport.Format(entries, format));
            if (format == HistoryCopyFormat.Table) data.SetData(DataFormats.Html, HistoryExport.HtmlClipboard(entries));
            // WinForms retries transient clipboard contention; failures remain visible to the user.
            _writeClipboard(data);
        }
        catch (Exception exception) { ShowCopyError(exception); }
    }

    private void CopyCellValue()
    {
        if (_contextCell is null) return;
        try
        {
            var text = _contextCell.Value?.ToString() ?? string.Empty;
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            _writeClipboard(data);
        }
        catch (Exception exception) { ShowCopyError(exception); }
    }

    private void ShowCopyError(Exception exception)
    {
        new ErrorLogger().Log(exception);
        MessageBox.Show(this, "Could not copy to the clipboard. Try copying again.", "Copy History",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task DeleteSelectedEntryAsync()
    {
        var entries = GetSelectedEntries();
        if (entries.Length == 0) return;
        if (entries.Length > 1 && MessageBox.Show(this, $"Delete {entries.Length} selected history entries?",
            "Delete History", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        StopPlayback();
        foreach (var entry in entries) await _historyLogger.DeleteAsync(entry.Timestamp, entry.Text, id: entry.Id);
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
        StopPlayback();
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

        var visibleEntries = _rows.Select(row => row.Entry).ToArray();

        await using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);

        foreach (var entry in visibleEntries)
        {
            await writer.WriteLineAsync($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz} | {entry.Text}");
        }
    }

    private sealed record HistoryRow(TranscriptionEntry Entry)
    {
        public string Status => Entry.Status switch
        {
            HistoryEntryStatus.CleaningUp => "Cleaning up",
            HistoryEntryStatus.NoSpeech => "No speech",
            _ => Entry.Status.ToString(),
        };
        public string Timestamp => Entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz");
        public string Provider => Entry.Provider;
        public string Language => Entry.Language;
        public long DurationMs => Entry.DurationMs;
        public long TranscriptionDurationMs => Entry.TranscriptionDurationMs;
        public long PostProcessingDurationMs => Entry.PostProcessingDurationMs;
        public long? RecordingDurationMs => Entry.RecordingDurationMs;
        public long? StopToResultDurationMs => Entry.StopToResultDurationMs;
        public long? CaptureToResultDurationMs => Entry.CaptureToResultDurationMs;
        public bool? PostProcessingApplied => Entry.PostProcessingApplied;
        public string RawText => Entry.RawText ?? string.Empty;
        public string Text => Entry.Text;
        public string Configuration => HistoryExport.ConfigurationText(Entry.ConfigurationSnapshot);
        public string AudioFilePath => Entry.AudioFilePath ?? string.Empty;
    }

    private void TogglePlayback(string? path)
    {
        var stopOnly = _player is not null && _playingPath == path;
        StopPlayback();
        if (stopOnly) return;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The recording has been moved or removed.");
            _playbackAudio = new AudioFileReader(path);
            _player = new WaveOutEvent();
            _playingPath = path;
            _player.PlaybackStopped += HandlePlaybackStopped;
            _player.Init(_playbackAudio);
            _player.Play();
            _grid.InvalidateColumn(_grid.Columns["Playback"].Index);
        }
        catch (Exception exception)
        {
            StopPlayback();
            ShowRefreshError(exception, "Could not play recording: " + exception.Message);
        }
    }

    private void HandlePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_resourcesDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!ReferenceEquals(sender, _player)) return;
                StopPlayback();
                if (e.Exception is { } error)
                {
                    ShowRefreshError(error, "Audio playback failed: " + error.Message);
                }
            });
        }
        catch (InvalidOperationException) { /* The history window closed before the playback callback. */ }
    }

    private void StopPlayback()
    {
        if (_player is { } player)
        {
            _player = null;
            player.PlaybackStopped -= HandlePlaybackStopped;
            try { player.Stop(); }
            catch (Exception error) { ShowRefreshError(error, "Could not stop audio playback."); }
            finally { player.Dispose(); }
        }
        _playbackAudio?.Dispose();
        _playbackAudio = null;
        _playingPath = null;
        if (!_resourcesDisposed) _grid.InvalidateColumn(_grid.Columns["Playback"].Index);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            StopPlayback();
            _refreshCancellation.Cancel();
            _historyLogger.Changed -= HandleHistoryChanged;
            _historyWatcher?.Dispose();
            _refreshTimer.Dispose();
            _refreshCancellation.Dispose();
            _rowMenu.Dispose();
            _copyMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
