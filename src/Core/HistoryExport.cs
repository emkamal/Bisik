using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

public enum HistoryCopyFormat { Json, Csv, Table }

public static class HistoryExport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly (string Name, Func<TranscriptionEntry, string> Value)[] Columns =
    [
        ("timestamp", e => e.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
        ("rawText", e => e.RawText ?? string.Empty),
        ("text", e => e.Text),
        ("recording_duration_ms", e => Number(e.RecordingDurationMs)),
        ("stop_to_result_duration_ms", e => Number(e.StopToResultDurationMs)),
        ("capture_to_result_duration_ms", e => Number(e.CaptureToResultDurationMs)),
        ("duration_ms", e => Number(e.DurationMs)),
        ("transcription_duration_ms", e => Number(e.TranscriptionDurationMs)),
        ("post_processing_duration_ms", e => Number(e.PostProcessingDurationMs)),
        ("post_processing_applied", e => e.PostProcessingApplied?.ToString().ToLowerInvariant() ?? string.Empty),
        ("provider", e => e.Provider),
        ("language", e => e.Language),
        ("configuration_snapshot", e => ConfigurationText(e.ConfigurationSnapshot)),
        ("audio_file_path", e => e.AudioFilePath ?? string.Empty),
        ("id", e => e.Id?.ToString() ?? string.Empty),
        ("status", e => e.Status.ToString()),
    ];

    public static string Format(IReadOnlyList<TranscriptionEntry> entries, HistoryCopyFormat format) => format switch
    {
        // A single object is convenient to hand to an agent; multi-row exports are JSON arrays.
        HistoryCopyFormat.Json => entries.Count == 1
            ? JsonSerializer.Serialize(entries[0], JsonOptions) : JsonSerializer.Serialize(entries, JsonOptions),
        HistoryCopyFormat.Csv => Delimited(entries, ','),
        HistoryCopyFormat.Table => Delimited(entries, '\t'),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string ConfigurationText(JsonElement? configuration) => configuration is { } value
        ? JsonSerializer.Serialize(value) : string.Empty;

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Delimited(IReadOnlyList<TranscriptionEntry> entries, char separator) =>
        TabularExport.Delimited(Columns.Select(c => c.Name).ToArray(), entries.Select(e => Columns.Select(c => c.Value(e)).ToArray()), separator);

    public static string HtmlClipboard(IReadOnlyList<TranscriptionEntry> entries) =>
        TabularExport.HtmlClipboard(Columns.Select(c => c.Name).ToArray(), entries.Select(e => Columns.Select(c => c.Value(e)).ToArray()));
}
