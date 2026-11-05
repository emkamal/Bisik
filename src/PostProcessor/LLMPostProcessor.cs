using System.Text;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.PostProcessor;

public sealed class LLMPostProcessor(HttpClient httpClient) : IPostProcessor
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

        var requestUri = BuildRequestUri(settings.Provider);
        var payload = BuildRequestPayload(rawText, settings);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        ApplyHeaders(request, BuildHeaders(settings.Provider));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ParseChatCompletionAsync(response, cancellationToken);
    }

    private static string BuildRequestPayload(string rawText, PostProcessorSettings settings)
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
        if (settings.ReasoningEffort is { } effort && settings.ReasoningCapability is { } capability && capability.Matches(settings))
        {
            if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") || !capability.SupportedEfforts.Contains(effort, StringComparer.Ordinal))
                throw new InvalidOperationException("The selected reasoning effort has not been verified for this model/API.");
            payload["reasoning_effort"] = effort;
        }

        return JsonSerializer.Serialize(payload);
    }

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

    private static async Task<string> ParseChatCompletionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            throw new InvalidOperationException("Post-processor response did not contain any content.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

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
            return !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidDataException("Post-processor returned empty text.");
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
                return string.Concat(textParts);
            }
        }

        throw new InvalidDataException("Post-processor response contained an unsupported or empty message content format.");
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
