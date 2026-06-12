using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Providers;

public abstract class OpenAICompatibleProviderBase(HttpClient httpClient, ProviderSettings settings) : ITranscriptionProvider
{
    private static readonly HashSet<string> ReservedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "api-key",
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

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ParseTextResponseAsync(response, cancellationToken);
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

        if (!string.IsNullOrWhiteSpace(language))
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
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        return headers.Where(header => !ReservedHeaderNames.Contains(header.Key));
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? response.StatusCode.ToString()
            : body;

        throw new HttpRequestException(
            $"Provider request failed with status {(int)response.StatusCode} ({response.StatusCode}): {detail}",
            null,
            response.StatusCode);
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
            var text = textElement.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException("Provider response did not contain a non-empty 'text' field.");
    }
}
