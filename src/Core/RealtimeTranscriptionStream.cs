using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bisik.Models;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Bisik.Core;

/// <summary>
/// Realtime streaming transcription using the OpenAI/Azure Realtime API WebSocket protocol.
/// Runs a continuous background receive loop so partial (delta) results are dispatched
/// as they arrive, even before CommitAndFinalizeAsync is called.
/// </summary>
public sealed class RealtimeTranscriptionStream : IRealtimeTranscriber
{
    private const int MaxChunkBytes = 32 * 1024;
    private const string RealtimeCallsEndpointHeaderName = "Bisik-Realtime-Calls-Endpoint";
    private const int CommitTimeoutSeconds = 30;

    private readonly HttpClient _httpClient;
    private readonly Func<ClientWebSocket> _webSocketFactory;
    private ClientWebSocket? _socket;
    private readonly StringBuilder _accumulatedDelta = new();
    private string _deploymentName = string.Empty;
    private string _targetLanguage = string.Empty;
    private bool _started;
    private bool _disposed;
    private bool _finalCommitted;
    private readonly object _eventLock = new();

    // Background receive infrastructure
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _receiveLoopCts;
    private readonly TaskCompletionSource<string?> _finalTranscriptTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RealtimeTranscriptionStream(HttpClient httpClient, Func<ClientWebSocket>? webSocketFactory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _webSocketFactory = webSocketFactory ?? (() => new ClientWebSocket());
    }

    public event EventHandler<PartialTranscriptEventArgs>? PartialTranscriptAvailable;
    public event EventHandler<RealtimeCompletedEventArgs>? TranscriptionCompleted;
    public event EventHandler<RealtimeFailedEventArgs>? TranscriptionFailed;

    public async Task StartAsync(AppSettings settings, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();

        if (_started)
        {
            throw new InvalidOperationException("Realtime transcription stream is already started.");
        }

        _deploymentName = settings.Provider.DeploymentName;
        _targetLanguage = language;

        if (string.IsNullOrWhiteSpace(settings.Provider.Endpoint))
        {
            throw new InvalidOperationException("Realtime transcription requires an endpoint URL.");
        }

        if (string.IsNullOrWhiteSpace(settings.Provider.ApiKey))
        {
            throw new InvalidOperationException("Realtime transcription requires an API key.");
        }

        if (string.IsNullOrWhiteSpace(_deploymentName))
        {
            throw new InvalidOperationException("Realtime transcription requires a deployment name.");
        }

        var token = await CreateClientSecretAsync(settings, ct);
        _socket = _webSocketFactory();
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        var requestUri = BuildRealtimeUri(settings);
        await _socket.ConnectAsync(requestUri, ct);

        await SendEventAsync(_socket, BuildSessionUpdatePayload(_deploymentName, _targetLanguage), ct);

        // Start the background receive loop to process incoming events
        _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoopTask = ReceiveLoopAsync(_socket, _receiveLoopCts.Token);

        _started = true;
        _finalCommitted = false;
    }

    public async Task PushAudioChunkAsync(byte[] pcmData, int bytesRecorded, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcmData);
        ThrowIfDisposed();
        EnsureStarted();

        var actualLength = Math.Min(bytesRecorded, pcmData.Length);
        if (actualLength <= 0)
        {
            return;
        }

        // Resample to 24 kHz (Azure Realtime expects 24 kHz PCM 16-bit mono)
        var resampled = ResampleTo24Khz(pcmData, actualLength);
        foreach (var chunk in ChunkAudio(resampled, MaxChunkBytes))
        {
            await SendEventAsync(_socket!, new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(chunk),
            }, ct);
        }
    }

    public async Task<string> CommitAndFinalizeAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        EnsureStarted();

        if (!_finalCommitted)
        {
            await SendEventAsync(_socket!, new
            {
                type = "input_audio_buffer.commit",
            }, ct);
            _finalCommitted = true;
        }

        // Wait for the final transcript from the receive loop, or timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(CommitTimeoutSeconds));

        try
        {
            var transcript = await _finalTranscriptTcs.Task.WaitAsync(cts.Token);
            return transcript ?? string.Empty;
        }
        catch (TimeoutException)
        {
            // If no completed event arrived, return whatever we have
            return _accumulatedDelta.ToString();
        }
    }

    /// <summary>
    /// Periodically commits the current audio buffer to get partial transcription results
    /// while recording is still active. Call this from the owning context's timer.
    /// </summary>
    public async Task CommitIntermediateSegmentAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        EnsureStarted();

        if (_finalCommitted)
        {
            return;
        }

        await SendEventAsync(_socket!, new
        {
            type = "input_audio_buffer.commit",
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;

        // Stop the receive loop
        _receiveLoopCts?.Cancel();
        _receiveLoopCts?.Dispose();

        if (_socket is not null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                }
            }
            catch
            {
                // Best-effort close.
            }
            finally
            {
                _socket.Dispose();
                _socket = null;
            }
        }

        // Try to set the TCS if it wasn't already set (avoids hanging waiters)
        _finalTranscriptTcs.TrySetResult(_accumulatedDelta.ToString());
    }

    /// <summary>
    /// Background loop that continuously reads WebSocket messages and dispatches
    /// delta/completed/failed events. Runs from StartAsync until DisposeAsync or error.
    /// </summary>
    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextMessageAsync(socket, ct);
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
                            var delta = deltaElement.GetString() ?? string.Empty;
                            _accumulatedDelta.Append(delta);
                            var accumulated = _accumulatedDelta.ToString();
                            lock (_eventLock)
                            {
                                PartialTranscriptAvailable?.Invoke(this, new PartialTranscriptEventArgs(delta, accumulated));
                            }
                        }
                        break;

                    case "conversation.item.input_audio_transcription.completed":
                        var transcript = string.Empty;
                        if (document.RootElement.TryGetProperty("transcript", out var transcriptElement) &&
                            transcriptElement.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(transcriptElement.GetString()))
                        {
                            transcript = transcriptElement.GetString() ?? string.Empty;
                        }
                        else
                        {
                            transcript = _accumulatedDelta.ToString();
                        }

                        lock (_eventLock)
                        {
                            TranscriptionCompleted?.Invoke(this, new RealtimeCompletedEventArgs(transcript));
                        }

                        // If this is the final commit (committed flag set), signal the waiting task.
                        // For intermediate commits, we don't set the TCS — just fire the event.
                        if (_finalCommitted)
                        {
                            _finalTranscriptTcs.TrySetResult(transcript);
                            return; // Exit the receive loop
                        }

                        // Clear accumulated delta for the next segment after an intermediate commit
                        _accumulatedDelta.Clear();
                        break;

                    case "conversation.item.input_audio_transcription.failed":
                        var errorMsg = ExtractErrorMessage(document.RootElement) ?? "Realtime transcription failed.";
                        lock (_eventLock)
                        {
                            TranscriptionFailed?.Invoke(this, new RealtimeFailedEventArgs(errorMsg));
                        }
                        if (_finalCommitted)
                        {
                            _finalTranscriptTcs.TrySetException(new InvalidOperationException(errorMsg));
                            return;
                        }
                        break;

                    case "error":
                        var errMsg = ExtractErrorMessage(document.RootElement) ?? "Realtime session returned an error.";
                        lock (_eventLock)
                        {
                            TranscriptionFailed?.Invoke(this, new RealtimeFailedEventArgs(errMsg));
                        }
                        if (_finalCommitted)
                        {
                            _finalTranscriptTcs.TrySetException(new InvalidOperationException(errMsg));
                            return;
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during shutdown
        }
        catch (WebSocketException exception)
        {
            lock (_eventLock)
            {
                TranscriptionFailed?.Invoke(this, new RealtimeFailedEventArgs(exception.Message));
            }
            _finalTranscriptTcs.TrySetException(exception);
        }
        catch (Exception exception)
        {
            lock (_eventLock)
            {
                TranscriptionFailed?.Invoke(this, new RealtimeFailedEventArgs(exception.Message));
            }
            _finalTranscriptTcs.TrySetException(exception);
        }
    }

    private async Task<string> CreateClientSecretAsync(AppSettings settings, CancellationToken ct)
    {
        var requestUri = AppendPath(
            RequireAbsoluteUri(settings.Provider.Endpoint, nameof(settings.Provider.Endpoint)),
            "openai/v1/realtime/client_secrets");

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
                                    model = _deploymentName,
                                },
                            },
                        },
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.TryAddWithoutValidation("api-key", settings.Provider.ApiKey);
        ApplySecretRequestHeaders(request, settings.Provider.ExtraHeaders);

        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Realtime session request failed with status {(int)response.StatusCode} ({response.StatusCode}): {responseBody}",
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

        throw new InvalidOperationException("Realtime session response did not include an ephemeral client secret.");
    }

    private static object BuildSessionUpdatePayload(string deploymentName, string language)
    {
        var transcription = new Dictionary<string, object?>
        {
            ["model"] = deploymentName,
            ["language"] = string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : language,
            ["delay"] = "low",
        };

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

    private static byte[] ResampleTo24Khz(byte[] pcm16Buffer, int validBytes)
    {
        var actualBuffer = new byte[validBytes];
        Buffer.BlockCopy(pcm16Buffer, 0, actualBuffer, 0, validBytes);

        try
        {
            using var rawStream = new MemoryStream();
            WriteWavHeader(rawStream, 16000, 16, 1, validBytes);
            rawStream.Write(actualBuffer, 0, validBytes);
            rawStream.Position = 0;

            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(rawStream);
            var targetFormat = new WaveFormat(24000, 16, 1);

            if (reader.WaveFormat.SampleRate == 24000 &&
                reader.WaveFormat.BitsPerSample == 16 &&
                reader.WaveFormat.Channels == 1)
            {
                return actualBuffer;
            }

            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60,
            };

            using var outputStream = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var bytesRead = resampler.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                outputStream.Write(buffer, 0, bytesRead);
            }

            return outputStream.ToArray();
        }
        catch
        {
            return actualBuffer;
        }
    }

    private static void WriteWavHeader(Stream stream, int sampleRate, short bitsPerSample, short channels, int dataSize)
    {
        var writer = new BinaryWriter(stream);
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Flush();
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

    private static async Task SendEventAsync(ClientWebSocket socket, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException(
                    $"Realtime connection closed: {socket.CloseStatus} {socket.CloseStatusDescription}".Trim());
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
                return errorElement.GetString();
            if (errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
                return messageElement.GetString();
        }
        if (root.TryGetProperty("message", out var directMessageElement) &&
            directMessageElement.ValueKind == JsonValueKind.String)
            return directMessageElement.GetString();
        return null;
    }

    private static void ApplySecretRequestHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "api-key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, RealtimeCallsEndpointHeaderName, StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static Uri BuildRealtimeUri(AppSettings settings)
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

    private static string ResolveRealtimeEndpoint(AppSettings settings)
    {
        if (settings.Provider.ExtraHeaders is not null &&
            settings.Provider.ExtraHeaders.TryGetValue(RealtimeCallsEndpointHeaderName, out var explicitEndpoint) &&
            !string.IsNullOrWhiteSpace(explicitEndpoint))
            return explicitEndpoint;
        return TryDeriveDirectAzureOpenAiEndpoint(settings.Provider.Endpoint, out var derivedEndpoint)
            ? derivedEndpoint
            : settings.Provider.Endpoint;
    }

    private static bool TryDeriveDirectAzureOpenAiEndpoint(string endpoint, out string derivedEndpoint)
    {
        derivedEndpoint = string.Empty;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.EndsWith(".azure-api.net", StringComparison.OrdinalIgnoreCase)) return false;
        var gatewayIndex = uri.Host.IndexOf("-gateway.", StringComparison.OrdinalIgnoreCase);
        if (gatewayIndex <= 0) return false;
        var resourceName = uri.Host[..gatewayIndex];
        if (string.IsNullOrWhiteSpace(resourceName)) return false;
        derivedEndpoint = $"https://{resourceName}.openai.azure.com";
        return true;
    }

    private static Uri RequireAbsoluteUri(string endpoint, string parameterName)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{parameterName} must be an absolute URL.");
        return uri;
    }

    private static Uri AppendPath(Uri baseUri, string path)
    {
        var normalizedPath = path.Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedPath)) return baseUri;
        var builder = new UriBuilder(baseUri);
        var existingPath = builder.Path ?? "/";
        if (!PathEndsWith(existingPath, normalizedPath))
            builder.Path = CombinePath(existingPath, normalizedPath);
        return builder.Uri;
    }

    private static string CombinePath(string existingPath, string pathToAppend)
    {
        var trimmedBase = string.IsNullOrWhiteSpace(existingPath) ? string.Empty : existingPath.TrimEnd('/');
        if (trimmedBase.Length == 0 || trimmedBase == "/") return $"/{pathToAppend}";
        return $"{trimmedBase}/{pathToAppend}";
    }

    private static bool PathEndsWith(string existingPath, string appendedPath)
    {
        var existingSegments = existingPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var appendedSegments = appendedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (appendedSegments.Length == 0) return true;
        if (existingSegments.Length < appendedSegments.Length) return false;
        for (var index = 1; index <= appendedSegments.Length; index++)
            if (!string.Equals(existingSegments[^index], appendedSegments[^index], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private void EnsureStarted()
    {
        if (!_started)
            throw new InvalidOperationException("Realtime transcription stream has not been started. Call StartAsync first.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RealtimeTranscriptionStream));
    }
}