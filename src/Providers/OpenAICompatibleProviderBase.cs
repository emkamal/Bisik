using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Providers;

public abstract class OpenAICompatibleProviderBase(HttpClient httpClient, ProviderSettings settings) : ITranscriptionProvider
{
    private static readonly HashSet<string> StrictReservedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "api-key",
        "Content-Type",
        "Content-Length",
    };

    private static readonly HashSet<string> MultipartReservedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Length",
    };

    protected HttpClient HttpClient { get; } = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    protected ProviderSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public abstract string Name { get; }

    public abstract ProviderType Type { get; }

    public abstract Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken);

    protected async Task<string> SendMultipartRequestAsync(
        string requestUri,
        string audioFilePath,
        string language,
        string? model,
        IEnumerable<KeyValuePair<string, string>>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        using var content = CreateMultipartContent(audioFilePath, model, language);
        request.Content = content;
        ApplyHeaders(request, headers);

        using var response = await SendRequestAsync(request, cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ParseTextResponseAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException("Provider connection failed. Check the network and provider endpoint.", null, exception.StatusCode);
        }
    }

    protected MultipartFormDataContent CreateMultipartContent(string audioFilePath, string? model, string language)
    {
        var content = new MultipartFormDataContent();
        var fileStream = File.OpenRead(audioFilePath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));

        if (!string.IsNullOrWhiteSpace(model))
        {
            content.Add(new StringContent(model), "model");
        }

        if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            content.Add(new StringContent(language), "language");
        }

        return content;
    }

    protected static Uri RequireAbsoluteUri(string endpoint, string parameterName)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{parameterName} must be an absolute URL.");
        }

        return uri;
    }

    protected static Uri AppendPath(Uri baseUri, string path)
    {
        var normalizedPath = path.Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return baseUri;
        }

        var builder = new UriBuilder(baseUri);
        var existingPath = builder.Path ?? "/";

        if (PathEndsWith(existingPath, normalizedPath))
        {
            return builder.Uri;
        }

        builder.Path = CombinePath(existingPath, normalizedPath);
        return builder.Uri;
    }

    protected IEnumerable<KeyValuePair<string, string>> MergeHeaders(params IEnumerable<KeyValuePair<string, string>>?[] headerSets)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var headerSet in headerSets)
        {
            if (headerSet is null)
            {
                continue;
            }

            foreach (var header in headerSet)
            {
                merged[header.Key] = header.Value;
            }
        }

        return merged;
    }

    protected IEnumerable<KeyValuePair<string, string>> CreateBearerAuthHeader(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{Name} requires an API key.");
        }

        return [new KeyValuePair<string, string>("Authorization", $"Bearer {apiKey}")];
    }

    protected IEnumerable<KeyValuePair<string, string>> CreateApiKeyHeader(string headerName, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{Name} requires an API key.");
        }

        return [new KeyValuePair<string, string>(headerName, apiKey)];
    }

    protected static IEnumerable<KeyValuePair<string, string>>? ToHeaders(Dictionary<string, string>? headers)
    {
        return ToHeaders(headers, StrictReservedHeaderNames);
    }

    protected static IEnumerable<KeyValuePair<string, string>>? ToHeaders(
        Dictionary<string, string>? headers,
        IEnumerable<string>? reservedHeaderNames)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var blockedHeaders = new HashSet<string>(MultipartReservedHeaderNames, StringComparer.OrdinalIgnoreCase);
        if (reservedHeaderNames is not null)
        {
            blockedHeaders.UnionWith(reservedHeaderNames);
        }

        return headers.Where(header => !blockedHeaders.Contains(header.Key));
    }

    protected static Uri AppendQueryParameter(Uri baseUri, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var builder = new UriBuilder(baseUri);
        var parameters = ParseQueryString(builder.Query);
        parameters[name] = value;
        builder.Query = BuildQueryString(parameters);
        return builder.Uri;
    }

    private static void ApplyHeaders(
        HttpRequestMessage request,
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
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
        var existingSegments = SplitSegments(existingPath);
        var appendedSegments = SplitSegments(appendedPath);

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
            if (!string.Equals(
                    existingSegments[^index],
                    appendedSegments[^index],
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] SplitSegments(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            parameters[key] = value;
        }

        return parameters;
    }

    private static string BuildQueryString(Dictionary<string, string> parameters)
    {
        return string.Join(
            "&",
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);
        var unsupportedAudioTranscriptions = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                code.GetString()?.Equals("OperationNotSupported", StringComparison.OrdinalIgnoreCase) == true &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                unsupportedAudioTranscriptions = message.GetString()?.Contains("audioTranscriptions", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (JsonException)
        {
            // Gateways may return HTML/plain text. Never expose arbitrary response bodies to logs or tray UI.
        }

        throw new ProviderRequestException(response.StatusCode, unsupportedAudioTranscriptions);
    }

    private static async Task<string> ParseTextResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            throw new InvalidOperationException("Provider response did not contain any content.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("Provider response did not contain a string 'text' field.");
    }
}

internal sealed class ProviderRequestException(HttpStatusCode statusCode, bool unsupportedAudioTranscriptions)
    : HttpRequestException($"Provider request failed (HTTP {(int)statusCode}). Check provider settings and retry.", null, statusCode)
{
    // Preserve the Azure fallback signal without retaining potentially sensitive response text.
    public bool UnsupportedAudioTranscriptions { get; } = unsupportedAudioTranscriptions;
}
