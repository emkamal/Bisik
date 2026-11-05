using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

public sealed class SettingsManager
{
    private const string SettingsFileName = "settings.json";
    private const string CorruptSettingsBackupFileName = "settings.json.bad";
    private const string DpapiPrefix = "dpapi:v1:";
    private static readonly string[] SensitiveHeaderTokens =
    {
        "auth",
        "authorization",
        "key",
        "apikey",
        "token",
        "secret",
        "password",
        "credential",
        "endpoint",
    };
    private static readonly HashSet<string> PublicHeaderNames = new(StringComparer.Ordinal)
    {
        "httpreferer",
        "referer",
        "useragent",
    };

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

            if (recovered.HadHealedSecrets)
            {
                Save(recovered.PersistedSettings);
            }

            return recovered.InMemorySettings;
        }
        catch (JsonException)
        {
            BackupMalformedSettingsFile();
            return SaveDefaults();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

        var normalized = NormalizeSettings(settings);
        var protectedSettings = ProtectSecrets(normalized);
        var json = JsonSerializer.Serialize(protectedSettings, SerializerOptions);
        var settingsDirectory = Path.GetDirectoryName(_settingsPath)!;
        var tempPath = Path.Combine(settingsDirectory, $"{SettingsFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (File.Exists(_settingsPath))
            {
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private AppSettings SaveDefaults()
    {
        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    private void BackupMalformedSettingsFile()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        var backupPath = Path.Combine(Path.GetDirectoryName(_settingsPath)!, CorruptSettingsBackupFileName);
        // Malformed legacy JSON can still contain readable credentials; keep the recovery copy private too.
        File.WriteAllText(backupPath, ProtectSecretValue(File.ReadAllText(_settingsPath)), Encoding.UTF8);
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
            InMemorySettings: settings with
            {
                Provider = providerRecovery.InMemoryProvider,
                PostProcessor = settings.PostProcessor with
                {
                    Provider = postProcessorProviderRecovery.InMemoryProvider,
                },
            },
            PersistedSettings: settings with
            {
                Provider = providerRecovery.PersistedProvider,
                PostProcessor = settings.PostProcessor with
                {
                    Provider = postProcessorProviderRecovery.PersistedProvider,
                },
            },
            HadHealedSecrets: providerRecovery.HadHealedSecrets || postProcessorProviderRecovery.HadHealedSecrets);
    }

    private static ProviderRecoveryResult UnprotectProvider(ProviderSettings provider)
    {
        var apiKeyRecovery = UnprotectSecretValue(provider.ApiKey);
        var endpointRecovery = UnprotectSecretValue(provider.Endpoint);
        var headersRecovery = UnprotectExtraHeaders(provider.ExtraHeaders);

        return new ProviderRecoveryResult(
            InMemoryProvider: provider with
            {
                ApiKey = apiKeyRecovery.InMemoryValue,
                Endpoint = endpointRecovery.InMemoryValue,
                ExtraHeaders = headersRecovery.InMemoryHeaders,
                PersistedUndecryptableApiKeyCiphertext = apiKeyRecovery.PersistedUndecryptableCiphertext,
                PersistedUndecryptableEndpointCiphertext = endpointRecovery.PersistedUndecryptableCiphertext,
                PersistedUndecryptableHeaderCiphertexts = headersRecovery.PersistedUndecryptableHeaderCiphertexts,
            },
            PersistedProvider: provider with
            {
                ApiKey = apiKeyRecovery.PersistedValue,
                Endpoint = endpointRecovery.PersistedValue,
                ExtraHeaders = headersRecovery.PersistedHeaders,
                PersistedUndecryptableApiKeyCiphertext = null,
                PersistedUndecryptableEndpointCiphertext = null,
                PersistedUndecryptableHeaderCiphertexts = null,
            },
            HadHealedSecrets: apiKeyRecovery.ShouldHealPersistedValue || endpointRecovery.ShouldHealPersistedValue || headersRecovery.HadHealedSecrets);
    }

    private static ProviderSettings ProtectProvider(ProviderSettings provider)
    {
        return provider with
        {
            ApiKey = ProtectSecretValue(provider.ApiKey, provider.PersistedUndecryptableApiKeyCiphertext),
            Endpoint = ProtectSecretValue(provider.Endpoint, provider.PersistedUndecryptableEndpointCiphertext),
            ExtraHeaders = ProtectExtraHeaders(provider.ExtraHeaders, provider.PersistedUndecryptableHeaderCiphertexts),
            PersistedUndecryptableApiKeyCiphertext = null,
            PersistedUndecryptableEndpointCiphertext = null,
            PersistedUndecryptableHeaderCiphertexts = null,
        };
    }

    private static string ProtectSecretValue(string value, string? preservedUndecryptableCiphertext = null)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return string.IsNullOrEmpty(value)
                ? preservedUndecryptableCiphertext ?? value
                : value;
        }

        var secretBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(secretBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return $"{DpapiPrefix}{Convert.ToBase64String(protectedBytes)}";
    }

    private static SecretValueRecoveryResult UnprotectSecretValue(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            // Older settings stored endpoints (and sometimes credentials) in plaintext. Migrate on load.
            return new SecretValueRecoveryResult(InMemoryValue: value, PersistedValue: value, ShouldHealPersistedValue: !string.IsNullOrEmpty(value));
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var plainText = Encoding.UTF8.GetString(plainBytes);
            return new SecretValueRecoveryResult(InMemoryValue: plainText, PersistedValue: plainText, ShouldHealPersistedValue: false);
        }
        catch (FormatException)
        {
            return new SecretValueRecoveryResult(InMemoryValue: string.Empty, PersistedValue: string.Empty, ShouldHealPersistedValue: true);
        }
        catch (CryptographicException)
        {
            return new SecretValueRecoveryResult(
                InMemoryValue: string.Empty,
                PersistedValue: value,
                ShouldHealPersistedValue: false,
                PersistedUndecryptableCiphertext: value);
        }
    }

    private static Dictionary<string, string>? ProtectExtraHeaders(
        Dictionary<string, string>? headers,
        Dictionary<string, string>? preservedUndecryptableCiphertexts)
    {
        if (headers is null)
        {
            return null;
        }

        var protectedHeaders = new Dictionary<string, string>(headers.Count, headers.Comparer);

        foreach (var header in headers)
        {
            protectedHeaders[header.Key] = IsSensitiveHeaderName(header.Key)
                ? ProtectSecretValue(
                    header.Value,
                    TryGetPreservedUndecryptableCiphertext(preservedUndecryptableCiphertexts, header.Key, out var ciphertext)
                        ? ciphertext
                        : null)
                : header.Value;
        }

        return protectedHeaders;
    }

    private static HeaderRecoveryResult UnprotectExtraHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return new HeaderRecoveryResult(
                InMemoryHeaders: null,
                PersistedHeaders: null,
                HadHealedSecrets: false,
                PersistedUndecryptableHeaderCiphertexts: null);
        }

        var inMemoryHeaders = new Dictionary<string, string>(headers.Count, headers.Comparer);
        var persistedHeaders = new Dictionary<string, string>(headers.Count, headers.Comparer);
        var hadHealedSecrets = false;
        Dictionary<string, string>? persistedUndecryptableCiphertexts = null;

        foreach (var header in headers)
        {
            if (!IsSensitiveHeaderName(header.Key))
            {
                inMemoryHeaders[header.Key] = header.Value;
                persistedHeaders[header.Key] = header.Value;
                continue;
            }

            var recovery = UnprotectSecretValue(header.Value);
            inMemoryHeaders[header.Key] = recovery.InMemoryValue;
            persistedHeaders[header.Key] = recovery.PersistedValue;
            hadHealedSecrets |= recovery.ShouldHealPersistedValue;

            if (recovery.PersistedUndecryptableCiphertext is not null)
            {
                persistedUndecryptableCiphertexts ??= new Dictionary<string, string>(StringComparer.Ordinal);
                persistedUndecryptableCiphertexts[CanonicalizeHeaderName(header.Key)] = recovery.PersistedUndecryptableCiphertext;
            }
        }

        return new HeaderRecoveryResult(
            inMemoryHeaders,
            persistedHeaders,
            hadHealedSecrets,
            persistedUndecryptableCiphertexts);
    }

    private static bool IsSensitiveHeaderName(string headerName)
    {
        var canonicalName = CanonicalizeHeaderName(headerName);
        if (PublicHeaderNames.Contains(canonicalName))
        {
            return false;
        }

        return SensitiveHeaderTokens.Any(canonicalName.Contains);
    }

    private static bool TryGetPreservedUndecryptableCiphertext(
        Dictionary<string, string>? persistedUndecryptableCiphertexts,
        string headerName,
        out string ciphertext)
    {
        ciphertext = string.Empty;
        if (persistedUndecryptableCiphertexts?.TryGetValue(CanonicalizeHeaderName(headerName), out var preserved) == true)
        {
            ciphertext = preserved;
            return true;
        }

        return false;
    }

    private static string CanonicalizeHeaderName(string headerName)
    {
        var builder = new StringBuilder(headerName.Length);

        foreach (var ch in headerName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private readonly record struct SecretRecoveryResult(AppSettings InMemorySettings, AppSettings PersistedSettings, bool HadHealedSecrets);
    private readonly record struct ProviderRecoveryResult(ProviderSettings InMemoryProvider, ProviderSettings PersistedProvider, bool HadHealedSecrets);
    private readonly record struct HeaderRecoveryResult(
        Dictionary<string, string>? InMemoryHeaders,
        Dictionary<string, string>? PersistedHeaders,
        bool HadHealedSecrets,
        Dictionary<string, string>? PersistedUndecryptableHeaderCiphertexts);
    private readonly record struct SecretValueRecoveryResult(
        string InMemoryValue,
        string PersistedValue,
        bool ShouldHealPersistedValue,
        string? PersistedUndecryptableCiphertext = null);
}
