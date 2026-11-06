using System.Diagnostics;
using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;

internal static class ExperimentDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        ExperimentOptions options;
        try { options = ExperimentOptions.Parse(args); }
        catch (ArgumentException) { Console.WriteLine("Use --experiment --definition setup.json --fixture take.json --settings encrypted.json --output timing.json [--quality quality.json] [--wav saved.wav]"); return 2; }
        var scratch = Path.Combine(Path.GetFullPath("artifacts/experiments"), Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); }; Console.CancelKeyPress += cancel;
        try
        {
            var definition = JsonSerializer.Deserialize<ExperimentDefinition>(await ReadBoundedAsync(options.DefinitionPath))!;
            var fixture = JsonSerializer.Deserialize<ExperimentFixture>(await ReadBoundedAsync(options.FixturePath))!;
            fixture.Validate(options.AudioPath is not null);
            var settings = SettingsManager.LoadEncryptedCopy(options.SettingsPath, scratch);
            using var client = new HttpClient(new HttpMetricsHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            var service = LatencyDiagnostics.CreateService(client, settings, scratch);
            var run = await ExecuteAsync(definition, fixture, settings, service, cancellation.Token, options.AudioPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
            await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(Summary(run, options.Label, fixture.Kind), new JsonSerializerOptions { WriteIndented = true }));
            if (options.QualityPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.QualityPath))!);
                await File.WriteAllTextAsync(options.QualityPath, ExperimentExport.Format(run, run.Definition.Weights, HistoryCopyFormat.Json));
            }
            return run.State == ExperimentState.Completed && run.Trials.All(t => t.Generation == ExperimentGenerationStatus.Succeeded
                && t.EvaluationStatus is ExperimentEvaluationStatus.Succeeded or ExperimentEvaluationStatus.Cached) ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine(TranscriptionService.SafeFailureCode(ex)); return 1; }
        finally { Console.CancelKeyPress -= cancel; if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
    private static async Task<string> ReadBoundedAsync(string path) => new FileInfo(path).Length <= 2 * 1024 * 1024
        ? await File.ReadAllTextAsync(path) : throw new InvalidDataException("Oversized fixture.");

    internal static async Task<ExperimentRun> ExecuteAsync(ExperimentDefinition definition, ExperimentFixture fixture,
        AppSettings settings, TranscriptionService service, CancellationToken ct, string? wav = null)
    {
        fixture.Validate(wav is not null); definition.Validate();
        var raw = fixture.RawText; var elapsed = fixture.MeasuredStopToRawMs;
        if (wav is not null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(definition.Limits.RecognitionSeconds));
            var watch = Stopwatch.StartNew();
            var result = await service.TranscribeAsync(wav, settings.DefaultLanguage,
                settings with { HistoryEnabled = false, SoundEnabled = false, PostProcessor = settings.PostProcessor with { Enabled = false } }, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.RawText)) throw new InvalidDataException("Recognition failed.");
            raw = result.RawText; elapsed = watch.Elapsed.TotalMilliseconds;
        }
        var take = new ExperimentTake(Guid.NewGuid(), raw, elapsed!.Value, null, definition.InputIdentity);
        return await new ExperimentRunner(service, new(service)).RunAsync(definition, take, settings.PostProcessor, ct);
    }
    internal static object Summary(ExperimentRun run, string label, ExperimentFixtureKind kind) => new
    {
        SchemaVersion = 1, Label = label, SourceKind = kind.ToString(), run.State, run.OrderSeed, run.BatchElapsedMs,
        run.Definition.InputIdentity, run.Definition.ConfigurationIdentity, run.Definition.ScoringVersion, run.Definition.RubricVersion,
        run.Take.RawIdentity, run.Take.SharedStopToRawMs, Rankings = ExperimentScoring.Rank(run, run.Definition.Weights).Select(r => new
            { r.CandidateId, r.Factors, r.Score, r.Rank, r.Successful, r.Failed, r.Judged, r.Provisional, WarningCount = r.Warnings.Length }),
        Trials = run.Trials.Select(t => new { t.Id, CandidateId = t.Candidate.Id, t.RepeatIndex, t.ExecutionIndex, t.Generation, t.EvaluationStatus,
            t.CleanupMs, t.JudgeMs, t.FailureCode, t.Candidate.RequestedEffort, t.Candidate.EffectiveEffort, t.Candidate.Verification, Attempts = t.Completion?.Attempts,
            Usage = t.Completion?.Usage, HeadersMs = t.Completion?.HeadersMs, FirstContentMs = t.Completion?.FirstContentMs, FullResponseMs = t.Completion?.FullResponseMs }),
    };
}
