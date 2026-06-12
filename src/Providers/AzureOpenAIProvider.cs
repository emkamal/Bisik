using Bisik.Models;

namespace Bisik.Providers;

public sealed class AzureOpenAIProvider(HttpClient httpClient, ProviderSettings settings)
    : OpenAICompatibleProviderBase(httpClient, settings)
{
    public override string Name => "Azure OpenAI";

    public override ProviderType Type => ProviderType.AzureOpenAi;

    public override Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Settings.DeploymentName))
        {
            throw new InvalidOperationException("Azure OpenAI requires a deployment name.");
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

        return SendMultipartRequestAsync(requestUri, audioFilePath, language, model, headers, cancellationToken);
    }
}
