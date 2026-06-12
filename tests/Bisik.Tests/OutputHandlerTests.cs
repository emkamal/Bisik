using System.Reflection;
using Bisik.Core;
using Bisik.Models;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class OutputHandlerTests
{
    [Fact]
    public async Task Dispatch_async_should_route_successful_text_to_auto_type_sink()
    {
        var sinks = new CapturedSinks();
        var handler = CreateHandler(sinks);
        var result = new TranscriptionResult(
            Succeeded: true,
            OutputText: "typed text",
            RawText: "raw text");

        await InvokeDispatchAsync(handler, result, OutputMode.AutoType);

        sinks.AutoTyped.Should().ContainSingle().Which.Should().Be("typed text");
        sinks.Clipboard.Should().BeEmpty();
        sinks.Popups.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatch_async_should_route_successful_text_to_clipboard_sink()
    {
        var sinks = new CapturedSinks();
        var handler = CreateHandler(sinks);
        var result = new TranscriptionResult(
            Succeeded: true,
            OutputText: "clipboard text",
            RawText: "raw text");

        await InvokeDispatchAsync(handler, result, OutputMode.Clipboard);

        sinks.AutoTyped.Should().BeEmpty();
        sinks.Clipboard.Should().ContainSingle().Which.Should().Be("clipboard text");
        sinks.Popups.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatch_async_should_route_successful_text_to_popup_sink()
    {
        var sinks = new CapturedSinks();
        var handler = CreateHandler(sinks);
        var result = new TranscriptionResult(
            Succeeded: true,
            OutputText: "popup text",
            RawText: "raw text");

        await InvokeDispatchAsync(handler, result, OutputMode.Popup);

        sinks.AutoTyped.Should().BeEmpty();
        sinks.Clipboard.Should().BeEmpty();
        sinks.Popups.Should().ContainSingle().Which.Should().Be("popup text");
    }

    [Fact]
    public async Task Dispatch_async_should_do_nothing_when_transcription_failed()
    {
        var sinks = new CapturedSinks();
        var handler = CreateHandler(sinks);
        var result = new TranscriptionResult(
            Succeeded: false,
            OutputText: string.Empty,
            RawText: string.Empty,
            ErrorMessage: "provider failed");

        await InvokeDispatchAsync(handler, result, OutputMode.AutoType);
        await InvokeDispatchAsync(handler, result, OutputMode.Clipboard);
        await InvokeDispatchAsync(handler, result, OutputMode.Popup);

        sinks.AutoTyped.Should().BeEmpty();
        sinks.Clipboard.Should().BeEmpty();
        sinks.Popups.Should().BeEmpty();
    }

    private static object CreateHandler(CapturedSinks sinks)
    {
        var handlerType = RequireBisikType("Bisik.Core.OutputHandler");
        var constructor = handlerType.GetConstructor(
            [
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Action<string>),
            ]);

        if (constructor is null)
        {
            throw new XunitException("Expected OutputHandler constructor to accept (Action<string>, Action<string>, Action<string>).");
        }

        return constructor.Invoke(
            [
                (Action<string>)sinks.AutoTyped.Add,
                (Action<string>)sinks.Clipboard.Add,
                (Action<string>)sinks.Popups.Add,
            ]);
    }

    private static async Task InvokeDispatchAsync(object handler, TranscriptionResult result, OutputMode outputMode)
    {
        var method = handler.GetType().GetMethod(
            "DispatchAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(TranscriptionResult), typeof(OutputMode), typeof(CancellationToken)],
            modifiers: null);

        if (method is null)
        {
            throw new XunitException("Expected OutputHandler.DispatchAsync(TranscriptionResult, OutputMode, CancellationToken).");
        }

        var task = method.Invoke(handler, [result, outputMode, CancellationToken.None]);
        task.Should().BeAssignableTo<Task>();
        await (Task)task!;
    }

    private static Type RequireBisikType(string fullName)
    {
        return typeof(AppSettings).Assembly.GetType(fullName) ??
            throw new XunitException($"Expected type '{fullName}' to exist in the Bisik assembly.");
    }

    private sealed class CapturedSinks
    {
        public List<string> AutoTyped { get; } = [];

        public List<string> Clipboard { get; } = [];

        public List<string> Popups { get; } = [];
    }
}
