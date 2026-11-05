using System.Reflection;
using System.Net;
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
    public async Task Feedback_runs_only_after_delivery_decision_and_failure_is_logged()
    {
        var provider = new FakeProvider(ProviderType.OpenAi, "fake", _ => "final");
        var feedbackCalls = 0;
        var errors = new ErrorLogger(_baseDirectory);
        var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            new FakePostProcessor((raw, _) => raw), new HistoryLogger(_baseDirectory), errors,
            () => { feedbackCalls++; throw new IOException("sound failed"); }, () => { });
        var result = await service.TranscribeAsync(CreateAudioFile(), "en", new AppSettings { HistoryEnabled = false, Provider = new() { Type = ProviderType.OpenAi } }, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(0, feedbackCalls);
        service.NotifyDeliveryCompleted();
        Assert.Equal(1, feedbackCalls);
        Assert.True(File.Exists(Path.Combine(_baseDirectory, "error.log")));
    }

    [Fact]
    public async Task Failed_live_final_falls_back_once_and_runs_cleanup_history_once()
    {
        var stream = new FakeSessionStream();
        stream.Final.SetException(new IOException("connection lost"));
        var provider = new StreamingProvider(stream);
        var cleanup = new FakePostProcessor((raw, _) => raw);
        var history = new HistoryLogger(_baseDirectory);
        var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            cleanup, history, new ErrorLogger(_baseDirectory), () => { }, () => { });
        var metrics = new SessionMetrics("fallback");
        using var scope = metrics.Activate();
        await using var session = service.CreateSession(new AppSettings { LiveCapture = true,
            Provider = new ProviderSettings { Type = ProviderType.OpenAi }, PostProcessor = new PostProcessorSettings { Enabled = true } }, CancellationToken.None);
        await session.StartAsync();
        var result = await session.CompleteAsync(CreateAudioFile());
        Assert.Equal("File result", result.OutputText);
        Assert.Equal(1, provider.FileCalls);
        Assert.Equal(1, cleanup.CallCount);
        Assert.Single(await history.ReadAllAsync());
        Assert.Equal(1, metrics.Snapshot(SessionOutcome.Ready).Counts[MetricCount.Fallbacks]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Live_empty_final_skips_cleanup_and_history(string final)
    {
        var stream = new FakeSessionStream();
        stream.Final.SetResult(final);
        var provider = new StreamingProvider(stream);
        var cleanup = new FakePostProcessor((_, _) => throw new InvalidOperationException());
        var history = new HistoryLogger(_baseDirectory);
        var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            cleanup, history, new ErrorLogger(_baseDirectory), () => { }, () => { });
        await using var session = service.CreateSession(new AppSettings { LiveCapture = true,
            Provider = new ProviderSettings { Type = ProviderType.OpenAi }, PostProcessor = new PostProcessorSettings { Enabled = true } }, CancellationToken.None);
        await session.StartAsync();
        Assert.Equal(string.Empty, (await session.CompleteAsync("fixture.wav")).OutputText);
        Assert.Empty(await history.ReadAllAsync());
        Assert.Equal(0, cleanup.CallCount);
    }

    [Fact]
    public async Task Live_session_uses_one_final_pipeline_and_snapshots_settings()
    {
        var stream = new FakeSessionStream();
        var provider = new StreamingProvider(stream);
        var cleanup = new FakePostProcessor((raw, _) => raw + " cleaned");
        var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            cleanup, new HistoryLogger(_baseDirectory), new ErrorLogger(_baseDirectory), () => { }, () => { });
        var headers = new Dictionary<string, string> { ["test"] = "original" };
        var settings = new AppSettings { LiveCapture = true, HistoryEnabled = false,
            Provider = new ProviderSettings { Type = ProviderType.OpenAi, ExtraHeaders = headers },
            PostProcessor = new PostProcessorSettings { Enabled = true } };
        await using var session = service.CreateSession(settings, CancellationToken.None);
        headers["test"] = "changed";
        Assert.Equal("original", session.Settings.Provider.ExtraHeaders!["test"]);
        await session.StartAsync();
        var first = session.CompleteAsync("fixture.wav");
        var second = session.CompleteAsync("fixture.wav");
        Assert.Same(first, second);
        stream.Final.TrySetResult("Final words");
        Assert.Equal("Final words cleaned", (await first).OutputText);
        Assert.Equal(1, cleanup.CallCount);
        Assert.Equal(0, provider.FileCalls);
        Assert.Equal(DictationState.Ready, session.State);
    }

    [Fact]
    public async Task Cancelled_session_rejects_late_final_and_default_remains_file()
    {
        var stream = new FakeSessionStream();
        var provider = new StreamingProvider(stream);
        var service = new TranscriptionService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            new FakePostProcessor((raw, _) => raw), new HistoryLogger(_baseDirectory), new ErrorLogger(_baseDirectory), () => { }, () => { });
        var settings = new AppSettings { HistoryEnabled = false, Provider = new ProviderSettings { Type = ProviderType.OpenAi } };
        await using (var file = service.CreateSession(settings, CancellationToken.None))
        {
            Assert.Null(file.Stream);
            Assert.True((await file.CompleteAsync("fixture.wav")).Succeeded);
            Assert.Equal(1, provider.FileCalls);
        }
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.LiveCapture);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings with { LiveCapture = true }))!.LiveCapture);
        await using var session = service.CreateSession(settings with { LiveCapture = true }, CancellationToken.None);
        await session.StartAsync();
        var task = session.CompleteAsync("fixture.wav");
        session.Cancel();
        stream.Final.TrySetResult("Too late");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(DictationState.Cancelled, session.State);
    }

    private sealed class StreamingProvider(FakeSessionStream stream) : ITranscriptionProvider, IStreamingTranscriptionProvider
    {
        public string Name => "fake";
        public ProviderType Type => ProviderType.OpenAi;
        public bool SupportsStreaming => true;
        public int FileCalls;
        public IRealtimeTranscriber CreateStream() => stream;
        public Task<string> TranscribeAsync(string path, string language, CancellationToken ct)
        { FileCalls++; return Task.FromResult("File result"); }
    }

    private sealed class FakeSessionStream : IRealtimeTranscriber
    {
        public TaskCompletionSource<string> Final { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(AppSettings settings, string language, CancellationToken ct) => Task.CompletedTask;
        public Task PushAudioChunkAsync(byte[] data, int count, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CommitAndFinalizeAsync(CancellationToken ct) => Final.Task;
        public Task CommitIntermediateSegmentAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event EventHandler<PartialTranscriptEventArgs>? PartialTranscriptAvailable { add { } remove { } }
        public event EventHandler<RealtimeCompletedEventArgs>? TranscriptionCompleted { add { } remove { } }
        public event EventHandler<RealtimeFailedEventArgs>? TranscriptionFailed { add { } remove { } }
    }

    [Fact]
    public async Task Metrics_cover_pipeline_order_without_content()
    {
        var provider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ => "Synthetic fixture.");
        var service = CreateService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            new FakePostProcessor((raw, _) => raw), out _, out _);
        var metrics = new SessionMetrics("offline");
        using var scope = metrics.Activate();
        metrics.Mark(TimingStage.StopRequested);
        await InvokeTranscribeAsync(service, CreateAudioFile(), "en", new AppSettings
        {
            Provider = new ProviderSettings { Type = ProviderType.OpenAi },
            PostProcessor = new PostProcessorSettings { Enabled = true },
        });
        var stages = metrics.Snapshot(SessionOutcome.Ready).Events.Select(e => e.Stage).ToArray();
        Assert.Equal(new[] { TimingStage.StopRequested, TimingStage.TranscriptionStart, TimingStage.TranscriptionEnd,
            TimingStage.CleanupStart, TimingStage.CleanupEnd, TimingStage.HistoryStart, TimingStage.HistoryEnd,
            TimingStage.Ready }, stages);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    public async Task Silence_should_not_invoke_cleanup_history_or_success_feedback(string transcript)
    {
        var provider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ => transcript);
        var postProcessor = new FakePostProcessor((_, _) => "hallucinated cleanup");
        var service = CreateService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            postProcessor, out var history, out _);
        var settings = new AppSettings
        {
            Provider = new ProviderSettings { Type = ProviderType.OpenAi },
            PostProcessor = new PostProcessorSettings { Enabled = true },
        };

        var result = await InvokeTranscribeAsync(service, CreateAudioFile(), "auto", settings);

        GetResultBool(result, "Succeeded").Should().BeTrue();
        GetResultString(result, "OutputText").Should().BeEmpty();
        postProcessor.CallCount.Should().Be(0);
        (await history.ReadAllAsync()).Should().BeEmpty();
        GetSuccessSounds(service).Should().Be(0);
        GetErrorSounds(service).Should().Be(0);
    }

    [Fact]
    public async Task Disabling_history_should_preserve_successful_output_without_writing_history()
    {
        var provider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ => "Selamat pagi.");
        var service = CreateService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            new FakePostProcessor((raw, _) => raw), out var history, out _);
        var settings = new AppSettings
        {
            HistoryEnabled = false,
            Provider = new ProviderSettings { Type = ProviderType.OpenAi },
        };

        var result = await InvokeTranscribeAsync(service, CreateAudioFile(), "id", settings);

        GetResultString(result, "OutputText").Should().Be("Selamat pagi.");
        (await history.ReadAllAsync()).Should().BeEmpty();
        File.Exists(Path.Combine(_baseDirectory, "history.jsonl")).Should().BeFalse();
        GetSuccessSounds(service).Should().Be(0);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Terminal_http_errors_should_fail_once_without_retry(int status)
    {
        var provider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ =>
            throw new HttpRequestException("Provider rejected request.", null, (HttpStatusCode)status));
        var postProcessor = new FakePostProcessor((raw, _) => raw);
        var service = CreateService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            postProcessor, out var history, out _);

        var result = await InvokeTranscribeAsync(service, CreateAudioFile(), "en", new AppSettings
        {
            Provider = new ProviderSettings { Type = ProviderType.OpenAi },
        });

        GetResultBool(result, "Succeeded").Should().BeFalse();
        GetResultString(result, "OutputText").Should().BeEmpty();
        provider.CallCount.Should().Be(1);
        postProcessor.CallCount.Should().Be(0);
        (await history.ReadAllAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Transient_http_errors_should_retry_once_and_not_duplicate_history(int status)
    {
        var provider = new FakeProvider(ProviderType.OpenAi, "OpenAI", attempt => attempt == 1
            ? throw new HttpRequestException("Temporary failure.", null, (HttpStatusCode)status)
            : "Recovered.");
        var service = CreateService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            new FakePostProcessor((raw, _) => raw), out var history, out _);

        var result = await InvokeTranscribeAsync(service, CreateAudioFile(), "en", new AppSettings
        {
            Provider = new ProviderSettings { Type = ProviderType.OpenAi },
        });

        GetResultString(result, "OutputText").Should().Be("Recovered.");
        provider.CallCount.Should().Be(2);
        (await history.ReadAllAsync()).Should().ContainSingle();
        GetSuccessSounds(service).Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_during_provider_call_should_skip_cleanup_history_and_error_feedback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ => "unused")
        {
            AsyncTranscribe = async cancellation =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
                return "unused";
            },
        };
        var postProcessor = new FakePostProcessor((raw, _) => raw);
        var service = CreateService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            postProcessor, out var history, out _);
        using var cancellation = new CancellationTokenSource();
        var pending = InvokeTranscribeAsync(service, CreateAudioFile(), "en", new AppSettings
        {
            Provider = new ProviderSettings { Type = ProviderType.OpenAi },
            PostProcessor = new PostProcessorSettings { Enabled = true },
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
        provider.CallCount.Should().Be(1);
        postProcessor.CallCount.Should().Be(0);
        (await history.ReadAllAsync()).Should().BeEmpty();
        GetSuccessSounds(service).Should().Be(0);
        GetErrorSounds(service).Should().Be(0);
        File.Exists(Path.Combine(_baseDirectory, "error.log")).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_after_provider_returns_should_not_accept_late_results()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider(ProviderType.OpenAi, "OpenAI", _ =>
        {
            cancellation.Cancel();
            return "late result";
        });
        var postProcessor = new FakePostProcessor((raw, _) => raw);
        var service = CreateService(new Dictionary<ProviderType, ITranscriptionProvider> { [ProviderType.OpenAi] = provider },
            postProcessor, out var history, out _);

        await FluentActions.Awaiting(() => InvokeTranscribeAsync(service, CreateAudioFile(), "en", new AppSettings
        {
            Provider = new ProviderSettings { Type = ProviderType.OpenAi },
            PostProcessor = new PostProcessorSettings { Enabled = true },
        }, cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();

        postProcessor.CallCount.Should().Be(0);
        (await history.ReadAllAsync()).Should().BeEmpty();
        GetSuccessSounds(service).Should().Be(0);
        GetErrorSounds(service).Should().Be(0);
    }

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
        GetSuccessSounds(service).Should().Be(0);
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
        GetSuccessSounds(service).Should().Be(0);
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

    private static async Task<object> InvokeTranscribeAsync(object service, string audioFilePath, string language, AppSettings settings,
        CancellationToken cancellationToken = default)
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

        var task = method.Invoke(serviceInstance, [audioFilePath, language, settings, cancellationToken]);
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

        public Func<CancellationToken, Task<string>>? AsyncTranscribe { get; init; }

        public Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken)
        {
            Languages.Add(language);
            CallCount++;
            return AsyncTranscribe?.Invoke(cancellationToken) ?? Task.FromResult(_onTranscribe(CallCount));
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
