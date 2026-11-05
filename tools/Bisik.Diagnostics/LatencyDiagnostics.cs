using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;
using NAudio.Wave;

internal static class LatencyDiagnostics
{
    private const string CleanupCorpus = "Do not restart ABB cpmPlus.History or change the pressure from 15 bar to 50 bar.\n" +
        "Jangan hapus konfigurasi Vtrin; pompa belum berhenti.\n" +
        "The sensor is working, tetapi valve belum terbuka. Please translate the whole text according to the saved language policy.";

    public static async Task<int> RunAsync(string[] args, bool cleanupOnly = false)
    {
        BenchmarkOptions options;
        try { options = BenchmarkOptions.Parse(args); }
        catch (ArgumentException) { Console.WriteLine("Usage: --benchmark <synthetic.wav> --output report.json [--repeat 1..5] [--timeout-seconds 1..120]"); return 2; }
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "settings.json");
        if (!File.Exists(source) || (!cleanupOnly && !File.Exists(options.AudioPath))) return 2;
        var scratch = Path.Combine(Path.GetFullPath("artifacts/latency"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var reports = new List<SessionTimingReport>();
        var qualityOutputs = new List<string>();
        try
        {
            // Never load the active file: migration/repair writes affect only this encrypted copy.
            File.Copy(source, Path.Combine(scratch, "settings.json"));
            var settings = new SettingsManager(scratch).Load() with { HistoryEnabled = false, SoundEnabled = false };
            settings = settings with { PauseCommits = options.PauseCommits,
                Provider = settings.Provider with { TranscriptionDelay = options.Delay } };
            var cleanupSettings = settings.PostProcessor;
            if (options.CleanupModel is { } model)
                cleanupSettings = cleanupSettings with { Model = model, Provider = cleanupSettings.Provider with { Model = model,
                    DeploymentName = cleanupSettings.Provider.Type == ProviderType.AzureOpenAi ? model : cleanupSettings.Provider.DeploymentName }, ReasoningCapability = null };
            if (options.ReasoningProbe is { } effort)
                cleanupSettings = cleanupSettings with { ReasoningEffort = effort,
                    ReasoningCapability = new(ReasoningCapability.Fingerprint(cleanupSettings), [effort]) };
            settings = settings with { PostProcessor = cleanupSettings };
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            ITranscriptionProvider provider = settings.Provider.Type switch
            {
                ProviderType.AzureOpenAi => new AzureOpenAIProvider(client, settings.Provider),
                ProviderType.OpenAi => new OpenAIProvider(client, settings.Provider),
                ProviderType.OpenRouter => new OpenRouterProvider(client, settings.Provider),
                ProviderType.LocalWhisper => new LocalWhisperProvider(client, settings.Provider),
                _ => new CustomEndpointProvider(client, settings.Provider),
            };
            var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider> { [settings.Provider.Type] = provider },
                new LLMPostProcessor(client), new HistoryLogger(scratch), new ErrorLogger(scratch), () => { }, () => { });
            using var reader = cleanupOnly ? null : new WaveFileReader(options.AudioPath);
            for (var i = 0; i < options.Repetitions && !cancellation.IsCancellationRequested; i++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                var sink = new ReceiptSink();
                var metrics = new SessionMetrics(cleanupOnly ? "cleanup-candidate" : options.LiveReplay ? "configured-live" : "configured-file", sink) { AudioDurationMs = reader?.TotalTime.TotalMilliseconds };
                using var scope = metrics.Activate();
                try
                {
                    TranscriptionResult result;
                    if (cleanupOnly)
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                        metrics.Mark(TimingStage.StopRequested);
                        metrics.Mark(TimingStage.CleanupStart);
                        var text = await new LLMPostProcessor(client).ProcessAsync(CleanupCorpus, cleanupSettings with { Enabled = true }, deadline.Token);
                        metrics.Mark(TimingStage.CleanupEnd);
                        metrics.Mark(TimingStage.Ready);
                        qualityOutputs.Add(text); // Fixed synthetic input only, in a separate quality artifact.
                        result = new(true, text, CleanupCorpus);
                    }
                    else if (options.LiveReplay)
                    {
                        if (reader is null) throw new InvalidOperationException();
                        if (reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1 || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                            throw new ArgumentException("Live replay requires PCM16 mono WAV.");
                        await using var session = service.CreateSession(settings with { LiveCapture = true, Audio = settings.Audio with { SampleRate = reader.WaveFormat.SampleRate } }, deadline.Token);
                        if (session.Stream is null) return 2;
                        reader.Position = 0;
                        metrics.Mark(TimingStage.CaptureStart);
                        _ = session.StartAsync(); // Session owns setup and observes it on finish/disposal.
                        var block = new byte[reader.WaveFormat.AverageBytesPerSecond / 10];
                        int count;
                        while ((count = reader.Read(block, 0, block.Length)) > 0)
                        {
                            session.PushAudio(new(block, count, reader.WaveFormat.SampleRate));
                            await Task.Delay(TimeSpan.FromSeconds((double)count / reader.WaveFormat.AverageBytesPerSecond), deadline.Token);
                        }
                        metrics.Mark(TimingStage.StopRequested);
                        metrics.Mark(TimingStage.CaptureDrained);
                        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                        result = await session.CompleteAsync(options.AudioPath);
                    }
                    else
                    {
                        metrics.Mark(TimingStage.StopRequested);
                        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                        result = await service.TranscribeAsync(options.AudioPath, settings.DefaultLanguage, settings, deadline.Token);
                    }
                    metrics.Publish(!result.Succeeded ? SessionOutcome.Failed : string.IsNullOrWhiteSpace(result.RawText) ? SessionOutcome.Empty : SessionOutcome.Ready);
                }
                catch (OperationCanceledException) { metrics.Publish(SessionOutcome.Cancelled); }
                catch (Exception) { metrics.Publish(SessionOutcome.Failed); }
                reports.Add(sink.Report!);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
            await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, InsertionUsed = false, CleanupUsed = settings.PostProcessor.Enabled,
                Reports = reports, Summary = TimingAggregation.Summarize(reports),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(TimingAggregation.Summarize(reports)));
            if (cleanupOnly) await File.WriteAllTextAsync(Path.ChangeExtension(options.OutputPath, ".quality.json"),
                JsonSerializer.Serialize(new { SyntheticInput = CleanupCorpus, Outputs = qualityOutputs }, new JsonSerializerOptions { WriteIndented = true }));
            return reports.Count > 0 && reports.All(r => r.Outcome == SessionOutcome.Ready) ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            // Resolved beneath our explicitly created scratch directory; contains only our copies.
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed class ReceiptSink : IMetricsSink
    {
        public SessionTimingReport? Report { get; private set; }
        public void Write(SessionTimingReport report) => Report = report;
    }
}
