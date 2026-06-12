using System.Reflection;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class TranscriptionServiceTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), "Bisik.TranscriptionServiceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_provider_should_select_provider_by_provider_type()
    {
        var selectedProvider = new FakeProvider(ProviderType.OpenRouter, "OpenRouter", _ => "ok");
        var otherProvider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ => "wrong");
        var service = CreateService(
            new Dictionary<ProviderType, ITranscriptionProvider>
            {
                [ProviderType.OpenRouter] = selectedProvider,
                [ProviderType.OpenAi] = otherProvider,
            },
            new FakePostProcessor((rawText, _) => rawText),
            out _,
            out _);

        var provider = InvokeResolveProvider(service, new ProviderSettings
        {
            Type = ProviderType.OpenRouter,
        });

        provider.Should().BeSameAs(selectedProvider);
    }

    [Fact]
    public async Task Transcribe_async_should_select_provider_forward_language_run_post_processor_and_log_history()
    {
        var selectedProvider = new FakeProvider(ProviderType.OpenRouter, "OpenRouter", _ => "raw mixed text");
        var unusedProvider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ => "wrong");
        var postProcessor = new FakePostProcessor((rawText, _) => $"clean::{rawText}");
        var service = CreateService(
            new Dictionary<ProviderType, ITranscriptionProvider>
            {
                [ProviderType.OpenRouter] = selectedProvider,
                [ProviderType.OpenAi] = unusedProvider,
            },
            postProcessor,
            out var historyLogger,
            out _);
        var audioFilePath = CreateAudioFile();
        var settings = new AppSettings
        {
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenRouter,
            },
            PostProcessor = new PostProcessorSettings
            {
                Enabled = true,
            },
        };

        var result = await InvokeTranscribeAsync(service, audioFilePath, "id", settings);

        GetResultBool(result, "Succeeded").Should().BeTrue();
        GetResultString(result, "OutputText").Should().Be("clean::raw mixed text");
        GetResultString(result, "RawText").Should().Be("raw mixed text");
        selectedProvider.Languages.Should().ContainSingle().Which.Should().Be("id");
        selectedProvider.CallCount.Should().Be(1);
        unusedProvider.CallCount.Should().Be(0);
        postProcessor.CallCount.Should().Be(1);
        GetSuccessSounds(service).Should().Be(1);
        GetErrorSounds(service).Should().Be(0);

        var history = await historyLogger.ReadAllAsync();
        history.Should().ContainSingle();
        history[0].Provider.Should().Be("OpenRouter");
        history[0].Language.Should().Be("id");
        history[0].RawText.Should().Be("raw mixed text");
        history[0].Text.Should().Be("clean::raw mixed text");
        history[0].AudioFilePath.Should().Be(audioFilePath);
    }

    [Fact]
    public async Task Transcribe_async_should_retry_once_for_timeout_before_succeeding()
    {
        var provider = new FakeProvider(
            ProviderType.OpenAi,
            "OpenAI",
            attempt =>
            {
                if (attempt == 1)
                {
                    throw new TimeoutException("timed out");
                }

                return "retry success";
            });
        var postProcessor = new FakePostProcessor((rawText, _) => rawText);
        var service = CreateService(
            new Dictionary<ProviderType, ITranscriptionProvider>
            {
                [ProviderType.OpenAi] = provider,
            },
            postProcessor,
            out _,
            out _);
        var audioFilePath = CreateAudioFile();
        var settings = new AppSettings
        {
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenAi,
            },
        };

        var result = await InvokeTranscribeAsync(service, audioFilePath, "en", settings);

        GetResultBool(result, "Succeeded").Should().BeTrue();
        GetResultString(result, "OutputText").Should().Be("retry success");
        provider.CallCount.Should().Be(2);
        GetSuccessSounds(service).Should().Be(1);
        GetErrorSounds(service).Should().Be(0);
    }

    [Fact]
    public async Task Transcribe_async_should_return_failed_result_without_output_and_log_error_when_provider_fails()
    {
        var provider = new FakeProvider(
            ProviderType.OpenAi,
            "OpenAI",
            _ => throw new InvalidOperationException("missing api key"));
        var postProcessor = new FakePostProcessor((rawText, _) => rawText);
        var service = CreateService(
            new Dictionary<ProviderType, ITranscriptionProvider>
            {
                [ProviderType.OpenAi] = provider,
            },
            postProcessor,
            out var historyLogger,
            out var errorLogger);
        var audioFilePath = CreateAudioFile();
        var settings = new AppSettings
        {
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenAi,
            },
        };

        var result = await InvokeTranscribeAsync(service, audioFilePath, "en", settings);

        GetResultBool(result, "Succeeded").Should().BeFalse();
        GetResultString(result, "OutputText").Should().BeEmpty();
        GetResultString(result, "RawText").Should().BeEmpty();
        provider.CallCount.Should().Be(1);
        postProcessor.CallCount.Should().Be(0);
        GetSuccessSounds(service).Should().Be(0);
        GetErrorSounds(service).Should().Be(1);
        (await historyLogger.ReadAllAsync()).Should().BeEmpty();

        var errorLogPath = Path.Combine(_baseDirectory, "error.log");
        File.Exists(errorLogPath).Should().BeTrue();
        File.ReadAllText(errorLogPath).Should().Contain("missing api key");
        _ = errorLogger;
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private object CreateService(
        IReadOnlyDictionary<ProviderType, ITranscriptionProvider> providers,
        IPostProcessor postProcessor,
        out HistoryLogger historyLogger,
        out ErrorLogger errorLogger)
    {
        var serviceType = RequireBisikType("Bisik.Core.TranscriptionService");
        historyLogger = new HistoryLogger(_baseDirectory);
        errorLogger = new ErrorLogger(_baseDirectory);
        var soundCounters = new SoundCounters();
        Action playSuccess = () => soundCounters.SuccessCount++;
        Action playError = () => soundCounters.ErrorCount++;

        var constructor = serviceType.GetConstructor(
            [
                typeof(IReadOnlyDictionary<ProviderType, ITranscriptionProvider>),
                typeof(IPostProcessor),
                typeof(HistoryLogger),
                typeof(ErrorLogger),
                typeof(Action),
                typeof(Action),
            ]);

        if (constructor is null)
        {
            throw new XunitException(
                "Expected TranscriptionService constructor to accept (IReadOnlyDictionary<ProviderType, ITranscriptionProvider>, IPostProcessor, HistoryLogger, ErrorLogger, Action, Action).");
        }

        var service = constructor.Invoke([providers, postProcessor, historyLogger, errorLogger, playSuccess, playError]);
        return new ServiceWithSoundCounters(service, soundCounters);
    }

    private static async Task<object> InvokeTranscribeAsync(object service, string audioFilePath, string language, AppSettings settings)
    {
        var serviceInstance = service is ServiceWithSoundCounters wrapper ? wrapper.Service : service;
        var method = serviceInstance.GetType().GetMethod(
            "TranscribeAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(string), typeof(string), typeof(AppSettings), typeof(CancellationToken)],
            modifiers: null);

        if (method is null)
        {
            throw new XunitException("Expected TranscriptionService.TranscribeAsync(string, string, AppSettings, CancellationToken).");
        }

        var task = method.Invoke(serviceInstance, [audioFilePath, language, settings, CancellationToken.None]);
        task.Should().BeAssignableTo<Task>();
        await (Task)task!;

        var resultProperty = task!.GetType().GetProperty("Result");
        resultProperty.Should().NotBeNull();
        return resultProperty!.GetValue(task) ??
            throw new XunitException("Expected TranscribeAsync to return a non-null result.");
    }

    private static object InvokeResolveProvider(object service, ProviderSettings settings)
    {
        var serviceInstance = service is ServiceWithSoundCounters wrapper ? wrapper.Service : service;
        var method = serviceInstance.GetType().GetMethod(
            "ResolveProvider",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(ProviderSettings)],
            modifiers: null);

        if (method is null)
        {
            throw new XunitException("Expected TranscriptionService.ResolveProvider(ProviderSettings).");
        }

        return method.Invoke(serviceInstance, [settings]) ??
            throw new XunitException("Expected ResolveProvider to return a provider instance.");
    }

    private static bool GetResultBool(object result, string propertyName)
    {
        var property = result.GetType().GetProperty(propertyName) ??
            throw new XunitException($"Expected transcription result to expose '{propertyName}'.");
        return (bool)(property.GetValue(result) ??
            throw new XunitException($"Expected '{propertyName}' to have a value."));
    }

    private static string GetResultString(object result, string propertyName)
    {
        var property = result.GetType().GetProperty(propertyName) ??
            throw new XunitException($"Expected transcription result to expose '{propertyName}'.");
        return (string?)(property.GetValue(result) ?? string.Empty) ?? string.Empty;
    }

    private Type RequireBisikType(string fullName)
    {
        return typeof(AppSettings).Assembly.GetType(fullName) ??
            throw new XunitException($"Expected type '{fullName}' to exist in the Bisik assembly.");
    }

    private static int GetSuccessSounds(object service)
    {
        return service is ServiceWithSoundCounters wrapper
            ? wrapper.Counters.SuccessCount
            : throw new XunitException("Expected service wrapper with sound counters.");
    }

    private static int GetErrorSounds(object service)
    {
        return service is ServiceWithSoundCounters wrapper
            ? wrapper.Counters.ErrorCount
            : throw new XunitException("Expected service wrapper with sound counters.");
    }

    private string CreateAudioFile()
    {
        Directory.CreateDirectory(_baseDirectory);
        var path = Path.Combine(_baseDirectory, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00]);
        return path;
    }

    private sealed class FakeProvider(
        ProviderType type,
        string name,
        Func<int, string> onTranscribe) : ITranscriptionProvider
    {
        private readonly Func<int, string> _onTranscribe = onTranscribe;

        public string Name { get; } = name;

        public ProviderType Type { get; } = type;

        public int CallCount { get; private set; }

        public List<string> Languages { get; } = [];

        public Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
        {
            Languages.Add(language);
            CallCount++;
            return Task.FromResult(_onTranscribe(CallCount));
        }
    }

    private sealed class FakePostProcessor(Func<string, PostProcessorSettings, string> onProcess) : IPostProcessor
    {
        private readonly Func<string, PostProcessorSettings, string> _onProcess = onProcess;

        public int CallCount { get; private set; }

        public Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_onProcess(rawText, settings));
        }
    }

    private sealed class SoundCounters
    {
        public int SuccessCount { get; set; }

        public int ErrorCount { get; set; }
    }

    private sealed record ServiceWithSoundCounters(object Service, SoundCounters Counters);
}
