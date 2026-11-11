using System.Text;
using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Microsoft.VisualBasic.FileIO;

namespace Bisik.Tests;

public sealed class HistoryExportTests
{
    private static TranscriptionEntry Entry(string text = "hasil, \"bagus\"\r\nbaris\t🙂") => new(
        DateTimeOffset.Parse("2026-11-06T10:00:00+07:00"), text, "raw\nsource", "test", "id", 400,
        "recording.wav", 100, 300, 1500, JsonSerializer.SerializeToElement(new { prompt = "Keep\nlines" }),
        450, true, 2000);

    [Fact]
    public void Json_keeps_complete_single_objects_and_ordered_multi_row_arrays()
    {
        var entry = Entry();
        using var single = JsonDocument.Parse(HistoryExport.Format([entry], HistoryCopyFormat.Json));
        Assert.Equal(JsonValueKind.Object, single.RootElement.ValueKind);
        Assert.Equal(entry.Text, single.RootElement.GetProperty("text").GetString());
        Assert.Equal(1500, single.RootElement.GetProperty("recording_duration_ms").GetInt64());
        Assert.Equal("Keep\nlines", single.RootElement.GetProperty("configuration_snapshot").GetProperty("prompt").GetString());
        using var multi = JsonDocument.Parse(HistoryExport.Format([entry, Entry("second")], HistoryCopyFormat.Json));
        Assert.Equal(2, multi.RootElement.GetArrayLength());
        Assert.Equal("second", multi.RootElement[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(HistoryCopyFormat.Csv, ",")]
    [InlineData(HistoryCopyFormat.Table, "\t")]
    public void Delimited_exports_round_trip_multiline_quotes_tabs_unicode_and_metadata(HistoryCopyFormat format, string delimiter)
    {
        var entry = Entry();
        using var parser = new TextFieldParser(new StringReader(HistoryExport.Format([entry, Entry("second")], format)))
        {
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(delimiter);
        var headers = parser.ReadFields()!;
        var fields = parser.ReadFields()!;
        Assert.Equal(headers.Length, fields.Length);
        var row = headers.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second);
        Assert.Equal(entry.Text.Replace("\r\n", "\n"), row["text"].Replace("\r\n", "\n"));
        Assert.Equal(entry.RawText, row["rawText"]);
        Assert.Equal("1500", row["recording_duration_ms"]);
        Assert.Equal("2000", row["capture_to_result_duration_ms"]);
        Assert.Equal("true", row["post_processing_applied"]);
        Assert.Equal("Keep\nlines", JsonDocument.Parse(row["configuration_snapshot"]).RootElement.GetProperty("prompt").GetString());
        Assert.Equal(headers.Length, parser.ReadFields()!.Length);
        Assert.True(parser.EndOfData);
    }

    [Fact]
    public void Html_clipboard_uses_utf8_offsets_and_preserves_multiline_cells_as_text()
    {
        var html = HistoryExport.HtmlClipboard([Entry("<tag>\t🙂\nnext")]);
        var offsets = html.Split("\r\n").Take(5).Skip(1)
            .Select(line => line.Split(':')).ToDictionary(p => p[0], p => int.Parse(p[1]));
        var bytes = Encoding.UTF8.GetBytes(html);
        var fragment = Encoding.UTF8.GetString(bytes[offsets["StartFragment"]..offsets["EndFragment"]]);
        Assert.StartsWith("<table>", fragment);
        Assert.EndsWith("</table>", fragment);
        Assert.Contains("&lt;tag&gt;\t", fragment);
        Assert.Contains("<br style=\"mso-data-placement:same-cell\">", fragment);
        Assert.Contains("mso-number-format:'\\@'", fragment);
        Assert.Contains("mso-number-format:'0';white-space:pre-wrap\">1500</td>", fragment);
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes[offsets["StartHTML"]..]));
        Assert.Equal(bytes.Length, offsets["EndHTML"]);
        Assert.Equal(16, fragment.Split("<td ").Length - 1);
    }

    [Fact]
    public void Missing_optional_values_stay_absent_in_json_and_blank_in_tables()
    {
        var old = Entry() with { RecordingDurationMs = null, ConfigurationSnapshot = null,
            StopToResultDurationMs = null, CaptureToResultDurationMs = null, PostProcessingApplied = null };
        using var json = JsonDocument.Parse(HistoryExport.Format([old], HistoryCopyFormat.Json));
        Assert.False(json.RootElement.TryGetProperty("configuration_snapshot", out _));
        Assert.False(json.RootElement.TryGetProperty("recording_duration_ms", out _));
        using var parser = new TextFieldParser(new StringReader(HistoryExport.Format([old], HistoryCopyFormat.Table)));
        parser.SetDelimiters("\t");
        var headers = parser.ReadFields()!;
        var fields = parser.ReadFields()!;
        Assert.Empty(fields[Array.IndexOf(headers, "recording_duration_ms")]);
        Assert.Empty(fields[Array.IndexOf(headers, "configuration_snapshot")]);
    }
}
