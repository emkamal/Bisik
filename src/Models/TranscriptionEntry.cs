using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bisik.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HistoryEntryStatus { Transcribing, CleaningUp, Completed, NoSpeech, Cancelled, Failed }

public sealed record TranscriptionEntry
{
    public TranscriptionEntry(
        DateTimeOffset Timestamp,
        string Text,
        string? RawText,
        string Provider,
        string Language,
        long DurationMs,
        string? AudioFilePath,
        long TranscriptionDurationMs = 0,
        long PostProcessingDurationMs = 0,
        long? RecordingDurationMs = null,
        JsonElement? ConfigurationSnapshot = null,
        long? StopToResultDurationMs = null,
        bool? PostProcessingApplied = null,
        long? CaptureToResultDurationMs = null,
        Guid? Id = null,
        HistoryEntryStatus Status = HistoryEntryStatus.Completed)
    {
        this.Timestamp = Timestamp;
        this.Text = Text;
        this.RawText = RawText;
        this.Provider = Provider;
        this.Language = Language;
        this.DurationMs = DurationMs;
        this.AudioFilePath = AudioFilePath;
        this.TranscriptionDurationMs = TranscriptionDurationMs;
        this.PostProcessingDurationMs = PostProcessingDurationMs;
        this.RecordingDurationMs = RecordingDurationMs;
        this.ConfigurationSnapshot = ConfigurationSnapshot;
        this.StopToResultDurationMs = StopToResultDurationMs;
        this.PostProcessingApplied = PostProcessingApplied;
        this.CaptureToResultDurationMs = CaptureToResultDurationMs;
        this.Id = Id;
        this.Status = Status;
    }

    // New dictations keep the same identity through audio, recognition and cleanup updates.
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Id { get; init; }
    [JsonPropertyName("status")]
    public HistoryEntryStatus Status { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    // Text is the final history value after optional post-processing.
    [JsonPropertyName("text")]
    public string Text { get; init; }

    [JsonPropertyName("rawText")]
    public string? RawText { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; }

    [JsonPropertyName("language")]
    public string Language { get; init; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; init; }

    [JsonPropertyName("audio_file_path")]
    public string? AudioFilePath { get; init; }

    [JsonPropertyName("transcription_duration_ms")]
    public long TranscriptionDurationMs { get; init; }

    [JsonPropertyName("post_processing_duration_ms")]
    public long PostProcessingDurationMs { get; init; }

    // Audio length includes pauses and VAD pre-roll; it is not a speech-only estimate.
    [JsonPropertyName("recording_duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RecordingDurationMs { get; init; }

    [JsonPropertyName("configuration_snapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ConfigurationSnapshot { get; init; }

    // Result availability before history persistence, sound or output dispatch.
    [JsonPropertyName("stop_to_result_duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StopToResultDurationMs { get; init; }

    [JsonPropertyName("post_processing_applied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PostProcessingApplied { get; init; }

    [JsonPropertyName("capture_to_result_duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CaptureToResultDurationMs { get; init; }
}
