using System.Text;
using System.Text.RegularExpressions;

namespace Bisik.Core;

public sealed partial class ErrorLogger
{
    private const string ErrorLogFileName = "error.log";
    private readonly string _errorLogPath;

    public ErrorLogger(string? baseDirectory = null)
    {
        var rootDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik")
            : baseDirectory;

        _errorLogPath = Path.Combine(rootDirectory, ErrorLogFileName);
    }

    public void Log(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Directory.CreateDirectory(Path.GetDirectoryName(_errorLogPath)!);

        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.UtcNow.ToString("O"));
        builder.Append(" | ");
        builder.Append(exception.GetType().FullName);
        builder.Append(" | ");
        builder.AppendLine(Sanitize(exception.Message));

        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            builder.AppendLine(Sanitize(exception.StackTrace));
        }

        File.AppendAllText(_errorLogPath, builder.ToString(), Encoding.UTF8);
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sanitizedLines = new List<string>(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (IsRequestLine(line) && HasFollowingHeaderLine(lines, index + 1))
            {
                sanitizedLines.Add("[REDACTED HEADERS]");

                while (index + 1 < lines.Length && IsHeaderLine(lines[index + 1]))
                {
                    index++;
                }

                continue;
            }

            if (IsHeaderLine(line))
            {
                sanitizedLines.Add("[REDACTED HEADERS]");

                while (index + 1 < lines.Length && IsHeaderLine(lines[index + 1]))
                {
                    index++;
                }

                continue;
            }

            var sanitizedLine = SecretAssignmentRegex().Replace(line, "$1[REDACTED]");
            sanitizedLine = BearerTokenRegex().Replace(sanitizedLine, "$1[REDACTED]");
            sanitizedLines.Add(sanitizedLine);
        }

        return string.Join(Environment.NewLine, sanitizedLines);
    }

    private static bool HasFollowingHeaderLine(string[] lines, int startIndex)
    {
        return startIndex < lines.Length && IsHeaderLine(lines[startIndex]);
    }

    private static bool IsRequestLine(string line)
    {
        return RequestLineRegex().IsMatch(line) || StatusLineRegex().IsMatch(line);
    }

    private static bool IsHeaderLine(string line)
    {
        return HeaderLineRegex().IsMatch(line);
    }

    [GeneratedRegex(@"((?:authorization|api[-_ ]?key|token|secret|password)\s*[:=]\s*)([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(bearer\s+)([^\s\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+\S+\s+HTTP/\d(?:\.\d)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestLineRegex();

    [GeneratedRegex(@"^HTTP/\d(?:\.\d)?\s+\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatusLineRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9\-]*\s*:\s*.+$")]
    private static partial Regex HeaderLineRegex();
}
