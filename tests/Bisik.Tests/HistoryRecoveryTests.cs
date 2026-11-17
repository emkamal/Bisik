using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;

namespace Bisik.Tests;

public sealed class HistoryRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Bisik.HistoryRecovery", Guid.NewGuid().ToString("N"));
    private static AppSettings Settings() => new() { Provider = new() { Type = ProviderType.OpenAi },
        PostProcessor = new() { Enabled = false }, HistoryEnabled = false, HistoryConfigurationEnabled = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audio_recovery_updates_the_same_row_and_uses_saved_language(bool cleanUp)
    {
        var history = new HistoryLogger(_directory);
        var entry = await SeedAsync(history);
        var calls = 0;
        var service = Service(history, (path, language, _) =>
        {
            Assert.Equal(entry.AudioFilePath, path); Assert.Equal("id", language);
            calls++; return Task.FromResult("raw wording");
        }, (raw, settings, _) =>
        {
            Assert.Equal("raw wording", raw); Assert.True(settings.Enabled);
            return Task.FromResult("clean wording");
        });
        var result = await service.RecoverHistoryAsync(entry, Settings(), cleanUp, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);
        var final = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(entry.Id, final.Id); Assert.Equal(entry.Timestamp, final.Timestamp);
        Assert.Equal(entry.RecordingDurationMs, final.RecordingDurationMs);
        Assert.Equal("raw wording", final.RawText); Assert.Equal(cleanUp ? "clean wording" : "raw wording", final.Text);
        Assert.Equal(HistoryEntryStatus.Completed, final.Status); Assert.Equal(cleanUp, final.PostProcessingApplied);
        Assert.Null(final.StopToResultDurationMs); Assert.Null(final.CaptureToResultDurationMs);
        Assert.Equal(1, calls); Assert.True(File.Exists(final.AudioFilePath));
        Assert.Equal("id", final.ConfigurationSnapshot!.Value.GetProperty("defaultLanguage").GetString());
    }

    [Fact]
    public async Task Cleanup_of_existing_raw_skips_recognition_and_preserves_its_configuration()
    {
        var history = new HistoryLogger(_directory);
        var original = Settings() with { Provider = new() { Type = ProviderType.CustomEndpoint, Model = "original-model" } };
        var entry = await SeedAsync(history, "saved raw", config: HistoryConfigurationSnapshot.Capture(original, "id", null));
        File.Delete(entry.AudioFilePath!); // Raw text is sufficient even if the audio has gone.
        var service = Service(history, (_, _, _) => throw new InvalidOperationException("Recognition must not run"),
            (raw, _, _) => Task.FromResult(raw + " cleaned"));
        Assert.True((await service.RecoverHistoryAsync(entry, Settings(), true, CancellationToken.None)).Succeeded);
        var final = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("saved raw cleaned", final.Text); Assert.Equal(entry.Provider, final.Provider);
        Assert.Equal("original-model", final.ConfigurationSnapshot!.Value.GetProperty("provider").GetProperty("model").GetString());
        Assert.True(final.ConfigurationSnapshot.Value.GetProperty("postProcessor").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Failed_cleanup_retains_recognition_and_a_second_attempt_does_not_recognize_again()
    {
        var history = new HistoryLogger(_directory);
        var entry = await SeedAsync(history);
        var recognized = 0; var cleaned = 0;
        var service = Service(history, (_, _, _) => { recognized++; return Task.FromResult("retained raw"); },
            async (_, _, _) =>
            {
                var pending = Assert.Single(await history.ReadAllAsync());
                Assert.Equal("retained raw", pending.RawText); Assert.Equal(HistoryEntryStatus.CleaningUp, pending.Status);
                if (++cleaned == 1) throw new InvalidDataException("Cleanup unavailable");
                return "edited";
            });
        Assert.False((await service.RecoverHistoryAsync(entry, Settings(), true, CancellationToken.None)).Succeeded);
        var failed = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("retained raw", failed.RawText); Assert.Empty(failed.Text); Assert.Equal(HistoryEntryStatus.Failed, failed.Status);
        Assert.True((await service.RecoverHistoryAsync(failed, Settings(), true, CancellationToken.None)).Succeeded);
        Assert.Equal(1, recognized); Assert.Equal("edited", Assert.Single(await history.ReadAllAsync()).Text);
    }

    [Theory]
    [InlineData("failure", HistoryEntryStatus.Failed)]
    [InlineData("empty", HistoryEntryStatus.NoSpeech)]
    public async Task Recognition_errors_keep_audio_and_do_not_call_cleanup(string outcome, HistoryEntryStatus status)
    {
        var history = new HistoryLogger(_directory);
        var entry = await SeedAsync(history);
        var service = Service(history, (_, _, _) => outcome == "empty" ? Task.FromResult(" ")
            : Task.FromException<string>(new InvalidDataException("Bad response")),
            (_, _, _) => throw new InvalidOperationException("Cleanup must not run"));
        Assert.False((await service.RecoverHistoryAsync(entry, Settings(), true, CancellationToken.None)).Succeeded);
        var final = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(status, final.Status); Assert.True(File.Exists(final.AudioFilePath)); Assert.Empty(final.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_or_external_edit_during_recognition_wins(bool edit)
    {
        var history = new HistoryLogger(_directory);
        var entry = await SeedAsync(history);
        var service = Service(history, async (_, _, _) =>
        {
            if (edit)
            {
                var pending = Assert.Single(await history.ReadAllAsync());
                await history.UpdateAsync(pending with { Text = "external result", Status = HistoryEntryStatus.Completed });
            }
            else await history.ClearAsync();
            return "late raw";
        }, (_, _, _) => throw new InvalidOperationException("Cleanup must not run"));
        Assert.False((await service.RecoverHistoryAsync(entry, Settings(), true, CancellationToken.None)).Succeeded);
        if (edit) Assert.Equal("external result", Assert.Single(await history.ReadAllAsync()).Text);
        else Assert.Empty(await history.ReadAllAsync());
    }

    [Fact]
    public async Task Cancellation_rejects_late_cleanup_and_keeps_raw_text()
    {
        var history = new HistoryLogger(_directory);
        var entry = await SeedAsync(history, "raw");
        using var cancellation = new CancellationTokenSource();
        var service = Service(history, (_, _, _) => throw new InvalidOperationException(), (_, _, _) =>
        {
            cancellation.Cancel(); return Task.FromResult("late edited");
        });
        Assert.False((await service.RecoverHistoryAsync(entry, Settings(), true, cancellation.Token)).Succeeded);
        var final = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("raw", final.RawText); Assert.Empty(final.Text); Assert.Equal(HistoryEntryStatus.Cancelled, final.Status);
    }

    [Fact]
    public async Task Legacy_row_is_upgraded_once_and_stale_or_completed_rows_make_no_requests()
    {
        var history = new HistoryLogger(_directory);
        var entry = await SeedAsync(history, legacy: true);
        await history.AppendAsync(entry); // Preserve unrelated legacy duplicates.
        var calls = 0;
        var service = Service(history, (_, _, _) => { calls++; return Task.FromResult("raw"); }, (_, _, _) => Task.FromResult("edited"));
        Assert.True((await service.RecoverHistoryAsync(entry, Settings(), true, CancellationToken.None)).Succeeded);
        var rows = await history.ReadAllAsync();
        Assert.Equal(2, rows.Count); Assert.Single(rows.Where(e => e.Id is not null)); Assert.Single(rows.Where(e => e.Id is null));
        var final = rows.Single(e => e.Id is not null);
        Assert.False((await service.RecoverHistoryAsync(final, Settings(), true, CancellationToken.None)).Succeeded);
        await history.ClearAsync();
        Assert.False((await service.RecoverHistoryAsync(entry, Settings(), false, CancellationToken.None)).Succeeded);
        Assert.Equal(1, calls);
    }

    private async Task<TranscriptionEntry> SeedAsync(HistoryLogger history, string? raw = null, bool legacy = false,
        System.Text.Json.JsonElement? config = null)
    {
        Directory.CreateDirectory(_directory);
        var audio = Path.Combine(_directory, "recording.wav");
        await File.WriteAllBytesAsync(audio, [1, 2, 3]);
        var entry = new TranscriptionEntry(DateTimeOffset.UtcNow.AddDays(-1), "", raw, "original", "id", 0, audio,
            RecordingDurationMs: 1234, Id: legacy ? null : Guid.NewGuid(), Status: HistoryEntryStatus.Cancelled,
            StopToResultDurationMs: 42, CaptureToResultDurationMs: 1276, ConfigurationSnapshot: config);
        await history.AppendAsync(entry);
        return entry;
    }
    private TranscriptionService Service(HistoryLogger history,
        Func<string, string, CancellationToken, Task<string>> recognize,
        Func<string, PostProcessorSettings, CancellationToken, Task<string>> cleanup) =>
        new(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = new Provider(recognize) },
            new Cleanup(cleanup), history, new ErrorLogger(_directory), () => { }, () => { });
    private sealed class Provider(Func<string, string, CancellationToken, Task<string>> invoke) : ITranscriptionProvider
    {
        public string Name => "test";
        public ProviderType Type => ProviderType.OpenAi;
        public Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken ct) => invoke(audioFilePath, language, ct);
    }
    private sealed class Cleanup(Func<string, PostProcessorSettings, CancellationToken, Task<string>> invoke) : IPostProcessor
    {
        public Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken ct) => invoke(rawText, settings, ct);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
