using Bisik.Models;

namespace Bisik.Providers;

public sealed class AzureOpenAIProvider(HttpClient httpClient, ProviderSettings settings, IAzureRealtimeTranscriber? realtimeTranscriber = null)
    : OpenAICompatibleProviderBase(httpClient, settings), IStreamingTranscriptionProvider
{
    private readonly IAzureRealtimeTranscriber _realtimeTranscriber = realtimeTranscriber ?? new AzureRealtimeTranscriber(httpClient);

    public override string Name => "Azure OpenAI";

    public override ProviderType Type => ProviderType.AzureOpenAi;
    public static Uri BuildV1Uri(ProviderSettings settings, string operation) => AzureRealtimeProtocol.ApiUri(settings.Endpoint, operation);

    public bool SupportsStreaming => UsesRealtime(Settings);
    public Bisik.Core.IRealtimeTranscriber CreateStream() => new Bisik.Core.RealtimeTranscriptionStream(HttpClient);
    internal static bool UsesRealtime(ProviderSettings settings) => settings.AzureRealtime ||
        settings.DeploymentName.Contains("realtime", StringComparison.OrdinalIgnoreCase) ||
        (settings.Model?.Contains("realtime", StringComparison.OrdinalIgnoreCase) ?? false);

    public override async Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Settings.DeploymentName))
        {
            throw new InvalidOperationException("Azure OpenAI requires a deployment name.");
        }

        if (UsesRealtime(Settings))
        {
            return await _realtimeTranscriber.TranscribeAsync(audioFilePath, language, Settings, cancellationToken);
        }

        var baseUri = RequireAbsoluteUri(Settings.Endpoint, nameof(Settings.Endpoint));
        var deployment = Uri.EscapeDataString(Settings.DeploymentName);
        var apiVersion = string.IsNullOrWhiteSpace(Settings.ApiVersion) ? "2024-06-01" : Settings.ApiVersion;
        var deploymentUri = AppendPath(baseUri, $"openai/deployments/{deployment}");
        var requestUri = AppendQueryParameter(
            AppendPath(deploymentUri, "audio/transcriptions"),
            "api-version",
            apiVersion)
            .ToString();
        var model = string.IsNullOrWhiteSpace(Settings.Model) ? Settings.DeploymentName : Settings.Model;
        var headers = MergeHeaders(
            CreateApiKeyHeader("api-key", Settings.ApiKey),
            ToHeaders(Settings.ExtraHeaders));

        try
        {
            return await SendMultipartRequestAsync(requestUri, audioFilePath, language, model, headers, cancellationToken);
        }
        catch (HttpRequestException exception) when (ShouldUseRealtimeFallback(exception))
        {
            return await _realtimeTranscriber.TranscribeAsync(audioFilePath, language, Settings, cancellationToken);
        }
    }

    private static bool ShouldUseRealtimeFallback(HttpRequestException exception)
    {
        return exception.StatusCode == System.Net.HttpStatusCode.BadRequest &&
            exception is ProviderRequestException { UnsupportedAudioTranscriptions: true };
    }
}
