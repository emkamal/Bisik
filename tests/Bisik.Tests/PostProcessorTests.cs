using System.Net;
using System.Reflection;
using System.Text.Json;
using Bisik.Models;
using Bisik.Core;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class PostProcessorTests
{
    [Fact]
    public async Task New_completed_snapshot_restarts_idle_worker_within_request_limit()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var settings = new PostProcessorSettings { Enabled = true };
        await using var speculation = new SpeculativeCleanup(settings, (raw, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) first.TrySetResult(); else second.TrySetResult();
            return Task.FromResult(raw + " edited");
        }, CancellationToken.None);
        speculation.OnSnapshot("First complete snapshot");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        speculation.OnSnapshot("Later whole snapshot");
        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Later whole snapshot edited", await speculation.FinishAsync("Later whole snapshot", settings, CancellationToken.None));
        Assert.Equal(2, calls);
    }
    [Fact]
    public void Diagnostic_audio_adapter_requires_advertised_generation_contract_and_text_only_output()
    {
        Assert.Throws<NotSupportedException>(() => InstructedAudioDiagnostics.BuildPayload("gpt-realtime-whisper", "edit", [1], true));
        Assert.Throws<NotSupportedException>(() => InstructedAudioDiagnostics.BuildPayload("gpt-audio", "edit", [1], false));
        using var payload = JsonDocument.Parse(InstructedAudioDiagnostics.BuildPayload("gpt-audio", "Custom bilingual editing\nverbatim", [1, 2, 3], true));
        Assert.Equal("text", payload.RootElement.GetProperty("modalities")[0].GetString());
        Assert.Equal("Custom bilingual editing\nverbatim", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        var audio = payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("input_audio");
        Assert.Equal("wav", audio.GetProperty("format").GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(audio.GetProperty("data").GetString()!));
    }
    [Fact]
    public async Task Speculation_cancellation_and_new_sessions_do_not_share_results()
    {
        var settings = new PostProcessorSettings { Enabled = true };
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await using (var first = new SpeculativeCleanup(settings, (raw, _, ct) => { calls++; return Task.FromResult(raw); }, cancellation.Token, limit: 0))
        {
            first.OnSnapshot("ignored");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.FinishAsync("old", settings, cancellation.Token));
        }
        Assert.Equal(0, calls);
        await using var second = new SpeculativeCleanup(settings, (raw, _, _) => { calls++; return Task.FromResult(raw); }, CancellationToken.None, limit: 0);
        Assert.Equal("new session", await second.FinishAsync("new session", settings, CancellationToken.None));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Speculation_reuses_only_exact_whole_text_and_observes_obsolete_requests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsolete = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var settings = new PostProcessorSettings { Enabled = true, PresetName = PostProcessorPreset.Custom, SystemPrompt = "Translate the entire text." };
        await using var speculation = new SpeculativeCleanup(settings, (raw, _, _) =>
        {
            calls.Add(raw);
            if (calls.Count == 1) { started.SetResult(); return obsolete.Task; }
            return Task.FromResult(raw + " edited");
        }, CancellationToken.None);
        speculation.OnSnapshot("The pump is running.");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await speculation.FinishAsync("The pump is not running. Katup belum terbuka.", settings, CancellationToken.None);
        Assert.Equal("The pump is not running. Katup belum terbuka. edited", result);
        Assert.False(obsolete.Task.IsCompleted);
        obsolete.SetResult("Stale edited text");
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Exact_running_speculation_is_reused_and_snapshot_work_is_coalesced()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var settings = new PostProcessorSettings { Enabled = true };
        await using var speculation = new SpeculativeCleanup(settings, (raw, _, _) =>
        {
            Assert.Equal("Latest whole text", raw);
            calls++;
            started.TrySetResult();
            return pending.Task;
        }, CancellationToken.None);
        speculation.OnSnapshot("Old snapshot");
        speculation.OnSnapshot("Latest whole text");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var final = speculation.FinishAsync("Latest whole text", settings, CancellationToken.None);
        pending.SetResult("Cleaned whole text");
        Assert.Equal("Cleaned whole text", await final);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Changed_prompt_and_request_limit_reserve_required_final_cleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var settings = new PostProcessorSettings { Enabled = true, SystemPrompt = "Original" };
        await using var speculation = new SpeculativeCleanup(settings, (_, s, _) =>
        {
            if (++calls == 1) { started.SetResult(); return first.Task; }
            return Task.FromResult(s.SystemPrompt);
        }, CancellationToken.None, limit: 1);
        speculation.OnSnapshot("");
        speculation.OnSnapshot("Whole text");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        speculation.OnSnapshot("Pending newer text");
        Assert.Equal("New custom prompt", await speculation.FinishAsync("Whole text", settings with { SystemPrompt = "New custom prompt" }, CancellationToken.None));
        first.SetResult("Obsolete");
        Assert.Equal(2, calls);
    }
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
