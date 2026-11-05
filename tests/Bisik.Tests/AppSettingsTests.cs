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

        settings.TriggerMode.Should().Be(TriggerMode.Toggle);
        settings.Hotkey.Should().Be("Ctrl+Shift+Space");
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
        settings.PostProcessor.TargetLanguage.Should().BeEmpty();
        settings.PostProcessor.PresetName.Should().Be(PostProcessorPreset.CleanUp);
        settings.PostProcessor.SystemPrompt.Should().Be("You are a transcription editor. Fix grammar, punctuation, and remove filler words. Return only the cleaned text, nothing else.");
    }

    [Fact]
    public void Default_settings_should_serialize_enum_values_as_spec_strings()
    {
        var json = JsonSerializer.Serialize(new AppSettings());

        json.Should().Contain("\"triggerMode\":\"toggle\"");
        json.Should().Contain("\"outputMode\":\"auto-type\"");
        json.Should().Contain("\"indicatorStyle\":\"bar-overlay\"");
        json.Should().Contain("\"type\":\"azure-openai\"");
        json.Should().Contain("\"format\":\"wav\"");
        json.Should().Contain("\"presetName\":\"clean-up\"");
        json.Should().NotContain("\"targetLanguage\"");
    }

    [Fact]
    public void Post_processor_target_language_should_serialize_when_non_empty()
    {
        var settings = new AppSettings
        {
            PostProcessor = new PostProcessorSettings
            {
                Enabled = true,
                TargetLanguage = "en",
            },
        };

        var json = JsonSerializer.Serialize(settings);

        json.Should().Contain("\"targetLanguage\":\"en\"");
    }

    [Fact]
    public void Trigger_mode_enum_should_match_the_spec_surface()
    {
        Enum.GetValues<TriggerMode>().Should().Contain([TriggerMode.PushToTalk, TriggerMode.Toggle, TriggerMode.Vad]);
        Enum.GetNames<TriggerMode>().Should().NotContain(["ToggleRecording", "AlwaysListening"]);

        JsonSerializer.Serialize(TriggerMode.PushToTalk).Should().Be("\"push-to-talk\"");
        JsonSerializer.Serialize(TriggerMode.Toggle).Should().Be("\"toggle\"");
        JsonSerializer.Serialize(TriggerMode.Vad).Should().Be("\"vad\"");
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
    public void Settings_should_deserialize_enum_and_string_values_from_spec_surface()
    {
        const string json = """
            {
              "triggerMode": "vad",
              "outputMode": "clipboard",
              "indicatorStyle": "tray-icon-only",
              "provider": {
                "type": "openrouter",
                "endpoint": "https://openrouter.ai/api/v1",
                "apiKey": "secret",
                "deploymentName": "",
                "apiVersion": "2024-06-01",
                "model": "openai/gpt-4o-mini-transcribe",
                "extraHeaders": {
                  "HTTP-Referer": "https://bisik.test"
                }
              },
              "audio": {
                "deviceId": "default",
                "format": "ogg",
                "sampleRate": 16000,
                "vadSilenceThreshold": 0.01,
                "vadMinSpeechMs": 300
              },
              "postProcessor": {
                "enabled": true,
                "provider": {
                  "type": "openai",
                  "endpoint": "https://api.openai.com/v1",
                  "apiKey": "pp-key",
                  "deploymentName": "",
                  "apiVersion": "2024-06-01"
                },
                "model": "gpt-4.1-mini",
                "targetLanguage": "en",
                "presetName": "translate-to-language",
                "systemPrompt": "Translate the text."
              }
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json);

        settings.Should().NotBeNull();
        settings!.TriggerMode.Should().Be(TriggerMode.Vad);
        settings.OutputMode.Should().Be(OutputMode.Clipboard);
        settings.IndicatorStyle.Should().Be(IndicatorStyle.TrayIconOnly);
        settings.Provider.Type.Should().Be(ProviderType.OpenRouter);
        settings.Provider.Model.Should().Be("openai/gpt-4o-mini-transcribe");
        settings.Provider.ExtraHeaders.Should().ContainKey("HTTP-Referer").WhoseValue.Should().Be("https://bisik.test");
        settings.Audio.Format.Should().Be(AudioFormat.Ogg);
        settings.PostProcessor.PresetName.Should().Be(PostProcessorPreset.TranslateToLanguage);
        settings.PostProcessor.TargetLanguage.Should().Be("en");
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
        entry.Text.Should().Be("final text");

        var json = JsonSerializer.Serialize(entry);
        json.Should().Contain("\"timestamp\":\"2026-06-12T10:30:00+00:00\"");
        json.Should().Contain("\"text\":\"final text\"");
        json.Should().Contain("\"rawText\":\"raw text\"");
        json.Should().Contain("\"provider\":\"azure-openai\"");
        json.Should().Contain("\"language\":\"id\"");
        json.Should().Contain("\"duration_ms\":1234");
        json.Should().Contain("\"audio_file_path\":\"C:\\\\temp\\\\clip.wav\"");
        json.Should().NotContain("processedText");
    }
}
