using Bisik.Models;

namespace Bisik.PostProcessor;

public interface IPostProcessor
{
    // The processor receives raw transcription text plus the active settings for this run.
    Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken cancellationToken);
}
