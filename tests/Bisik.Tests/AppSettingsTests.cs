using System.Text.Json;
using Bisik.Models;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Default_app_settings_should_match_the_spec()
    {
        var settings = new AppSettings();

        settings.TriggerMode.Should().Be(TriggerMode.PushToTalk);
        settings.Hotkey.Should().Be("Ctrl+Win");
        settings.OutputMode.Should().Be(OutputMode.AutoType);
        settings.IndicatorStyle.Should().Be(IndicatorStyle.BarOverlay);
        settings.DefaultLanguage.Should().Be("auto");
        settings.QuickLanguages.Should().Equal("en", "id", "auto");
        settings.LaunchAtStartup.Should().BeFalse();
        settings.SoundEnabled.Should().BeTrue();
        settings.HistoryEnabled.Should().BeTrue();

        settings.Provider.Type.Should().Be(ProviderType.AzureOpenAi);
        settings.Provider.Endpoint.Should().BeEmpty();
        settings.Provider.ApiKey.Should().BeEmpty();
        settings.Provider.DeploymentName.Should().BeEmpty();
        settings.Provider.ApiVersion.Should().Be("2024-06-01");

        settings.Audio.DeviceId.Should().BeNull();
        settings.Audio.Format.Should().Be(AudioFormat.Wav);
        settings.Audio.SampleRate.Should().Be(16000);
        settings.Audio.VadSilenceThreshold.Should().Be(0.01);
        settings.Audio.VadMinSpeechMs.Should().Be(300);

        settings.PostProcessor.Enabled.Should().BeFalse();
        settings.PostProcessor.Provider.Type.Should().Be(ProviderType.AzureOpenAi);
        settings.PostProcessor.Provider.ApiVersion.Should().Be("2024-06-01");
        settings.PostProcessor.Model.Should().BeEmpty();
        settings.PostProcessor.PresetName.Should().Be(PostProcessorPreset.CleanUp);
        settings.PostProcessor.SystemPrompt.Should().Be("You are a transcription editor. Fix grammar, punctuation, and remove filler words. Return only the cleaned text, nothing else.");
    }

    [Fact]
    public void Default_settings_should_serialize_enum_values_as_spec_strings()
    {
        var json = JsonSerializer.Serialize(new AppSettings());

        json.Should().Contain("\"triggerMode\":\"push-to-talk\"");
        json.Should().Contain("\"outputMode\":\"auto-type\"");
        json.Should().Contain("\"indicatorStyle\":\"bar-overlay\"");
        json.Should().Contain("\"type\":\"azure-openai\"");
        json.Should().Contain("\"format\":\"wav\"");
        json.Should().Contain("\"presetName\":\"clean-up\"");
    }
}
