using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;

namespace Bisik.Tests;

public sealed class ProgressiveHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Bisik.ProgressiveHistory", Guid.NewGuid().ToString("N"));
    private static TaskCompletionSource<string> TextSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Audio_raw_and_cleanup_publish_one_identity_at_their_own_stage()
    {
        var raw = TextSource(); var edited = TextSource();
        var recognized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = new HistoryLogger(_directory);
        var service = Service(history, ct => { recognized.SetResult(); return raw.Task.WaitAsync(ct); },
            ct => { cleaning.SetResult(); return edited.Task.WaitAsync(ct); });
        await using var session = service.CreateSession(Settings(), CancellationToken.None);
        var recording = Recording();
        var persisted = new TaskCompletionSource<RecordedAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = session.CompleteAsync(recording, persisted.Task);
        Assert.Empty(await history.ReadAllAsync());
        Assert.False(recognized.Task.IsCompleted);
        persisted.SetResult(await recording.PersistAsync(CancellationToken.None));
        await recognized.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var first = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(session.Id, first.Id);
        Assert.Equal(HistoryEntryStatus.Transcribing, first.Status);
        Assert.True(File.Exists(first.AudioFilePath));
        Assert.Null(first.RawText); Assert.Empty(first.Text);
        raw.SetResult("raw wording");
        await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var intermediate = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(first.Id, intermediate.Id);
        Assert.Equal("raw wording", intermediate.RawText);
        Assert.Empty(intermediate.Text);
        Assert.Equal(HistoryEntryStatus.CleaningUp, intermediate.Status);
        edited.SetResult("edited wording");
        Assert.True((await operation).Succeeded);
        var final = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(first.Id, final.Id);
        Assert.Equal("raw wording", final.RawText);
        Assert.Equal("edited wording", final.Text);
        Assert.Equal(HistoryEntryStatus.Completed, final.Status);
        Assert.True(session.HistoryAudioRetained);
        Assert.Equal(3, (await File.ReadAllLinesAsync(history.HistoryPath)).Length);
    }

    [Theory]
    [InlineData("empty", HistoryEntryStatus.NoSpeech)]
    [InlineData("failed", HistoryEntryStatus.Failed)]
    [InlineData("cleanup-failed", HistoryEntryStatus.Failed)]
    [InlineData("cancelled", HistoryEntryStatus.Cancelled)]
    public async Task Terminal_outcomes_keep_audio_and_any_completed_raw_text(string outcome, HistoryEntryStatus expected)
    {
        var history = new HistoryLogger(_directory);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var service = Service(history, ct =>
        {
            called.SetResult();
            return outcome switch
            {
                "failed" => Task.FromException<string>(new InvalidDataException("Invalid transcript")),
                "empty" => Task.FromResult(""),
                "cancelled" => TextSource().Task.WaitAsync(ct),
                _ => Task.FromResult("retained raw"),
            };
        }, _ => Task.FromException<string>(new InvalidOperationException("Cleanup failed")));
        await using var session = service.CreateSession(Settings(), cancellation.Token);
        var recording = Recording();
        var task = session.CompleteAsync(recording, recording.PersistAsync(CancellationToken.None));
        await called.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (outcome == "cancelled")
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        else await task;
        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(expected, entry.Status);
        Assert.True(session.HistoryAudioRetained);
        Assert.True(File.Exists(entry.AudioFilePath));
        Assert.Equal(outcome == "cleanup-failed" ? "retained raw" : null, entry.RawText);
        Assert.Empty(entry.Text);
    }

    [Fact]
    public async Task Disabled_history_and_failed_audio_persistence_create_no_row()
    {
        var history = new HistoryLogger(_directory);
        var service = Service(history, _ => Task.FromResult("raw"), _ => Task.FromResult("edited"));
        await using (var session = service.CreateSession(Settings() with { HistoryEnabled = false }, CancellationToken.None))
        {
            var recording = Recording();
            Assert.True((await session.CompleteAsync(recording, recording.PersistAsync(CancellationToken.None))).Succeeded);
            Assert.False(session.HistoryAudioRetained);
        }
        await using (var session = service.CreateSession(Settings(), CancellationToken.None))
        {
            var failed = Task.FromException<RecordedAudio>(new IOException("Disk unavailable"));
            Assert.False((await session.CompleteAsync(Recording(), failed)).Succeeded);
            Assert.False(session.HistoryAudioRetained);
        }
        Assert.Empty(await history.ReadAllAsync());
    }

    [Fact]
    public async Task Revisions_preserve_legacy_duplicates_and_cannot_resurrect_deleted_rows()
    {
        var history = new HistoryLogger(_directory);
        var legacy = new TranscriptionEntry(DateTimeOffset.UtcNow, "same", "raw", "test", "en", 0, null);
        await history.AppendAsync(legacy); await history.AppendAsync(legacy);
        var pending = legacy with { Id = Guid.NewGuid(), Text = "", Status = HistoryEntryStatus.Transcribing };
        await history.AppendAsync(pending);
        var final = pending with { Text = "final", Status = HistoryEntryStatus.Completed };
        await history.UpdateAsync(final);
        Assert.Equal(3, (await history.ReadAllAsync()).Count);
        // Delete by identity even if the UI still holds the earlier text.
        Assert.True(await history.DeleteAsync(pending.Timestamp, pending.Text, id: pending.Id));
        await history.UpdateAsync(final);
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
        await history.ClearAsync(); await history.UpdateAsync(final);
        Assert.Empty(await history.ReadAllAsync());
    }

    private static AppSettings Settings() => new() { Provider = new() { Type = ProviderType.OpenAi }, PostProcessor = new() { Enabled = true } };
    private TranscriptionService Service(HistoryLogger history, Func<CancellationToken, Task<string>> recognize, Func<CancellationToken, Task<string>> cleanup) =>
        new(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = new Provider(recognize) },
            new Cleanup(cleanup), history, new ErrorLogger(_directory), () => { }, () => { });
    private CapturedRecording Recording()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.wav");
        byte[] bytes = [1, 2, 3];
        return new(path, bytes, async ct =>
        {
            Directory.CreateDirectory(_directory); await File.WriteAllBytesAsync(path, bytes, ct);
            return new(path, bytes, AudioFormat.Wav, 16000);
        });
    }
    private sealed class Provider(Func<CancellationToken, Task<string>> invoke) : ITranscriptionProvider
    {
        public ProviderType Type => ProviderType.OpenAi;
        public string Name => "test";
        public Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken ct) => invoke(ct);
    }
    private sealed class Cleanup(Func<CancellationToken, Task<string>> invoke) : IPostProcessor
    {
        public Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken ct) => invoke(ct);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
