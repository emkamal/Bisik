using Bisik.Models;

namespace Bisik.Providers;

public sealed class OpenRouterProvider(HttpClient httpClient, ProviderSettings settings)
    : OpenAICompatibleProviderBase(httpClient, settings)
{
    private const string Endpoint = "https://openrouter.ai/api/v1/audio/transcriptions";

    public override string Name => "OpenRouter";

    public override ProviderType Type => ProviderType.OpenRouter;

    public override Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Settings.Model))
        {
            throw new InvalidOperationException("OpenRouter requires a model.");
        }

        var headers = MergeHeaders(
            CreateBearerAuthHeader(Settings.ApiKey),
            ToHeaders(Settings.ExtraHeaders));

        return SendMultipartRequestAsync(Endpoint, audioFilePath, language, Settings.Model, headers, cancellationToken);
    }
}
