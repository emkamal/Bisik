using System.Text.Json.Serialization;

namespace Bisik.Models;

public sealed record TranscriptionEntry
{
    public TranscriptionEntry(
        DateTimeOffset Timestamp,
        string Text,
        string? RawText,
        string Provider,
        string Language,
        long DurationMs,
        string? AudioFilePath)
    {
        this.Timestamp = Timestamp;
        this.Text = Text;
        this.RawText = RawText;
        this.Provider = Provider;
        this.Language = Language;
        this.DurationMs = DurationMs;
        this.AudioFilePath = AudioFilePath;
    }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    // Text is the final history value after optional post-processing.
    [JsonPropertyName("text")]
    public string Text { get; init; }

    [JsonPropertyName("processedText")]
    public string ProcessedText => Text;

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
}
