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

    [Fact]
    public void Indicator_and_audio_enums_should_match_the_spec_surface()
    {
        Enum.GetValues<IndicatorStyle>().Should().Contain(IndicatorStyle.TrayIconOnly);
        Enum.GetValues<AudioFormat>().Should().Contain(AudioFormat.Ogg);
        Enum.GetNames<AudioFormat>().Should().NotContain("M4a");

        JsonSerializer.Serialize(IndicatorStyle.TrayIconOnly).Should().Be("\"tray-icon-only\"");
        JsonSerializer.Serialize(AudioFormat.Ogg).Should().Be("\"ogg\"");
    }

    [Fact]
    public void Provider_settings_should_support_shared_openai_compatible_fields()
    {
        var provider = new ProviderSettings
        {
            Type = ProviderType.OpenAi,
            Endpoint = "https://api.example.test/v1",
            ApiKey = "key",
            DeploymentName = string.Empty,
            ApiVersion = "2024-06-01",
            Model = "gpt-4o-mini-transcribe",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "https://bisik.test",
            },
        };

        provider.Model.Should().Be("gpt-4o-mini-transcribe");
        provider.ExtraHeaders.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new KeyValuePair<string, string>("HTTP-Referer", "https://bisik.test"));

        var json = JsonSerializer.Serialize(provider);
        json.Should().Contain("\"model\":\"gpt-4o-mini-transcribe\"");
        json.Should().Contain("\"extraHeaders\":{\"HTTP-Referer\":\"https://bisik.test\"}");
    }

    [Fact]
    public void Transcription_entry_should_serialize_spec_aligned_history_fields()
    {
        var entry = new TranscriptionEntry(
            Timestamp: new DateTimeOffset(2026, 6, 12, 10, 30, 0, TimeSpan.Zero),
            Text: "final text",
            RawText: "raw text",
            Provider: "azure-openai",
            Language: "id",
            DurationMs: 1234,
            AudioFilePath: @"C:\temp\clip.wav");

        entry.ProcessedText.Should().Be("final text");

        var json = JsonSerializer.Serialize(entry);
        json.Should().Contain("\"timestamp\":\"2026-06-12T10:30:00+00:00\"");
        json.Should().Contain("\"text\":\"final text\"");
        json.Should().Contain("\"processedText\":\"final text\"");
        json.Should().Contain("\"rawText\":\"raw text\"");
        json.Should().Contain("\"provider\":\"azure-openai\"");
        json.Should().Contain("\"language\":\"id\"");
        json.Should().Contain("\"duration_ms\":1234");
        json.Should().Contain("\"audio_file_path\":\"C:\\\\temp\\\\clip.wav\"");
    }
}
