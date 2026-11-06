using System.Text;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.PostProcessor;

public sealed class LLMPostProcessor(HttpClient httpClient) : ICompletionPostProcessor
{
    private static readonly HashSet<string> ReservedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "api-key",
        "Content-Type",
        "Content-Length",
    };

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
        {
            return rawText;
        }

        return (await CompleteAsync(rawText, settings, false, cancellationToken)).Text;
    }

    public async Task<CompletionText> CompleteAsync(string rawText, PostProcessorSettings settings,
        bool probeEffort, CancellationToken cancellationToken, IProgress<TextCompletionStage>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var requestUri = BuildRequestUri(settings.Provider);
        var payload = BuildRequestPayload(rawText, settings, probeEffort);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        ApplyHeaders(request, BuildHeaders(settings.Provider));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var headersMs = watch.Elapsed.TotalMilliseconds;
        progress?.Report(TextCompletionStage.Headers);

        await EnsureSuccessAsync(response, cancellationToken);
        var parsed = settings.StreamCompletion ? await ParseStreamAsync(response, watch, cancellationToken, progress)
            : await ParseCompletionAsync(response, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (parsed.Usage is { } usage)
        {
            var counts = new[] { (Core.MetricCount.PromptTokens, usage.PromptTokens), (Core.MetricCount.OutputTokens, usage.OutputTokens),
                (Core.MetricCount.ReasoningTokens, usage.ReasoningTokens), (Core.MetricCount.CachedTokens, usage.CachedTokens),
                (Core.MetricCount.AcceptedPredictionTokens, usage.AcceptedPredictionTokens), (Core.MetricCount.RejectedPredictionTokens, usage.RejectedPredictionTokens) };
            foreach (var (kind, value) in counts) if (value is { } n) Core.SessionMetrics.Current?.Add(kind, n);
        }
        progress?.Report(TextCompletionStage.Complete);
        return new(parsed.Text, ResolveModel(settings), PostProcessorPresets.ResolveSystemPrompt(settings),
            EffectiveEffort(settings, probeEffort), parsed.Usage, headersMs, parsed.FirstContentMs, watch.Elapsed.TotalMilliseconds);
    }

    private static string BuildRequestPayload(string rawText, PostProcessorSettings settings, bool probeEffort = false)
    {
        var prompt = PostProcessorPresets.ResolveSystemPrompt(settings);
        var model = ResolveModel(settings);
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = prompt },
                new { role = "user", content = rawText },
            },
        };
        if (settings.StreamCompletion)
        {
            if (settings.CompletionCapability?.Supports(settings, CompletionFeature.Streaming) != true)
                throw new InvalidOperationException("Streaming is not verified for this model/API.");
            payload["stream"] = true; payload["stream_options"] = new { include_usage = true };
        }
        if (settings.UsePrediction)
        {
            if (!PredictionSupported(settings)) throw new InvalidOperationException("Prediction is not verified for this model/API.");
            payload["prediction"] = new { type = "content", content = rawText };
        }
        if (EffectiveEffort(settings, probeEffort) is { } effort)
            payload["reasoning_effort"] = effort;

        return JsonSerializer.Serialize(payload);
    }

    public static string? EffectiveEffort(PostProcessorSettings settings, bool probeEffort = false)
    {
        if (settings.ReasoningEffort is not { } effort) return null;
        if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new InvalidOperationException("Invalid reasoning effort.");
        if (probeEffort) return effort;
        if (settings.ReasoningCapability is not { } capability || !capability.Matches(settings)) return null;
        if (!capability.SupportedEfforts.Contains(effort, StringComparer.Ordinal))
            throw new InvalidOperationException("The selected reasoning effort has not been verified for this model/API.");
        return effort;
    }
    public static bool PredictionSupported(PostProcessorSettings settings) =>
        settings.CompletionCapability?.Supports(settings, CompletionFeature.Prediction) == true
        && !(settings.Provider.Type == ProviderType.AzureOpenAi && !settings.Provider.AzureV1 && settings.Provider.ApiVersion == "2024-06-01");

    private static string ResolveModel(PostProcessorSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            return settings.Model;
        }

        if (!string.IsNullOrWhiteSpace(settings.Provider.Model))
        {
            return settings.Provider.Model;
        }

        if (settings.Provider.Type == ProviderType.AzureOpenAi &&
            !string.IsNullOrWhiteSpace(settings.Provider.DeploymentName))
        {
            return settings.Provider.DeploymentName;
        }

        throw new InvalidOperationException("Post-processing requires a configured model.");
    }

    private static string BuildRequestUri(ProviderSettings settings)
    {
        return settings.Type switch
        {
            ProviderType.AzureOpenAi => BuildAzureUri(settings),
            ProviderType.OpenAi => "https://api.openai.com/v1/chat/completions",
            ProviderType.OpenRouter => "https://openrouter.ai/api/v1/chat/completions",
            ProviderType.LocalWhisper => AppendPath(RequireAbsoluteUri(settings.Endpoint, nameof(settings.Endpoint)), "chat/completions").ToString(),
            ProviderType.CustomEndpoint => RequireAbsoluteUri(settings.Endpoint, nameof(settings.Endpoint)).ToString(),
            _ => throw new InvalidOperationException($"Unsupported post-processor provider type '{settings.Type}'."),
        };
    }

    private static string BuildAzureUri(ProviderSettings settings)
    {
        if (settings.AzureV1) return Bisik.Providers.AzureRealtimeProtocol.ApiUri(settings.Endpoint, "chat/completions").ToString();
        if (string.IsNullOrWhiteSpace(settings.DeploymentName))
        {
            throw new InvalidOperationException("Azure OpenAI post-processing requires a deployment name.");
        }

        var baseUri = RequireAbsoluteUri(settings.Endpoint, nameof(settings.Endpoint));
        var deploymentUri = AppendPath(
            baseUri,
            $"openai/deployments/{Uri.EscapeDataString(settings.DeploymentName)}");
        var apiVersion = string.IsNullOrWhiteSpace(settings.ApiVersion) ? "2024-06-01" : settings.ApiVersion;

        return AppendQueryParameter(
            AppendPath(deploymentUri, "chat/completions"),
            "api-version",
            apiVersion)
            .ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildHeaders(ProviderSettings settings)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in ToHeaders(settings.ExtraHeaders))
        {
            headers[header.Key] = header.Value;
        }

        switch (settings.Type)
        {
            case ProviderType.AzureOpenAi:
                if (string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    throw new InvalidOperationException("Azure OpenAI post-processing requires an API key.");
                }

                headers["api-key"] = settings.ApiKey;
                break;

            case ProviderType.OpenAi:
            case ProviderType.OpenRouter:
                if (string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    throw new InvalidOperationException("Post-processing requires an API key.");
                }

                headers["Authorization"] = $"Bearer {settings.ApiKey}";
                break;

            case ProviderType.LocalWhisper:
            case ProviderType.CustomEndpoint:
                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    headers["Authorization"] = $"Bearer {settings.ApiKey}";
                }

                break;

            default:
                throw new InvalidOperationException($"Unsupported post-processor provider type '{settings.Type}'.");
        }

        return headers;
    }

    private static IEnumerable<KeyValuePair<string, string>> ToHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return [];
        }

        return headers.Where(header => !ReservedHeaderNames.Contains(header.Key));
    }

    private static void ApplyHeaders(HttpRequestMessage request, IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Error bodies and reason phrases can contain private routing/credential data.
        throw new Bisik.Providers.ProviderRequestException(response.StatusCode, false, Core.RetryPolicy.FromResponse(response));
    }

    private static async Task<ParsedCompletion> ParseCompletionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            throw new InvalidOperationException("Post-processor response did not contain any content.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Oversized completion response.");
            bytes.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        var usage = ReadUsage(document.RootElement);

        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Post-processor response did not contain any choices.");
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop")
            throw new InvalidDataException("Post-processing was incomplete, filtered, or truncated. No edited text was accepted.");
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null))
            throw new InvalidDataException("Post-processor did not return a complete text message.");
        if (!message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("Post-processor response did not contain message content.");
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            return !string.IsNullOrWhiteSpace(text) && text.Length <= 100_000 ? new(text, usage) : throw new InvalidDataException("Post-processor returned empty or oversized text.");
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            if (content.EnumerateArray().Any(part => part.ValueKind != JsonValueKind.Object ||
                !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "text" ||
                !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("Post-processor returned unsupported or incomplete text parts.");
            var textParts = content.EnumerateArray()
                .Where(part =>
                    part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("type", out var typeElement) &&
                    string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrEmpty(text))
                .ToArray();

            if (textParts.Length > 0 && !string.IsNullOrWhiteSpace(string.Concat(textParts)))
            {
                var text = string.Concat(textParts);
                if (text.Length > 100_000) throw new InvalidDataException("Oversized complete text.");
                return new(text, usage);
            }
        }

        throw new InvalidDataException("Post-processor response contained an unsupported or empty message content format.");
    }

    public static async Task<string> ReadCompleteTextAsync(HttpResponseMessage response, CancellationToken ct) => (await ParseCompletionAsync(response, ct)).Text;
    private sealed record ParsedCompletion(string Text, CompletionUsage? Usage, double? FirstContentMs = null);

    private static CompletionUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null) return null;
        static long? Count(JsonElement node, string property)
        {
            if (!node.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            return value.TryGetInt64(out var count) && count is >= 0 and <= 1_000_000_000_000 ? count : throw new InvalidDataException("Invalid usage count.");
        }
        var promptDetails = usage.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object ? pd : default;
        var outputDetails = usage.TryGetProperty("completion_tokens_details", out var od) && od.ValueKind == JsonValueKind.Object ? od : default;
        long? Detail(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object ? Count(node, key) : null;
        return new(Count(usage, "prompt_tokens"), Count(usage, "completion_tokens"), Detail(outputDetails, "reasoning_tokens"),
            Detail(promptDetails, "cached_tokens"), Detail(outputDetails, "accepted_prediction_tokens"), Detail(outputDetails, "rejected_prediction_tokens"));
    }

    private static async Task<ParsedCompletion> ParseStreamAsync(HttpResponseMessage response, System.Diagnostics.Stopwatch watch, CancellationToken ct,
        IProgress<TextCompletionStage>? progress)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var buffer = new char[1024]; var line = new StringBuilder(); var frame = new StringBuilder(); var text = new StringBuilder();
        var total = 0; bool finished = false, done = false; double? first = null; CompletionUsage? usage = null;
        void Dispatch()
        {
            var data = frame.ToString().TrimEnd('\n'); frame.Clear();
            if (data.Length == 0) return;
            if (done) throw new InvalidDataException("Data after completed stream.");
            if (data == "[DONE]") { done = true; return; }
            using var json = JsonDocument.Parse(data); var root = json.RootElement;
            usage = ReadUsage(root) ?? usage;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid stream choices.");
            if (choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Refused stream.");
                if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    if (content.ValueKind != JsonValueKind.String || finished) throw new InvalidDataException("Invalid stream text.");
                    var part = content.GetString()!;
                    if (text.Length + part.Length > 100_000) throw new InvalidDataException("Oversized stream text.");
                    if (part.Length > 0 && first is null)
                    {
                        first = watch.Elapsed.TotalMilliseconds; Core.SessionMetrics.Current?.Mark(Core.TimingStage.CleanupFirstContent, once: true);
                        progress?.Report(TextCompletionStage.FirstContent);
                    }
                    text.Append(part);
                }
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                if (reason.ValueKind != JsonValueKind.String || reason.GetString() != "stop") throw new InvalidDataException("Incomplete/filtered stream.");
                finished = true;
            }
        }
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += count; if (total > 2 * 1024 * 1024) throw new InvalidDataException("Oversized stream.");
            foreach (var character in buffer.AsSpan(0, count).ToArray())
            {
                if (character != '\n') { line.Append(character); if (line.Length > 64_000) throw new InvalidDataException("Oversized SSE line."); continue; }
                var value = line.ToString().TrimEnd('\r'); line.Clear();
                if (value.Length == 0) Dispatch();
                else if (value.StartsWith("data:", StringComparison.Ordinal)) { frame.Append(value[5..].TrimStart()).Append('\n'); if (frame.Length > 128_000) throw new InvalidDataException("Oversized SSE frame."); }
            }
        }
        ct.ThrowIfCancellationRequested();
        if (!done || !finished || line.Length != 0 || frame.Length != 0 || string.IsNullOrWhiteSpace(text.ToString())) throw new InvalidDataException("Stream ended without a complete final result.");
        return new(text.ToString(), usage, first);
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

    private static Uri AppendQueryParameter(Uri baseUri, string name, string value)
    {
        var builder = new UriBuilder(baseUri);
        var parameters = ParseQueryString(builder.Query);
        parameters[name] = value;
        builder.Query = string.Join(
            "&",
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        return builder.Uri;
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return parameters;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                parameters[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            parameters[Uri.UnescapeDataString(pair[..separatorIndex])] =
                Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
        }

        return parameters;
    }

    private static bool PathEndsWith(string existingPath, string appendedPath)
    {
        var existingSegments = existingPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var appendedSegments = appendedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
}
