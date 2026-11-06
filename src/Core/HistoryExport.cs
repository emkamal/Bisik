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

    private static string Delimited(IReadOnlyList<TranscriptionEntry> entries, char separator)
    {
        static string Escape(string value, char separator) => value.IndexOfAny([separator, '"', '\r', '\n']) < 0
            ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        var result = new StringBuilder();
        result.AppendLine(string.Join(separator, Columns.Select(c => c.Name)));
        foreach (var entry in entries)
            result.AppendLine(string.Join(separator, Columns.Select(c => Escape(c.Value(entry), separator))));
        return result.ToString();
    }

    public static string HtmlClipboard(IReadOnlyList<TranscriptionEntry> entries)
    {
        // Excel's HTML clipboard preserves embedded tabs/newlines within their original cell.
        var table = new StringBuilder("<table><tr>");
        foreach (var column in Columns) table.Append("<th>").Append(WebUtility.HtmlEncode(column.Name)).Append("</th>");
        table.Append("</tr>");
        foreach (var entry in entries)
        {
            table.Append("<tr>");
            foreach (var column in Columns)
            {
                var text = WebUtility.HtmlEncode(column.Value(entry)).Replace("\r\n", "\n").Replace('\r', '\n')
                    .Replace("\n", "<br style=\"mso-data-placement:same-cell\">\n");
                // Timings remain numbers for Excel analysis; transcripts/prompts remain literal text.
                var numberFormat = column.Name.EndsWith("_ms", StringComparison.Ordinal) ? "0" : "\\@";
                table.Append("<td style=\"mso-number-format:'").Append(numberFormat)
                    .Append("';white-space:pre-wrap\">").Append(text).Append("</td>");
            }
            table.Append("</tr>");
        }
        table.Append("</table>");
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var startHtml = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(table.ToString());
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return string.Format(CultureInfo.InvariantCulture, header, startHtml, endHtml, startFragment, endFragment) + before + table + after;
    }
}
