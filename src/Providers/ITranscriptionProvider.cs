using Bisik.Models;

namespace Bisik.Providers;

/// <summary>Optional capability; file transcription remains available for fallback.</summary>
public interface IStreamingTranscriptionProvider
{
    bool SupportsStreaming { get; }
    Core.IRealtimeTranscriber CreateStream();
}

public interface ITranscriptionProvider
{
    string Name { get; }

    ProviderType Type { get; }

    Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken);
}
