using Bisik.Core;
using Bisik.Models;
using FluentAssertions;
using NAudio.Wave;

namespace Bisik.Tests;

public sealed class AudioCaptureLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bisik-capture-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Queue_copies_orders_and_uploads_before_stop_after_delayed_setup()
    {
        var stream = new QueueStream();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new LiveAudioQueue(stream, ready.Task, 16000, CancellationToken.None);
        var buffer = new byte[] { 1, 0 };
        queue.Enqueue(new(buffer, 2, 16000));
        buffer[0] = 99;
        queue.Enqueue(new([2, 0], 2, 16000));
        Assert.Empty(stream.Chunks);
        ready.SetResult();
        await stream.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Close();
        await queue.Sender;
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, stream.Chunks.SelectMany(c => c));
    }

    [Fact]
    public async Task Queue_overflow_is_explicit_and_immediate_stop_drains()
    {
        var stream = new QueueStream();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new LiveAudioQueue(stream, ready.Task, 16000, CancellationToken.None);
        queue.Enqueue(new(new byte[320002], 320002, 16000));
        ready.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => queue.Sender);
        Assert.Empty(stream.Chunks);
        var empty = new LiveAudioQueue(stream, Task.CompletedTask, 16000, CancellationToken.None);
        empty.Close();
        await empty.Sender;
    }

    [Fact]
    public async Task Slow_upload_is_bounded_including_in_flight_bytes()
    {
        var stream = new QueueStream { DelaySend = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var queue = new LiveAudioQueue(stream, Task.CompletedTask, 16000, CancellationToken.None);
        queue.Enqueue(new(new byte[320000], 320000, 16000));
        await stream.Uploaded.Task;
        queue.Enqueue(new([1, 0], 2, 16000));
        stream.DelaySend.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => queue.Sender);
    }

    private sealed class QueueStream : IRealtimeTranscriber
    {
        public List<byte[]> Chunks { get; } = [];
        public TaskCompletionSource Uploaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? DelaySend { get; init; }
        public Task StartAsync(AppSettings settings, string language, CancellationToken ct) => Task.CompletedTask;
        public async Task PushAudioChunkAsync(byte[] data, int count, CancellationToken ct)
        {
            Chunks.Add(data[..count]);
            Uploaded.TrySetResult();
            if (DelaySend is not null) await DelaySend.Task.WaitAsync(ct);
        }
        public Task<string> CommitAndFinalizeAsync(CancellationToken ct) => Task.FromResult("final");
        public Task CommitIntermediateSegmentAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event EventHandler<PartialTranscriptEventArgs>? PartialTranscriptAvailable { add { } remove { } }
        public event EventHandler<RealtimeCompletedEventArgs>? TranscriptionCompleted { add { } remove { } }
        public event EventHandler<RealtimeFailedEventArgs>? TranscriptionFailed { add { } remove { } }
    }

    [Fact]
    public async Task Stop_should_wait_for_the_final_audio_chunk_before_writing_the_wav()
    {
        var capture = new FakeCapture();
        using var recorder = CreateRecorder(capture);
        recorder.StartRecording();
        capture.Emit([1, 0, 2, 0]);

        var stopping = recorder.StopRecordingAsync(CancellationToken.None);
        stopping.IsCompleted.Should().BeFalse();
        capture.Emit([3, 0, 4, 0]);
        capture.Complete();
        var audio = await stopping;

        using var reader = new WaveFileReader(new MemoryStream(audio.AudioData));
        var pcm = new byte[8];
        reader.Read(pcm, 0, pcm.Length).Should().Be(8);
        pcm.Should().Equal(1, 0, 2, 0, 3, 0, 4, 0);
        recorder.State.Should().Be(RecordingState.Idle);
        capture.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_should_release_capture_and_allow_another_session()
    {
        var first = new FakeCapture();
        var second = new FakeCapture { CompleteOnStop = true };
        var devices = new Queue<FakeCapture>([first, second]);
        using var recorder = new AudioRecorder(new AudioSettings(), _directory, (_, _) => devices.Dequeue());
        recorder.StartRecording();
        using var cancellation = new CancellationTokenSource();
        var stopping = recorder.StopRecordingAsync(cancellation.Token);
        cancellation.Cancel();

        await FluentActions.Awaiting(() => stopping).Should().ThrowAsync<OperationCanceledException>();
        first.Disposed.Should().BeTrue();
        recorder.State.Should().Be(RecordingState.Idle);

        recorder.StartRecording();
        second.Emit([5, 0]);
        await recorder.StopRecordingAsync(CancellationToken.None);
        recorder.State.Should().Be(RecordingState.Idle);
    }

    [Fact]
    public async Task Driver_failure_should_be_reported_and_cleanup_should_still_run()
    {
        var capture = new FakeCapture();
        using var recorder = CreateRecorder(capture);
        var failure = new IOException("Microphone was disconnected.");
        Exception? reported = null;
        recorder.RecordingFailed += (_, args) => reported = args.Exception;
        recorder.StartRecording();
        capture.Complete(failure);

        var assertion = await FluentActions.Awaiting(() => recorder.StopRecordingAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        assertion.WithInnerException<IOException>();

        reported.Should().BeSameAs(failure);
        capture.Disposed.Should().BeTrue();
        recorder.State.Should().Be(RecordingState.Idle);
    }

    [Fact]
    public async Task Stop_failure_should_release_capture_and_reset_state()
    {
        var capture = new FakeCapture { StopException = new InvalidOperationException("Device failed to stop.") };
        using var recorder = CreateRecorder(capture);
        recorder.StartRecording();

        await FluentActions.Awaiting(() => recorder.StopRecordingAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        capture.Disposed.Should().BeTrue();
        recorder.State.Should().Be(RecordingState.Idle);
    }

    [Fact]
    public async Task Unresponsive_driver_should_time_out_and_release_capture()
    {
        var capture = new FakeCapture();
        using var recorder = CreateRecorder(capture);
        recorder.StartRecording();

        await FluentActions.Awaiting(() => recorder.StopRecordingAsync(CancellationToken.None))
            .Should().ThrowAsync<TimeoutException>();

        capture.Disposed.Should().BeTrue();
        recorder.State.Should().Be(RecordingState.Idle);
    }

    [Fact]
    public void Chunk_subscribers_should_receive_owned_audio_not_a_recycled_driver_buffer()
    {
        var capture = new FakeCapture();
        using var recorder = CreateRecorder(capture);
        byte[]? observed = null;
        recorder.AudioChunkAvailable += (_, args) => observed = args.Buffer;
        recorder.StartRecording();
        byte[] driverBuffer = [1, 0];
        capture.Emit(driverBuffer);
        driverBuffer[0] = 99;

        observed.Should().Equal(1, 0);
    }

    [Fact]
    public void Failed_vad_start_should_release_the_device_for_retry()
    {
        var first = new FakeCapture { StartException = new InvalidOperationException("Device busy.") };
        var second = new FakeCapture();
        var devices = new Queue<FakeCapture>([first, second]);
        using var recorder = new AudioRecorder(new AudioSettings(), _directory, (_, _) => devices.Dequeue());

        FluentActions.Invoking(() => recorder.StartVoiceActivityMonitoring((_, _) => { }))
            .Should().Throw<InvalidOperationException>();
        first.Disposed.Should().BeTrue();
        recorder.StartVoiceActivityMonitoring((_, _) => { });
        recorder.StopVoiceActivityMonitoring();
        second.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Aborting_an_active_capture_should_release_the_device_and_reset_state()
    {
        var capture = new FakeCapture();
        using var recorder = CreateRecorder(capture);
        recorder.StartRecording();

        recorder.AbortRecording();

        capture.Disposed.Should().BeTrue();
        recorder.State.Should().Be(RecordingState.Idle);
        Directory.Exists(_directory).Should().BeFalse();
    }

    [Fact]
    public void Recording_levels_should_include_actual_audio_duration_for_vad()
    {
        var capture = new FakeCapture();
        using var recorder = CreateRecorder(capture);
        var elapsed = 0;
        recorder.AudioLevelChanged += (_, args) => elapsed = args.ElapsedMilliseconds;
        recorder.StartRecording();
        capture.Emit(new byte[3200]);

        elapsed.Should().Be(100);
    }

    [Fact]
    public async Task Vad_trigger_audio_should_be_prepended_in_order_to_the_recording()
    {
        var monitor = new FakeCapture();
        var capture = new FakeCapture { CompleteOnStop = true };
        var devices = new Queue<FakeCapture>([monitor, capture]);
        using var recorder = new AudioRecorder(new AudioSettings(), _directory, (_, _) => devices.Dequeue());
        recorder.StartVoiceActivityMonitoring((_, _) => { });
        monitor.Emit([1, 0, 2, 0]);
        recorder.StopVoiceActivityMonitoring(preserveAudio: true);
        var stream = new QueueStream();
        var queue = new LiveAudioQueue(stream, Task.CompletedTask, 16000, CancellationToken.None);
        recorder.AudioChunkAvailable += (_, chunk) => queue.Enqueue(chunk);
        recorder.StartRecording();
        capture.Emit([3, 0, 4, 0]);

        var audio = await recorder.StopRecordingAsync(CancellationToken.None);
        queue.Close();
        await queue.Sender;
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }, stream.Chunks.SelectMany(c => c));

        using var reader = new WaveFileReader(new MemoryStream(audio.AudioData));
        var pcm = new byte[8];
        reader.Read(pcm, 0, pcm.Length).Should().Be(8);
        pcm.Should().Equal(1, 0, 2, 0, 3, 0, 4, 0);
    }

    [Fact]
    public async Task Turning_off_vad_should_not_leak_old_audio_into_the_next_recording()
    {
        var monitor = new FakeCapture();
        var capture = new FakeCapture { CompleteOnStop = true };
        var devices = new Queue<FakeCapture>([monitor, capture]);
        using var recorder = new AudioRecorder(new AudioSettings(), _directory, (_, _) => devices.Dequeue());
        recorder.StartVoiceActivityMonitoring((_, _) => { });
        monitor.Emit([1, 0]);
        recorder.StopVoiceActivityMonitoring();
        recorder.StartRecording();
        capture.Emit([2, 0]);

        var audio = await recorder.StopRecordingAsync(CancellationToken.None);

        using var reader = new WaveFileReader(new MemoryStream(audio.AudioData));
        reader.Length.Should().Be(2);
        var sample = new byte[2];
        reader.Read(sample, 0, sample.Length).Should().Be(2);
        sample.Should().Equal(2, 0);
    }

    [Fact]
    public async Task Vad_pre_roll_should_remain_bounded_during_extended_silence()
    {
        var monitor = new FakeCapture();
        var capture = new FakeCapture { CompleteOnStop = true };
        var devices = new Queue<FakeCapture>([monitor, capture]);
        using var recorder = new AudioRecorder(new AudioSettings(), _directory, (_, _) => devices.Dequeue());
        recorder.StartVoiceActivityMonitoring((_, _) => { });
        for (var index = 0; index < 100; index++) monitor.Emit(new byte[3200]);
        recorder.StopVoiceActivityMonitoring(preserveAudio: true);
        recorder.StartRecording();

        var audio = await recorder.StopRecordingAsync(CancellationToken.None);

        using var reader = new WaveFileReader(new MemoryStream(audio.AudioData));
        reader.Length.Should().Be(16000); // 500 ms at 16 kHz PCM16, not the full ten seconds.
    }

    [Fact]
    public async Task Sessions_completed_in_the_same_second_should_have_distinct_audio_paths()
    {
        var capture = new FakeCapture { CompleteOnStop = true };
        using var recorder = CreateRecorder(capture);
        recorder.StartRecording();
        var first = await recorder.StopRecordingAsync(CancellationToken.None);
        recorder.StartRecording();
        var second = await recorder.StopRecordingAsync(CancellationToken.None);

        first.FilePath.Should().NotBe(second.FilePath);
        File.Exists(first.FilePath).Should().BeTrue();
        File.Exists(second.FilePath).Should().BeTrue();
    }

    private AudioRecorder CreateRecorder(FakeCapture capture) => new(new AudioSettings(), _directory, (_, _) => capture);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public bool CompleteOnStop { get; init; }
        public bool Disposed { get; private set; }
        public Exception? StartException { get; init; }
        public Exception? StopException { get; init; }

        public void StartRecording()
        {
            if (StartException is not null) throw StartException;
        }

        public void StopRecording()
        {
            if (StopException is not null) throw StopException;
            if (CompleteOnStop) Complete();
        }

        public void Emit(byte[] pcm) => DataAvailable?.Invoke(this, new WaveInEventArgs(pcm, pcm.Length));
        public void Complete(Exception? error = null) => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public void Dispose() => Disposed = true;
    }
}
