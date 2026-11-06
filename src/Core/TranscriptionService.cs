using System.Diagnostics;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;

namespace Bisik.Core;

public enum ProcessingPhase
{
    None,
    Transcribing,
    PostProcessing,
}

public sealed class TranscriptionService(
    IReadOnlyDictionary<ProviderType, ITranscriptionProvider> providers,
    IPostProcessor postProcessor,
    HistoryLogger historyLogger,
    ErrorLogger errorLogger,
    Action playSuccess,
    Action playError,
    IMetricsSink? metricsSink = null)
{
    public TranscriptionService(IReadOnlyDictionary<ProviderType, ITranscriptionProvider> providers,
        IPostProcessor postProcessor, HistoryLogger historyLogger, ErrorLogger errorLogger,
        Action playSuccess, Action playError)
        : this(providers, postProcessor, historyLogger, errorLogger, playSuccess, playError, null) { }

    private readonly IReadOnlyDictionary<ProviderType, ITranscriptionProvider> _providers = providers ??
        throw new ArgumentNullException(nameof(providers));
    private readonly IPostProcessor _postProcessor = postProcessor ??
        throw new ArgumentNullException(nameof(postProcessor));
    private readonly HistoryLogger _historyLogger = historyLogger ??
        throw new ArgumentNullException(nameof(historyLogger));
    private readonly ErrorLogger _errorLogger = errorLogger ??
        throw new ArgumentNullException(nameof(errorLogger));
    private readonly Action _playSuccess = playSuccess ??
        throw new ArgumentNullException(nameof(playSuccess));
    private readonly Action _playError = playError ??
        throw new ArgumentNullException(nameof(playError));

    public event Action<ProcessingPhase>? PhaseChanged;
    private RealtimePrewarmer? _prewarmer;
    public Task PrewarmAsync(AppSettings settings, CancellationToken ct)
    {
        if (!settings.PrewarmRealtime || !settings.LiveCapture) return Task.CompletedTask;
        _prewarmer ??= new RealtimePrewarmer(s => ResolveProvider(s.Provider) is IStreamingTranscriptionProvider { SupportsStreaming: true } live ? live.CreateStream() : null);
        return _prewarmer.PrepareAsync(settings, ct);
    }
    public async ValueTask DisposePrewarmerAsync()
    {
        var prewarmer = _prewarmer;
        _prewarmer = null;
        if (prewarmer is not null) await prewarmer.DisposeAsync().ConfigureAwait(false);
    }

    public void NotifyDeliveryCompleted()
    {
        try { _playSuccess(); }
        catch (Exception exception) { _errorLogger.Log(exception); }
    }

    public TranscriptionSession CreateSession(AppSettings settings, CancellationToken ct)
    {
        var snapshot = settings.Snapshot();
        var provider = ResolveProvider(snapshot.Provider);
        var stream = snapshot.LiveCapture && provider is IStreamingTranscriptionProvider { SupportsStreaming: true } live
            ? _prewarmer?.Claim(snapshot, ct) ?? live.CreateStream() : null;
        var created = new TranscriptionSession(snapshot, ct, stream, (session, path, token) =>
            TranscribeCoreAsync(path, session.Settings.DefaultLanguage, session.Settings, token,
                ct => RecognizeSessionAsync(session, provider, path, ct), session.Persistence,
                (raw, token) => session.SelectFinalAsync(raw, cleanupToken => session.Speculation is null
                    ? InvokeCleanupAsync(raw, session.Settings.PostProcessor, cleanupToken)
                    : session.Speculation.FinishAsync(raw, session.Settings.PostProcessor, cleanupToken), token),
                () => session.CleanupSelected));
        if (snapshot.SpeculativeCleanup && snapshot.PostProcessor.Enabled && stream is not null)
        {
            created.Speculation = new SpeculativeCleanup(snapshot.PostProcessor, _postProcessor.ProcessAsync, created.Token, logError: _errorLogger.Log);
            created.SnapshotHandler = (_, args) => created.Speculation.OnSnapshot(args.Text);
            stream.CompletedTranscriptAvailable += created.SnapshotHandler;
        }
        return created;
    }

    private static async Task<string> RecognizeSessionAsync(TranscriptionSession session, ITranscriptionProvider provider,
        string path, CancellationToken ct)
    {
        if (session.Stream is not null)
        {
            try { return await session.FinalizeLiveAsync(ct).ConfigureAwait(false); }
            catch (Exception exception) when (!ct.IsCancellationRequested && exception is
                IOException or TimeoutException or HttpRequestException or System.Net.WebSockets.WebSocketException or InvalidOperationException)
            {
                await session.AbandonStreamAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                SessionMetrics.Current?.Add(MetricCount.Fallbacks);
            }
        }
        if (session.Persistence is not null) await session.Persistence.WaitAsync(ct).ConfigureAwait(false);
        if (session.Stream is not null && (!File.Exists(path) || new FileInfo(path).Length == 0))
            throw new IOException("No complete recording is available for stream fallback.");
        return await TranscribeWithRetryAsync(provider, path, session.Settings.DefaultLanguage, ct).ConfigureAwait(false);
    }

    public ITranscriptionProvider ResolveProvider(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_providers.TryGetValue(settings.Type, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"No transcription provider is registered for '{settings.Type}'.");
    }

    public Task<TranscriptionResult> TranscribeAsync(
        string audioFilePath,
        string language,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return TranscribeCoreAsync(audioFilePath, language, settings.Snapshot(), cancellationToken);
    }

    private async Task<TranscriptionResult> TranscribeCoreAsync(
        string audioFilePath,
        string language,
        AppSettings settings,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string>>? recognize = null,
        Task? persistence = null,
        Func<string, CancellationToken, Task<string>>? cleanup = null,
        Func<bool>? cleanupSelected = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(settings);

        var metrics = SessionMetrics.Current ?? new SessionMetrics(settings.Provider.Type.ToString(), metricsSink);
        using var metricsScope = metrics.Activate();
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var processingStopwatch = Stopwatch.StartNew();
            var provider = ResolveProvider(settings.Provider);

            // Phase 1: Transcription
            PhaseChanged?.Invoke(ProcessingPhase.Transcribing);
            var transcriptionStopwatch = Stopwatch.StartNew();
            metrics.Mark(TimingStage.TranscriptionStart);
            var rawText = recognize is null
                ? await TranscribeWithRetryAsync(provider, audioFilePath, language, cancellationToken)
                : await recognize(cancellationToken);
            metrics.Mark(TimingStage.TranscriptionEnd);
            transcriptionStopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                metrics.Publish(SessionOutcome.Empty);
                return new TranscriptionResult(true, string.Empty, string.Empty);
            }

            // Phase 2: Post-processing
            long postProcessingDurationMs = 0;
            string outputText;

            if (settings.PostProcessor.Enabled)
            {
                PhaseChanged?.Invoke(ProcessingPhase.PostProcessing);
                var postProcessingStopwatch = Stopwatch.StartNew();
                metrics.Mark(TimingStage.CleanupStart);
                outputText = cleanup is null ? await InvokeCleanupAsync(rawText, settings.PostProcessor, cancellationToken)
                    : await cleanup(rawText, cancellationToken);
                metrics.Mark(TimingStage.CleanupEnd);
                postProcessingStopwatch.Stop();
                postProcessingDurationMs = postProcessingStopwatch.ElapsedMilliseconds;
            }
            else
            {
                outputText = rawText;
            }

            var totalDurationMs = transcriptionStopwatch.ElapsedMilliseconds + postProcessingDurationMs;
            var stopToResultMs = (long)Math.Round(metrics.ElapsedSince(TimingStage.StopRequested) ?? processingStopwatch.Elapsed.TotalMilliseconds);
            var captureToResultMs = metrics.ElapsedSince(TimingStage.CaptureStart);
            var postProcessingApplied = settings.PostProcessor.Enabled && (cleanupSelected?.Invoke() ?? true);

            var entry = new TranscriptionEntry(
                startedAt,
                outputText,
                rawText,
                provider.Name,
                language,
                DurationMs: totalDurationMs,
                AudioFilePath: audioFilePath,
                TranscriptionDurationMs: transcriptionStopwatch.ElapsedMilliseconds,
                PostProcessingDurationMs: postProcessingDurationMs,
                RecordingDurationMs: settings.HistoryRecordingDurationEnabled && metrics.AudioDurationMs is { } audioMs
                    ? (long)Math.Round(audioMs) : null,
                ConfigurationSnapshot: settings.HistoryEnabled && settings.HistoryConfigurationEnabled
                    ? HistoryConfigurationSnapshot.Capture(settings, language,
                        postProcessingApplied ? PostProcessorPresets.ResolveSystemPrompt(settings.PostProcessor) : null) : null,
                StopToResultDurationMs: stopToResultMs,
                PostProcessingApplied: postProcessingApplied,
                CaptureToResultDurationMs: captureToResultMs is { } captureMs ? (long)Math.Round(captureMs) : null);
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.HistoryEnabled)
            {
                if (persistence is not null) await persistence.WaitAsync(cancellationToken);
                metrics.Mark(TimingStage.HistoryStart);
                await _historyLogger.AppendAsync(entry, cancellationToken);
                metrics.Mark(TimingStage.HistoryEnd);
            }
            metrics.Mark(TimingStage.Ready);
            metrics.Publish(SessionOutcome.Ready);

            return new TranscriptionResult(
                Succeeded: true,
                OutputText: outputText,
                RawText: rawText,
                TranscriptionDurationMs: transcriptionStopwatch.ElapsedMilliseconds,
                PostProcessingDurationMs: postProcessingDurationMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            metrics.Publish(SessionOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            metrics.Publish(SessionOutcome.Failed);
            _errorLogger.Log(exception);
            try { _playError(); }
            catch (Exception feedbackException) { _errorLogger.Log(feedbackException); }

            return new TranscriptionResult(
                Succeeded: false,
                OutputText: string.Empty,
                RawText: string.Empty,
                ErrorMessage: exception.Message);
        }
        finally
        {
            PhaseChanged?.Invoke(ProcessingPhase.None);
        }
    }

    private async Task<string> InvokeCleanupAsync(string text, PostProcessorSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var output = await _postProcessor.ProcessAsync(text, settings, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return output;
    }

    public Task<TextProcessingReceipt> ProcessTextAsync(string text, PostProcessorSettings settings,
        CancellationToken ct, int timeoutSeconds = 120, bool retryTransient = false) =>
        ProcessTextCoreAsync(text, settings, false, ct, timeoutSeconds, retryTransient);

    public Task<TextProcessingReceipt> ProbeTextCompatibilityAsync(string text, PostProcessorSettings settings,
        CancellationToken ct, int timeoutSeconds = 120) =>
        ProcessTextCoreAsync(text, settings, true, ct, timeoutSeconds, false);

    private async Task<TextProcessingReceipt> ProcessTextCoreAsync(string text, PostProcessorSettings settings,
        bool probe, CancellationToken ct, int timeoutSeconds, bool retryTransient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 200_000 || timeoutSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        settings = (new AppSettings { PostProcessor = settings }).Snapshot().PostProcessor with { Enabled = true };
        var fingerprint = ReasoningCapability.Fingerprint(settings);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var watch = Stopwatch.StartNew();
        var attempts = 0;
        var metrics = new SessionMetrics("text-stage");
        using var scope = metrics.Activate();
        try
        {
            if (probe && (_postProcessor is not ICompletionPostProcessor || settings.ReasoningEffort is null))
                throw new NotSupportedException("An explicit effort probe requires a completion adapter and an effort.");
            if (!probe && settings.ReasoningEffort != LLMPostProcessor.EffectiveEffort(settings))
                throw new InvalidOperationException("Verify the requested effort for this exact model/API before running a trial.");
            CompletionText completion;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                attempts++;
                metrics.Add(MetricCount.Attempts);
                try
                {
                    completion = _postProcessor is ICompletionPostProcessor rich
                        ? await rich.CompleteAsync(text, settings, probe, deadline.Token).ConfigureAwait(false)
                        : new(await InvokeCleanupAsync(text, settings, deadline.Token).ConfigureAwait(false),
                            settings.Model, PostProcessorPresets.ResolveSystemPrompt(settings), LLMPostProcessor.EffectiveEffort(settings));
                    break;
                }
                catch (Exception ex) when (retryTransient && attempts == 1 && !deadline.IsCancellationRequested && IsRetryable(ex))
                {
                    await RetryPolicy.WaitAsync((ex as ProviderRequestException)?.RetryAfter, deadline.Token).ConfigureAwait(false);
                }
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(completion.Text) || completion.Text.Length > 100_000)
                throw new InvalidDataException("Missing or oversized complete text.");
            if (completion.EffectiveEffort != settings.ReasoningEffort)
                throw new InvalidDataException("Completion parameters did not match the requested trial.");
            return new(true, completion.Text, watch.Elapsed.TotalMilliseconds, attempts, settings.ReasoningEffort,
                completion.EffectiveEffort, completion.Model, completion.SystemPrompt, fingerprint,
                probe || completion.EffectiveEffort is not null ? ExperimentVerification.Verified : ExperimentVerification.ProviderDefault);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(false, null, watch.Elapsed.TotalMilliseconds, attempts, settings.ReasoningEffort,
                null, settings.Model, PostProcessorPresets.ResolveSystemPrompt(settings), fingerprint,
                probe ? ExperimentVerification.Rejected : ExperimentVerification.Unknown,
                deadline.IsCancellationRequested ? "deadline" : SafeFailureCode(ex));
        }
    }

    internal static string SafeFailureCode(Exception ex) => ex is HttpRequestException http && http.StatusCode is { } status
        ? $"http-{(int)status}" : ex.GetType().Name;

    private static async Task<string> TranscribeWithRetryAsync(
        ITranscriptionProvider provider,
        string audioFilePath,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            SessionMetrics.Current?.Add(MetricCount.Attempts);
            return await provider.TranscribeAsync(audioFilePath, language, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsRetryable(exception))
        {
            await RetryPolicy.WaitAsync((exception as ProviderRequestException)?.RetryAfter, cancellationToken);
            SessionMetrics.Current?.Add(MetricCount.Attempts);
            return await provider.TranscribeAsync(audioFilePath, language, cancellationToken);
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception is TimeoutException || exception is HttpRequestException request &&
            (request.StatusCode is null || (int)request.StatusCode >= 500 || (int)request.StatusCode is 408 or 429);
    }
}

public sealed record TranscriptionResult(
    bool Succeeded,
    string OutputText,
    string RawText,
    string ErrorMessage = "",
    long TranscriptionDurationMs = 0,
    long PostProcessingDurationMs = 0);
