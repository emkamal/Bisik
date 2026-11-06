using System.Globalization;
using System.Net;
using System.Text;

namespace Bisik.Core;

// Shared by explicit History and Experiments copying; it never touches the clipboard itself.
public static class TabularExport
{
    public static string Delimited(string[] headers, IEnumerable<string[]> rows, char separator)
    {
        static string Escape(string value, char sep) => value.IndexOfAny([sep, '"', '\r', '\n']) < 0
            ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        return string.Join(Environment.NewLine, rows.Prepend(headers).Select(row => string.Join(separator,
            row.Select(v => Escape(v, separator))))) + Environment.NewLine;
    }
    public static string HtmlClipboard(string[] headers, IEnumerable<string[]> rows)
    {
        var table = new StringBuilder("<table><tr>");
        foreach (var name in headers) table.Append("<th>").Append(WebUtility.HtmlEncode(name)).Append("</th>");
        table.Append("</tr>");
        foreach (var row in rows)
        {
            table.Append("<tr>");
            for (var i = 0; i < row.Length; i++)
            {
                var text = WebUtility.HtmlEncode(row[i]).Replace("\r\n", "\n").Replace('\r', '\n')
                    .Replace("\n", "<br style=\"mso-data-placement:same-cell\">\n");
                table.Append("<td style=\"mso-number-format:'").Append(headers[i].EndsWith("_ms", StringComparison.Ordinal) ? "0" : "\\@")
                    .Append("';white-space:pre-wrap\">").Append(text).Append("</td>");
            }
            table.Append("</tr>");
        }
        table.Append("</table>");
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var start = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var fragment = start + Encoding.UTF8.GetByteCount(before);
        var endFragment = fragment + Encoding.UTF8.GetByteCount(table.ToString());
        var end = endFragment + Encoding.UTF8.GetByteCount(after);
        return string.Format(CultureInfo.InvariantCulture, header, start, end, fragment, endFragment) + before + table + after;
    }
}
