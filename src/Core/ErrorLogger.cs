using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Bisik.Core;

public sealed partial class ErrorLogger
{
    private const string ErrorLogFileName = "error.log";
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _errorLogPath;
    private readonly object _pathLock;
    private readonly ErrorLoggerTestHooks? _testHooks;

    public ErrorLogger(string? baseDirectory = null)
        : this(baseDirectory, testHooks: null)
    {
    }

    internal ErrorLogger(string? baseDirectory, ErrorLoggerTestHooks? testHooks)
    {
        var rootDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik")
            : baseDirectory;

        _errorLogPath = NormalizeErrorLogPath(rootDirectory);
        _pathLock = PathLocks.GetOrAdd(_errorLogPath, static _ => new object());
        _testHooks = testHooks;
    }

    public void Log(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

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

        _testHooks?.BeforeAcquireLock?.Invoke();

        lock (_pathLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_errorLogPath)!);

            using var stream = new FileStream(_errorLogPath, FileMode.Append, FileAccess.Write, FileShare.None);
            _testHooks?.AfterOpenStream?.Invoke();

            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(builder.ToString());
        }
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

            var sanitizedLine = JsonSecretPropertyRegex().Replace(line, "\"$1\":\"[REDACTED]\"");
            sanitizedLine = SecretAssignmentRegex().Replace(sanitizedLine, "$1[REDACTED]");
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

    [GeneratedRegex(@"""((?:authorization|api[-_ ]?key|token|secret|password))""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretPropertyRegex();

    [GeneratedRegex(@"^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+\S+\s+HTTP/\d(?:\.\d)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestLineRegex();

    [GeneratedRegex(@"^HTTP/\d(?:\.\d)?\s+\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatusLineRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9\-]*\s*:\s*.+$")]
    private static partial Regex HeaderLineRegex();

    private static string NormalizeErrorLogPath(string rootDirectory)
    {
        return Path.GetFullPath(Path.Combine(rootDirectory, ErrorLogFileName));
    }
}

internal sealed class ErrorLoggerTestHooks
{
    public Action? BeforeAcquireLock { get; init; }

    public Action? AfterOpenStream { get; init; }
}
