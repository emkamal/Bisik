using Bisik.Core;
using Bisik.Models;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class HistoryLoggerTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), "Bisik.HistoryLoggerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AppendAsync_should_create_history_file_and_store_jsonl_entries()
    {
        var logger = CreateLogger();
        var firstEntry = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero),
            text: "first line");
        var secondEntry = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 9, 1, 0, TimeSpan.Zero),
            text: "second line");

        await logger.AppendAsync(firstEntry);
        await logger.AppendAsync(secondEntry);

        var historyPath = Path.Combine(_baseDirectory, "history.jsonl");
        File.Exists(historyPath).Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(historyPath);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"text\":\"first line\"");
        lines[1].Should().Contain("\"text\":\"second line\"");
    }

    [Fact]
    public async Task ReadAllAsync_should_return_entries_in_file_order()
    {
        var logger = CreateLogger();
        var firstEntry = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero),
            text: "first line");
        var secondEntry = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 9, 1, 0, TimeSpan.Zero),
            text: "second line");

        await logger.AppendAsync(firstEntry);
        await logger.AppendAsync(secondEntry);

        var entries = await logger.ReadAllAsync();

        entries.Should().Equal(firstEntry, secondEntry);
    }

    [Fact]
    public async Task SearchAsync_should_match_text_and_raw_text_case_insensitively()
    {
        var logger = CreateLogger();
        var nonMatch = CreateEntry(text: "plain text", rawText: "boring source");
        var textMatch = CreateEntry(text: "Needle in final text", rawText: "ignored");
        var rawMatch = CreateEntry(text: "different", rawText: "contains NEEDLE here");

        await logger.AppendAsync(nonMatch);
        await logger.AppendAsync(textMatch);
        await logger.AppendAsync(rawMatch);

        var matches = await logger.SearchAsync("needle");

        matches.Should().Equal(textMatch, rawMatch);
    }

    [Fact]
    public async Task DeleteAsync_should_remove_only_the_matching_entry()
    {
        var logger = CreateLogger();
        var keepFirst = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero),
            text: "keep first");
        var deleteMe = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 9, 1, 0, TimeSpan.Zero),
            text: "delete me");
        var keepLast = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 9, 2, 0, TimeSpan.Zero),
            text: "keep last");

        await logger.AppendAsync(keepFirst);
        await logger.AppendAsync(deleteMe);
        await logger.AppendAsync(keepLast);

        var deleted = await logger.DeleteAsync(deleteMe.Timestamp, deleteMe.Text);
        var remaining = await logger.ReadAllAsync();

        deleted.Should().BeTrue();
        remaining.Should().Equal(keepFirst, keepLast);
    }

    [Fact]
    public async Task ClearAsync_should_remove_all_history_entries()
    {
        var logger = CreateLogger();
        await logger.AppendAsync(CreateEntry(text: "first"));
        await logger.AppendAsync(CreateEntry(text: "second"));

        await logger.ClearAsync();

        var entries = await logger.ReadAllAsync();
        var historyPath = Path.Combine(_baseDirectory, "history.jsonl");

        entries.Should().BeEmpty();
        File.Exists(historyPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExportToTextAsync_should_write_plain_text_in_chronological_order()
    {
        var logger = CreateLogger();
        var later = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.Zero),
            text: "second text");
        var earlier = CreateEntry(
            timestamp: new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero),
            text: "first text");
        var exportPath = Path.Combine(_baseDirectory, "export.txt");

        await logger.AppendAsync(later);
        await logger.AppendAsync(earlier);

        await logger.ExportToTextAsync(exportPath);

        var export = await File.ReadAllTextAsync(exportPath);
        export.Should().Contain("2026-06-12 10:00:00 +00:00");
        export.Should().Contain("first text");
        export.Should().Contain("2026-06-12 11:00:00 +00:00");
        export.Should().Contain("second text");
        export.IndexOf("first text", StringComparison.Ordinal)
            .Should().BeLessThan(export.IndexOf("second text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppendAsync_should_be_a_no_op_when_history_is_disabled_but_reads_still_return_existing_entries()
    {
        var enabledLogger = CreateLogger(historyEnabled: true);
        var disabledLogger = CreateLogger(historyEnabled: false);
        var persistedEntry = CreateEntry(text: "persisted");
        var ignoredEntry = CreateEntry(text: "ignored");

        await enabledLogger.AppendAsync(persistedEntry);
        await disabledLogger.AppendAsync(ignoredEntry);

        var entries = await disabledLogger.ReadAllAsync();

        entries.Should().Equal(persistedEntry);
    }

    [Fact]
    public async Task ReadAllAsync_should_skip_malformed_trailing_lines()
    {
        var logger = CreateLogger();
        var validEntry = CreateEntry(
            text: "valid entry",
            timestamp: new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero));
        var historyPath = Path.Combine(_baseDirectory, "history.jsonl");

        Directory.CreateDirectory(_baseDirectory);
        var validJson = System.Text.Json.JsonSerializer.Serialize(validEntry);
        await File.WriteAllTextAsync(historyPath, $"{validJson}{Environment.NewLine}{{\"timestamp\":");

        var entries = await logger.ReadAllAsync();
        var searchMatches = await logger.SearchAsync("valid");
        var deleted = await logger.DeleteAsync(validEntry.Timestamp, validEntry.Text);

        entries.Should().Equal(validEntry);
        searchMatches.Should().Equal(validEntry);
        deleted.Should().BeTrue();
    }

    [Fact]
    public async Task Two_instances_for_the_same_history_path_should_coordinate_append_and_delete()
    {
        var seedLogger = CreateLogger();
        var entryToDelete = CreateEntry(
            text: "seed entry",
            timestamp: new DateTimeOffset(2026, 6, 12, 13, 0, 0, TimeSpan.Zero));
        var appendedEntry = CreateEntry(
            text: "appended entry",
            timestamp: new DateTimeOffset(2026, 6, 12, 13, 1, 0, TimeSpan.Zero));
        var rewriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRewrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await seedLogger.AppendAsync(entryToDelete);

        var deletingLogger = CreateLogger(new HistoryLoggerTestHooks
        {
            BeforeRewriteAsync = ct =>
            {
                rewriteStarted.TrySetResult();
                return allowRewrite.Task.WaitAsync(ct);
            },
        });
        var appendingLogger = CreateLogger();

        var deleteTask = deletingLogger.DeleteAsync(entryToDelete.Timestamp, entryToDelete.Text);
        await rewriteStarted.Task;
        var appendTask = appendingLogger.AppendAsync(appendedEntry);
        allowRewrite.TrySetResult();

        await Task.WhenAll(deleteTask, appendTask);

        var entries = await seedLogger.ReadAllAsync();
        entries.Should().Equal(appendedEntry);
    }

    [Fact]
    public async Task ReadAllAsync_should_not_prevent_ClearAsync_from_succeeding()
    {
        var seedLogger = CreateLogger();
        var readOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReadToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await seedLogger.AppendAsync(CreateEntry(text: "clear me"));

        var readingLogger = CreateLogger(new HistoryLoggerTestHooks
        {
            AfterReadOpenAsync = ct =>
            {
                readOpened.TrySetResult();
                return allowReadToFinish.Task.WaitAsync(ct);
            },
        });
        var clearingLogger = CreateLogger();

        var readTask = readingLogger.ReadAllAsync();
        await readOpened.Task;
        var clearTask = clearingLogger.ClearAsync();
        allowReadToFinish.TrySetResult();

        await readTask;
        await clearTask;

        var historyPath = Path.Combine(_baseDirectory, "history.jsonl");
        File.Exists(historyPath).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private HistoryLogger CreateLogger(bool historyEnabled = true)
    {
        return new HistoryLogger(_baseDirectory, historyEnabled);
    }

    private HistoryLogger CreateLogger(HistoryLoggerTestHooks testHooks, bool historyEnabled = true)
    {
        return new HistoryLogger(_baseDirectory, historyEnabled, testHooks);
    }

    private static TranscriptionEntry CreateEntry(
        string text,
        string? rawText = null,
        DateTimeOffset? timestamp = null)
    {
        return new TranscriptionEntry(
            Timestamp: timestamp ?? new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero),
            Text: text,
            RawText: rawText ?? $"{text} raw",
            Provider: "azure-openai",
            Language: "id",
            DurationMs: 1234,
            AudioFilePath: null);
    }
}
