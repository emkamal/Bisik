using System.Text.Json;
using Bisik.Core;
using Bisik.Models;

internal static class InfrastructureDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output") return 2;
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "settings.json");
        var scratch = Path.Combine(Path.GetFullPath("artifacts/latency"), "discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var settings = SettingsManager.LoadEncryptedCopy(source, scratch);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var client = new HttpClient(new HttpMetricsHandler());
            var p = settings.Provider;
            int? status = null;
            int? modelCount = null;
            if (p.Type == ProviderType.AzureOpenAi)
            {
                // Azure v1 model listing is read-only; model catalog entries do not prove deployments or editing capability.
                var uri = new UriBuilder(p.Endpoint);
                var path = uri.Path.TrimEnd('/');
                var index = path.IndexOf("/openai/", StringComparison.OrdinalIgnoreCase);
                if (index >= 0) path = path[..index];
                uri.Path = path + "/openai/v1/models";
                using var request = new HttpRequestMessage(HttpMethod.Get, uri.Uri);
                request.Headers.TryAddWithoutValidation("api-key", p.ApiKey);
                if (p.ExtraHeaders is not null)
                    foreach (var header in p.ExtraHeaders.Where(h => !h.Key.Equals("Bisik-Realtime-Calls-Endpoint", StringComparison.OrdinalIgnoreCase)))
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                try
                {
                    using var response = await client.SendAsync(request, timeout.Token);
                    status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            modelCount = data.GetArrayLength();
                            var candidateIds = new[] { "gpt-4.1-mini", "gpt-5-mini", "gpt-audio-mini", "gpt-audio", "gpt-6-luna" };
                            var advertised = data.EnumerateArray().Where(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && candidateIds.Contains(id.GetString()))
                                .Select(m => m.GetProperty("id").GetString()).ToArray();
                            Console.WriteLine("Advertised candidates (availability unverified): " + string.Join(", ", advertised));
                        }
                    }
                }
                catch (Exception) { /* Safe unavailable result; no hostnames/error bodies in receipts. */ }
            }
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
            {
                Configuration = "existing-authorized", DiscoveryStatus = status, CatalogCount = modelCount,
                LocalCandidateConfigured = p.Type is ProviderType.LocalWhisper or ProviderType.CustomEndpoint,
                FasterDeploymentVerified = false, InstructedAudioEditingVerified = false,
                AudioExperiment = "Unsupported: no accessible instructed-audio model/API contract has been verified.",
                ProductionRoutingChanged = false,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
