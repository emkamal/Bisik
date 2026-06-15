using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bisik.Models;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Bisik.Providers;

public interface IAzureRealtimeTranscriber
{
    Task<string> TranscribeAsync(
        string audioFilePath,
        string language,
        ProviderSettings settings,
        CancellationToken cancellationToken);
}

public sealed class AzureRealtimeTranscriber(HttpClient httpClient, Func<ClientWebSocket>? webSocketFactory = null)
    : IAzureRealtimeTranscriber
{
    private const int MaxChunkBytes = 32 * 1024;
    private const string RealtimeCallsEndpointHeaderName = "Bisik-Realtime-Calls-Endpoint";

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly Func<ClientWebSocket> _webSocketFactory = webSocketFactory ?? (() => new ClientWebSocket());

    public async Task<string> TranscribeAsync(
        string audioFilePath,
        string language,
        ProviderSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new InvalidOperationException("Azure OpenAI requires an endpoint URL.");
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("Azure OpenAI requires an API key.");
        }

        if (string.IsNullOrWhiteSpace(settings.DeploymentName))
        {
            throw new InvalidOperationException("Azure OpenAI requires a deployment name.");
        }

        var token = await CreateRealtimeClientSecretAsync(settings, cancellationToken);
        using var socket = _webSocketFactory();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        var requestUri = BuildRealtimeUri(settings);
        await socket.ConnectAsync(requestUri, cancellationToken);

        await SendEventAsync(socket, BuildSessionUpdatePayload(settings.DeploymentName, language), cancellationToken);

        var audioBytes = ReadAudioAsPcm16Mono24Khz(audioFilePath);
        foreach (var chunk in ChunkAudio(audioBytes, MaxChunkBytes))
        {
            await SendEventAsync(socket, new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(chunk),
            }, cancellationToken);
        }

        await SendEventAsync(socket, new
        {
            type = "input_audio_buffer.commit",
        }, cancellationToken);

        return await ReceiveTranscriptAsync(socket, cancellationToken);
    }

    private async Task<string> CreateRealtimeClientSecretAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var requestUri = AppendPath(RequireAbsoluteUri(settings.Endpoint, nameof(settings.Endpoint)), "openai/v1/realtime/client_secrets");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    session = new
                    {
                        type = "transcription",
                        audio = new
                        {
                            input = new
                            {
                                transcription = new
                                {
                                    model = settings.DeploymentName,
                                },
                            },
                        },
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.TryAddWithoutValidation("api-key", settings.ApiKey);
        ApplySecretRequestHeaders(request, settings.ExtraHeaders);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Azure realtime session request failed with status {(int)response.StatusCode} ({response.StatusCode}): {responseBody}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.TryGetProperty("value", out var tokenElement) &&
            tokenElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            return tokenElement.GetString()!;
        }

        if (document.RootElement.TryGetProperty("client_secret", out var clientSecretElement) &&
            clientSecretElement.ValueKind == JsonValueKind.Object &&
            clientSecretElement.TryGetProperty("value", out var nestedTokenElement) &&
            nestedTokenElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nestedTokenElement.GetString()))
        {
            return nestedTokenElement.GetString()!;
        }

        throw new InvalidOperationException("Azure realtime session response did not include an ephemeral client secret.");
    }

    private static void ApplySecretRequestHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "api-key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, RealtimeCallsEndpointHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static Uri BuildRealtimeUri(ProviderSettings settings)
    {
        var endpoint = ResolveRealtimeEndpoint(settings);
        var baseUri = RequireAbsoluteUri(endpoint, nameof(endpoint));
        var realtimeUri = AppendPath(baseUri, "openai/v1/realtime");
        var builder = new UriBuilder(realtimeUri)
        {
            Scheme = realtimeUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Port = realtimeUri.IsDefaultPort ? -1 : realtimeUri.Port,
            Query = "intent=transcription",
        };

        return builder.Uri;
    }

    private static string ResolveRealtimeEndpoint(ProviderSettings settings)
    {
        if (settings.ExtraHeaders is not null &&
            settings.ExtraHeaders.TryGetValue(RealtimeCallsEndpointHeaderName, out var explicitEndpoint) &&
            !string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            return explicitEndpoint;
        }

        return TryDeriveDirectAzureOpenAiEndpoint(settings.Endpoint, out var derivedEndpoint)
            ? derivedEndpoint
            : settings.Endpoint;
    }

    private static bool TryDeriveDirectAzureOpenAiEndpoint(string endpoint, out string derivedEndpoint)
    {
        derivedEndpoint = string.Empty;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.EndsWith(".azure-api.net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var gatewayIndex = uri.Host.IndexOf("-gateway.", StringComparison.OrdinalIgnoreCase);
        if (gatewayIndex <= 0)
        {
            return false;
        }

        var resourceName = uri.Host[..gatewayIndex];
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return false;
        }

        derivedEndpoint = $"https://{resourceName}.openai.azure.com";
        return true;
    }

    private static object BuildSessionUpdatePayload(string deploymentName, string language)
    {
        var transcription = new Dictionary<string, object?>
        {
            ["model"] = deploymentName,
        };

        if (!string.IsNullOrWhiteSpace(language) &&
            !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            transcription["language"] = language;
        }

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
                        transcription,
                    },
                },
            },
        };
    }

    private static IEnumerable<byte[]> ChunkAudio(byte[] bytes, int chunkSize)
    {
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(bytes, offset, chunk, 0, length);
            yield return chunk;
        }
    }

    private static async Task SendEventAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<string> ReceiveTranscriptAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));

        var accumulatedDelta = new StringBuilder();
        while (!timeoutSource.IsCancellationRequested)
        {
            var message = await ReceiveTextMessageAsync(socket, timeoutSource.Token);
            using var document = JsonDocument.Parse(message);
            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var eventType = typeElement.GetString();
            switch (eventType)
            {
                case "conversation.item.input_audio_transcription.delta":
                    if (document.RootElement.TryGetProperty("delta", out var deltaElement) &&
                        deltaElement.ValueKind == JsonValueKind.String)
                    {
                        accumulatedDelta.Append(deltaElement.GetString());
                    }

                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (document.RootElement.TryGetProperty("transcript", out var transcriptElement) &&
                        transcriptElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(transcriptElement.GetString()))
                    {
                        return transcriptElement.GetString() ?? string.Empty;
                    }

                    return accumulatedDelta.ToString();

                case "conversation.item.input_audio_transcription.failed":
                    throw new InvalidOperationException(ExtractErrorMessage(document.RootElement) ?? "Azure realtime transcription failed.");

                case "error":
                    throw new InvalidOperationException(ExtractErrorMessage(document.RootElement) ?? "Azure realtime session returned an error.");
            }
        }

        throw new TimeoutException("Azure realtime transcription did not complete within 30 seconds.");
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException($"Azure realtime connection closed: {socket.CloseStatus} {socket.CloseStatusDescription}".Trim());
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ExtractErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorElement))
        {
            if (errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }

            if (errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString();
            }
        }

        if (root.TryGetProperty("message", out var directMessageElement) &&
            directMessageElement.ValueKind == JsonValueKind.String)
        {
            return directMessageElement.GetString();
        }

        return null;
    }

    private static byte[] ReadAudioAsPcm16Mono24Khz(string audioFilePath)
    {
        MediaFoundationApi.Startup();
        using var source = OpenWaveStream(audioFilePath);
        var targetFormat = new WaveFormat(24000, 16, 1);
        using var converted = NeedsConversion(source.WaveFormat, targetFormat)
            ? new MediaFoundationResampler(source, targetFormat) { ResamplerQuality = 60 }
            : null;
        IWaveProvider readStream = converted is null ? source : converted;
        using var output = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var bytesRead = readStream.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                break;
            }

            output.Write(buffer, 0, bytesRead);
        }

        return output.ToArray();
    }

    private static WaveStream OpenWaveStream(string audioFilePath)
    {
        if (Path.GetExtension(audioFilePath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return new WaveFileReader(audioFilePath);
        }

        return new MediaFoundationReader(audioFilePath);
    }

    private static bool NeedsConversion(WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        return sourceFormat.Encoding != targetFormat.Encoding ||
            sourceFormat.SampleRate != targetFormat.SampleRate ||
            sourceFormat.BitsPerSample != targetFormat.BitsPerSample ||
            sourceFormat.Channels != targetFormat.Channels;
    }

    private static Uri RequireAbsoluteUri(string endpoint, string parameterName)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{parameterName} must be an absolute URL.");
        }

        return uri;
    }

    private static Uri AppendPath(Uri baseUri, string path)
    {
        var normalizedPath = path.Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return baseUri;
        }

        var builder = new UriBuilder(baseUri);
        var existingPath = builder.Path ?? "/";
        if (!PathEndsWith(existingPath, normalizedPath))
        {
            builder.Path = CombinePath(existingPath, normalizedPath);
        }

        return builder.Uri;
    }

    private static string CombinePath(string existingPath, string pathToAppend)
    {
        var trimmedBase = string.IsNullOrWhiteSpace(existingPath)
            ? string.Empty
            : existingPath.TrimEnd('/');

        if (trimmedBase.Length == 0 || trimmedBase == "/")
        {
            return $"/{pathToAppend}";
        }

        return $"{trimmedBase}/{pathToAppend}";
    }

    private static bool PathEndsWith(string existingPath, string appendedPath)
    {
        var existingSegments = existingPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var appendedSegments = appendedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (appendedSegments.Length == 0)
        {
            return true;
        }

        if (existingSegments.Length < appendedSegments.Length)
        {
            return false;
        }

        for (var index = 1; index <= appendedSegments.Length; index++)
        {
            if (!string.Equals(existingSegments[^index], appendedSegments[^index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
