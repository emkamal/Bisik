using Bisik.Models;

namespace Bisik.Providers;

public sealed class LocalWhisperProvider(HttpClient httpClient, ProviderSettings settings)
    : OpenAICompatibleProviderBase(httpClient, settings)
{
    public override string Name => "Local Whisper";

    public override ProviderType Type => ProviderType.LocalWhisper;

    public override Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
    {
        var baseUri = RequireAbsoluteUri(Settings.Endpoint, nameof(Settings.Endpoint));
        var requestUri = AppendPath(baseUri, "audio/transcriptions").ToString();
        var headers = MergeHeaders(
            string.IsNullOrWhiteSpace(Settings.ApiKey)
                ? null
                : CreateBearerAuthHeader(Settings.ApiKey),
            ToHeaders(Settings.ExtraHeaders));

        return SendMultipartRequestAsync(requestUri, audioFilePath, language, Settings.Model, headers, cancellationToken);
    }
}
