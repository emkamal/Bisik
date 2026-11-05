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

    public static async Task<int> RunAsync(string[] args)
    {
        var options = BenchmarkOptions.Parse(args);
        using var wave = new WaveFileReader(options.AudioPath);
        if (wave.TotalTime > TimeSpan.FromMinutes(1) || new FileInfo(options.AudioPath).Length > 8 * 1024 * 1024) return 2;
        var scratch = Path.Combine(Path.GetFullPath("artifacts/latency"), "audio-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var source = options.SettingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "settings.json");
            var settings = SettingsManager.LoadEncryptedCopy(source, scratch);
            if (settings.Provider.Type != Bisik.Models.ProviderType.AzureOpenAi) return 2;
            using var client = new HttpClient(new HttpMetricsHandler());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var metrics = new SessionMetrics("audio-edit-a") { AudioDurationMs = wave.TotalTime.TotalMilliseconds };
            using var scope = metrics.Activate();
            var advertised = false;
            int? status = null;
            var succeeded = false;
            var attempted = false;
            var model = "gpt-audio";
            string? output = null;
            try
            {
                using var catalog = new HttpRequestMessage(HttpMethod.Get, AzureOpenAIProvider.BuildV1Uri(settings.Provider, "models"));
                InfrastructureDiagnostics.ApplyHeaders(catalog, settings.Provider);
                using var response = await client.SendAsync(catalog, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    advertised = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array &&
                        data.EnumerateArray().Any(m => m.TryGetProperty("id", out var id) && id.GetString() == model);
                }
                if (advertised)
                {
                    var payload = BuildPayload(model, PostProcessorPresets.ResolveSystemPrompt(settings.PostProcessor), await File.ReadAllBytesAsync(options.AudioPath, timeout.Token), true);
                    using var request = new HttpRequestMessage(HttpMethod.Post, AzureOpenAIProvider.BuildV1Uri(settings.Provider, "chat/completions"))
                    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                    InfrastructureDiagnostics.ApplyHeaders(request, settings.Provider);
                    metrics.Mark(TimingStage.StopRequested);
                    metrics.Mark(TimingStage.TranscriptionStart);
                    attempted = true;
                    using var result = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    status = (int)result.StatusCode;
                    if (result.IsSuccessStatusCode)
                    {
                        output = await LLMPostProcessor.ReadCompleteTextAsync(result, timeout.Token);
                        succeeded = true;
                        metrics.Mark(TimingStage.TranscriptionEnd);
                        metrics.Mark(TimingStage.Ready);
                    }
                }
            }
            catch (Exception) { /* Safe failed/unsupported receipt; no arbitrary API body or hostname. */ }
            await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(new
            {
                DiagnosticOnly = true, Advertised = advertised, CallAttempted = attempted, Succeeded = succeeded,
                HttpStatus = status, ProductionRoutingChanged = false,
                Reports = new[] { metrics.Snapshot(succeeded ? SessionOutcome.Ready : SessionOutcome.Failed) },
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (output is not null) await File.WriteAllTextAsync(Path.ChangeExtension(options.OutputPath, ".quality.json"), JsonSerializer.Serialize(new { SyntheticOutput = output }));
            return succeeded ? 0 : 1;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
