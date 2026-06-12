using Bisik.Models;

namespace Bisik.Providers;

public interface ITranscriptionProvider
{
    string Name { get; }

    ProviderType Type { get; }

    Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken);
}
