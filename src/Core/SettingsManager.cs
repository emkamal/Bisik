using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

public sealed class SettingsManager
{
    private const string SettingsFileName = "settings.json";
    private const string DpapiPrefix = "dpapi:v1:";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public SettingsManager(string? baseDirectory = null)
    {
        var rootDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik")
            : baseDirectory;

        _settingsPath = Path.Combine(rootDirectory, SettingsFileName);
    }

    public AppSettings Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

        if (!File.Exists(_settingsPath))
        {
            return SaveDefaults();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var persisted = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            var recovered = UnprotectSecrets(NormalizeSettings(persisted));

            if (recovered.HadRecoveredSecrets)
            {
                Save(recovered.Settings);
            }

            return recovered.Settings;
        }
        catch (JsonException)
        {
            return SaveDefaults();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

        var normalized = NormalizeSettings(settings);
        var protectedSettings = ProtectSecrets(normalized);
        var json = JsonSerializer.Serialize(protectedSettings, SerializerOptions);

        File.WriteAllText(_settingsPath, json, Encoding.UTF8);
    }

    private AppSettings SaveDefaults()
    {
        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    private static AppSettings NormalizeSettings(AppSettings? settings)
    {
        var defaults = new AppSettings();
        if (settings is null)
        {
            return defaults;
        }

        return settings with
        {
            Hotkey = settings.Hotkey ?? defaults.Hotkey,
            DefaultLanguage = settings.DefaultLanguage ?? defaults.DefaultLanguage,
            QuickLanguages = settings.QuickLanguages ?? defaults.QuickLanguages,
            Provider = NormalizeProvider(settings.Provider),
            Audio = NormalizeAudio(settings.Audio),
            PostProcessor = NormalizePostProcessor(settings.PostProcessor),
        };
    }

    private static ProviderSettings NormalizeProvider(ProviderSettings? provider)
    {
        var defaults = new ProviderSettings();
        if (provider is null)
        {
            return defaults;
        }

        return provider with
        {
            Endpoint = provider.Endpoint ?? defaults.Endpoint,
            ApiKey = provider.ApiKey ?? defaults.ApiKey,
            DeploymentName = provider.DeploymentName ?? defaults.DeploymentName,
            ApiVersion = provider.ApiVersion ?? defaults.ApiVersion,
        };
    }

    private static AudioSettings NormalizeAudio(AudioSettings? audio)
    {
        var defaults = new AudioSettings();
        if (audio is null)
        {
            return defaults;
        }

        return audio with
        {
            DeviceId = audio.DeviceId,
        };
    }

    private static PostProcessorSettings NormalizePostProcessor(PostProcessorSettings? postProcessor)
    {
        var defaults = new PostProcessorSettings();
        if (postProcessor is null)
        {
            return defaults;
        }

        return postProcessor with
        {
            Provider = NormalizeProvider(postProcessor.Provider),
            Model = postProcessor.Model ?? defaults.Model,
            TargetLanguage = postProcessor.TargetLanguage ?? defaults.TargetLanguage,
            SystemPrompt = postProcessor.SystemPrompt ?? defaults.SystemPrompt,
        };
    }

    private static AppSettings ProtectSecrets(AppSettings settings)
    {
        return settings with
        {
            Provider = ProtectProvider(settings.Provider),
            PostProcessor = settings.PostProcessor with
            {
                Provider = ProtectProvider(settings.PostProcessor.Provider),
            },
        };
    }

    private static SecretRecoveryResult UnprotectSecrets(AppSettings settings)
    {
        var providerRecovery = UnprotectProvider(settings.Provider);
        var postProcessorProviderRecovery = UnprotectProvider(settings.PostProcessor.Provider);

        return new SecretRecoveryResult(
            settings with
            {
                Provider = providerRecovery.Provider,
                PostProcessor = settings.PostProcessor with
                {
                    Provider = postProcessorProviderRecovery.Provider,
                },
            },
            providerRecovery.HadRecoveredSecret || postProcessorProviderRecovery.HadRecoveredSecret);
    }

    private static ProviderRecoveryResult UnprotectProvider(ProviderSettings provider)
    {
        var apiKeyRecovery = UnprotectApiKey(provider.ApiKey);

        return new ProviderRecoveryResult(
            provider with
            {
                ApiKey = apiKeyRecovery.ApiKey,
            },
            apiKeyRecovery.HadRecoveredSecret);
    }

    private static ApiKeyRecoveryResult UnprotectApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return new ApiKeyRecoveryResult(apiKey, HadRecoveredSecret: false);
        }

        try
        {
            // Prefixed values are treated as persisted secrets and must round-trip back to plain text in memory.
            var protectedBytes = Convert.FromBase64String(apiKey[DpapiPrefix.Length..]);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return new ApiKeyRecoveryResult(Encoding.UTF8.GetString(plainBytes), HadRecoveredSecret: false);
        }
        catch (FormatException)
        {
            return new ApiKeyRecoveryResult(string.Empty, HadRecoveredSecret: true);
        }
        catch (CryptographicException)
        {
            return new ApiKeyRecoveryResult(string.Empty, HadRecoveredSecret: true);
        }
    }

    private static ProviderSettings ProtectProvider(ProviderSettings provider)
    {
        return provider with
        {
            ApiKey = ProtectApiKey(provider.ApiKey),
        };
    }

    private static string ProtectApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return apiKey;
        }

        var secretBytes = Encoding.UTF8.GetBytes(apiKey);
        var protectedBytes = ProtectedData.Protect(secretBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return $"{DpapiPrefix}{Convert.ToBase64String(protectedBytes)}";
    }

    private readonly record struct SecretRecoveryResult(AppSettings Settings, bool HadRecoveredSecrets);
    private readonly record struct ProviderRecoveryResult(ProviderSettings Provider, bool HadRecoveredSecret);
    private readonly record struct ApiKeyRecoveryResult(string ApiKey, bool HadRecoveredSecret);
}
