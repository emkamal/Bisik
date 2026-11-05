using System.Net;
using System.Reflection;
using System.Text.Json;
using Bisik.Models;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class PostProcessorTests
{
    [Fact]
    public async Task Reasoning_is_scoped_to_verified_model_api_and_round_trips()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var processor = CreateProcessor(client);
        var settings = new PostProcessorSettings { Enabled = true, Model = "alias", ReasoningEffort = "low",
            PresetName = PostProcessorPreset.Custom, SystemPrompt = "Keep  every newline.\nJangan ubah makna.",
            Provider = new() { Type = ProviderType.OpenAi, ApiKey = "test" } };
        settings = settings with { ReasoningCapability = new(ReasoningCapability.Fingerprint(settings), ["low"]) };
        settings = JsonSerializer.Deserialize<PostProcessorSettings>(JsonSerializer.Serialize(settings))!;
        await ProcessAsync(processor, "Tidak restart ABB cpmPlus.History.", settings);
        Assert.Equal("low", handler.CapturedRequest!.Body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(settings.SystemPrompt, GetMessageContent(handler.CapturedRequest.Body, 0));
        await ProcessAsync(processor, "fixture", settings with { Model = "different-model" });
        Assert.False(handler.CapturedRequest!.Body.RootElement.TryGetProperty("reasoning_effort", out _));
        await ProcessAsync(processor, "fixture", settings with { Provider = settings.Provider with { Type = ProviderType.CustomEndpoint, Endpoint = "https://example.test/chat" } });
        Assert.False(handler.CapturedRequest!.Body.RootElement.TryGetProperty("reasoning_effort", out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(processor, "fixture", settings with { ReasoningEffort = "high" }));
        Assert.Null(JsonSerializer.Deserialize<PostProcessorSettings>("{}")!.ReasoningEffort);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("tool_calls")]
    [InlineData(null)]
    public async Task Incomplete_cleanup_is_rejected(string? finish)
    {
        using var handler = new CapturingHttpMessageHandler(JsonSerializer.Serialize(new
        { choices = new[] { new { finish_reason = finish, message = new { content = "Clipped translation" } } } }));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProcessAsync(CreateProcessor(client), "fixture",
            new PostProcessorSettings { Enabled = true, Model = "alias", Provider = new() { Type = ProviderType.OpenAi, ApiKey = "test" } }));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("[]")]
    [InlineData("[{\"type\":\"text\",\"text\":\"ok\"},{\"type\":\"refusal\"}]")]
    public async Task Empty_or_malformed_text_parts_are_rejected(string content)
    {
        using var handler = new CapturingHttpMessageHandler("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":" + content + "}}]}");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProcessAsync(CreateProcessor(client), "fixture",
            new PostProcessorSettings { Enabled = true, Model = "alias", Provider = new() { Type = ProviderType.OpenAi, ApiKey = "test" } }));
    }
    [Fact]
    public void Post_processor_contract_should_expose_expected_signature()
    {
        var contractType = RequireBisikType("Bisik.PostProcessor.IPostProcessor");
        var method = contractType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "ProcessAsync", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(PostProcessorSettings) &&
                    parameters[2].ParameterType == typeof(CancellationToken) &&
                    candidate.ReturnType == typeof(Task<string>);
            });

        method.Should().NotBeNull();
    }

    [Fact]
    public async Task Disabled_post_processor_should_return_raw_text_without_sending_a_request()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var processor = CreateProcessor(client);
        var settings = new PostProcessorSettings
        {
            Enabled = false,
            Model = "gpt-4o-mini",
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenAi,
                ApiKey = "openai-key",
            },
        };

        var result = await ProcessAsync(processor, "halo dunia", settings);

        result.Should().Be("halo dunia");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public void Translate_preset_should_generate_a_prompt_that_mentions_the_target_language()
    {
        var settings = new PostProcessorSettings
        {
            PresetName = PostProcessorPreset.TranslateToLanguage,
            TargetLanguage = "Japanese",
        };

        var prompt = ResolveSystemPrompt(settings);

        prompt.Should().Contain("Japanese");
        prompt.Should().ContainEquivalentOf("translate");
    }

    [Fact]
    public void Custom_preset_should_return_the_exact_custom_prompt()
    {
        var settings = new PostProcessorSettings
        {
            PresetName = PostProcessorPreset.Custom,
            SystemPrompt = "Rewrite this transcription as a concise email.",
        };

        var prompt = ResolveSystemPrompt(settings);

        prompt.Should().Be("Rewrite this transcription as a concise email.");
    }

    [Fact]
    public async Task OpenAI_post_processor_should_send_bearer_auth_custom_prompt_and_parse_chat_output()
    {
        using var handler = new CapturingHttpMessageHandler("""{"choices":[{"finish_reason":"stop","message":{"content":"Refined final text"}}]}""");
        using var client = new HttpClient(handler);
        var processor = CreateProcessor(client);
        var settings = new PostProcessorSettings
        {
            Enabled = true,
            Model = "gpt-4o-mini",
            PresetName = PostProcessorPreset.Custom,
            SystemPrompt = "Clean this text up and keep the meaning.",
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenAi,
                ApiKey = "openai-key",
            },
        };

        var result = await ProcessAsync(processor, "raw mixed bahasa and english", settings);

        result.Should().Be("Refined final text");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("https://api.openai.com/v1/chat/completions");
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer openai-key"));
        handler.CapturedRequest.Body.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        GetMessageContent(handler.CapturedRequest.Body, 0).Should().Be("Clean this text up and keep the meaning.");
        GetMessageContent(handler.CapturedRequest.Body, 1).Should().Be("raw mixed bahasa and english");
    }

    [Fact]
    public async Task Azure_post_processor_should_send_api_key_auth_to_the_chat_completions_endpoint()
    {
        using var handler = new CapturingHttpMessageHandler("""{"choices":[{"finish_reason":"stop","message":{"content":"Rapikan hasilnya"}}]}""");
        using var client = new HttpClient(handler);
        var processor = CreateProcessor(client);
        var settings = new PostProcessorSettings
        {
            Enabled = true,
            Model = "ignored-by-azure-path",
            PresetName = PostProcessorPreset.CleanUp,
            Provider = new ProviderSettings
            {
                Type = ProviderType.AzureOpenAi,
                Endpoint = "https://example.openai.azure.com/",
                ApiKey = "azure-key",
                DeploymentName = "cleanup-prod",
                ApiVersion = "2024-06-01",
            },
        };

        var result = await ProcessAsync(processor, "teks mentah", settings);

        result.Should().Be("Rapikan hasilnya");
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri.Should().Be("https://example.openai.azure.com/openai/deployments/cleanup-prod/chat/completions?api-version=2024-06-01");
        handler.CapturedRequest.Headers.Should().Contain(new KeyValuePair<string, string>("api-key", "azure-key"));
        handler.CapturedRequest.Headers.Should().NotContainKey("Authorization");
        GetMessageContent(handler.CapturedRequest.Body, 1).Should().Be("teks mentah");
    }

    private static object CreateProcessor(HttpClient httpClient)
    {
        var processorType = RequireBisikType("Bisik.PostProcessor.LLMPostProcessor");
        var constructor = processorType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(ctor =>
            {
                var parameters = ctor.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(HttpClient);
            });

        if (constructor is null)
        {
            throw new XunitException("Expected LLMPostProcessor to expose a public constructor accepting HttpClient.");
        }

        return constructor.Invoke([httpClient]);
    }

    private static async Task<string> ProcessAsync(object processor, string rawText, PostProcessorSettings settings)
    {
        var method = processor.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "ProcessAsync", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(PostProcessorSettings) &&
                    parameters[2].ParameterType == typeof(CancellationToken);
            });

        if (method is null)
        {
            throw new XunitException("Expected LLMPostProcessor.ProcessAsync(string, PostProcessorSettings, CancellationToken).");
        }

        var task = method.Invoke(processor, [rawText, settings, CancellationToken.None]);
        task.Should().BeAssignableTo<Task<string>>();
        return await (Task<string>)task!;
    }

    private static string ResolveSystemPrompt(PostProcessorSettings settings)
    {
        var presetsType = RequireBisikType("Bisik.PostProcessor.PostProcessorPresets");
        var method = presetsType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "ResolveSystemPrompt", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(PostProcessorSettings);
            });

        if (method is null)
        {
            throw new XunitException("Expected PostProcessorPresets.ResolveSystemPrompt(PostProcessorSettings).");
        }

        var result = method.Invoke(null, [settings]);
        result.Should().BeOfType<string>();
        return (string)result!;
    }

    private static Type RequireBisikType(string fullName)
    {
        return typeof(AppSettings).Assembly.GetType(fullName) ??
            throw new XunitException($"Expected type '{fullName}' to exist in the Bisik assembly.");
    }

    private static string? GetMessageContent(JsonDocument body, int index)
    {
        return body.RootElement
            .GetProperty("messages")[index]
            .GetProperty("content")
            .GetString();
    }

    private sealed class CapturingHttpMessageHandler(string responseBody = """{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""")
        : HttpMessageHandler
    {
        public CapturedRequest? CapturedRequest { get; private set; }

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            CapturedRequest = await CapturedRequest.CreateAsync(request, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            };
        }
    }

    private sealed record CapturedRequest(
        string RequestUri,
        IReadOnlyDictionary<string, string> Headers,
        JsonDocument Body)
    {
        public static async Task<CapturedRequest> CreateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase);
            var payload = request.Content is null
                ? throw new XunitException("Expected a JSON request body.")
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new CapturedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                headers,
                JsonDocument.Parse(payload));
        }
    }
}
