using System.Text;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Providers;

internal static class AzureRealtimeProtocol
{
    internal const string CallsEndpointHeader = "Bisik-Realtime-Calls-Endpoint";

    internal static async Task<string> CreateClientSecretAsync(HttpClient client, ProviderSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.DeploymentName))
            throw new InvalidOperationException("Azure realtime requires an API key and deployment name.");
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri(settings.Endpoint, "realtime/client_secrets"))
        {
            // This minimal creation payload matches the working mywhisper Azure session handshake.
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                session = new { type = "transcription", audio = new { input = new { transcription = new { model = settings.DeploymentName } } } },
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("api-key", settings.ApiKey);
        if (settings.ExtraHeaders is not null)
            foreach (var header in settings.ExtraHeaders)
                if (!header.Key.Equals(CallsEndpointHeader, StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var response = await SendSecretRequestAsync(client, request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            // Never put raw gateway response bodies (which may contain credentials) in error.log.
            throw new ProviderRequestException(response.StatusCode, false, Core.RetryPolicy.FromResponse(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.TryGetProperty("client_secret", out var nested)) root = nested;
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw new InvalidDataException("Azure realtime session response did not include a client secret.");
    }

    private static async Task<HttpResponseMessage> SendSecretRequestAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        try { return await client.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException exception)
        {
            // DNS/TLS errors can include the configured private hostname in their text.
            throw new HttpRequestException("Azure realtime session connection failed. Check the network and provider endpoint.", null, exception.StatusCode);
        }
    }

    internal static object SessionUpdate(string deployment, string language, string? delay = null)
    {
        var transcription = new Dictionary<string, object> { ["model"] = deployment };
        if (delay is not null)
        {
            if (delay is not ("minimal" or "low" or "medium" or "high" or "xhigh"))
                throw new ArgumentException("Unsupported realtime transcription delay.");
            transcription["delay"] = delay;
        }
        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            transcription["language"] = language.Trim();
        return new
        {
            type = "session.update",
            session = new
            {
                type = "transcription",
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        transcription,
                        // Manual commits prevent a VAD completion from truncating the final recording.
                        turn_detection = (object?)null,
                    },
                },
            },
        };
    }

    internal static Uri BuildRealtimeUri(ProviderSettings settings)
    {
        var endpoint = settings.Endpoint;
        var explicitEndpoint = settings.ExtraHeaders?.FirstOrDefault(header =>
            header.Key.Equals(CallsEndpointHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(explicitEndpoint)) endpoint = explicitEndpoint;
        else
        {
            var uri = ApiUri(endpoint, "realtime");
            var gatewayIndex = uri.Host.IndexOf("-gateway.", StringComparison.OrdinalIgnoreCase);
            // Preserve the existing ABB gateway -> direct realtime route; the key stays at the gateway.
            if (uri.Host.EndsWith(".azure-api.net", StringComparison.OrdinalIgnoreCase) && gatewayIndex > 0)
                endpoint = $"https://{uri.Host[..gatewayIndex]}.openai.azure.com";
        }
        var realtime = ApiUri(endpoint, "realtime");
        return new UriBuilder(realtime) { Scheme = "wss", Port = realtime.IsDefaultPort ? -1 : realtime.Port, Query = "intent=transcription" }.Uri;
    }

    internal static Uri ApiUri(string endpoint, string suffix)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Azure realtime endpoint must be an absolute HTTPS URL without embedded credentials or a fragment.");
        var path = uri.AbsolutePath.TrimEnd('/');
        var apiIndex = path.IndexOf("/openai/", StringComparison.OrdinalIgnoreCase);
        if (apiIndex >= 0) path = path[..apiIndex];
        return new UriBuilder(uri) { Path = $"{path}/openai/v1/{suffix}" }.Uri;
    }

    internal static string DescribeError(JsonElement root)
    {
        var code = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() : null;
        // Only retain a bounded error identifier, not the server's arbitrary message text.
        var safeCode = code is { Length: > 0 and <= 80 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? $" ({code})" : string.Empty;
        return $"Azure realtime transcription failed{safeCode}. Check provider configuration and retry.";
    }
}
