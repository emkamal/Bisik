using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.Tests;

public class PredictionTests
{
    [Fact]
    public async Task PayloadUsesRawAndVariantIdentityDistinguishesOnOff()
    {
        JsonDocument? captured = null;
        var settings = TranscriptionServiceTextTests.Settings() with { UsePrediction = true };
        var definition = ExperimentModelTests.Definition() with { ConnectionFingerprint = ExperimentMatrix.ConnectionIdentity(settings),
            Models = [new("off", "deployment", "model", [null]), new("on", "deployment", "model", [null], UsePrediction: true)] };
        var exact = ExperimentMatrix.Settings(settings, definition.Models[1], definition.Prompts[0], null);
        settings = settings with { CompletionCapability = new(ReasoningCapability.Fingerprint(exact), [CompletionFeature.Prediction]) };
        var matrix = ExperimentMatrix.Build(definition, settings); Assert.Equal(2, matrix.ScheduledTrials); Assert.Equal(2, matrix.Rows.Select(r => r.Candidate.Id).Distinct().Count());
        using var client = new HttpClient(new TranscriptionServiceTextTests.Handler(async (r, _) =>
            { captured = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()); return TranscriptionServiceTextTests.Complete("edited"); }));
        var receipt = await new LLMPostProcessor(client).CompleteAsync("raw transcript", ExperimentMatrix.Settings(settings, matrix.Rows[1].Candidate), false, CancellationToken.None);
        Assert.Equal("raw transcript", captured!.RootElement.GetProperty("prediction").GetProperty("content").GetString());
        Assert.Null(receipt.Usage); captured.Dispose();
        await new LLMPostProcessor(client).CompleteAsync("raw transcript", settings with { UsePrediction = false }, false, CancellationToken.None);
        Assert.False(captured!.RootElement.TryGetProperty("prediction", out _)); captured.Dispose();
    }
    [Fact]
    public void UnknownStaleAndLegacyAzureContractsAreBlocked()
    {
        var settings = TranscriptionServiceTextTests.Settings() with { UsePrediction = true };
        Assert.False(LLMPostProcessor.PredictionSupported(settings));
        settings = settings with { Provider = settings.Provider with { Type = ProviderType.AzureOpenAi, ApiVersion = "2024-06-01" } };
        settings = settings with { CompletionCapability = new(ReasoningCapability.Fingerprint(settings), [CompletionFeature.Prediction]) };
        Assert.False(LLMPostProcessor.PredictionSupported(settings));
        Assert.False(LLMPostProcessor.PredictionSupported(settings with { Provider = settings.Provider with { AzureV1 = true } }));
    }
}
