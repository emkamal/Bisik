using System.Net;
using System.Net.Http.Headers;
using Bisik.Models;
using Bisik.Providers;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class ProviderRequestTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "Bisik.ProviderRequestTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Azure_provider_should_send_expected_url_headers_and_multipart_fields()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new AzureOpenAIProvider(client, new ProviderSettings
        {
            Type = ProviderType.AzureOpenAi,
            Endpoint = "https://example.openai.azure.com/",
            ApiKey = "azure-key",
            DeploymentName = "whisper-prod",
            ApiVersion = "2024-06-01",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "id", CancellationToken.None);

        result.Should().Be("ok");
        provider.Type.Should().Be(ProviderType.AzureOpenAi);
        provider.Name.Should().Be("Azure OpenAI");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
        handler.CapturedRequest.RequestUri.Should().Be("https://example.openai.azure.com/openai/deployments/whisper-prod/audio/transcriptions?api-version=2024-06-01");
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("api-key", "azure-key"));
        handler.CapturedRequest.Headers.Should().NotContainKey("Authorization");
        handler.CapturedRequest.Parts.Should().ContainKey("file");
        handler.CapturedRequest.Parts["file"].FileName.Should().Be(Path.GetFileName(audioFilePath));
        handler.CapturedRequest.Parts["model"].Value.Should().Be("whisper-prod");
        handler.CapturedRequest.Parts["language"].Value.Should().Be("id");
    }

    [Fact]
    public async Task Azure_provider_should_not_allow_extra_headers_to_override_api_key()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new AzureOpenAIProvider(client, new ProviderSettings
        {
            Type = ProviderType.AzureOpenAi,
            Endpoint = "https://example.openai.azure.com/",
            ApiKey = "azure-key",
            DeploymentName = "whisper-prod",
            ApiVersion = "2024-06-01",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["api-key"] = "wrong-key",
                ["Content-Length"] = "42",
                ["X-Client"] = "bisik-tests",
            },
        });

        var result = await provider.TranscribeAsync(audioFilePath, "id", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.Should().Contain(new KeyValuePair<string, string>("api-key", "azure-key"));
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("X-Client", "bisik-tests"));
        handler.CapturedRequest.Headers.Should().NotContainKey("Content-Length");
    }

    [Fact]
    public async Task Azure_provider_should_normalize_existing_deployment_path_and_preserve_query_string()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new AzureOpenAIProvider(client, new ProviderSettings
        {
            Type = ProviderType.AzureOpenAi,
            Endpoint = "https://example.openai.azure.com/openai/deployments/whisper-prod/?client=bisik",
            ApiKey = "azure-key",
            DeploymentName = "whisper-prod",
            ApiVersion = "2024-06-01-preview",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "id", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("https://example.openai.azure.com/openai/deployments/whisper-prod/audio/transcriptions?client=bisik&api-version=2024-06-01-preview");
    }

    [Fact]
    public async Task OpenAI_provider_should_use_default_model_and_bearer_auth()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new OpenAIProvider(client, new ProviderSettings
        {
            Type = ProviderType.OpenAi,
            ApiKey = "openai-key",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "en", CancellationToken.None);

        result.Should().Be("ok");
        provider.Type.Should().Be(ProviderType.OpenAi);
        provider.Name.Should().Be("OpenAI");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("https://api.openai.com/v1/audio/transcriptions");
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer openai-key"));
        handler.CapturedRequest.Parts.Should().ContainKey("file");
        handler.CapturedRequest.Parts["file"].FileName.Should().Be(Path.GetFileName(audioFilePath));
        handler.CapturedRequest.Parts["model"].Value.Should().Be("whisper-1");
        handler.CapturedRequest.Parts["language"].Value.Should().Be("en");
    }

    [Fact]
    public async Task OpenAI_provider_should_not_allow_extra_headers_to_override_reserved_headers()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new OpenAIProvider(client, new ProviderSettings
        {
            Type = ProviderType.OpenAi,
            ApiKey = "openai-key",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer overridden",
                ["Content-Type"] = "application/json",
                ["X-Trace-Id"] = "trace-123",
            },
        });

        var result = await provider.TranscribeAsync(audioFilePath, "en", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer openai-key"));
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("X-Trace-Id", "trace-123"));
        handler.CapturedRequest.ContentType.Should().NotBeNull();
        handler.CapturedRequest.ContentType!.MediaType.Should().Be("multipart/form-data");
    }

    [Fact]
    public async Task OpenRouter_provider_should_send_configured_model_and_bearer_auth()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new OpenRouterProvider(client, new ProviderSettings
        {
            Type = ProviderType.OpenRouter,
            ApiKey = "openrouter-key",
            Model = "openai/gpt-4o-mini-transcribe",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "auto", CancellationToken.None);

        result.Should().Be("ok");
        provider.Type.Should().Be(ProviderType.OpenRouter);
        provider.Name.Should().Be("OpenRouter");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("https://openrouter.ai/api/v1/audio/transcriptions");
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer openrouter-key"));
        handler.CapturedRequest.Parts.Should().ContainKey("file");
        handler.CapturedRequest.Parts["file"].FileName.Should().Be(Path.GetFileName(audioFilePath));
        handler.CapturedRequest.Parts["model"].Value.Should().Be("openai/gpt-4o-mini-transcribe");
        handler.CapturedRequest.Parts.Should().NotContainKey("language");
    }

    [Fact]
    public async Task Local_provider_should_append_default_path_and_skip_auth_when_api_key_is_empty()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new LocalWhisperProvider(client, new ProviderSettings
        {
            Type = ProviderType.LocalWhisper,
            Endpoint = "http://localhost:8080/",
            Model = "tiny",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "ja", CancellationToken.None);

        result.Should().Be("ok");
        provider.Type.Should().Be(ProviderType.LocalWhisper);
        provider.Name.Should().Be("Local Whisper");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("http://localhost:8080/audio/transcriptions");
        handler.CapturedRequest.Headers.Should().NotContainKey("Authorization");
        handler.CapturedRequest.Parts.Should().ContainKey("file");
        handler.CapturedRequest.Parts["file"].FileName.Should().Be(Path.GetFileName(audioFilePath));
        handler.CapturedRequest.Parts["model"].Value.Should().Be("tiny");
        handler.CapturedRequest.Parts["language"].Value.Should().Be("ja");
    }

    [Fact]
    public async Task Local_provider_should_append_default_path_before_query_string()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new LocalWhisperProvider(client, new ProviderSettings
        {
            Type = ProviderType.LocalWhisper,
            Endpoint = "http://localhost:8080/base/?client=bisik",
            Model = "tiny",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "ja", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("http://localhost:8080/base/audio/transcriptions?client=bisik");
    }

    [Fact]
    public async Task Local_provider_should_allow_auth_style_extra_headers_when_api_key_is_empty()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new LocalWhisperProvider(client, new ProviderSettings
        {
            Type = ProviderType.LocalWhisper,
            Endpoint = "http://localhost:8080/",
            Model = "tiny",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer passthrough-token",
                ["api-key"] = "local-key",
                ["Content-Type"] = "application/json",
            },
        });

        var result = await provider.TranscribeAsync(audioFilePath, "ja", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer passthrough-token"));
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("api-key", "local-key"));
        handler.CapturedRequest.ContentType.Should().NotBeNull();
        handler.CapturedRequest.ContentType!.MediaType.Should().Be("multipart/form-data");
    }

    [Fact]
    public async Task Local_provider_should_keep_bearer_auth_authoritative_when_api_key_is_configured()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new LocalWhisperProvider(client, new ProviderSettings
        {
            Type = ProviderType.LocalWhisper,
            Endpoint = "http://localhost:8080/",
            ApiKey = "provider-key",
            Model = "tiny",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer overridden",
                ["api-key"] = "passthrough-local-key",
            },
        });

        var result = await provider.TranscribeAsync(audioFilePath, "ja", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer provider-key"));
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("api-key", "passthrough-local-key"));
    }

    [Fact]
    public async Task Custom_provider_should_use_configured_url_optional_auth_and_extra_headers()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new CustomEndpointProvider(client, new ProviderSettings
        {
            Type = ProviderType.CustomEndpoint,
            Endpoint = "https://transcribe.example.test/v2/upload",
            ApiKey = "custom-key",
            Model = "custom-whisper",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["X-App-Id"] = "bisik-tests",
                ["HTTP-Referer"] = "https://bisik.test",
            },
        });

        var result = await provider.TranscribeAsync(audioFilePath, "fr", CancellationToken.None);

        result.Should().Be("ok");
        provider.Type.Should().Be(ProviderType.CustomEndpoint);
        provider.Name.Should().Be("Custom Endpoint");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("https://transcribe.example.test/v2/upload");
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer custom-key"));
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("X-App-Id", "bisik-tests"));
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("HTTP-Referer", "https://bisik.test"));
        handler.CapturedRequest.Parts.Should().ContainKey("file");
        handler.CapturedRequest.Parts["file"].FileName.Should().Be(Path.GetFileName(audioFilePath));
        handler.CapturedRequest.Parts["model"].Value.Should().Be("custom-whisper");
        handler.CapturedRequest.Parts["language"].Value.Should().Be("fr");
    }

    [Fact]
    public async Task Custom_provider_should_allow_auth_style_extra_headers_when_no_first_class_auth_is_configured()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new CustomEndpointProvider(client, new ProviderSettings
        {
            Type = ProviderType.CustomEndpoint,
            Endpoint = "https://transcribe.example.test/v2/upload",
            Model = "custom-whisper",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer passthrough-token",
                ["api-key"] = "escape-hatch-key",
                ["Content-Length"] = "42",
            },
        });

        var result = await provider.TranscribeAsync(audioFilePath, "fr", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer passthrough-token"));
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("api-key", "escape-hatch-key"));
        handler.CapturedRequest.Headers.Should().NotContainKey("Content-Length");
    }

    [Fact]
    public async Task Providers_should_omit_language_part_when_requested_language_is_auto()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new CustomEndpointProvider(client, new ProviderSettings
        {
            Type = ProviderType.CustomEndpoint,
            Endpoint = "https://transcribe.example.test/v2/upload",
            ApiKey = "custom-key",
            Model = "custom-whisper",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "AUTO", CancellationToken.None);

        result.Should().Be("ok");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Parts.Should().NotContainKey("language");
    }

    [Fact]
    public async Task Provider_base_should_accept_empty_text_response_as_success()
    {
        using var handler = new CapturingHttpMessageHandler("""{"text":""}""");
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new OpenAIProvider(client, new ProviderSettings
        {
            Type = ProviderType.OpenAi,
            ApiKey = "openai-key",
        });

        var result = await provider.TranscribeAsync(audioFilePath, "en", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_base_should_throw_when_response_does_not_contain_text()
    {
        using var handler = new CapturingHttpMessageHandler("""{"unexpected":"value"}""");
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var provider = new OpenAIProvider(client, new ProviderSettings
        {
            Type = ProviderType.OpenAi,
            ApiKey = "openai-key",
        });

        var act = () => provider.TranscribeAsync(audioFilePath, "en", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*text*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string CreateAudioFile()
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00]);
        return path;
    }

    private sealed class CapturingHttpMessageHandler(string responseBody = """{"text":"ok"}""") : HttpMessageHandler
    {
        public CapturedRequest? CapturedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = await CapturedRequest.CreateAsync(request, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            };
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string RequestUri,
        IReadOnlyDictionary<string, string> Headers,
        MediaTypeHeaderValue? ContentType,
        IReadOnlyDictionary<string, CapturedPart> Parts)
    {
        public static async Task<CapturedRequest> CreateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase);

            var multipart = request.Content.Should().BeOfType<MultipartFormDataContent>().Subject;
            var parts = new Dictionary<string, CapturedPart>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in multipart)
            {
                var name = TrimQuotes(part.Headers.ContentDisposition?.Name);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var fileName = TrimQuotes(part.Headers.ContentDisposition?.FileName) ?? TrimQuotes(part.Headers.ContentDisposition?.FileNameStar);
                var value = fileName is null
                    ? await part.ReadAsStringAsync(cancellationToken)
                    : string.Empty;
                parts[name] = new CapturedPart(fileName, value, part.Headers.ContentType);
            }

            return new CapturedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                headers,
                request.Content?.Headers.ContentType,
                parts);
        }

        private static string? TrimQuotes(string? value)
        {
            return value?.Trim().Trim('"');
        }
    }

    private sealed record CapturedPart(string? FileName, string Value, MediaTypeHeaderValue? ContentType);
}
