using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using NAudio.Wave;

internal static class LocalPersistenceDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = BenchmarkOptions.Parse(args);
        using var reader = new WaveFileReader(options.AudioPath);
        if (reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1) return 2;
        var pcm = new byte[reader.Length];
        reader.Read(pcm, 0, pcm.Length);
        var directory = Path.Combine(Path.GetFullPath("artifacts/latency"), "local-costs-" + Guid.NewGuid().ToString("N"));
        var reports = new List<SessionTimingReport>();
        try
        {
            for (var i = 0; i < options.Repetitions; i++)
            {
                var metrics = new SessionMetrics("local-persistence") { AudioDurationMs = reader.TotalTime.TotalMilliseconds };
                using var scope = metrics.Activate();
                var capture = new ReplayCapture();
                using var recorder = new AudioRecorder(new AudioSettings { SampleRate = reader.WaveFormat.SampleRate }, directory, (_, _) => capture);
                recorder.StartRecording();
                capture.Emit(pcm);
                metrics.Mark(TimingStage.StopRequested);
                var drained = await recorder.DrainCaptureAsync(CancellationToken.None);
                var audio = await drained.PersistAsync(CancellationToken.None);
                metrics.Mark(TimingStage.HistoryStart);
                await new HistoryLogger(directory).AppendAsync(new(DateTimeOffset.UtcNow, "Synthetic fixture", "Synthetic fixture", "diagnostic", "en", 0, audio.FilePath));
                metrics.Mark(TimingStage.HistoryEnd);
                metrics.Mark(TimingStage.Ready);
                reports.Add(metrics.Snapshot(SessionOutcome.Ready));
            }
            await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    // Drives the production drain/export path without accessing or starting a physical microphone.
    private sealed class ReplayCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new(null));
        public void Emit(byte[] bytes) => DataAvailable?.Invoke(this, new(bytes, bytes.Length));
        public void Dispose() { }
    }
}
