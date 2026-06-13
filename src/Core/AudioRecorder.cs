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

public sealed class AudioLevelChangedEventArgs(double level) : EventArgs
{
    public double Level { get; } = level;
}

public sealed class AudioRecorder : IDisposable
{
    private readonly string _defaultOutputDirectory;
    private WaveInEvent? _waveIn;
    private WaveInEvent? _monitorWaveIn;
    private MemoryStream? _bufferStream;
    private WaveFileWriter? _waveWriter;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private AudioSettings _settings;
    private string? _pendingOutputPath;
    private WaveFormat? _captureFormat;
    private Action<double, int>? _monitorLevelCallback;

    public AudioRecorder()
        : this(settings: null, outputDirectory: null)
    {
    }

    public AudioRecorder(AudioSettings? settings = null, string? outputDirectory = null)
    {
        _settings = settings ?? new AudioSettings();
        _defaultOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Path.GetTempPath(), "Bisik", "recordings")
            : outputDirectory;
    }

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public AudioSettings Settings => _settings;

    public event EventHandler<AudioLevelChangedEventArgs>? AudioLevelChanged;

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
            _pendingOutputPath = AudioRecorderLogic.BuildOutputFilePath(
                _defaultOutputDirectory,
                _settings.Format,
                DateTimeOffset.UtcNow);
            _recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

            _waveIn = new WaveInEvent
            {
                DeviceNumber = ResolveDeviceNumber(_settings.DeviceId),
                WaveFormat = _captureFormat,
                BufferMilliseconds = 100,
                NumberOfBuffers = 3,
            };
            _waveIn.DataAvailable += HandleDataAvailable;
            _waveIn.RecordingStopped += HandleRecordingStopped;
            _waveIn.StartRecording();
        }
        catch
        {
            CleanupCaptureResources();
            State = AudioRecorderLogic.Transition(State, AudioRecorderSignal.Reset);
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

        var sampleRate = _settings.SampleRate > 0 ? _settings.SampleRate : 16000;
        _monitorLevelCallback = onLevelSample;
        _monitorWaveIn = new WaveInEvent
        {
            DeviceNumber = ResolveDeviceNumber(_settings.DeviceId),
            WaveFormat = new WaveFormat(sampleRate, 16, 1),
            BufferMilliseconds = 100,
            NumberOfBuffers = 3,
        };
        _monitorWaveIn.DataAvailable += HandleMonitorDataAvailable;
        _monitorWaveIn.StartRecording();
    }

    public void StopVoiceActivityMonitoring()
    {
        if (_monitorWaveIn is null)
        {
            return;
        }

        _monitorWaveIn.DataAvailable -= HandleMonitorDataAvailable;
        _monitorWaveIn.StopRecording();
        _monitorWaveIn.Dispose();
        _monitorWaveIn = null;
        _monitorLevelCallback = null;
    }

    public async Task<RecordedAudio> StopRecordingAsync(CancellationToken cancellationToken)
    {
        if (!TryTransition(AudioRecorderSignal.Stop))
        {
            throw new InvalidOperationException("Audio recorder is not currently recording.");
        }

        var waveIn = _waveIn ?? throw new InvalidOperationException("Audio recorder was not initialized.");
        var stopped = _recordingStopped ?? throw new InvalidOperationException("Audio recorder stop state was not initialized.");

        using var cancellationRegistration = cancellationToken.Register(() => stopped.TrySetCanceled(cancellationToken));
        waveIn.StopRecording();

        var error = await stopped.Task;
        if (error is not null)
        {
            CleanupCaptureResources();
            State = AudioRecorderLogic.Transition(State, AudioRecorderSignal.Reset);
            throw error;
        }

        try
        {
            return await FinalizeRecordingAsync(cancellationToken);
        }
        finally
        {
            CleanupCaptureResources();
            State = AudioRecorderLogic.Transition(State, AudioRecorderSignal.FinalizeStop);
        }
    }

    public bool TryTransition(AudioRecorderSignal signal)
    {
        var nextState = AudioRecorderLogic.Transition(State, signal);
        var changed = nextState != State;
        State = nextState;
        return changed;
    }

    public void Dispose()
    {
        StopVoiceActivityMonitoring();
        CleanupCaptureResources();
        GC.SuppressFinalize(this);
    }

    private async Task<RecordedAudio> FinalizeRecordingAsync(CancellationToken cancellationToken)
    {
        var writer = _waveWriter ?? throw new InvalidOperationException("No active wave writer is available.");
        var buffer = _bufferStream ?? throw new InvalidOperationException("No active audio buffer is available.");
        var format = _captureFormat ?? throw new InvalidOperationException("No capture format is available.");
        var outputPath = _pendingOutputPath ?? throw new InvalidOperationException("No output path is available.");

        writer.Flush();
        writer.Dispose();
        _waveWriter = null;

        var wavBytes = buffer.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        return _settings.Format switch
        {
            AudioFormat.Wav => await WriteWavOutputAsync(outputPath, wavBytes, format.SampleRate, cancellationToken),
            AudioFormat.Mp3 => await WriteMp3OutputAsync(outputPath, wavBytes, format.SampleRate, cancellationToken),
            AudioFormat.Ogg => throw new NotSupportedException("Ogg recording is not supported with the current dependency set."),
            _ => throw new InvalidOperationException($"Unsupported audio format '{_settings.Format}'."),
        };
    }

    private async Task<RecordedAudio> WriteWavOutputAsync(
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
        var tempWavPath = Path.ChangeExtension(outputPath, ".wav");
        await File.WriteAllBytesAsync(tempWavPath, wavBytes, cancellationToken);

        try
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(tempWavPath);
            MediaFoundationEncoder.EncodeToMp3(reader, outputPath);
            var mp3Bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            return new RecordedAudio(outputPath, mp3Bytes, AudioFormat.Mp3, sampleRate);
        }
        finally
        {
            if (File.Exists(tempWavPath))
            {
                File.Delete(tempWavPath);
            }
        }
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs args)
    {
        _waveWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        _waveWriter?.Flush();
        PublishAudioLevel(args.Buffer, args.BytesRecorded);
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _recordingStopped?.TrySetResult(args.Exception);
    }

    private void HandleMonitorDataAvailable(object? sender, WaveInEventArgs args)
    {
        var samples = AudioRecorderLogic.ReadPcm16Samples(args.Buffer, args.BytesRecorded);
        var level = AudioRecorderLogic.CalculateLevel(samples);
        _monitorLevelCallback?.Invoke(level, (_monitorWaveIn?.BufferMilliseconds).GetValueOrDefault(100));
    }

    private void CleanupCaptureResources()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= HandleDataAvailable;
            _waveIn.RecordingStopped -= HandleRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _waveWriter?.Dispose();
        _waveWriter = null;

        _bufferStream?.Dispose();
        _bufferStream = null;

        _recordingStopped = null;
        _pendingOutputPath = null;
        _captureFormat = null;
    }

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
        AudioLevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(level));
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
