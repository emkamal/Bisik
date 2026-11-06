using System.Text.Json;
using Bisik.Models;

namespace Bisik.Tests;

public sealed class HistoryConfigurationTests
{
    [Fact]
    public void Snapshot_captures_options_and_effective_prompt_without_either_providers_connection_secrets()
    {
        var provider = new ProviderSettings { Type = ProviderType.CustomEndpoint, Endpoint = "https://secret.test",
            ApiKey = "SECRET_KEY", ExtraHeaders = new() { ["SECRET_HEADER"] = "SECRET_VALUE" },
            DeploymentName = "deployment", Model = "model", TranscriptionDelay = "low", AzureV1 = true };
        var cleanup = new PostProcessorSettings { Enabled = true, Provider = provider, Model = "cleanup",
            PresetName = PostProcessorPreset.Custom, SystemPrompt = "Custom instructions", ReasoningEffort = "low" };
        cleanup = cleanup with { ReasoningCapability = new(ReasoningCapability.Fingerprint(cleanup), ["low"]) };
        var settings = new AppSettings { Provider = provider, PostProcessor = cleanup, LiveCapture = true,
            SpeculativeCleanup = true, HistoryConfigurationEnabled = true, QuickLanguages = ["id", "en"] };
        var snapshot = HistoryConfigurationSnapshot.Capture(settings, "id", "Actual instructions");
        var json = snapshot.GetRawText();
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("secret.test", json);
        foreach (var p in new[] { snapshot.GetProperty("provider"), snapshot.GetProperty("postProcessor").GetProperty("provider") })
        {
            Assert.False(p.TryGetProperty("endpoint", out _));
            Assert.False(p.TryGetProperty("apiKey", out _));
            Assert.False(p.TryGetProperty("extraHeaders", out _));
            Assert.True(p.GetProperty("endpointConfigured").GetBoolean());
            Assert.Equal(1, p.GetProperty("extraHeaderCount").GetInt32());
            Assert.Equal("model", p.GetProperty("model").GetString());
        }
        Assert.True(snapshot.GetProperty("liveCapture").GetBoolean());
        Assert.Equal("id", snapshot.GetProperty("defaultLanguage").GetString());
        Assert.Equal("Actual instructions", snapshot.GetProperty("postProcessor").GetProperty("effectiveSystemPrompt").GetString());
        Assert.Equal("low", snapshot.GetProperty("postProcessor").GetProperty("effectiveReasoningEffort").GetString());
        settings.QuickLanguages[0] = "changed";
        Assert.Equal("id", snapshot.GetProperty("quickLanguages")[0].GetString());
    }

    [Fact]
    public void Disabled_cleanup_has_no_effective_prompt_or_effort_and_old_settings_get_defaults()
    {
        var snapshot = HistoryConfigurationSnapshot.Capture(new AppSettings(), "en", null);
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("postProcessor").GetProperty("effectiveSystemPrompt").ValueKind);
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.True(settings.HistoryRecordingDurationEnabled);
        Assert.False(settings.HistoryConfigurationEnabled);
    }
}
