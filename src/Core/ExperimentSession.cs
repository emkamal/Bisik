using System.Diagnostics;
using Bisik.Models;

namespace Bisik.Core;

public sealed record ExperimentCaptureProgress(Guid SessionId, ExperimentState State, double ElapsedSeconds,
    double Level, bool HasBuffers, bool SpeechDetected, bool QuietHint, bool LimitReached);

// Owns a dedicated recorder and raw-only recognition session; never captures an insertion target.
public sealed class ExperimentSession : IAsyncDisposable
{
    private readonly AudioRecorder _recorder;
    private readonly TranscriptionService _service;
    private readonly AppSettings _settings;
    private readonly ExperimentDefinition _definition;
    private readonly IProgress<ExperimentCaptureProgress>? _progress;
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task<ExperimentTake>? _run;
    private Task? _disposal;
    private TranscriptionSession? _recognition;
    private Task<RecordedAudio>? _persistence;
    private double _level;
    private int _audioMs;
    private int _sustainedMs;
    private bool _speech;
    private Exception? _captureFailure;
    public Guid Id { get; } = Guid.NewGuid();
    public ExperimentState State { get; private set; } = ExperimentState.Draft;
    public string? AudioPath { get; private set; }

    public ExperimentSession(AudioRecorder recorder, TranscriptionService service, AppSettings settings,
        ExperimentDefinition definition, CancellationToken ct, IProgress<ExperimentCaptureProgress>? progress = null)
    {
        _recorder = recorder; _service = service; _definition = definition.Snapshot(); _progress = progress;
        _settings = settings.Snapshot() with { HistoryEnabled = false, SoundEnabled = false,
            SpeculativeCleanup = false, PrewarmRealtime = false, PostProcessor = settings.PostProcessor with { Enabled = false } };
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public Task<ExperimentTake> RunAsync() { lock (_gate) return _run ??= RunCoreAsync(); }
    public void Done() => _done.TrySetResult();
    public void Cancel() => _lifetime.Cancel();

    private async Task<ExperimentTake> RunCoreAsync()
    {
        var metrics = new SessionMetrics("experiment-capture");
        using var scope = metrics.Activate();
        using var monitorLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var watch = Stopwatch.StartNew();
        Task? monitor = null;
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            ExperimentScoring.Compare(_definition.Reference, "", _definition.Terms);
            _recorder.UpdateSettings(_settings.Audio);
            _recognition = _service.CreateSession(_settings, _lifetime.Token);
            _recorder.AudioChunkAvailable += OnChunk;
            _recorder.AudioLevelChanged += OnLevel;
            _recorder.RecordingFailed += OnFailure;
            State = ExperimentState.Recording;
            metrics.Mark(TimingStage.CaptureStart);
            _recorder.StartRecording();
            // StartAsync owns network setup; recording is already listening while it connects.
            _ = _recognition.StartAsync();
            monitor = MonitorAsync(watch, monitorLifetime.Token);
            await _done.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            State = ExperimentState.Stopping;
            var stop = Stopwatch.StartNew();
            metrics.Mark(TimingStage.StopRequested);
            _recognition.RequestStop();
            _lifetime.CancelAfter(TimeSpan.FromSeconds(_definition.Limits.RecognitionSeconds));
            var captured = await _recorder.DrainCaptureAsync(_lifetime.Token).ConfigureAwait(false);
            AudioPath = captured.FilePath;
            _persistence = captured.PersistAsync(_lifetime.Token);
            if (_captureFailure is not null) throw new IOException("Microphone capture failed.", _captureFailure);
            State = ExperimentState.Transcribing;
            var result = await _recognition.CompleteAsync(captured, _persistence).ConfigureAwait(false);
            var sharedMs = stop.Elapsed.TotalMilliseconds;
            // Live recognition can finish before disk persistence with history disabled; still own that task.
            await _persistence.ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!result.Succeeded) throw new InvalidOperationException("Shared recognition failed.");
            if (string.IsNullOrWhiteSpace(result.RawText)) throw new InvalidDataException("Recognition returned no final text.");
            int audioMs;
            lock (_gate) audioMs = _audioMs;
            if (audioMs == 0) throw new InvalidDataException("No microphone buffers were received.");
            var take = new ExperimentTake(Id, result.RawText, sharedMs, audioMs, _definition.InputIdentity);
            take.Validate();
            State = ExperimentState.Completed;
            return take;
        }
        catch (OperationCanceledException) { State = ExperimentState.Cancelled; throw; }
        catch { State = ExperimentState.Failed; throw; }
        finally
        {
            monitorLifetime.Cancel();
            if (monitor is not null) { try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            _recorder.AudioChunkAvailable -= OnChunk;
            _recorder.AudioLevelChanged -= OnLevel;
            _recorder.RecordingFailed -= OnFailure;
            if (_recognition is not null) await _recognition.DisposeAsync().ConfigureAwait(false);
            if (_persistence is not null) { try { await _persistence.ConfigureAwait(false); } catch { /* Original failure is retained. */ } }
            _recorder.Dispose();
            Report(watch.Elapsed.TotalSeconds, false);
        }
    }

    private void OnChunk(object? sender, AudioChunkEventArgs chunk) => _recognition?.PushAudio(chunk);
    private void OnLevel(object? sender, AudioLevelChangedEventArgs sample)
    {
        lock (_gate)
        {
            _level = sample.Level; _audioMs += sample.ElapsedMilliseconds;
            _sustainedMs = sample.Level >= _settings.Audio.VadSilenceThreshold ? _sustainedMs + sample.ElapsedMilliseconds : 0;
            _speech |= _sustainedMs >= _settings.Audio.VadMinSpeechMs;
        }
    }
    private void OnFailure(object? sender, AudioRecordingFailedEventArgs failure)
    { lock (_gate) _captureFailure = failure.Exception; Done(); }
    private async Task MonitorAsync(Stopwatch watch, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var limit = State == ExperimentState.Recording && watch.Elapsed.TotalSeconds >= _definition.Limits.RecordingSeconds;
            Report(watch.Elapsed.TotalSeconds, limit);
            if (limit) Done();
        }
    }
    private void Report(double elapsed, bool limit)
    {
        ExperimentCaptureProgress progress;
        lock (_gate) progress = new(Id, State, elapsed, _level, _audioMs > 0, _speech,
            State == ExperimentState.Recording && elapsed >= _definition.Limits.QuietHintSeconds && !_speech, limit);
        _progress?.Report(progress);
    }

    public ValueTask DisposeAsync() { lock (_gate) return new(_disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        if (_run is not null) { try { await _run.ConfigureAwait(false); } catch { /* Run caller receives the outcome. */ } }
        else _recorder.Dispose();
        // Only this session's generated path is deleted. Retention copies are owned by ExperimentStore.
        try { if (AudioPath is not null) File.Delete(AudioPath); }
        finally { _lifetime.Dispose(); }
    }
}
