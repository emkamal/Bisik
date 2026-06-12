using Bisik.Models;

namespace Bisik.Providers;

public sealed class CustomEndpointProvider(HttpClient httpClient, ProviderSettings settings)
    : OpenAICompatibleProviderBase(httpClient, settings)
{
    public override string Name => "Custom Endpoint";

    public override ProviderType Type => ProviderType.CustomEndpoint;

    public override Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
    {
        var endpoint = RequireAbsoluteUri(Settings.Endpoint, nameof(Settings.Endpoint)).ToString();
        var headers = MergeHeaders(
            string.IsNullOrWhiteSpace(Settings.ApiKey)
                ? null
                : CreateBearerAuthHeader(Settings.ApiKey),
            ToHeaders(Settings.ExtraHeaders));

        return SendMultipartRequestAsync(endpoint, audioFilePath, language, Settings.Model, headers, cancellationToken);
    }
}
