using Bisik.Models;

namespace Bisik.Providers;

public sealed class OpenAIProvider(HttpClient httpClient, ProviderSettings settings)
    : OpenAICompatibleProviderBase(httpClient, settings)
{
    private const string DefaultEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    private const string DefaultModel = "whisper-1";

    public override string Name => "OpenAI";

    public override ProviderType Type => ProviderType.OpenAi;

    public override Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(Settings.Model) ? DefaultModel : Settings.Model;
        var headers = MergeHeaders(
            CreateBearerAuthHeader(Settings.ApiKey),
            ToHeaders(Settings.ExtraHeaders));

        return SendMultipartRequestAsync(DefaultEndpoint, audioFilePath, language, model, headers, cancellationToken);
    }
}
