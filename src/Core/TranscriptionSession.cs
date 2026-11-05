using Bisik.Models;

namespace Bisik.Core;

public enum DictationState { Recording, Stopping, Processing, Ready, Cancelled, Failed }

/// <summary>One owned dictation. Settings, cancellation and final completion cannot cross sessions.</summary>
public sealed class TranscriptionSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Func<TranscriptionSession, string, CancellationToken, Task<TranscriptionResult>> _complete;
    private Task? _setup;
    private Task<TranscriptionResult>? _completion;
    private readonly TaskCompletionSource _setupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LiveAudioQueue? _audioQueue;
    public Guid Id { get; } = Guid.NewGuid();
    public AppSettings Settings { get; }
    public DictationState State { get; private set; } = DictationState.Recording;
    public CancellationToken Token => _lifetime.Token;
    public IRealtimeTranscriber? Stream { get; }

    internal TranscriptionSession(AppSettings settings, CancellationToken ct, IRealtimeTranscriber? stream,
        Func<TranscriptionSession, string, CancellationToken, Task<TranscriptionResult>> complete)
    {
        Settings = settings.Snapshot();
        Stream = stream;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _complete = complete;
        if (stream is not null) _audioQueue = new LiveAudioQueue(stream, _setupReady.Task, Settings.Audio.SampleRate, Token);
    }

    public Task StartAsync()
    {
        lock (_gate) return _setup ??= SetupAsync();
    }

    private async Task SetupAsync()
    {
        try
        {
            if (Stream is not null) await Stream.StartAsync(Settings, Settings.DefaultLanguage, Token).ConfigureAwait(false);
            _setupReady.TrySetResult();
        }
        catch (Exception exception) { _setupReady.TrySetException(exception); throw; }
    }

    public void PushAudio(AudioChunkEventArgs chunk) => _audioQueue?.Enqueue(chunk);

    public Task<TranscriptionResult> CompleteAsync(string audioPath)
    {
        lock (_gate) return _completion ??= CompleteCoreAsync(audioPath);
    }

    private async Task<TranscriptionResult> CompleteCoreAsync(string audioPath)
    {
        State = DictationState.Stopping;
        try
        {
            Token.ThrowIfCancellationRequested();
            if (_setup is not null) await _setup.WaitAsync(Token).ConfigureAwait(false);
            _audioQueue?.Close();
            if (_audioQueue is not null) await _audioQueue.Sender.WaitAsync(Token).ConfigureAwait(false);
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
        _setupReady.TrySetCanceled();
        _audioQueue?.Close();
        if (_setup is not null) { try { await _setup.ConfigureAwait(false); } catch { /* Observed setup failure. */ } }
        if (Stream is not null) await Stream.DisposeAsync().ConfigureAwait(false);
        if (_audioQueue is not null) { try { await _audioQueue.Sender.ConfigureAwait(false); } catch { /* Observed queue failure. */ } }
        if (_completion is not null) { try { await _completion.ConfigureAwait(false); } catch { /* Caller receives the failure. */ } }
        _lifetime.Dispose();
    }
}
