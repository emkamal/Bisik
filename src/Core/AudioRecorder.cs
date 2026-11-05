using Bisik.Models;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Bisik.Core;

public enum RecordingState
{
    Idle,
    Recording,
    Stopping,
}

public enum AudioRecorderSignal
{
    Start,
    Stop,
    FinalizeStop,
    Reset,
}

public enum VoiceActivityAction
{
    None,
    StartRecording,
    StopRecording,
}

public sealed record AudioInputDevice(string Id, string Name);

public sealed record RecordedAudio(
    string FilePath,
    byte[] AudioData,
    AudioFormat Format,
    int SampleRate);

public sealed class CapturedRecording(string filePath, byte[] wavData, Func<CancellationToken, Task<RecordedAudio>> persist)
{
    private readonly object _gate = new();
    private Task<RecordedAudio>? _persistence;
    public string FilePath { get; } = filePath;
    public byte[] WavData { get; } = wavData;
    public Task<RecordedAudio> PersistAsync(CancellationToken ct)
    { lock (_gate) return _persistence ??= persist(ct); }
}

public sealed class AudioLevelChangedEventArgs(double level, int elapsedMilliseconds = 0) : EventArgs
{
    public double Level { get; } = level;
    public int ElapsedMilliseconds { get; } = elapsedMilliseconds;
}

public sealed class AudioRecordingFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed class AudioRecorder : IDisposable
{
    private readonly string _defaultOutputDirectory;
    private readonly Func<WaveFormat, int, IWaveIn> _captureFactory;
    private readonly object _captureLock = new();
    private IWaveIn? _waveIn;
    private IWaveIn? _monitorWaveIn;
    private MemoryStream? _bufferStream;
    private WaveFileWriter? _waveWriter;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private AudioSettings _settings;
    private string? _pendingOutputPath;
    private WaveFormat? _captureFormat;
    private Action<double, int>? _monitorLevelCallback;
    private readonly Queue<byte[]> _monitoredAudio = new();
    private int _monitoredAudioBytes;
    private byte[]? _pendingPreRoll;
    private int _preRollSampleRate;

    public AudioRecorder()
        : this(settings: null, outputDirectory: null)
    {
    }

    public AudioRecorder(
        AudioSettings? settings = null,
        string? outputDirectory = null,
        Func<WaveFormat, int, IWaveIn>? captureFactory = null)
    {
        _settings = settings ?? new AudioSettings();
        _captureFactory = captureFactory ?? CreateCapture;
        _defaultOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Path.GetTempPath(), "Bisik", "recordings")
            : outputDirectory;
    }

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public AudioSettings Settings => _settings;

    public event EventHandler<AudioLevelChangedEventArgs>? AudioLevelChanged;

    // Like the level/chunk events, this is raised on the capture thread; UI subscribers must marshal it.
    public event EventHandler<AudioRecordingFailedEventArgs>? RecordingFailed;

    /// <summary>
    /// Fires on each <see cref="WaveInEvent.DataAvailable"/> callback with the raw PCM buffer,
    /// so downstream consumers (e.g. realtime transcription) can process chunks as they arrive.
    /// </summary>
    public event EventHandler<AudioChunkEventArgs>? AudioChunkAvailable;

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        var devices = new List<AudioInputDevice>();
        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            var capabilities = WaveIn.GetCapabilities(index);
            devices.Add(new AudioInputDevice(index.ToString(), capabilities.ProductName));
        }

        return devices;
    }

    public void UpdateSettings(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (State != RecordingState.Idle)
        {
            throw new InvalidOperationException("Audio recorder settings cannot change while recording is active.");
        }

        _settings = settings;
    }

    public void StartRecording()
    {
        if (_monitorWaveIn is not null)
        {
            throw new InvalidOperationException("Stop voice activity monitoring before recording.");
        }

        if (!TryTransition(AudioRecorderSignal.Start))
        {
            throw new InvalidOperationException("Audio recorder is already active.");
        }

        try
        {
            var sampleRate = _settings.SampleRate > 0 ? _settings.SampleRate : 16000;
            _captureFormat = new WaveFormat(sampleRate, 16, 1);
            _bufferStream = new MemoryStream();
            _waveWriter = new WaveFileWriter(new NonClosingStream(_bufferStream), _captureFormat);
            byte[]? preRoll;
            lock (_captureLock)
            {
                preRoll = _preRollSampleRate == sampleRate ? _pendingPreRoll : null;
                _pendingPreRoll = null;
            }
            if (preRoll is not null)
            {
                _waveWriter.Write(preRoll, 0, preRoll.Length);
                AudioChunkAvailable?.Invoke(this, new AudioChunkEventArgs(preRoll, preRoll.Length, sampleRate));
            }
            var timestampedPath = AudioRecorderLogic.BuildOutputFilePath(
                _defaultOutputDirectory,
                _settings.Format,
                DateTimeOffset.UtcNow);
            // Rapid retries must not overwrite a previous recording used by history playback.
            _pendingOutputPath = Path.Combine(_defaultOutputDirectory,
                $"{Path.GetFileNameWithoutExtension(timestampedPath)}-{Guid.NewGuid():N}{Path.GetExtension(timestampedPath)}");
            _recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

            _waveIn = _captureFactory(_captureFormat, ResolveDeviceNumber(_settings.DeviceId));
            _waveIn.DataAvailable += HandleDataAvailable;
            _waveIn.RecordingStopped += HandleRecordingStopped;
            _waveIn.StartRecording();
        }
        catch
        {
            try
            {
                CleanupCaptureResources();
            }
            finally
            {
                State = RecordingState.Idle;
            }
            throw;
        }
    }

    public void StartVoiceActivityMonitoring(Action<double, int> onLevelSample)
    {
        ArgumentNullException.ThrowIfNull(onLevelSample);

        if (_monitorWaveIn is not null)
        {
            throw new InvalidOperationException("Voice activity monitoring is already active.");
        }

        if (State != RecordingState.Idle)
        {
            throw new InvalidOperationException("Voice activity monitoring cannot start while recording is active.");
        }

        try
        {
            var sampleRate = _settings.SampleRate > 0 ? _settings.SampleRate : 16000;
            lock (_captureLock)
            {
                _monitoredAudio.Clear();
                _monitoredAudioBytes = 0;
                _pendingPreRoll = null;
            }
            _monitorLevelCallback = onLevelSample;
            _monitorWaveIn = _captureFactory(new WaveFormat(sampleRate, 16, 1), ResolveDeviceNumber(_settings.DeviceId));
            _monitorWaveIn.DataAvailable += HandleMonitorDataAvailable;
            _monitorWaveIn.RecordingStopped += HandleMonitorRecordingStopped;
            _monitorWaveIn.StartRecording();
        }
        catch
        {
            StopVoiceActivityMonitoring();
            throw;
        }
    }

    public void StopVoiceActivityMonitoring(bool preserveAudio = false)
    {
        IWaveIn? monitor;
        lock (_captureLock)
        {
            monitor = _monitorWaveIn;
            // Preserve the speech that triggered VAD instead of dropping its first syllables.
            _pendingPreRoll = preserveAudio ? _monitoredAudio.SelectMany(chunk => chunk).ToArray() : null;
            _preRollSampleRate = monitor?.WaveFormat.SampleRate ?? 0;
            _monitoredAudio.Clear();
            _monitoredAudioBytes = 0;
            _monitorWaveIn = null;
        }
        if (monitor is null)
        {
            return;
        }

        _monitorLevelCallback = null;
        monitor.DataAvailable -= HandleMonitorDataAvailable;
        monitor.RecordingStopped -= HandleMonitorRecordingStopped;
        try
        {
            monitor.StopRecording();
        }
        finally
        {
            monitor.Dispose();
        }
    }

    public async Task<RecordedAudio> StopRecordingAsync(CancellationToken cancellationToken)
    {
        var recording = await DrainCaptureAsync(cancellationToken);
        return await recording.PersistAsync(cancellationToken);
    }

    public async Task<CapturedRecording> DrainCaptureAsync(CancellationToken cancellationToken)
    {
        if (!TryTransition(AudioRecorderSignal.Stop))
        {
            throw new InvalidOperationException("Audio recorder is not currently recording.");
        }

        try
        {
            var waveIn = _waveIn ?? throw new InvalidOperationException("Audio recorder was not initialized.");
            var stopped = _recordingStopped ?? throw new InvalidOperationException("Audio recorder stop state was not initialized.");
            waveIn.StopRecording();

            // NAudio emits its last DataAvailable buffers before RecordingStopped. Never finalize early.
            var error = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (error is not null)
            {
                throw new InvalidOperationException("Microphone capture failed.", error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SessionMetrics.Current?.Mark(TimingStage.CaptureDrained);
            return FinalizeCapture();
        }
        finally
        {
            try
            {
                CleanupCaptureResources();
            }
            finally
            {
                State = RecordingState.Idle;
            }
        }
    }

    public bool TryTransition(AudioRecorderSignal signal)
    {
        var nextState = AudioRecorderLogic.Transition(State, signal);
        var changed = nextState != State;
        State = nextState;
        return changed;
    }

    public void AbortRecording()
    {
        _pendingPreRoll = null;
        _recordingStopped?.TrySetCanceled();
        try
        {
            CleanupCaptureResources();
        }
        finally
        {
            State = RecordingState.Idle;
        }
    }

    public void Dispose()
    {
        try
        {
            StopVoiceActivityMonitoring();
        }
        finally
        {
            CleanupCaptureResources();
            State = RecordingState.Idle;
            GC.SuppressFinalize(this);
        }
    }

    private CapturedRecording FinalizeCapture()
    {
        var writer = _waveWriter ?? throw new InvalidOperationException("No active wave writer is available.");
        var buffer = _bufferStream ?? throw new InvalidOperationException("No active audio buffer is available.");
        var format = _captureFormat ?? throw new InvalidOperationException("No capture format is available.");
        var outputPath = _pendingOutputPath ?? throw new InvalidOperationException("No output path is available.");

        writer.Flush();
        writer.Dispose();
        _waveWriter = null;

        var wavBytes = buffer.ToArray();
        var audioFormat = _settings.Format;
        return new CapturedRecording(outputPath, wavBytes, ct => PersistRecordingAsync(outputPath, wavBytes, format.SampleRate, audioFormat, ct));
    }

    private static async Task<RecordedAudio> PersistRecordingAsync(string outputPath, byte[] wavBytes,
        int sampleRate, AudioFormat audioFormat, CancellationToken cancellationToken)
    {
        SessionMetrics.Current?.Mark(TimingStage.ExportStart);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        try
        {
            var result = audioFormat switch
            {
                AudioFormat.Wav => await WriteWavOutputAsync(outputPath, wavBytes, sampleRate, cancellationToken),
                AudioFormat.Mp3 => await WriteMp3OutputAsync(outputPath, wavBytes, sampleRate, cancellationToken),
                AudioFormat.Ogg => throw new NotSupportedException("Ogg recording is not supported with the current dependency set."),
                _ => throw new InvalidOperationException($"Unsupported audio format '{audioFormat}'."),
            };
            SessionMetrics.Current?.Mark(TimingStage.ExportEnd);
            return result;
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
    }

    private static async Task<RecordedAudio> WriteWavOutputAsync(
        string outputPath,
        byte[] wavBytes,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(outputPath, wavBytes, cancellationToken);
        return new RecordedAudio(outputPath, wavBytes, AudioFormat.Wav, sampleRate);
    }

    private static async Task<RecordedAudio> WriteMp3OutputAsync(
        string outputPath,
        byte[] wavBytes,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(new MemoryStream(wavBytes));
            MediaFoundationEncoder.EncodeToMp3(reader, outputPath);
        }, cancellationToken);
        var mp3Bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
        return new RecordedAudio(outputPath, mp3Bytes, AudioFormat.Mp3, sampleRate);
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_captureLock)
        {
            if (!ReferenceEquals(sender, _waveIn))
            {
                return;
            }

            _waveWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        }
        PublishAudioLevel(args.Buffer, args.BytesRecorded);

        // NAudio reuses its buffer after the callback; async consumers need owned bytes.
        var sampleRate = _captureFormat?.SampleRate ?? _settings.SampleRate;
        if (AudioChunkAvailable is { } chunkHandler)
        {
            var chunk = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
            chunkHandler(this, new AudioChunkEventArgs(chunk, chunk.Length, sampleRate));
        }
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (!ReferenceEquals(sender, _waveIn))
        {
            return;
        }

        _recordingStopped?.TrySetResult(args.Exception);
        if (args.Exception is not null)
        {
            RecordingFailed?.Invoke(this, new AudioRecordingFailedEventArgs(args.Exception));
        }
    }

    private void HandleMonitorDataAvailable(object? sender, WaveInEventArgs args)
    {
        int bytesPerSecond;
        lock (_captureLock)
        {
            if (!ReferenceEquals(sender, _monitorWaveIn)) return;
            bytesPerSecond = _monitorWaveIn!.WaveFormat.AverageBytesPerSecond;
            var limit = bytesPerSecond * Math.Clamp(_settings.VadMinSpeechMs + 200, 300, 2000) / 1000;
            var count = Math.Min(args.BytesRecorded, limit);
            _monitoredAudio.Enqueue(args.Buffer.AsSpan(args.BytesRecorded - count, count).ToArray());
            _monitoredAudioBytes += count;
            while (_monitoredAudioBytes > limit && _monitoredAudio.Count > 1)
                _monitoredAudioBytes -= _monitoredAudio.Dequeue().Length;
        }

        var samples = AudioRecorderLogic.ReadPcm16Samples(args.Buffer, args.BytesRecorded);
        var level = AudioRecorderLogic.CalculateLevel(samples);
        _monitorLevelCallback?.Invoke(level, args.BytesRecorded * 1000 / bytesPerSecond);
    }

    private void HandleMonitorRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (ReferenceEquals(sender, _monitorWaveIn) && args.Exception is not null)
        {
            RecordingFailed?.Invoke(this, new AudioRecordingFailedEventArgs(args.Exception));
        }
    }

    private void CleanupCaptureResources()
    {
        IWaveIn? capture;
        lock (_captureLock)
        {
            capture = _waveIn;
            _waveIn = null;
        }

        if (capture is not null)
        {
            capture.DataAvailable -= HandleDataAvailable;
            capture.RecordingStopped -= HandleRecordingStopped;
            capture.Dispose();
        }

        _waveWriter?.Dispose();
        _waveWriter = null;

        _bufferStream?.Dispose();
        _bufferStream = null;

        _recordingStopped = null;
        _pendingOutputPath = null;
        _captureFormat = null;
    }

    private static IWaveIn CreateCapture(WaveFormat format, int deviceNumber) => new WaveInEvent
    {
        DeviceNumber = deviceNumber,
        WaveFormat = format,
        BufferMilliseconds = 100,
        NumberOfBuffers = 3,
    };

    private static int ResolveDeviceNumber(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return 0;
        }

        return int.TryParse(deviceId, out var parsedDeviceNumber) ? parsedDeviceNumber : 0;
    }

    private void PublishAudioLevel(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            AudioLevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(0d));
            return;
        }

        var samples = AudioRecorderLogic.ReadPcm16Samples(buffer, bytesRecorded);
        var level = AudioRecorderLogic.CalculateLevel(samples);
        var bytesPerSecond = _captureFormat?.AverageBytesPerSecond ?? Math.Max(1, _settings.SampleRate) * 2;
        AudioLevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(level, bytesRecorded * 1000 / bytesPerSecond));
    }

    private sealed class NonClosingStream(Stream innerStream) : Stream
    {
        private readonly Stream _innerStream = innerStream;

        public override bool CanRead => _innerStream.CanRead;

        public override bool CanSeek => _innerStream.CanSeek;

        public override bool CanWrite => _innerStream.CanWrite;

        public override long Length => _innerStream.Length;

        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override void Flush()
        {
            _innerStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _innerStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _innerStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _innerStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _innerStream.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Flush();
            }
        }
    }
}

public sealed class VoiceActivityTracker(AudioSettings settings)
{
    private readonly AudioSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private int _speechDurationMs;
    private int _silenceDurationMs;
    private bool _recordingActive;

    public VoiceActivityAction ProcessLevel(double currentLevel, int elapsedMs)
    {
        var clampedElapsedMs = Math.Max(0, elapsedMs);
        var threshold = _settings.VadSilenceThreshold;

        if (currentLevel > threshold)
        {
            _speechDurationMs += clampedElapsedMs;
            _silenceDurationMs = 0;

            if (!_recordingActive && _speechDurationMs >= _settings.VadMinSpeechMs)
            {
                _recordingActive = true;
                _silenceDurationMs = 0;
                return VoiceActivityAction.StartRecording;
            }

            return VoiceActivityAction.None;
        }

        _speechDurationMs = 0;

        if (!_recordingActive)
        {
            return VoiceActivityAction.None;
        }

        _silenceDurationMs += clampedElapsedMs;
        if (_silenceDurationMs >= _settings.VadMinSpeechMs)
        {
            _recordingActive = false;
            _silenceDurationMs = 0;
            return VoiceActivityAction.StopRecording;
        }

        return VoiceActivityAction.None;
    }

    public void Reset()
    {
        _speechDurationMs = 0;
        _silenceDurationMs = 0;
        _recordingActive = false;
    }
}

public static class AudioRecorderLogic
{
    // Keep the recorder lifecycle explicit so hotkey/VAD flows cannot start overlapping sessions.
    public static RecordingState Transition(RecordingState currentState, AudioRecorderSignal signal)
    {
        return (currentState, signal) switch
        {
            (RecordingState.Idle, AudioRecorderSignal.Start) => RecordingState.Recording,
            (RecordingState.Recording, AudioRecorderSignal.Stop) => RecordingState.Stopping,
            (RecordingState.Stopping, AudioRecorderSignal.FinalizeStop) => RecordingState.Idle,
            (_, AudioRecorderSignal.Reset) => RecordingState.Idle,
            _ => currentState,
        };
    }

    public static double CalculateLevel(short[] samples)
    {
        if (samples.Length == 0)
        {
            return 0d;
        }

        var peak = samples.Max(sample => Math.Abs((double)sample));
        return Math.Min(1d, peak / short.MaxValue);
    }

    public static short[] ReadPcm16Samples(byte[] buffer, int bytesRecorded)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var sampleByteCount = Math.Max(0, Math.Min(bytesRecorded, buffer.Length));
        var sampleCount = sampleByteCount / sizeof(short);
        if (sampleCount == 0)
        {
            return [];
        }

        var samples = new short[sampleCount];
        Buffer.BlockCopy(buffer, 0, samples, 0, sampleCount * sizeof(short));
        return samples;
    }

    public static bool ShouldStopForSilence(
        AudioSettings settings,
        double currentLevel,
        int silenceDurationMs,
        bool speechDetected)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!speechDetected)
        {
            return false;
        }

        if (currentLevel > settings.VadSilenceThreshold)
        {
            return false;
        }

        return silenceDurationMs >= settings.VadMinSpeechMs;
    }

    public static string BuildOutputFilePath(string directory, AudioFormat format, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var extension = format switch
        {
            AudioFormat.Wav => "wav",
            AudioFormat.Mp3 => "mp3",
            AudioFormat.Ogg => "ogg",
            _ => "wav",
        };

        return Path.Combine(directory, $"bisik-{timestamp:yyyyMMdd-HHmmss}.{extension}");
    }
}
