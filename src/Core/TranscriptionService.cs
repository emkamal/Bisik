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
    Action playError)
{
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
        return TranscribeCoreAsync(audioFilePath, language, settings, cancellationToken);
    }

    private async Task<TranscriptionResult> TranscribeCoreAsync(
        string audioFilePath,
        string language,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var provider = ResolveProvider(settings.Provider);

            // Phase 1: Transcription
            PhaseChanged?.Invoke(ProcessingPhase.Transcribing);
            var transcriptionStopwatch = Stopwatch.StartNew();
            var rawText = await TranscribeWithRetryAsync(provider, audioFilePath, language, cancellationToken);
            transcriptionStopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawText))
                return new TranscriptionResult(true, string.Empty, string.Empty);

            // Phase 2: Post-processing
            long postProcessingDurationMs = 0;
            string outputText;

            if (settings.PostProcessor.Enabled)
            {
                PhaseChanged?.Invoke(ProcessingPhase.PostProcessing);
                var postProcessingStopwatch = Stopwatch.StartNew();
                outputText = await _postProcessor.ProcessAsync(rawText, settings.PostProcessor, cancellationToken);
                postProcessingStopwatch.Stop();
                postProcessingDurationMs = postProcessingStopwatch.ElapsedMilliseconds;
            }
            else
            {
                outputText = rawText;
            }

            var totalDurationMs = transcriptionStopwatch.ElapsedMilliseconds + postProcessingDurationMs;

            var entry = new TranscriptionEntry(
                startedAt,
                outputText,
                rawText,
                provider.Name,
                language,
                DurationMs: totalDurationMs,
                AudioFilePath: audioFilePath,
                TranscriptionDurationMs: transcriptionStopwatch.ElapsedMilliseconds,
                PostProcessingDurationMs: postProcessingDurationMs);
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.HistoryEnabled) await _historyLogger.AppendAsync(entry, cancellationToken);
            _playSuccess();

            return new TranscriptionResult(
                Succeeded: true,
                OutputText: outputText,
                RawText: rawText,
                TranscriptionDurationMs: transcriptionStopwatch.ElapsedMilliseconds,
                PostProcessingDurationMs: postProcessingDurationMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _errorLogger.Log(exception);
            _playError();

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

    private static async Task<string> TranscribeWithRetryAsync(
        ITranscriptionProvider provider,
        string audioFilePath,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.TranscribeAsync(audioFilePath, language, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsRetryable(exception))
        {
            await Task.Delay(300, cancellationToken);
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
