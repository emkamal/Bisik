using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using NAudio.Wave;

internal static class SegmentedCleanupDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        var scratch = Path.Combine(Path.GetFullPath("artifacts/segments"), Guid.NewGuid().ToString("N"));
        using var batch = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; batch.Cancel(); }; Console.CancelKeyPress += cancel;
        try
        {
            var options = ExperimentOptions.Parse(args);
            if (options.AudioPath is null) throw new ArgumentException("--wav is required for paced live replay.");
            var definition = JsonSerializer.Deserialize<ExperimentDefinition>(await File.ReadAllTextAsync(options.DefinitionPath))!;
            var fixture = JsonSerializer.Deserialize<ExperimentFixture>(await File.ReadAllTextAsync(options.FixturePath))!; fixture.Validate(true);
            var settings = SettingsManager.LoadEncryptedCopy(options.SettingsPath, scratch);
            using var client = new HttpClient(new HttpMetricsHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            var service = LatencyDiagnostics.CreateService(client, settings, scratch);
            using var reader = new WaveFileReader(options.AudioPath);
            if (reader.TotalTime.TotalSeconds > 300 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1
                || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm) throw new NotSupportedException("Replay requires at most five minutes of PCM16 mono WAV.");
            using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(batch.Token);
            await using var segments = new SegmentedCleanup(service, settings.PostProcessor, batch.Token);
            var rawSettings = settings with { LiveCapture = true, HistoryEnabled = false, SoundEnabled = false, SpeculativeCleanup = false,
                Audio = settings.Audio with { SampleRate = reader.WaveFormat.SampleRate }, PostProcessor = settings.PostProcessor with { Enabled = false } };
            await using var session = service.CreateSession(rawSettings, stopDeadline.Token);
            if (session.Stream is null) throw new NotSupportedException("A configured streaming recognition contract is required.");
            EventHandler<CompletedTranscriptEventArgs> prefix = (_, p) => segments.OnCompletedPrefix(p.Text, p.Revision);
            session.Stream.CompletedTranscriptAvailable += prefix;
            try
            {
                _ = session.StartAsync();
                var block = new byte[reader.WaveFormat.AverageBytesPerSecond / 10]; int count;
                while ((count = reader.Read(block, 0, block.Length)) > 0)
                {
                    session.PushAudio(new(block, count, reader.WaveFormat.SampleRate));
                    await Task.Delay(TimeSpan.FromSeconds((double)count / reader.WaveFormat.AverageBytesPerSecond), batch.Token);
                }
                stopDeadline.CancelAfter(TimeSpan.FromSeconds(definition.Limits.RecognitionSeconds));
                var stop = Stopwatch.StartNew(); session.RequestStop();
                var raw = await session.CompleteAsync(options.AudioPath); stopDeadline.Token.ThrowIfCancellationRequested();
                if (!raw.Succeeded || string.IsNullOrWhiteSpace(raw.RawText)) throw new InvalidDataException("Live final raw unavailable.");
                var sharedMs = stop.Elapsed.TotalMilliseconds;
                var result = await segments.FinishAsync(raw.RawText, settings.PostProcessor, stopDeadline.Token);
                stopDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                var take = new ExperimentTake(Guid.NewGuid(), raw.RawText, sharedMs, reader.TotalTime.TotalMilliseconds, definition.InputIdentity);
                var scores = ExperimentScoring.Compare(definition.Reference, result.Output, definition.Terms);
                var judge = new LLMExperimentJudge(service);
                var evaluation = await judge.EvaluateAsync(definition, result.Output, LLMExperimentJudge.JudgeSettings(settings.PostProcessor, definition.Judge), batch.Token);
                var trial = new ExperimentTrial { Id = "segmented", Candidate = ExperimentCandidate.Baseline with { Id = "segmented", Name = "Diagnostic segmented cleanup", IsBaseline = false },
                    Generation = ExperimentGenerationStatus.Succeeded, Output = result.Output, CleanupMs = result.TailMs,
                    Factors = new(scores.Wording, scores.Identifiers, null, null, null), TermEvidence = scores.Terms,
                    Evaluation = evaluation.Evaluation, EvaluationStatus = evaluation.Status, JudgeMs = evaluation.ElapsedMs };
                var whole = await service.ProcessTextAsync(raw.RawText, settings.PostProcessor, batch.Token, definition.Limits.TrialSeconds);
                var baseline = new ExperimentTrial { Id = "whole", Candidate = ExperimentCandidate.Baseline with { Id = "whole", Name = "Full-text cleanup", IsBaseline = false },
                    Generation = whole.Succeeded ? ExperimentGenerationStatus.Succeeded : ExperimentGenerationStatus.Failed,
                    Output = whole.Output, CleanupMs = whole.ElapsedMs, Completion = whole, FailureCode = whole.FailureCode };
                if (whole.Succeeded)
                {
                    var local = ExperimentScoring.Compare(definition.Reference, whole.Output!, definition.Terms);
                    var judged = await judge.EvaluateAsync(definition, whole.Output!, LLMExperimentJudge.JudgeSettings(settings.PostProcessor, definition.Judge), batch.Token);
                    baseline = baseline with { Factors = new(local.Wording, local.Identifiers, null, null, null), TermEvidence = local.Terms,
                        Evaluation = judged.Evaluation, EvaluationStatus = judged.Status, JudgeMs = judged.ElapsedMs };
                }
                var run = new ExperimentRun(definition, take, ExperimentState.Completed, 0, [trial, baseline]);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
                await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(new { Summary = ExperimentDiagnostics.Summary(run, options.Label, fixture.Kind),
                    result.SegmentRequests, result.UsedFallback, result.OverlapRequestMs, result.TailMs, result.FallbackReason }));
                if (options.QualityPath is not null) await File.WriteAllTextAsync(options.QualityPath, ExperimentExport.Format(run, definition.Weights, HistoryCopyFormat.Json));
                return evaluation.Evaluation is null || baseline.Evaluation is null ? 1 : 0;
            }
            finally { session.Stream.CompletedTranscriptAvailable -= prefix; }
        }
        catch (Exception ex) { Console.WriteLine(TranscriptionService.SafeFailureCode(ex)); return 1; }
        finally { Console.CancelKeyPress -= cancel; if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}
