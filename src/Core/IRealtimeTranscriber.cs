namespace Bisik.Core;

/// <summary>
/// Arguments for the <see cref="IRealtimeTranscriber.PartialTranscriptAvailable"/> event.
/// </summary>
public sealed class PartialTranscriptEventArgs(string delta, string accumulatedText) : EventArgs
{
    /// <summary>The incremental text delta for this partial result.</summary>
    public string Delta { get; } = delta;

    /// <summary>The complete accumulated partial transcript so far.</summary>
    public string AccumulatedText { get; } = accumulatedText;
}

/// <summary>
/// Arguments for the <see cref="IRealtimeTranscriber.TranscriptionCompleted"/> event.
/// </summary>
public sealed class RealtimeCompletedEventArgs(string finalText) : EventArgs
{
    public string FinalText { get; } = finalText;
}

public sealed class CompletedTranscriptEventArgs(string text, long revision) : EventArgs
{
    public string Text { get; } = text;
    public long Revision { get; } = revision;
}

/// <summary>
/// Arguments for the <see cref="IRealtimeTranscriber.TranscriptionFailed"/> event.
/// </summary>
public sealed class RealtimeFailedEventArgs(string errorMessage) : EventArgs
{
    public string ErrorMessage { get; } = errorMessage;
}

/// <summary>
/// Abstracts a realtime (streaming) transcription session that produces partial
/// (delta) results while audio is still being captured.
/// </summary>
public interface IRealtimeTranscriber : IAsyncDisposable
{
    bool IsConnected => true;
    event EventHandler<CompletedTranscriptEventArgs>? CompletedTranscriptAvailable { add { } remove { } }
    /// <summary>
    /// Opens a WebSocket/streaming connection to the transcription provider.
    /// Must be called before <see cref="PushAudioChunkAsync"/>.
    /// </summary>
    Task StartAsync(Models.AppSettings settings, string language, CancellationToken ct);

    /// <summary>
    /// Pushes a PCM 16-bit mono 16 kHz audio chunk to the streaming session.
    /// Chunks should be pushed in chronological order as they are captured.
    /// </summary>
    Task PushAudioChunkAsync(byte[] pcmData, int bytesRecorded, CancellationToken ct);

    /// <summary>PCM16 mono at the actual capture rate; conversion state spans callbacks.</summary>
    Task PushAudioChunkAsync(byte[] pcmData, int bytesRecorded, int sampleRate, CancellationToken ct) =>
        sampleRate == 16000 ? PushAudioChunkAsync(pcmData, bytesRecorded, ct) :
            throw new NotSupportedException("This stream does not support the capture sample rate.");

    /// <summary>
    /// Commits the current audio buffer and waits for the final transcription result.
    /// Once returned, no more <see cref="PartialTranscriptAvailable"/> events will fire.
    /// </summary>
    Task<string> CommitAndFinalizeAsync(CancellationToken ct);

    /// <summary>
    /// Periodically commits the current audio buffer to trigger partial transcription
    /// results from the server while recording is still active.
    /// </summary>
    Task CommitIntermediateSegmentAsync(CancellationToken ct);

    /// <summary>
    /// Fires when a partial (delta) transcription result is available.
    /// The delta is an incremental addition to the accumulated transcript.
    /// </summary>
    event EventHandler<PartialTranscriptEventArgs>? PartialTranscriptAvailable;

    /// <summary>
    /// Fires once when all committed items have completed at finalization.
    /// </summary>
    event EventHandler<RealtimeCompletedEventArgs>? TranscriptionCompleted;

    /// <summary>
    /// Fires when the streaming session encounters a non-recoverable error.
    /// </summary>
    event EventHandler<RealtimeFailedEventArgs>? TranscriptionFailed;
}
