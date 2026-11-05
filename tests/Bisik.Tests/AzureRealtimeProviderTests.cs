using System.Net;
using System.Reflection;
using Bisik.Models;
using Bisik.Providers;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class AzureRealtimeProviderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "Bisik.AzureRealtimeProviderTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Azure_provider_should_fallback_to_realtime_transcriber_when_audio_transcriptions_operation_is_not_supported()
    {
        using var handler = new StaticResponseHttpMessageHandler(
            HttpStatusCode.BadRequest,
            """
            {"error":{"code":"OperationNotSupported","message":"The audioTranscriptions operation does not work with the specified model, gpt-realtime-whisper."}}
            """);
        using var client = new HttpClient(handler);
        var audioFilePath = CreateAudioFile();
        var providerSettings = new ProviderSettings
        {
            Type = ProviderType.AzureOpenAi,
            Endpoint = "https://example.openai.azure.com/",
            ApiKey = "azure-key",
            DeploymentName = "custom-transcription-deployment",
            ApiVersion = "2024-06-01",
        };

        var realtimeTranscriberType = RequireBisikType("Bisik.Providers.IAzureRealtimeTranscriber");
        var realtimeProxy = CreateRealtimeTranscriberProxy(
            realtimeTranscriberType,
            (audioPath, language, settings, cancellationToken) =>
            {
                audioPath.Should().Be(audioFilePath);
                language.Should().Be("id");
                settings.Should().Be(providerSettings);
                return Task.FromResult("realtime ok");
            },
            out var proxyState);

        var provider = CreateAzureProvider(client, providerSettings, realtimeTranscriberType, realtimeProxy);

        var result = await provider.TranscribeAsync(audioFilePath, "id", CancellationToken.None);

        result.Should().Be("realtime ok");
        handler.RequestCount.Should().Be(1);
        proxyState.InvokeCount.Should().Be(1);
    }

    [Theory]
    [InlineData("gpt-realtime-whisper", false)]
    [InlineData("custom-transcription-deployment", true)]
    public async Task Realtime_deployment_should_skip_the_unsupported_multipart_endpoint(string deployment, bool forceRealtime)
    {
        using var handler = new StaticResponseHttpMessageHandler(HttpStatusCode.BadRequest, "unused");
        using var client = new HttpClient(handler);
        var provider = new AzureOpenAIProvider(client, new ProviderSettings
        {
            Endpoint = "https://example.openai.azure.com", ApiKey = "test-key",
            DeploymentName = deployment, AzureRealtime = forceRealtime,
        }, new StubRealtimeTranscriber());

        (await provider.TranscribeAsync(CreateAudioFile(), "auto", CancellationToken.None)).Should().Be("direct realtime");
        handler.RequestCount.Should().Be(0);
    }

    private sealed class StubRealtimeTranscriber : IAzureRealtimeTranscriber
    {
        public Task<string> TranscribeAsync(string audioFilePath, string language, ProviderSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult("direct realtime");
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"Unauthorized\",\"message\":\"secret-echo https://private-endpoint.example\"}}")]
    [InlineData("<html>secret-echo https://private-endpoint.example</html>")]
    [InlineData("null")]
    public async Task Provider_errors_should_keep_http_status_without_exposing_raw_gateway_content(string response)
    {
        using var handler = new StaticResponseHttpMessageHandler(HttpStatusCode.Unauthorized, response);
        using var client = new HttpClient(handler);
        var provider = new AzureOpenAIProvider(client, new ProviderSettings
        {
            Endpoint = "https://example.openai.azure.com", ApiKey = "test-key", DeploymentName = "standard-transcription",
        });
        var action = () => provider.TranscribeAsync(CreateAudioFile(), "auto", CancellationToken.None);
        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        exception.Which.ToString().Should().Contain("HTTP 401").And.NotContain("secret-echo").And.NotContain("private-endpoint");
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

    private static ITranscriptionProvider CreateAzureProvider(HttpClient client, ProviderSettings settings, Type realtimeTranscriberType, object realtimeTranscriber)
    {
        var providerType = RequireBisikType("Bisik.Providers.AzureOpenAIProvider");
        var constructor = providerType.GetConstructor([typeof(HttpClient), typeof(ProviderSettings), realtimeTranscriberType]);
        if (constructor is null)
        {
            throw new XunitException("Expected AzureOpenAIProvider constructor to accept (HttpClient, ProviderSettings, IAzureRealtimeTranscriber).");
        }

        var provider = constructor.Invoke([client, settings, realtimeTranscriber]);
        return provider.Should().BeAssignableTo<ITranscriptionProvider>().Subject;
    }

    private static object CreateRealtimeTranscriberProxy(
        Type realtimeTranscriberType,
        Func<string, string, ProviderSettings, CancellationToken, Task<string>> implementation,
        out RealtimeTranscriberProxyState proxyState)
    {
        var createMethod = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(method => method.Name == nameof(DispatchProxy.Create) && method.IsGenericMethodDefinition)?
            .MakeGenericMethod(realtimeTranscriberType, typeof(RealtimeTranscriberProxyState))
            ?? throw new XunitException("Expected DispatchProxy.Create to be available for realtime transcriber proxy tests.");
        var proxy = createMethod.Invoke(null, null) ?? throw new XunitException("Expected realtime proxy instance.");
        proxyState = proxy.Should().BeAssignableTo<RealtimeTranscriberProxyState>().Subject;
        proxyState.Handler = (method, arguments) =>
        {
            method.Name.Should().Be("TranscribeAsync");
            arguments.Should().NotBeNull();
            return implementation(
                (string)arguments![0]!,
                (string)arguments[1]!,
                (ProviderSettings)arguments[2]!,
                (CancellationToken)arguments[3]!);
        };
        return proxy;
    }

    private static Type RequireBisikType(string fullName)
    {
        return typeof(AppSettings).Assembly.GetType(fullName) ??
            throw new XunitException($"Expected type '{fullName}' to exist in the Bisik assembly.");
    }

    private sealed class StaticResponseHttpMessageHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            });
        }
    }

    public class RealtimeTranscriberProxyState : DispatchProxy
    {
        public int InvokeCount { get; private set; }

        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            InvokeCount++;
            return Handler!.Invoke(targetMethod!, args);
        }
    }
}
