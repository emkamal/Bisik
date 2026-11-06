using Bisik.Core;
using Bisik.Models;
using Bisik.Providers;
using NAudio.Wave;

namespace Bisik.Tests;

public sealed class ExperimentSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Bisik.CaptureTests", Guid.NewGuid().ToString("N"));
    private ExperimentSession Session(TestCapture capture, Func<string, Task<string>> recognize,
        ExperimentDefinition? definition = null, IProgress<ExperimentCaptureProgress>? progress = null)
    {
        var settings = new AppSettings { Provider = new() { Type = ProviderType.OpenAi },
            PostProcessor = new() { Enabled = true }, HistoryEnabled = true, SpeculativeCleanup = true };
        var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider>
            { [ProviderType.OpenAi] = new TestProvider(recognize) },
            new TranscriptionServiceTextTests.Processor((_, _, _) => throw new Exception("Cleanup must not run")),
            new HistoryLogger(_directory), new ErrorLogger(_directory), () => throw new Exception(), () => { });
        return new(new AudioRecorder(outputDirectory: _directory, captureFactory: (_, _) => capture), service,
            settings, definition ?? ExperimentModelTests.Definition(), CancellationToken.None, progress);
    }

    [Fact]
    public async Task DoneDrainsFinalBufferRecognizesOnceAndDisposalDeletesOwnedAudio()
    {
        var capture = new TestCapture { FinalBuffer = new byte[3200] };
        var calls = 0;
        var session = Session(capture, path =>
        {
            calls++;
            using var wav = new WaveFileReader(path);
            Assert.Equal(6400, wav.Length);
            return Task.FromResult("final raw");
        });
        var run = session.RunAsync();
        capture.Emit(new byte[3200]);
        session.Done(); session.Done();
        var take = await run;
        Assert.Equal("final raw", take.RawText); Assert.Equal(200, take.RecordingMs);
        Assert.Equal(1, calls); Assert.Equal(1, capture.Starts);
        Assert.True(File.Exists(session.AudioPath));
        Assert.False(File.Exists(Path.Combine(_directory, "history.jsonl")));
        await session.DisposeAsync();
        Assert.False(File.Exists(session.AudioPath)); Assert.True(capture.Disposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EmptyRawAndNoBuffersCannotProduceATake(bool buffers)
    {
        var capture = new TestCapture();
        await using var session = Session(capture, _ => Task.FromResult(buffers ? "" : "raw"));
        var run = session.RunAsync();
        if (buffers) capture.Emit(new byte[3200]);
        session.Done();
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => run);
        Assert.Equal(ExperimentState.Failed, session.State);
    }

    [Fact]
    public async Task CancellationRejectsLateRecognitionAndOwnsRecorder()
    {
        var capture = new TestCapture();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = Session(capture, _ => { started.SetResult(); return pending.Task; });
        var run = session.RunAsync(); capture.Emit(new byte[3200]); session.Done();
        await started.Task; session.Cancel(); pending.SetResult("late raw");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(capture.Disposed);
    }

    [Fact]
    public async Task QuietHintUsesRealBuffersAndLimitStopsWithoutPausesStoppingCapture()
    {
        var capture = new TestCapture();
        var events = new List<ExperimentCaptureProgress>();
        var definition = ExperimentModelTests.Definition() with { Limits = new() { QuietHintSeconds = 1, RecordingSeconds = 2 } };
        await using var session = Session(capture, _ => Task.FromResult("quiet raw"), definition,
            new InlineProgress<ExperimentCaptureProgress>(e => { lock (events) events.Add(e); }));
        var run = session.RunAsync(); capture.Emit(new byte[3200]);
        var take = await run;
        Assert.Contains(events, e => e.QuietHint && e.HasBuffers && !e.SpeechDetected);
        Assert.Contains(events, e => e.LimitReached);
        Assert.True(take.SharedStopToRawMs >= 0);
    }

    [Fact]
    public async Task DeviceFailureFailsAndDrains()
    {
        var capture = new TestCapture();
        await using var session = Session(capture, _ => Task.FromResult("raw"));
        var run = session.RunAsync(); capture.Fail(new IOException("device disconnected"));
        await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.True(capture.Disposed);
        Assert.Equal(ExperimentState.Failed, session.State);
    }

    [Fact]
    public async Task CancelDuringCaptureOwnsAbortAndNeverRecognizes()
    {
        var capture = new TestCapture();
        await using var session = Session(capture, _ => throw new Exception("Recognition must not run"));
        var run = session.RunAsync(); capture.Emit(new byte[3200]); session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(capture.Disposed);
        Assert.Null(session.AudioPath);
    }

    private sealed class TestProvider(Func<string, Task<string>> recognize) : ITranscriptionProvider
    {
        public string Name => "test";
        public ProviderType Type => ProviderType.OpenAi;
        public Task<string> TranscribeAsync(string path, string language, CancellationToken ct) => recognize(path);
    }
    internal sealed class TestCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public byte[]? FinalBuffer { get; init; }
        public int Starts { get; private set; }
        public bool Disposed { get; private set; }
        public void StartRecording() => Starts++;
        public void StopRecording() { if (FinalBuffer is not null) Emit(FinalBuffer); RecordingStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Emit(byte[] pcm) => DataAvailable?.Invoke(this, new WaveInEventArgs(pcm, pcm.Length));
        public void Fail(Exception error) => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public void Dispose() => Disposed = true;
    }
    internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
