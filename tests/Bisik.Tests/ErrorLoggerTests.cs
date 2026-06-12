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
        var exception = new InvalidOperationException("Authorization: Bearer super-secret-key");

        logger.Log(exception);

        var log = File.ReadAllText(Path.Combine(_baseDirectory, "error.log"));
        log.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}");
        log.Should().Contain("[REDACTED]");
        log.Should().NotContain("super-secret-key");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }
}
