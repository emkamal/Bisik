using System.Text;
using System.Text.Json;
using Bisik.Core;
using Bisik.PostProcessor;
using Bisik.Providers;
using NAudio.Wave;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Bisik.Tests")]

internal static class InstructedAudioDiagnostics
{
    // Dedicated transcription sessions do not accept editing instructions. This adapter uses
    // the documented audio Chat Completions contract only: /api/docs/guides/audio-chat-completions.
    internal static string BuildPayload(string model, string instructions, byte[] wav, bool advertisedAudioModel)
    {
        if (!advertisedAudioModel || model is not ("gpt-audio" or "gpt-audio-mini"))
            throw new NotSupportedException("No verified instructed-audio contract is available.");
        return JsonSerializer.Serialize(new
        {
            model, modalities = new[] { "text" },
            messages = new object[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = new object[] { new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(wav), format = "wav" } } } },
            },
        });
    }

    internal static string BuildPayload(string model, string instructions, byte[] wav,
        InstructedAudioContract contract, Bisik.Models.ProviderSettings provider)
    {
        contract.Validate(provider, model, 0, wav.Length);
        return JsonSerializer.Serialize(new { model, modalities = new[] { "text" }, messages = new object[]
        {
            new { role = "system", content = instructions },
            new { role = "user", content = new object[] { new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(wav), format = "wav" } } } },
        } });
    }

    internal sealed record Options(BenchmarkOptions Benchmark, string? ContractPath, string? Model,
        string? DefinitionPath, string? QualityPath, string FixtureKind)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args.Length % 2 != 1) throw new ArgumentException("WAV plus option/value pairs required.");
            var extras = new Dictionary<string, string>(); var benchmark = new List<string> { args[0] };
            for (var i = 1; i < args.Length; i += 2)
                if (args[i] is "--audio-contract" or "--audio-model" or "--definition" or "--quality" or "--fixture-kind")
                { if (!extras.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate audio option."); }
                else { benchmark.Add(args[i]); benchmark.Add(args[i + 1]); }
            var kind = extras.GetValueOrDefault("--fixture-kind", "unspecified");
            if (kind is not ("human" or "synthetic" or "unspecified")) throw new ArgumentException("Declare human or synthetic fixture provenance.");
            var parsed = BenchmarkOptions.Parse(benchmark.ToArray());
            var inputs = new[] { parsed.AudioPath, parsed.SettingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "settings.json"),
                extras.GetValueOrDefault("--audio-contract"), extras.GetValueOrDefault("--definition") }.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).ToArray();
            var outputs = new[] { parsed.OutputPath, extras.GetValueOrDefault("--quality") }.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).ToArray();
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length || outputs.Any(p => inputs.Contains(p, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("Outputs must not overwrite input/settings files.");
            return new(parsed, extras.GetValueOrDefault("--audio-contract"), extras.GetValueOrDefault("--audio-model"),
                extras.GetValueOrDefault("--definition"), extras.GetValueOrDefault("--quality"), kind);
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try { options = Options.Parse(args); } catch (ArgumentException) { return 2; }
        var benchmark = options.Benchmark;
        var scratch = Path.Combine(Path.GetFullPath("artifacts/latency"), "audio-edit-" + Guid.NewGuid().ToString("N"));
        int? status = null; bool attempted = false; string? failure = null;
        Bisik.Models.ExperimentDefinition? definition = null; Bisik.Models.ExperimentEvaluation? evaluation = null; ExperimentLocalScores? local = null; Bisik.Models.ExperimentFactors? factors = null; CompletionText? completion = null; double? headersMs = null, fullMs = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); }; Console.CancelKeyPress += cancel;
        try
        {
            if (options.ContractPath is null || options.FixtureKind == "unspecified") throw new NotSupportedException("Verified contract and truthful fixture label required.");
            var source = benchmark.SettingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "settings.json");
            var settings = SettingsManager.LoadEncryptedCopy(source, scratch);
            var provider = settings.Provider with { AzureV1 = true };
            if (provider.Type != Bisik.Models.ProviderType.AzureOpenAi) throw new NotSupportedException("The current instructed-audio adapter requires Azure v1.");
            var contract = JsonSerializer.Deserialize<InstructedAudioContract>(await File.ReadAllTextAsync(options.ContractPath))!;
            var model = options.Model ?? contract.Model;
            using var wave = new WaveFileReader(benchmark.AudioPath);
            contract.Validate(provider, model, wave.TotalTime.TotalSeconds, new FileInfo(benchmark.AudioPath).Length);
            using var client = new HttpClient(new HttpMetricsHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(benchmark.TimeoutSeconds));
            var payload = BuildPayload(model, PostProcessorPresets.ResolveSystemPrompt(settings.PostProcessor),
                await File.ReadAllBytesAsync(benchmark.AudioPath, deadline.Token), contract, provider);
            using var request = new HttpRequestMessage(HttpMethod.Post, AzureOpenAIProvider.BuildV1Uri(provider, "chat/completions"))
                { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            InfrastructureDiagnostics.ApplyHeaders(request, provider);
            attempted = true; var watch = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            headersMs = watch.Elapsed.TotalMilliseconds; status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode, false);
            completion = await ReadAudioCompletionAsync(response, deadline.Token); fullMs = watch.Elapsed.TotalMilliseconds;
            if (options.DefinitionPath is not null)
            {
                definition = JsonSerializer.Deserialize<Bisik.Models.ExperimentDefinition>(await File.ReadAllTextAsync(options.DefinitionPath))!;
                local = ExperimentScoring.Compare(definition.Reference, completion.Text, definition.Terms);
                var service = LatencyDiagnostics.CreateService(client, settings, scratch);
                var judge = new LLMExperimentJudge(service);
                var evaluated = await judge.EvaluateAsync(definition, completion.Text,
                    LLMExperimentJudge.JudgeSettings(settings.PostProcessor, definition.Judge), cancellation.Token);
                evaluation = evaluated.Evaluation;
                factors = new(local.Wording, local.Identifiers, evaluation?.EffectiveMeaning, evaluation?.Readability, ExperimentScoring.Speed(definition.Weights.SpeedReferenceSeconds, fullMs.Value / 1000));
            }
        }
        catch (Exception ex) { failure = TranscriptionService.SafeFailureCode(ex); }
        finally { Console.CancelKeyPress -= cancel; if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(benchmark.OutputPath))!);
        await File.WriteAllTextAsync(benchmark.OutputPath, JsonSerializer.Serialize(new
        {
            DiagnosticOnly = true, FixtureKind = options.FixtureKind, CallAttempted = attempted, Succeeded = completion is not null && failure is null,
            HttpStatus = status, FailureCode = failure, UploadAndGenerationMs = fullMs, HeadersMs = headersMs, Usage = completion?.Usage,
            Factors = factors, Score = definition is null || factors is null ? (double?)null : ExperimentScoring.WeightedScore(factors, definition.Weights),
            WarningCount = evaluation?.Warnings.Length, EvaluationAvailable = evaluation is not null, ProductionRoutingChanged = false,
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (options.QualityPath is not null && completion is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.QualityPath))!);
            await File.WriteAllTextAsync(options.QualityPath, JsonSerializer.Serialize(new { options.FixtureKind, Reference = definition?.Reference, Output = completion.Text, LocalEvidence = local, Evaluation = evaluation }));
        }
        return completion is not null && failure is null && (definition is null || evaluation is not null) ? 0 : 1;
    }
    internal static Task<CompletionText> ReadAudioCompletionAsync(HttpResponseMessage response, CancellationToken ct) =>
        response.IsSuccessStatusCode ? LLMPostProcessor.ReadCompletionAsync(response, ct)
            : throw new ProviderRequestException(response.StatusCode, false);
}
