using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public void Load_should_clear_only_the_corrupted_secret_when_a_prefixed_key_has_invalid_base64()
    {
        var manager = new SettingsManager(_baseDirectory);
        var expected = new AppSettings
        {
            TriggerMode = TriggerMode.Toggle,
            Hotkey = "Ctrl+Shift+Space",
            DefaultLanguage = "id",
            Provider = new ProviderSettings
            {
                Type = ProviderType.OpenAi,
                Endpoint = "https://api.openai.com/v1",
                ApiKey = "provider-secret",
                ApiVersion = "2024-06-01",
            },
            PostProcessor = new PostProcessorSettings
            {
                Enabled = true,
                Provider = new ProviderSettings
                {
                    Type = ProviderType.OpenRouter,
                    Endpoint = "https://openrouter.ai/api/v1",
                    ApiKey = "postprocessor-secret",
                    ApiVersion = "2024-06-01",
                },
                Model = "gpt-4.1-mini",
            },
        };

        manager.Save(expected);
        CorruptPersistedApiKey("dpapi:v1:not-valid-base64", keyPath: ["provider", "apiKey"]);

        var loaded = manager.Load();

        loaded.TriggerMode.Should().Be(expected.TriggerMode);
        loaded.Hotkey.Should().Be(expected.Hotkey);
        loaded.DefaultLanguage.Should().Be(expected.DefaultLanguage);
        loaded.Provider.Endpoint.Should().Be(expected.Provider.Endpoint);
        loaded.Provider.ApiKey.Should().BeEmpty();
        loaded.PostProcessor.Enabled.Should().BeTrue();
        loaded.PostProcessor.Provider.ApiKey.Should().Be("postprocessor-secret");

        var persisted = ReadPersistedSettingsJson();
        persisted["provider"]!["apiKey"]!.GetValue<string>().Should().BeEmpty();
        persisted["postProcessor"]!["provider"]!["apiKey"]!.GetValue<string>().Should().StartWith("dpapi:v1:");
    }

    [Fact]
    public void Load_should_clear_only_the_corrupted_secret_when_a_prefixed_key_cannot_be_decrypted()
    {
        var manager = new SettingsManager(_baseDirectory);
        var expected = new AppSettings
        {
            OutputMode = OutputMode.Popup,
            IndicatorStyle = IndicatorStyle.TrayIconOnly,
            Provider = new ProviderSettings
            {
                Type = ProviderType.AzureOpenAi,
                Endpoint = "https://example.openai.azure.com",
                ApiKey = "provider-secret",
                DeploymentName = "whisper-prod",
                ApiVersion = "2024-06-01",
            },
            PostProcessor = new PostProcessorSettings
            {
                Enabled = true,
                Provider = new ProviderSettings
                {
                    Type = ProviderType.OpenAi,
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "postprocessor-secret",
                    ApiVersion = "2024-06-01",
                },
                Model = "gpt-4.1-mini",
            },
        };

        manager.Save(expected);
        CorruptPersistedApiKey($"dpapi:v1:{Convert.ToBase64String([1, 2, 3, 4, 5])}", keyPath: ["postProcessor", "provider", "apiKey"]);

        var loaded = manager.Load();

        loaded.OutputMode.Should().Be(expected.OutputMode);
        loaded.IndicatorStyle.Should().Be(expected.IndicatorStyle);
        loaded.Provider.ApiKey.Should().Be("provider-secret");
        loaded.PostProcessor.Enabled.Should().BeTrue();
        loaded.PostProcessor.Provider.ApiKey.Should().BeEmpty();

        var persisted = ReadPersistedSettingsJson();
        persisted["provider"]!["apiKey"]!.GetValue<string>().Should().StartWith("dpapi:v1:");
        persisted["postProcessor"]!["provider"]!["apiKey"]!.GetValue<string>().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private void CorruptPersistedApiKey(string corruptedValue, params string[] keyPath)
    {
        var json = ReadPersistedSettingsJson();
        var current = json;

        foreach (var segment in keyPath[..^1])
        {
            current = current[segment]!.AsObject();
        }

        current[keyPath[^1]] = corruptedValue;
        File.WriteAllText(Path.Combine(_baseDirectory, "settings.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private JsonObject ReadPersistedSettingsJson()
    {
        var settingsPath = Path.Combine(_baseDirectory, "settings.json");
        return JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
    }
}
