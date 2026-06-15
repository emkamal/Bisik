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
            DeploymentName = "gpt-realtime-whisper",
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
