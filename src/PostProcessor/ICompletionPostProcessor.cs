using Bisik.Models;

namespace Bisik.PostProcessor;

public sealed record CompletionText(string Text, string Model, string SystemPrompt, string? EffectiveEffort,
    CompletionUsage? Usage = null, double? HeadersMs = null, double? FirstContentMs = null, double? FullResponseMs = null);
public enum TextCompletionStage { Headers, FirstContent, Complete }

// Probing sends an explicit parameter without inventing a verified ReasoningCapability.
public interface ICompletionPostProcessor : IPostProcessor
{
    Task<CompletionText> CompleteAsync(string text, PostProcessorSettings settings, bool probeEffort, CancellationToken ct,
        IProgress<TextCompletionStage>? progress = null);
}
