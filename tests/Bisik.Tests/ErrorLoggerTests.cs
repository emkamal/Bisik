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

    [Fact]
    public void Log_should_redact_json_shaped_secret_values()
    {
        var logger = new ErrorLogger(_baseDirectory);
        var exception = new InvalidOperationException("{\"apiKey\":\"secret-key\",\"token\":\"secret-token\",\"authorization\":\"Bearer super-secret\"}");

        logger.Log(exception);

        var log = File.ReadAllText(Path.Combine(_baseDirectory, "error.log"));
        log.Should().Contain("\"apiKey\":\"[REDACTED]\"");
        log.Should().Contain("\"token\":\"[REDACTED]\"");
        log.Should().Contain("\"authorization\":\"[REDACTED]\"");
        log.Should().NotContain("secret-key");
        log.Should().NotContain("secret-token");
        log.Should().NotContain("super-secret");
    }

    [Fact]
    public async Task Log_should_serialize_parallel_writes_for_the_same_error_log_path()
    {
        const int taskCount = 12;
        var beforeLockBarrier = new Barrier(taskCount);
        var firstWriterOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstWriter = new ManualResetEventSlim(false);
        var firstWriterWaited = 0;

        var hooks = new ErrorLoggerTestHooks
        {
            BeforeAcquireLock = () => beforeLockBarrier.SignalAndWait(TimeSpan.FromSeconds(5)),
            AfterOpenStream = () =>
            {
                if (Interlocked.CompareExchange(ref firstWriterWaited, 1, 0) == 0)
                {
                    firstWriterOpened.TrySetResult();
                    releaseFirstWriter.Wait(TimeSpan.FromSeconds(5));
                }
            },
        };

        var tasks = Enumerable.Range(0, taskCount)
            .Select(index => Task.Run(() =>
            {
                var logger = new ErrorLogger(_baseDirectory, hooks);
                logger.Log(new InvalidOperationException($"parallel failure {index} api_key=secret-{index}"));
            }))
            .ToArray();

        await firstWriterOpened.Task;
        releaseFirstWriter.Set();

        Func<Task> act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        var lines = File.ReadAllLines(Path.Combine(_baseDirectory, "error.log"));
        lines.Should().HaveCount(taskCount);
        lines.Should().OnlyContain(line => line.Contains("parallel failure", StringComparison.Ordinal));
        lines.Count(line => line.Contains("[REDACTED]", StringComparison.Ordinal)).Should().Be(taskCount);

        var log = File.ReadAllText(Path.Combine(_baseDirectory, "error.log"));
        log.Should().NotContain("secret-0");
        log.Should().NotContain("secret-11");
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
