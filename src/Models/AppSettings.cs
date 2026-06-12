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

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("extraHeaders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ExtraHeaders { get; init; }
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

public sealed record PostProcessorSettings
{
    private const string DefaultSystemPrompt = "You are a transcription editor. Fix grammar, punctuation, and remove filler words. Return only the cleaned text, nothing else.";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("provider")]
    public ProviderSettings Provider { get; init; } = new();

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("presetName")]
    public PostProcessorPreset PresetName { get; init; } = PostProcessorPreset.CleanUp;

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;
}

public sealed record AppSettings
{
    [JsonPropertyName("triggerMode")]
    public TriggerMode TriggerMode { get; init; } = TriggerMode.PushToTalk;

    [JsonPropertyName("hotkey")]
    public string Hotkey { get; init; } = "Ctrl+Win";

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

    [JsonPropertyName("provider")]
    public ProviderSettings Provider { get; init; } = new();

    [JsonPropertyName("audio")]
    public AudioSettings Audio { get; init; } = new();

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
