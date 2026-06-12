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
        var sanitized = SecretAssignmentRegex().Replace(value, "$1[REDACTED]");
        sanitized = BearerTokenRegex().Replace(sanitized, "$1[REDACTED]");
        return sanitized;
    }

    [GeneratedRegex(@"((?:authorization|api[-_ ]?key|token|secret|password)\s*[:=]\s*)([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(bearer\s+)([^\s\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();
}
