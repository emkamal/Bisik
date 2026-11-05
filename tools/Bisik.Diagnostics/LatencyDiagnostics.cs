using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;
using NAudio.Wave;

internal static class LatencyDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        BenchmarkOptions options;
        try { options = BenchmarkOptions.Parse(args); }
        catch (ArgumentException) { Console.WriteLine("Usage: --benchmark <synthetic.wav> --output report.json [--repeat 1..5] [--timeout-seconds 1..120]"); return 2; }
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "settings.json");
        if (!File.Exists(source) || !File.Exists(options.AudioPath)) return 2;
        var scratch = Path.Combine(Path.GetFullPath("artifacts/latency"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var reports = new List<SessionTimingReport>();
        try
        {
            // Never load the active file: migration/repair writes affect only this encrypted copy.
            File.Copy(source, Path.Combine(scratch, "settings.json"));
            var settings = new SettingsManager(scratch).Load() with { HistoryEnabled = false, SoundEnabled = false };
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
            using var reader = new WaveFileReader(options.AudioPath);
            for (var i = 0; i < options.Repetitions && !cancellation.IsCancellationRequested; i++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                var sink = new ReceiptSink();
                var metrics = new SessionMetrics("configured-file", sink) { AudioDurationMs = reader.TotalTime.TotalMilliseconds };
                using var scope = metrics.Activate();
                metrics.Mark(TimingStage.StopRequested);
                try
                {
                    var result = await service.TranscribeAsync(options.AudioPath, settings.DefaultLanguage, settings, deadline.Token);
                    metrics.Publish(!result.Succeeded ? SessionOutcome.Failed : string.IsNullOrWhiteSpace(result.RawText) ? SessionOutcome.Empty : SessionOutcome.Ready);
                }
                catch (OperationCanceledException) { metrics.Publish(SessionOutcome.Cancelled); }
                reports.Add(sink.Report!);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
            await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, InsertionUsed = false, CleanupUsed = settings.PostProcessor.Enabled,
                Reports = reports, Summary = TimingAggregation.Summarize(reports),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(TimingAggregation.Summarize(reports)));
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
