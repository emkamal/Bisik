using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bisik.Models;

public static class HistoryConfigurationSnapshot
{
    public static JsonElement Capture(AppSettings settings, string language, string? effectiveSystemPrompt)
    {
        // Never serialize live connection secrets, even temporarily into a history JSON node.
        var snapshot = settings.Snapshot();
        var safe = snapshot with
        {
            DefaultLanguage = language,
            Provider = SafeProvider(snapshot.Provider),
            PostProcessor = snapshot.PostProcessor with { Provider = SafeProvider(snapshot.PostProcessor.Provider) },
        };
        var root = JsonSerializer.SerializeToNode(safe)!.AsObject();
        ReplaceConnectionFields(root["provider"]!.AsObject(), snapshot.Provider);
        var cleanup = root["postProcessor"]!.AsObject();
        ReplaceConnectionFields(cleanup["provider"]!.AsObject(), snapshot.PostProcessor.Provider);
        cleanup["effectiveSystemPrompt"] = effectiveSystemPrompt;
        cleanup["effectiveReasoningEffort"] = effectiveSystemPrompt is not null &&
            snapshot.PostProcessor.ReasoningCapability?.Matches(snapshot.PostProcessor) == true
                ? snapshot.PostProcessor.ReasoningEffort : null;
        return JsonSerializer.SerializeToElement(root);
    }

    // Explicit allowlist keeps credentials, endpoint overrides and undecryptable ciphertext out.
    private static ProviderSettings SafeProvider(ProviderSettings provider) => new()
    {
        Type = provider.Type,
        DeploymentName = provider.DeploymentName,
        ApiVersion = provider.ApiVersion,
        AzureRealtime = provider.AzureRealtime,
        AzureV1 = provider.AzureV1,
        TranscriptionDelay = provider.TranscriptionDelay,
        Model = provider.Model,
        RecognitionContext = provider.RecognitionContext,
        RecognitionCapability = provider.RecognitionCapability,
    };

    private static void ReplaceConnectionFields(JsonObject node, ProviderSettings provider)
    {
        node.Remove("endpoint");
        node.Remove("apiKey");
        node.Remove("extraHeaders");
        node["endpointConfigured"] = !string.IsNullOrEmpty(provider.Endpoint);
        node["apiKeyConfigured"] = !string.IsNullOrEmpty(provider.ApiKey);
        node["extraHeaderCount"] = provider.ExtraHeaders?.Count ?? 0;
    }
}
