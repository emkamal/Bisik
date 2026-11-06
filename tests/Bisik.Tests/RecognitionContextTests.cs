using System.Collections.Immutable;
using System.Text.Json;
using Bisik.Models;
using Bisik.Providers;

namespace Bisik.Tests;

public class RecognitionContextTests
{
    [Fact]
    public void ForceRealtimeDoesNotForceDelayAndWhisperNeverReceivesPrompt()
    {
        var provider = new ProviderSettings { DeploymentName = "alias", AzureRealtime = true, TranscriptionDelay = "low",
            RecognitionContext = new("domain", ["HAPPI"], "id") };
        var payload = JsonSerializer.Serialize(AzureRealtimeProtocol.SessionUpdate(provider, "en"));
        Assert.DoesNotContain("delay", payload); Assert.DoesNotContain("prompt", payload); Assert.DoesNotContain("language", payload);
        var whisper = provider with { Model = "gpt-realtime-whisper" };
        payload = JsonSerializer.Serialize(AzureRealtimeProtocol.SessionUpdate(whisper, "en"));
        Assert.Contains("delay", payload); Assert.Contains("language", payload); Assert.DoesNotContain("prompt", payload);
    }
    [Fact]
    public void ExplicitFieldsAreScopedAndContextChangesWarmIdentity()
    {
        var provider = new ProviderSettings { Endpoint = "https://example.invalid", DeploymentName = "new-alias", AzureRealtime = true,
            RecognitionContext = new("domain terms", ["HAPPI"], "id") };
        provider = provider with { RecognitionCapability = new(RecognitionCapability.Fingerprint(provider, true), [RecognitionField.Prompt, RecognitionField.Keywords, RecognitionField.Language]) };
        var payload = JsonSerializer.Serialize(AzureRealtimeProtocol.SessionUpdate(provider, "auto"));
        Assert.Contains("domain terms", payload); Assert.Contains("HAPPI", payload); Assert.Contains("id", payload);
        Assert.DoesNotContain("domain terms", JsonSerializer.Serialize(AzureRealtimeProtocol.SessionUpdate(provider with { DeploymentName = "changed" }, "auto")));
        var snapshot = new AppSettings { Provider = provider }.Snapshot();
        Assert.NotEqual(JsonSerializer.Serialize(snapshot.Provider), JsonSerializer.Serialize(provider with { RecognitionContext = null }));
        Assert.Equal(provider.RecognitionContext, snapshot.Provider.RecognitionContext);
    }
}
