using Bisik.Models;

namespace Bisik.Core;

public enum DictationState { Recording, Stopping, Processing, Ready, Cancelled, Failed }

/// <summary>One owned dictation. Settings, cancellation and final completion cannot cross sessions.</summary>
public sealed class TranscriptionSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenSource _streamLifetime;
    private bool _stopRequested;
    private Task? _abandonment;
    private readonly Func<TranscriptionSession, string, CancellationToken, Task<TranscriptionResult>> _complete;
    private Task? _setup;
    private Task<TranscriptionResult>? _completion;
    private readonly TaskCompletionSource _setupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LiveAudioQueue? _audioQueue;
    private readonly FinalTextSelection _selection = new();
    public Guid Id { get; } = Guid.NewGuid();
    private readonly AppSettings _settings;
    public AppSettings Settings => _settings.Snapshot();
    public DictationState State { get; private set; } = DictationState.Recording;
    public CancellationToken Token => _lifetime.Token;
    public IRealtimeTranscriber? Stream { get; }
    internal Task? Persistence { get; private set; }
    internal SpeculativeCleanup? Speculation { get; set; }
    internal EventHandler<CompletedTranscriptEventArgs>? SnapshotHandler { get; set; }
    public bool HasFinalTranscript => _selection.HasTranscript;
    public void SkipCleanup() { _selection.SkipCleanup(); Speculation?.CancelPending(); }
    public bool UseTranscriptNow()
    {
        var selected = _selection.UseTranscriptNow();
        if (selected) Speculation?.CancelPending();
        return selected;
    }
    internal Task<string> SelectFinalAsync(string raw, Func<CancellationToken, Task<string>> cleanup, CancellationToken ct) =>
        _selection.SelectAsync(raw, cleanup, ct);

    internal TranscriptionSession(AppSettings settings, CancellationToken ct, IRealtimeTranscriber? stream,
        Func<TranscriptionSession, string, CancellationToken, Task<TranscriptionResult>> complete)
    {
        _settings = settings.Snapshot();
        Stream = stream;
        if (stream is not null && SessionMetrics.Current is { } metrics) stream.BindMetrics(metrics);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _complete = complete;
        if (stream is not null) _audioQueue = new LiveAudioQueue(stream, _setupReady.Task, Settings.Audio.SampleRate,
            _streamLifetime.Token, Settings.PauseCommits, Settings.Audio);
    }

    public Task StartAsync()
    {
        lock (_gate) return _setup ??= SetupAsync();
    }

    private async Task SetupAsync()
    {
        try
        {
            if (Stream is not null) await Stream.StartAsync(Settings, Settings.DefaultLanguage, _streamLifetime.Token).ConfigureAwait(false);
            _setupReady.TrySetResult();
        }
        catch (Exception exception) { _setupReady.TrySetException(exception); throw; }
    }

    public void PushAudio(AudioChunkEventArgs chunk) => _audioQueue?.Enqueue(chunk);

    public Task<TranscriptionResult> CompleteAsync(string audioPath)
    {
        lock (_gate) return _completion ??= CompleteCoreAsync(audioPath);
    }

    public Task<TranscriptionResult> CompleteAsync(CapturedRecording recording, Task<RecordedAudio> persistence)
    {
        lock (_gate)
        {
            Persistence ??= persistence;
            return _completion ??= CompleteCoreAsync(recording.FilePath);
        }
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            if (_stopRequested) return;
            _stopRequested = true;
            State = DictationState.Stopping;
            _lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        }
    }

    internal async Task<string> FinalizeLiveAsync(CancellationToken ct)
    {
        await StartAsync().WaitAsync(ct).ConfigureAwait(false);
        _audioQueue!.Close();
        await _audioQueue.Sender.WaitAsync(ct).ConfigureAwait(false);
        return await Stream!.CommitAndFinalizeAsync(ct).ConfigureAwait(false);
    }

    internal Task AbandonStreamAsync()
    { lock (_gate) return _abandonment ??= AbandonStreamCoreAsync(); }

    private async Task AbandonStreamCoreAsync()
    {
        _streamLifetime.Cancel();
        if (Stream is not null && SnapshotHandler is not null) Stream.CompletedTranscriptAvailable -= SnapshotHandler;
        _setupReady.TrySetCanceled();
        _audioQueue?.Close();
        if (Stream is not null) await Stream.DisposeAsync().ConfigureAwait(false);
        if (_setup is not null) { try { await _setup.ConfigureAwait(false); } catch { /* Observed setup failure. */ } }
        if (_audioQueue is not null) { try { await _audioQueue.Sender.ConfigureAwait(false); } catch { /* Observed queue failure. */ } }
    }

    private async Task<TranscriptionResult> CompleteCoreAsync(string audioPath)
    {
        RequestStop();
        try
        {
            Token.ThrowIfCancellationRequested();
            State = DictationState.Processing;
            var result = await _complete(this, audioPath, Token).ConfigureAwait(false);
            Token.ThrowIfCancellationRequested(); // Reject providers that ignore cancellation and complete late.
            State = result.Succeeded ? DictationState.Ready : DictationState.Failed;
            return result;
        }
        catch (OperationCanceledException) { State = DictationState.Cancelled; throw; }
        catch { State = DictationState.Failed; throw; }
    }

    public void Cancel() { State = DictationState.Cancelled; _lifetime.Cancel(); }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await AbandonStreamAsync().ConfigureAwait(false);
        if (_completion is not null) { try { await _completion.ConfigureAwait(false); } catch { /* Caller receives the failure. */ } }
        if (Speculation is not null) await Speculation.DisposeAsync().ConfigureAwait(false);
        await _selection.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _streamLifetime.Dispose();
    }
}
