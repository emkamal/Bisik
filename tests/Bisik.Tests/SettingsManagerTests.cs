using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class SettingsManagerTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), "Bisik.SettingsManagerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_should_create_default_settings_file_on_first_run()
    {
        var manager = new SettingsManager(_baseDirectory);

        var settings = manager.Load();

        settings.Should().BeEquivalentTo(new AppSettings());

        var settingsPath = Path.Combine(_baseDirectory, "settings.json");
        File.Exists(settingsPath).Should().BeTrue();

        var json = File.ReadAllText(settingsPath);
        json.Should().Contain(Environment.NewLine + "  \"triggerMode\": \"push-to-talk\"");
        json.Should().Contain("\"type\": \"azure-openai\"");
        json.Should().NotContain("\"targetLanguage\"");
    }

    [Fact]
    public void Save_and_load_should_round_trip_settings_values()
    {
        var manager = new SettingsManager(_baseDirectory);
        var expected = new AppSettings
        {
            TriggerMode = TriggerMode.Toggle,
            Hotkey = "Ctrl+Shift+Space",
            OutputMode = OutputMode.Clipboard,
            IndicatorStyle = IndicatorStyle.DotNearCursor,
            DefaultLanguage = "id",
            QuickLanguages = ["id", "en"],
            LaunchAtStartup = true,
            SoundEnabled = false,
            HistoryEnabled = false,
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenAi,
                Endpoint = "https://api.openai.com/v1",
                ApiKey = "provider-secret",
                DeploymentName = string.Empty,
                ApiVersion = "2024-06-01",
                Model = "whisper-1",
            },
            Audio = new AudioSettings
            {
                DeviceId = "default",
                Format = AudioFormat.Ogg,
                SampleRate = 22050,
                VadSilenceThreshold = 0.02,
                VadMinSpeechMs = 450,
            },
            PostProcessor = new PostProcessorSettings
            {
                Enabled = true,
                Provider = new ProviderSettings
                {
                    Type = ProviderType.OpenRouter,
                    Endpoint = "https://openrouter.ai/api/v1",
                    ApiKey = "postprocessor-secret",
                    DeploymentName = string.Empty,
                    ApiVersion = "2024-06-01",
                    Model = "openai/gpt-4.1-mini",
                },
                Model = "gpt-4.1-mini",
                TargetLanguage = "en",
                PresetName = PostProcessorPreset.TranslateToLanguage,
                SystemPrompt = "Translate to English.",
            },
        };

        manager.Save(expected);

        var persistedJson = File.ReadAllText(Path.Combine(_baseDirectory, "settings.json"));
        persistedJson.Should().Contain("\"targetLanguage\": \"en\"");

        var actual = manager.Load();

        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Load_should_fall_back_to_defaults_when_settings_json_is_malformed()
    {
        Directory.CreateDirectory(_baseDirectory);
        var settingsPath = Path.Combine(_baseDirectory, "settings.json");
        File.WriteAllText(settingsPath, "{ definitely-not-json");

        var manager = new SettingsManager(_baseDirectory);

        var settings = manager.Load();

        settings.Should().BeEquivalentTo(new AppSettings());

        var persisted = File.ReadAllText(settingsPath);
        persisted.Should().Contain("\"triggerMode\": \"push-to-talk\"");
    }

    [Fact]
    public void Save_should_encrypt_api_keys_at_rest_for_transcription_and_post_processor_providers()
    {
        var manager = new SettingsManager(_baseDirectory);
        var settings = new AppSettings
        {
            Provider = new ProviderSettings
            {
                ApiKey = "top-secret-provider-key",
            },
            PostProcessor = new PostProcessorSettings
            {
                Enabled = true,
                Provider = new ProviderSettings
                {
                    ApiKey = "top-secret-postprocessor-key",
                },
            },
        };

        manager.Save(settings);

        var json = File.ReadAllText(Path.Combine(_baseDirectory, "settings.json"));

        json.Should().NotContain("top-secret-provider-key");
        json.Should().NotContain("top-secret-postprocessor-key");
        json.Should().Contain("dpapi:v1:");

        var reloaded = manager.Load();
        reloaded.Provider.ApiKey.Should().Be("top-secret-provider-key");
        reloaded.PostProcessor.Provider.ApiKey.Should().Be("top-secret-postprocessor-key");
    }

    [Fact]
    public void Load_should_accept_legacy_plaintext_api_keys_and_encrypt_them_on_next_save()
    {
        Directory.CreateDirectory(_baseDirectory);
        var settingsPath = Path.Combine(_baseDirectory, "settings.json");

        var legacySettings = new AppSettings
        {
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenAi,
                ApiKey = "legacy-provider-key",
            },
            PostProcessor = new PostProcessorSettings
            {
                Enabled = true,
                Provider = new ProviderSettings
                {
                    Type = ProviderType.OpenRouter,
                    ApiKey = "legacy-postprocessor-key",
                },
            },
        };

        File.WriteAllText(settingsPath, JsonSerializer.Serialize(legacySettings, new JsonSerializerOptions { WriteIndented = true }));

        var manager = new SettingsManager(_baseDirectory);

        var loaded = manager.Load();
        loaded.Provider.ApiKey.Should().Be("legacy-provider-key");
        loaded.PostProcessor.Provider.ApiKey.Should().Be("legacy-postprocessor-key");

        manager.Save(loaded);

        var persisted = File.ReadAllText(settingsPath);
        persisted.Should().NotContain("legacy-provider-key");
        persisted.Should().NotContain("legacy-postprocessor-key");
        persisted.Should().Contain("dpapi:v1:");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }
}
