using Bisik.Core;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class ErrorLoggerTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), "Bisik.ErrorLoggerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Log_should_create_error_log_file()
    {
        var logger = new ErrorLogger(_baseDirectory);

        logger.Log(new InvalidOperationException("boom"));

        File.Exists(Path.Combine(_baseDirectory, "error.log")).Should().BeTrue();
    }

    [Fact]
    public void Log_should_append_exception_messages()
    {
        var logger = new ErrorLogger(_baseDirectory);

        logger.Log(new InvalidOperationException("first failure"));
        logger.Log(new InvalidOperationException("second failure"));

        var log = File.ReadAllText(Path.Combine(_baseDirectory, "error.log"));
        log.Should().Contain("first failure");
        log.Should().Contain("second failure");
    }

    [Fact]
    public void Log_should_include_a_timestamp_and_redact_secret_values()
    {
        var logger = new ErrorLogger(_baseDirectory);
        var exception = new InvalidOperationException("request failed with api_key=super-secret-key");

        logger.Log(exception);

        var log = File.ReadAllText(Path.Combine(_baseDirectory, "error.log"));
        log.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}");
        log.Should().Contain("[REDACTED]");
        log.Should().NotContain("super-secret-key");
    }

    [Fact]
    public void Log_should_redact_header_blocks_from_message_and_stack_trace()
    {
        var logger = new ErrorLogger(_baseDirectory);
        var headerBlock = string.Join(Environment.NewLine,
        [
            "POST /v1/audio/transcriptions HTTP/1.1",
            "Authorization: Bearer top-secret-token",
            "Content-Type: multipart/form-data; boundary=abc123",
            "X-Trace-Id: trace-123",
        ]);

        logger.Log(new ExceptionWithStackTrace($"request failed{Environment.NewLine}{headerBlock}", headerBlock));

        var log = File.ReadAllText(Path.Combine(_baseDirectory, "error.log"));
        log.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}");
        log.Should().Contain("request failed");
        log.Should().Contain("[REDACTED HEADERS]");
        log.Should().NotContain("Authorization: Bearer top-secret-token");
        log.Should().NotContain("Content-Type: multipart/form-data; boundary=abc123");
        log.Should().NotContain("X-Trace-Id: trace-123");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private sealed class ExceptionWithStackTrace(string message, string stackTrace) : Exception(message)
    {
        public override string StackTrace => stackTrace;
    }
}
