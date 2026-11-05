using System.Diagnostics;
using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.Providers;
using NAudio.Wave;

internal static class AzureDiagnostics
{
    public static async Task<int> RunAsync(string[] args, bool singleRequest = false)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.WriteLine("Usage: --azure-file <synthetic-speech.wav> or --azure-once <synthetic-speech.wav>");
            return 2;
        }
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "settings.json");
        if (!File.Exists(settingsPath))
        {
            Console.WriteLine("Azure check unavailable: no existing Bisik settings file.");
            return 2;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "Bisik.AzureDiagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            // Load only a disposable copy: SettingsManager may repair old settings while loading.
            File.Copy(settingsPath, Path.Combine(scratch, "settings.json"));
            var settings = new SettingsManager(scratch).Load();
            var configured = settings.Provider.Type == ProviderType.AzureOpenAi &&
                !string.IsNullOrWhiteSpace(settings.Provider.Endpoint) &&
                !string.IsNullOrWhiteSpace(settings.Provider.ApiKey) &&
                !string.IsNullOrWhiteSpace(settings.Provider.DeploymentName);
            Console.WriteLine(JsonSerializer.Serialize(new { Check = "preflight", AzureConfigured = configured, PostProcessingUsed = false }));
            if (!configured) return 2;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
            var provider = new AzureOpenAIProvider(client, settings.Provider);
            var allPassed = true;
            // These are transport/language-hint checks. Synthetic speech does not validate the microphone.
            foreach (var language in singleRequest ? new[] { "auto" } : new[] { "auto", "en", "id", "auto" })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                var elapsed = Stopwatch.StartNew();
                try
                {
                    var transcript = await provider.TranscribeAsync(args[0], language, timeout.Token);
                    var passed = !string.IsNullOrWhiteSpace(transcript);
                    allPassed &= passed;
                    if (singleRequest)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            Check = "capacity", Passed = passed, ElapsedMs = elapsed.ElapsedMilliseconds,
                            ObservedAtUtc = DateTimeOffset.UtcNow, LiveRequests = 1, Retried = false,
                        }));
                        return passed ? 0 : 1;
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { Check = "synthetic-speech", Language = language, Passed = passed, Transcript = transcript }));
                }
                catch (Exception exception)
                {
                    WriteFailure(singleRequest ? "capacity" : "synthetic-speech", language, exception, elapsed.ElapsedMilliseconds);
                    return 1;
                }
            }

            var silentFile = Path.Combine(scratch, "silence.wav");
            using (var writer = new WaveFileWriter(silentFile, new WaveFormat(16000, 16, 1)))
                writer.Write(new byte[64000], 0, 64000);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75)))
            {
                try
                {
                    var transcript = await provider.TranscribeAsync(silentFile, "auto", timeout.Token);
                    var passed = string.IsNullOrWhiteSpace(transcript);
                    allPassed &= passed;
                    Console.WriteLine(JsonSerializer.Serialize(new { Check = "silence", Passed = passed, Transcript = transcript }));
                }
                catch (Exception exception)
                {
                    WriteFailure("silence", "auto", exception);
                    allPassed = false;
                }
            }

            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                try
                {
                    await provider.TranscribeAsync(args[0], "auto", cancellation.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { Check = "cancellation", Passed = false }));
                    allPassed = false;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { Check = "cancellation", Passed = true }));
                }
                catch (Exception exception)
                {
                    WriteFailure("cancellation", "auto", exception);
                    allPassed = false;
                }
            }
            return allPassed ? 0 : 1;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void WriteFailure(string check, string language, Exception exception, long? elapsedMs = null) =>
        // System/network exception text can contain hostnames; receipts retain only type/status.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Check = check, Language = language, Passed = false, ErrorType = exception.GetType().Name,
            HttpStatus = exception is HttpRequestException request ? (int?)request.StatusCode : null,
            ElapsedMs = elapsedMs, ObservedAtUtc = DateTimeOffset.UtcNow,
        }));
}
