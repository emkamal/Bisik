using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Forms;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;
using Bisik.UI;

namespace Bisik.Tests;

public sealed class HistoryFormTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Bisik.HistoryFormTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Every_header_has_help_without_overriding_cell_value_tooltips()
    {
        RunOnSta(() =>
        {
            using var form = new HistoryForm(new HistoryLogger(_directory));
            var grid = Field<DataGridView>(form, "_grid");
            Assert.Equal(16, grid.Columns.Count);
            Assert.True(grid.ShowCellToolTips);
            foreach (DataGridViewColumn column in grid.Columns)
            {
                Assert.False(string.IsNullOrWhiteSpace(column.HeaderText), column.DataPropertyName);
                Assert.False(string.IsNullOrWhiteSpace(column.HeaderCell.ToolTipText), column.HeaderText);
                Assert.Empty(column.CellTemplate!.ToolTipText);
            }
            var language = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "Language");
            Assert.Contains("requested", language.HeaderCell.ToolTipText);
            var timestamp = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "Timestamp");
            Assert.Contains("processing began", timestamp.HeaderCell.ToolTipText);
        });
    }

    [Fact]
    public async Task Background_save_updates_open_history_without_moving_selection_or_reading_position()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 60; i++) await logger.AppendAsync(Entry(now.AddSeconds(i), $"row {i}"));
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 60);
            grid.CurrentCell = grid.Rows[20].Cells[2];
            grid.ClearSelection();
            grid.Rows[20].Selected = true;
            grid.Rows[22].Selected = true;
            grid.FirstDisplayedScrollingRowIndex = 20;
            grid.HorizontalScrollingOffset = 500;
            var selected = SelectedTexts(grid);
            var top = RowText(grid, grid.FirstDisplayedScrollingRowIndex);
            var horizontal = grid.HorizontalScrollingOffset;
            var current = RowText(grid, grid.CurrentCell.RowIndex);

            await Task.Run(() => logger.AppendAsync(Entry(now.AddMinutes(2), "newest")));
            await WaitUntilAsync(() => grid.Rows.Count == 61);
            Assert.Equal("newest", RowText(grid, 0));
            Assert.Equal(selected, SelectedTexts(grid));
            Assert.Equal(current, RowText(grid, grid.CurrentCell!.RowIndex));
            Assert.Equal(top, RowText(grid, grid.FirstDisplayedScrollingRowIndex));
            Assert.Equal(horizontal, grid.HorizontalScrollingOffset);
            form.WindowState = FormWindowState.Minimized;
            await logger.AppendAsync(Entry(now.AddMinutes(3), "while minimized"));
            await WaitUntilAsync(() => grid.Rows.Count == 62);
            Assert.Equal(FormWindowState.Minimized, form.WindowState);
        });
    }

    [Fact]
    public async Task Refresh_keeps_filters_with_no_matches_and_shows_new_matching_raw_text()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        await logger.AppendAsync(Entry(now, "old A", provider: "A"));
        await logger.AppendAsync(Entry(now.AddSeconds(1), "old B", provider: "B", language: "id"));
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 2);
            var provider = Field<ComboBox>(form, "_providerFilterComboBox");
            var language = Field<ComboBox>(form, "_languageFilterComboBox");
            provider.SelectedItem = "A";
            language.SelectedItem = "en";
            Field<TextBox>(form, "_searchTextBox").Text = "needle";
            await Invoke<Task>(form, "RefreshRowsAsync");
            Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("A", provider.SelectedItem);
            Assert.Equal("en", language.SelectedItem);

            await logger.AppendAsync(Entry(now.AddSeconds(2), "needle B", provider: "B"));
            await Invoke<Task>(form, "RefreshRowsAsync");
            Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("A", provider.SelectedItem);
            await logger.AppendAsync(Entry(now.AddSeconds(3), "matched", provider: "A") with { RawText = "NEEDLE in raw" });
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            Assert.Equal("matched", RowText(grid, 0));
            Assert.Equal("A", provider.SelectedItem);
            Assert.Equal("en", language.SelectedItem);
            await File.WriteAllTextAsync(logger.HistoryPath,
                JsonSerializer.Serialize(Entry(now.AddSeconds(4), "case-insensitive needle", provider: "a", language: "EN")) + "\n");
            await Invoke<Task>(form, "RefreshRowsAsync");
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("a", provider.SelectedItem);
            Assert.Equal("EN", language.SelectedItem);
        });
    }

    [Fact]
    public async Task Completed_transcription_pipeline_shows_raw_and_cleaned_result_automatically()
    {
        var logger = new HistoryLogger(_directory);
        var settings = new AppSettings { Provider = new() { Type = ProviderType.OpenAi }, PostProcessor = new() { Enabled = true } };
        var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider>
        {
            [ProviderType.OpenAi] = new FixedTranscriber(),
        }, new FixedCleanup(), logger, new ErrorLogger(_directory), () => { }, () => { });
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await Invoke<Task>(form, "RefreshRowsAsync");
            var result = await Task.Run(() => service.TranscribeAsync(Path.Combine(_directory, "synthetic.wav"), "en", settings, CancellationToken.None));
            Assert.True(result.Succeeded);
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            Assert.Equal("raw result", grid.Rows[0].Cells[1].Value);
            Assert.Equal("cleaned result", RowText(grid, 0));
        });
    }

    [Fact]
    public async Task Watcher_error_reconnects_and_reloads_before_following_external_changes()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        await logger.AppendAsync(Entry(now, "original"));
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            var watcher = Field<FileSystemWatcher>(form, "_historyWatcher");
            Invoke<object?>(form, "QueueHistoryRefresh", new InternalBufferOverflowException("Synthetic watcher overflow."));
            await WaitUntilAsync(() => Field<FileSystemWatcher?>(form, "_historyWatcher") is { } replacement && replacement != watcher);
            await File.WriteAllTextAsync(logger.HistoryPath, JsonSerializer.Serialize(Entry(now, "after reconnect")) + "\n");
            await WaitUntilAsync(() => grid.Rows.Count == 1 && RowText(grid, 0) == "after reconnect");
            Assert.False(Field<Label>(form, "_refreshStatus").Visible);
        });
    }

    [Fact]
    public async Task External_append_replace_rename_delete_and_recreate_update_without_reopening()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        await logger.AppendAsync(Entry(now, "original"));
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            await File.AppendAllTextAsync(logger.HistoryPath, JsonSerializer.Serialize(Entry(now.AddSeconds(1), "external")) + "\n");
            await WaitUntilAsync(() => grid.Rows.Count == 2);
            Assert.Equal("external", RowText(grid, 0));

            var replacement = Path.Combine(_directory, "replacement.tmp");
            await File.WriteAllTextAsync(replacement, JsonSerializer.Serialize(Entry(now.AddSeconds(2), "replacement")) + "\n");
            File.Move(replacement, logger.HistoryPath, overwrite: true);
            await WaitUntilAsync(() => grid.Rows.Count == 1 && RowText(grid, 0) == "replacement");
            File.Move(logger.HistoryPath, replacement);
            await WaitUntilAsync(() => grid.Rows.Count == 0);
            File.Move(replacement, logger.HistoryPath);
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            File.Delete(logger.HistoryPath);
            await WaitUntilAsync(() => grid.Rows.Count == 0);
            await File.WriteAllTextAsync(logger.HistoryPath, JsonSerializer.Serialize(Entry(now, "recreated")) + "\n");
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            Assert.Equal("recreated", RowText(grid, 0));
        });
    }

    [Fact]
    public async Task Empty_history_starts_watching_before_the_first_external_file_is_created()
    {
        var logger = new HistoryLogger(_directory);
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => Field<FileSystemWatcher?>(form, "_historyWatcher") is not null);
            await File.WriteAllTextAsync(logger.HistoryPath, JsonSerializer.Serialize(Entry(DateTimeOffset.UtcNow, "first")) + "\n");
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            Assert.Equal("first", RowText(grid, 0));
        });
    }

    [Fact]
    public async Task Locked_external_file_keeps_displayed_rows_and_retries_after_unlock_without_another_notification()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        await logger.AppendAsync(Entry(now, "original"));
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            using (var stream = new FileStream(logger.HistoryPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Entry(now.AddSeconds(1), "after lock")) + "\n");
                await stream.WriteAsync(bytes);
                stream.Flush(flushToDisk: true);
                await WaitUntilAsync(() => Field<Label>(form, "_refreshStatus").Visible);
                Assert.Equal("original", RowText(grid, 0));
                // Prove that recovery does not depend on another file-system notification.
                Field<FileSystemWatcher>(form, "_historyWatcher").EnableRaisingEvents = false;
            }
            await WaitUntilAsync(() => grid.Rows.Count == 2);
            Assert.Equal("after lock", RowText(grid, 0));
            Assert.False(Field<Label>(form, "_refreshStatus").Visible);
        });
    }

    [Fact]
    public async Task Superseded_search_does_not_apply_its_old_result()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = new HistoryLogger(_directory);
        await seed.AppendAsync(Entry(now, "keep"));
        await seed.AppendAsync(Entry(now.AddSeconds(1), "drop"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var logger = new HistoryLogger(_directory, true, new HistoryLoggerTestHooks
        {
            AfterReadOpenAsync = async ct =>
            {
                if (Interlocked.Increment(ref reads) != 2) return;
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
        });
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 2);
            var search = Field<TextBox>(form, "_searchTextBox");
            search.Text = "keep";
            await entered.Task;
            var displayed = new List<string>();
            grid.DataBindingComplete += (_, _) => displayed.AddRange(grid.Rows.Cast<DataGridViewRow>().Select(row => RowText(grid, row.Index)));
            search.Text = "drop";
            release.TrySetResult();
            await WaitUntilAsync(() => grid.Rows.Count == 1 && RowText(grid, 0) == "drop");
            Assert.DoesNotContain("keep", displayed);
            Assert.Equal(3, reads);
        });
    }

    [Fact]
    public async Task Closing_during_refresh_cancels_read_and_detaches_notifications()
    {
        var seed = new HistoryLogger(_directory);
        await seed.AppendAsync(Entry(DateTimeOffset.UtcNow, "original"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var logger = new HistoryLogger(_directory, true, new HistoryLoggerTestHooks
        {
            AfterReadOpenAsync = async ct =>
            {
                if (Interlocked.Increment(ref reads) != 2) return;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
        });
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            await WaitUntilAsync(() => Field<DataGridView>(form, "_grid").Rows.Count == 1);
            var refresh = Invoke<Task>(form, "RefreshRowsAsync");
            await entered.Task;
            form.Close();
            await refresh;
            Assert.Null(Field<Action?>(logger, "Changed"));
            await logger.AppendAsync(Entry(DateTimeOffset.UtcNow, "after close"));
        });
    }

    [Fact]
    public async Task Refresh_defers_while_copy_menu_uses_the_original_context_cell()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        await logger.AppendAsync(Entry(now, "original"));
        await RunOnStaAsync(async () =>
        {
            DataObject? copied = null;
            using var form = new HistoryForm(logger, data => copied = data);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            Invoke<object?>(form, "HandleCellMouseDown", null, new DataGridViewCellMouseEventArgs(2, 0, 0, 0,
                new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0)));
            await logger.AppendAsync(Entry(now.AddSeconds(1), "newest"));
            await WaitUntilAsync(() => Field<bool>(form, "_refreshPending"));
            await Task.Delay(350);
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Field<ToolStripMenuItem>(form, "_copyCellItem").PerformClick();
            Assert.Equal("original", copied!.GetData(DataFormats.UnicodeText));
            Field<ContextMenuStrip>(form, "_rowMenu").Close();
            await WaitUntilAsync(() => grid.Rows.Count == 2);
        });
    }

    [Fact]
    public async Task Equal_timestamp_and_text_rows_remain_separate_and_keep_the_selected_occurrence()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        await logger.AppendAsync(Entry(now, "same") with { RawText = "first" });
        await logger.AppendAsync(Entry(now, "same") with { RawText = "second" });
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger);
            form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 2);
            grid.CurrentCell = grid.Rows[1].Cells[1];
            grid.ClearSelection();
            grid.Rows[1].Selected = true;
            await logger.AppendAsync(Entry(now, "same") with { RawText = "third" });
            await WaitUntilAsync(() => grid.Rows.Count == 3);
            var selected = Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.Equal("second", selected.Cells[1].Value);
            Assert.Equal(new[] { "first", "second", "third" }, grid.Rows.Cast<DataGridViewRow>().Select(row => (string)row.Cells[1].Value));
        });
    }

    [Fact]
    public async Task Row_menu_and_toolbar_copy_selected_rows_and_right_clicked_cell()
    {
        var logger = new HistoryLogger(_directory);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++) await logger.AppendAsync(new TranscriptionEntry(now.AddMinutes(i), $"final {i}",
            $"raw {i}", "test", "en", 100, null, ConfigurationSnapshot: JsonSerializer.SerializeToElement(new { option = i })));
        var entries = (await logger.ReadAllAsync()).OrderByDescending(e => e.Timestamp).ToArray();
        RunOnSta(() =>
        {
            DataObject? copied = null;
            using var form = new HistoryForm(logger, data => copied = data);
            // No external clipboard or live user history is touched by this test.
            Invoke<object?>(form, "PopulateRows", (object)entries);
            var grid = Field<DataGridView>(form, "_grid");
            grid.BindingContext = new BindingContext();
            grid.ClearSelection();
            grid.Rows[0].Selected = true;
            grid.Rows[2].Selected = true;
            var copyButton = Field<Button>(form, "_copyButton");
            Assert.True(copyButton.Enabled);
            Assert.True(grid.MultiSelect);
            var menu = Field<ContextMenuStrip>(form, "_copyMenu");
            menu.Items[0].PerformClick();
            using var json = JsonDocument.Parse((string)copied!.GetData(DataFormats.UnicodeText)!);
            Assert.Equal(2, json.RootElement.GetArrayLength());
            Assert.Equal("final 2", json.RootElement[0].GetProperty("text").GetString());
            Assert.Equal("final 0", json.RootElement[1].GetProperty("text").GetString());
            menu.Items[2].PerformClick();
            Assert.True(copied!.GetDataPresent(DataFormats.Html));

            var rawColumn = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "RawText").Index;
            Invoke<object?>(form, "HandleCellMouseDown", null, new DataGridViewCellMouseEventArgs(rawColumn, 2, 0, 0,
                new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0)));
            Assert.Equal(2, grid.SelectedRows.Count);
            Field<ToolStripMenuItem>(form, "_copyCellItem").PerformClick();
            Assert.Equal("raw 0", copied!.GetData(DataFormats.UnicodeText));
            Field<ContextMenuStrip>(form, "_rowMenu").Close();

            Invoke<object?>(form, "HandleCellMouseDown", null, new DataGridViewCellMouseEventArgs(rawColumn, 1, 0, 0,
                new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0)));
            Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>());
            Field<ToolStripMenuItem>(form, "_copyCellItem").PerformClick();
            Assert.Equal("raw 1", copied!.GetData(DataFormats.UnicodeText));
        });
    }

    [Fact]
    public void History_options_load_save_and_disable_with_history_logging()
    {
        var manager = new SettingsManager(_directory);
        manager.Save(new AppSettings { HistoryRecordingDurationEnabled = false, HistoryConfigurationEnabled = true });
        RunOnSta(() =>
        {
            using var form = new SettingsForm(null, null, manager);
            var recording = Field<CheckBox>(form, "_historyRecordingDurationCheckBox");
            var config = Field<CheckBox>(form, "_historyConfigurationCheckBox");
            Assert.False(recording.Checked);
            Assert.True(config.Checked);
            recording.Checked = true;
            config.Checked = false;
            var updated = Invoke<AppSettings>(form, "BuildSettingsFromForm");
            manager.Save(updated);
            Assert.True(manager.Load().HistoryRecordingDurationEnabled);
            Assert.False(manager.Load().HistoryConfigurationEnabled);
            Field<CheckBox>(form, "_historyEnabledCheckBox").Checked = false;
            Assert.False(recording.Enabled);
            Assert.False(config.Enabled);
        });
    }

    [Fact]
    public async Task In_place_stage_updates_keep_the_pending_row_selected()
    {
        var logger = new HistoryLogger(_directory);
        var entry = Entry(DateTimeOffset.UtcNow, "") with { Id = Guid.NewGuid(), RawText = null, Status = HistoryEntryStatus.Transcribing };
        await logger.AppendAsync(entry);
        await RunOnStaAsync(async () =>
        {
            using var form = new HistoryForm(logger); form.Show();
            var grid = Field<DataGridView>(form, "_grid");
            await WaitUntilAsync(() => grid.Rows.Count == 1);
            grid.CurrentCell = grid.Rows[0].Cells[2]; grid.Rows[0].Selected = true;
            await logger.UpdateAsync(entry with { RawText = "raw", Status = HistoryEntryStatus.CleaningUp });
            await WaitUntilAsync(() => Equals(grid.Rows[0].Cells[1].Value, "raw"));
            Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>());
            await logger.UpdateAsync(entry with { RawText = "raw", Text = "edited", Status = HistoryEntryStatus.Completed });
            await WaitUntilAsync(() => RowText(grid, 0) == "edited");
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.Equal(2, grid.CurrentCell!.ColumnIndex);
        });
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static T Invoke<T>(object instance, string name, params object?[] args) => (T)instance.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args)!;

    private static TranscriptionEntry Entry(DateTimeOffset timestamp, string text, string provider = "test", string language = "en") =>
        new(timestamp, text, text, provider, language, 100, null);

    private sealed class FixedTranscriber : ITranscriptionProvider
    {
        public string Name => "synthetic";
        public ProviderType Type => ProviderType.OpenAi;
        public Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken ct) => Task.FromResult("raw result");
    }

    private sealed class FixedCleanup : IPostProcessor
    {
        public Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken ct) => Task.FromResult("cleaned result");
    }

    private static string RowText(DataGridView grid, int row) => (string)grid.Rows[row].Cells[2].Value;

    private static string[] SelectedTexts(DataGridView grid) => grid.SelectedRows.Cast<DataGridViewRow>()
        .OrderBy(row => row.Index).Select(row => RowText(grid, row.Index)).ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static Task RunOnStaAsync(Func<Task> action) => StaTestHost.RunAsync(action);

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "History UI check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
