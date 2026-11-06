using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bisik.Models;

[JsonConverter(typeof(EnumMemberJsonConverter<TriggerMode>))]
public enum TriggerMode
{
    [EnumMember(Value = "push-to-talk")]
    PushToTalk,
    [EnumMember(Value = "toggle")]
    Toggle,
    [EnumMember(Value = "vad")]
    Vad,
}

[JsonConverter(typeof(EnumMemberJsonConverter<OutputMode>))]
public enum OutputMode
{
    [EnumMember(Value = "auto-type")]
    AutoType,
    [EnumMember(Value = "clipboard")]
    Clipboard,
    [EnumMember(Value = "popup")]
    Popup,
    [EnumMember(Value = "realtime-auto-type")]
    RealtimeAutoType,
}

[JsonConverter(typeof(EnumMemberJsonConverter<IndicatorStyle>))]
public enum IndicatorStyle
{
    [EnumMember(Value = "dot-near-cursor")]
    DotNearCursor,
    [EnumMember(Value = "bar-overlay")]
    BarOverlay,
    [EnumMember(Value = "tray-icon-only")]
    TrayIconOnly,
}

[JsonConverter(typeof(EnumMemberJsonConverter<ProviderType>))]
public enum ProviderType
{
    [EnumMember(Value = "azure-openai")]
    AzureOpenAi,
    [EnumMember(Value = "openai")]
    OpenAi,
    [EnumMember(Value = "openrouter")]
    OpenRouter,
    [EnumMember(Value = "local-whisper")]
    LocalWhisper,
    [EnumMember(Value = "custom-endpoint")]
    CustomEndpoint,
}

[JsonConverter(typeof(EnumMemberJsonConverter<AudioFormat>))]
public enum AudioFormat
{
    [EnumMember(Value = "wav")]
    Wav,
    [EnumMember(Value = "mp3")]
    Mp3,
    [EnumMember(Value = "ogg")]
    Ogg,
}

[JsonConverter(typeof(EnumMemberJsonConverter<PostProcessorPreset>))]
public enum PostProcessorPreset
{
    [EnumMember(Value = "clean-up")]
    CleanUp,
    [EnumMember(Value = "homogenize-to-english")]
    HomogenizeToEnglish,
    [EnumMember(Value = "homogenize-to-indonesian")]
    HomogenizeToIndonesian,
    [EnumMember(Value = "translate-to-language")]
    TranslateToLanguage,
    [EnumMember(Value = "custom")]
    Custom,
}

public sealed record ProviderSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecognitionContext? RecognitionContext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecognitionCapability? RecognitionCapability { get; init; }
    [JsonPropertyName("type")]
    public ProviderType Type { get; init; } = ProviderType.AzureOpenAi;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("deploymentName")]
    public string DeploymentName { get; init; } = string.Empty;

    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "2024-06-01";

    [JsonPropertyName("azureRealtime")]
    public bool AzureRealtime { get; init; }

    [JsonPropertyName("azureV1")]
    public bool AzureV1 { get; init; }

    [JsonPropertyName("transcriptionDelay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranscriptionDelay { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("extraHeaders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ExtraHeaders { get; init; }

    [JsonIgnore]
    internal string? PersistedUndecryptableApiKeyCiphertext { get; init; }

    [JsonIgnore]
    internal string? PersistedUndecryptableEndpointCiphertext { get; init; }

    [JsonIgnore]
    internal Dictionary<string, string>? PersistedUndecryptableHeaderCiphertexts { get; init; }

    public ProviderSettings ClearApiKey()
    {
        return this with
        {
            ApiKey = string.Empty,
            PersistedUndecryptableApiKeyCiphertext = null,
        };
    }

    public ProviderSettings ClearEndpoint() => this with
    {
        Endpoint = string.Empty,
        PersistedUndecryptableEndpointCiphertext = null,
    };

    public ProviderSettings ClearHeader(string headerName)
    {
        var headers = ExtraHeaders is null
            ? null
            : new Dictionary<string, string>(ExtraHeaders, StringComparer.OrdinalIgnoreCase);
        headers?.Remove(headerName);
        if (headers is { Count: 0 })
        {
            headers = null;
        }

        Dictionary<string, string>? persistedCiphertexts = null;
        if (PersistedUndecryptableHeaderCiphertexts is not null)
        {
            persistedCiphertexts = new Dictionary<string, string>(PersistedUndecryptableHeaderCiphertexts, StringComparer.Ordinal);
            persistedCiphertexts.Remove(CanonicalizeHeaderName(headerName));

            if (persistedCiphertexts.Count == 0)
            {
                persistedCiphertexts = null;
            }
        }

        return this with
        {
            ExtraHeaders = headers,
            PersistedUndecryptableHeaderCiphertexts = persistedCiphertexts,
        };
    }

    private static string CanonicalizeHeaderName(string headerName)
    {
        var chars = headerName.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();

        return new string(chars);
    }
}

public sealed record AudioSettings
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("format")]
    public AudioFormat Format { get; init; } = AudioFormat.Wav;

    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; init; } = 16000;

    [JsonPropertyName("vadSilenceThreshold")]
    public double VadSilenceThreshold { get; init; } = 0.01;

    [JsonPropertyName("vadMinSpeechMs")]
    public int VadMinSpeechMs { get; init; } = 300;
}

[JsonConverter(typeof(PostProcessorSettingsJsonConverter))]
public sealed record PostProcessorSettings
{
    public bool StreamCompletion { get; init; }
    public bool UsePrediction { get; init; }
    public CompletionCapability? CompletionCapability { get; init; }
    public CleanupVocabulary? Vocabulary { get; init; }
    private const string DefaultSystemPrompt = "You are a transcription editor. Fix grammar, punctuation, and remove filler words. Return only the cleaned text, nothing else.";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("provider")]
    public ProviderSettings Provider { get; init; } = new();

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("reasoningCapability")]
    public ReasoningCapability? ReasoningCapability { get; init; }

    [JsonPropertyName("targetLanguage")]
    public string TargetLanguage { get; init; } = string.Empty;

    [JsonPropertyName("presetName")]
    public PostProcessorPreset PresetName { get; init; } = PostProcessorPreset.CleanUp;

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;
}

/// <summary>Explicitly verified effort values, scoped to the exact model/API rather than an alias guess.</summary>
public sealed record ReasoningCapability(string ApiFingerprint, string[] SupportedEfforts)
{
    public static string Fingerprint(PostProcessorSettings settings)
    {
        var p = settings.Provider;
        var identity = string.Join("\n", p.Type, p.Endpoint.TrimEnd('/'), p.ApiVersion, p.AzureV1, p.DeploymentName, settings.Model, p.Model);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    }
    public bool Matches(PostProcessorSettings settings) => SupportedEfforts is { Length: > 0 } && ApiFingerprint == Fingerprint(settings);
}

public sealed record AppSettings
{
    [JsonPropertyName("liveCapture")]
    public bool LiveCapture { get; init; }

    [JsonPropertyName("pauseCommits")]
    public bool PauseCommits { get; init; }

    [JsonPropertyName("speculativeCleanup")]
    public bool SpeculativeCleanup { get; init; }

    [JsonPropertyName("prewarmRealtime")]
    public bool PrewarmRealtime { get; init; }

    [JsonPropertyName("prewarmIdleSeconds")]
    public int PrewarmIdleSeconds { get; init; } = 15;

    // Records are shallowly immutable; copy mutable collections at the session boundary.
    public AppSettings Snapshot() => this with
    {
        QuickLanguages = (string[])QuickLanguages.Clone(),
        Provider = CopyProvider(Provider),
        PostProcessor = PostProcessor with { Provider = CopyProvider(PostProcessor.Provider), Vocabulary = PostProcessor.Vocabulary?.Snapshot(),
            ReasoningCapability = PostProcessor.ReasoningCapability is { } capability
                ? capability with { SupportedEfforts = capability.SupportedEfforts is null ? [] : (string[])capability.SupportedEfforts.Clone() } : null },
    };

    private static ProviderSettings CopyProvider(ProviderSettings provider) => provider with
    {
        RecognitionContext = provider.RecognitionContext?.Snapshot(),
        ExtraHeaders = provider.ExtraHeaders is null ? null : new(provider.ExtraHeaders),
    };

    [JsonPropertyName("triggerMode")]
    public TriggerMode TriggerMode { get; init; } = TriggerMode.Toggle;

    [JsonPropertyName("hotkey")]
    public string Hotkey { get; init; } = "Ctrl+Shift+Space";

    [JsonPropertyName("insertLatestHotkey")]
    public string InsertLatestHotkey { get; init; } = "Ctrl+Alt+Shift+V";

    [JsonPropertyName("fastOutputHotkey")]
    public string FastOutputHotkey { get; init; } = "Ctrl+Alt+Shift+Space";

    [JsonPropertyName("outputMode")]
    public OutputMode OutputMode { get; init; } = OutputMode.AutoType;

    [JsonPropertyName("indicatorStyle")]
    public IndicatorStyle IndicatorStyle { get; init; } = IndicatorStyle.BarOverlay;

    [JsonPropertyName("defaultLanguage")]
    public string DefaultLanguage { get; init; } = "auto";

    [JsonPropertyName("quickLanguages")]
    public string[] QuickLanguages { get; init; } = ["en", "id", "auto"];

    [JsonPropertyName("launchAtStartup")]
    public bool LaunchAtStartup { get; init; }

    [JsonPropertyName("soundEnabled")]
    public bool SoundEnabled { get; init; } = true;

    [JsonPropertyName("historyEnabled")]
    public bool HistoryEnabled { get; init; } = true;

    [JsonPropertyName("historyRecordingDurationEnabled")]
    public bool HistoryRecordingDurationEnabled { get; init; } = true;

    [JsonPropertyName("historyConfigurationEnabled")]
    public bool HistoryConfigurationEnabled { get; init; }

    [JsonPropertyName("provider")]
    public ProviderSettings Provider { get; init; } = new();

    [JsonPropertyName("audio")]
    public AudioSettings Audio { get; init; } = new();

    [JsonPropertyName("alwaysCopyToClipboard")]
    public bool AlwaysCopyToClipboard { get; init; }

    [JsonPropertyName("postProcessor")]
    public PostProcessorSettings PostProcessor { get; init; } = new();
}

internal sealed class EnumMemberJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<string, TEnum> ValueToEnum = BuildValueToEnumMap();
    private static readonly IReadOnlyDictionary<TEnum, string> EnumToValue = BuildEnumToValueMap();

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        if (value is not null && ValueToEnum.TryGetValue(value, out var parsed))
        {
            return parsed;
        }

        throw new JsonException($"Unknown {typeof(TEnum).Name} value '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!EnumToValue.TryGetValue(value, out var serialized))
        {
            throw new JsonException($"No serialized value is defined for {typeof(TEnum).Name}.{value}.");
        }

        writer.WriteStringValue(serialized);
    }

    private static Dictionary<string, TEnum> BuildValueToEnumMap()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);

        foreach (var enumValue in Enum.GetValues<TEnum>())
        {
            map[GetSerializedValue(enumValue)] = enumValue;
        }

        return map;
    }

    private static Dictionary<TEnum, string> BuildEnumToValueMap()
    {
        var map = new Dictionary<TEnum, string>();

        foreach (var enumValue in Enum.GetValues<TEnum>())
        {
            map[enumValue] = GetSerializedValue(enumValue);
        }

        return map;
    }

    private static string GetSerializedValue(TEnum value)
    {
        var memberName = value.ToString();
        var member = typeof(TEnum).GetMember(memberName)[0];
        var attribute = Attribute.GetCustomAttribute(member, typeof(EnumMemberAttribute)) as EnumMemberAttribute;

        return attribute?.Value ?? memberName;
    }
}

internal sealed class PostProcessorSettingsJsonConverter : JsonConverter<PostProcessorSettings>
{
    public override PostProcessorSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var defaults = new PostProcessorSettings();

        var enabled = root.TryGetProperty("enabled", out var enabledElement)
            ? enabledElement.GetBoolean()
            : defaults.Enabled;
        var provider = root.TryGetProperty("provider", out var providerElement)
            ? providerElement.Deserialize<ProviderSettings>(options) ?? defaults.Provider
            : defaults.Provider;
        var model = root.TryGetProperty("model", out var modelElement)
            ? modelElement.GetString() ?? defaults.Model
            : defaults.Model;
        var targetLanguage = root.TryGetProperty("targetLanguage", out var targetLanguageElement)
            ? targetLanguageElement.GetString() ?? defaults.TargetLanguage
            : defaults.TargetLanguage;
        var presetName = root.TryGetProperty("presetName", out var presetNameElement)
            ? presetNameElement.Deserialize<PostProcessorPreset>(options)
            : defaults.PresetName;
        var systemPrompt = root.TryGetProperty("systemPrompt", out var systemPromptElement)
            ? systemPromptElement.GetString() ?? defaults.SystemPrompt
            : defaults.SystemPrompt;

        return new PostProcessorSettings
        {
            Enabled = enabled,
            Provider = provider,
            Model = model,
            ReasoningEffort = root.TryGetProperty("reasoningEffort", out var effort) && effort.ValueKind != JsonValueKind.Null ? effort.GetString() : null,
            ReasoningCapability = root.TryGetProperty("reasoningCapability", out var capability) ? capability.Deserialize<ReasoningCapability>(options) : null,
            TargetLanguage = targetLanguage,
            PresetName = presetName,
            SystemPrompt = systemPrompt,
            Vocabulary = root.TryGetProperty("vocabulary", out var vocabulary) ? vocabulary.Deserialize<CleanupVocabulary>(options) : null,
            StreamCompletion = root.TryGetProperty("streamCompletion", out var streaming) && streaming.GetBoolean(),
            UsePrediction = root.TryGetProperty("usePrediction", out var prediction) && prediction.GetBoolean(),
            CompletionCapability = root.TryGetProperty("completionCapability", out var completionCapability) ? completionCapability.Deserialize<CompletionCapability>(options) : null,
        };
    }

    public override void Write(Utf8JsonWriter writer, PostProcessorSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", value.Enabled);
        if (value.StreamCompletion) writer.WriteBoolean("streamCompletion", true);
        if (value.UsePrediction) writer.WriteBoolean("usePrediction", true);
        if (value.CompletionCapability is not null) { writer.WritePropertyName("completionCapability"); JsonSerializer.Serialize(writer, value.CompletionCapability, options); }
        if (value.Vocabulary is not null) { writer.WritePropertyName("vocabulary"); JsonSerializer.Serialize(writer, value.Vocabulary, options); }
        writer.WritePropertyName("provider");
        JsonSerializer.Serialize(writer, value.Provider, options);
        writer.WriteString("model", value.Model);
        if (value.ReasoningEffort is not null) writer.WriteString("reasoningEffort", value.ReasoningEffort);
        if (value.ReasoningCapability is not null)
        {
            writer.WritePropertyName("reasoningCapability");
            JsonSerializer.Serialize(writer, value.ReasoningCapability, options);
        }

        if (!string.IsNullOrEmpty(value.TargetLanguage))
        {
            writer.WriteString("targetLanguage", value.TargetLanguage);
        }

        writer.WritePropertyName("presetName");
        JsonSerializer.Serialize(writer, value.PresetName, options);
        writer.WriteString("systemPrompt", value.SystemPrompt);
        writer.WriteEndObject();
    }
}
