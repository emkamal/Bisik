using System.Reflection;
using Bisik.Models;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class HotkeyManagerTests
{
    [Fact]
    public void Push_to_talk_key_down_should_start_recording_only_once_per_hold()
    {
        var manager = CreateManager();

        var firstPress = InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.PushToTalk]);
        var repeatedPress = InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.PushToTalk]);

        GetEnumName(firstPress).Should().Be("StartRecording");
        GetEnumName(repeatedPress).Should().Be("None");
    }

    [Fact]
    public void Push_to_talk_key_release_should_stop_and_transcribe_after_active_hold()
    {
        var manager = CreateManager();
        InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.PushToTalk]);

        var release = InvokeInstance(
            manager,
            "HandleKeyUp",
            [typeof(TriggerMode)],
            [TriggerMode.PushToTalk]);

        GetEnumName(release).Should().Be("StopRecordingAndTranscribe");
    }

    [Fact]
    public void Toggle_key_down_should_start_recording_on_first_press()
    {
        var manager = CreateManager();

        var action = InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.Toggle]);

        GetEnumName(action).Should().Be("StartRecording");
    }

    [Fact]
    public void Toggle_key_down_should_stop_and_transcribe_on_second_press()
    {
        var manager = CreateManager();
        InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.Toggle]);

        var action = InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.Toggle]);

        GetEnumName(action).Should().Be("StopRecordingAndTranscribe");
    }

    [Fact]
    public void Vad_key_down_should_toggle_voice_activity_listening()
    {
        var manager = CreateManager();

        var enable = InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.Vad]);
        var disable = InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.Vad]);

        GetEnumName(enable).Should().Be("StartVoiceActivityDetection");
        GetEnumName(disable).Should().Be("StopVoiceActivityDetection");
    }

    [Fact]
    public void Vad_voice_activity_actions_should_start_and_stop_recording_only_while_listening()
    {
        var manager = CreateManager();

        var ignoredBeforeEnable = InvokeInstance(
            manager,
            "HandleVoiceActivityAction",
            [typeof(Bisik.Core.VoiceActivityAction)],
            [Bisik.Core.VoiceActivityAction.StartRecording]);

        InvokeInstance(
            manager,
            "HandleKeyDown",
            [typeof(TriggerMode)],
            [TriggerMode.Vad]);

        var start = InvokeInstance(
            manager,
            "HandleVoiceActivityAction",
            [typeof(Bisik.Core.VoiceActivityAction)],
            [Bisik.Core.VoiceActivityAction.StartRecording]);
        var stop = InvokeInstance(
            manager,
            "HandleVoiceActivityAction",
            [typeof(Bisik.Core.VoiceActivityAction)],
            [Bisik.Core.VoiceActivityAction.StopRecording]);

        GetEnumName(ignoredBeforeEnable).Should().Be("None");
        GetEnumName(start).Should().Be("StartRecording");
        GetEnumName(stop).Should().Be("StopRecordingAndTranscribe");
    }

    [Fact]
    public void Parse_hotkey_should_round_trip_default_ctrl_win_binding()
    {
        var hotkey = InvokeStatic("ParseHotkey", [typeof(string)], ["Ctrl+Win"]);

        var formatted = InvokeStatic("FormatHotkey", [hotkey.GetType()], [hotkey]);

        formatted.Should().BeOfType<string>();
        formatted.Should().Be("Ctrl+Win");
    }

    [Fact]
    public void Parse_hotkey_should_round_trip_ctrl_shift_space_binding()
    {
        var hotkey = InvokeStatic("ParseHotkey", [typeof(string)], ["Ctrl+Shift+Space"]);

        var formatted = InvokeStatic("FormatHotkey", [hotkey.GetType()], [hotkey]);

        formatted.Should().BeOfType<string>();
        formatted.Should().Be("Ctrl+Shift+Space");
    }

    [Fact]
    public void Validate_hotkey_should_reject_bindings_without_any_modifier()
    {
        var hotkey = InvokeStatic("ParseHotkey", [typeof(string)], ["Space"]);

        var isSupported = InvokeStatic("IsSupportedHotkey", [hotkey.GetType()], [hotkey]);

        isSupported.Should().BeOfType<bool>();
        isSupported.Should().Be(false);
    }

    [Fact]
    public void Parse_hotkey_should_reject_unknown_tokens()
    {
        try
        {
            InvokeStatic("ParseHotkey", [typeof(string)], ["Ctrl+Banana"]);
            throw new XunitException("Expected ParseHotkey to reject unknown tokens.");
        }
        catch (TargetInvocationException exception)
        {
            exception.InnerException.Should().BeOfType<FormatException>();
        }
    }

    private static object InvokeStatic(string methodName, Type[] parameterTypes, object?[] arguments)
    {
        var managerType = RequireBisikType("Bisik.Core.HotkeyManager");
        var method = managerType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        if (method is null)
        {
            throw new XunitException($"Expected HotkeyManager.{methodName}({string.Join(", ", parameterTypes.Select(type => type.Name))}).");
        }

        return method.Invoke(null, arguments) ??
            throw new XunitException($"Expected HotkeyManager.{methodName} to return a value.");
    }

    private static object CreateManager()
    {
        var managerType = RequireBisikType("Bisik.Core.HotkeyManager");
        var registrarType = RequireBisikType("Bisik.Core.IHotkeyRegistrar");
        var fakeRegistrar = new FakeRegistrar();
        var constructor = managerType.GetConstructor([registrarType, typeof(IntPtr)]);
        if (constructor is null)
        {
            throw new XunitException("Expected HotkeyManager constructor to accept (IHotkeyRegistrar, IntPtr).");
        }

        return constructor.Invoke([fakeRegistrar, IntPtr.Zero]);
    }

    private static object InvokeInstance(object instance, string methodName, Type[] parameterTypes, object?[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        if (method is null)
        {
            throw new XunitException($"Expected {instance.GetType().Name}.{methodName}({string.Join(", ", parameterTypes.Select(type => type.Name))}).");
        }

        return method.Invoke(instance, arguments) ??
            throw new XunitException($"Expected {instance.GetType().Name}.{methodName} to return a value.");
    }

    private static string GetEnumName(object value)
    {
        return Enum.GetName(value.GetType(), value) ??
            throw new XunitException("Expected a named enum value.");
    }

    private static Type RequireBisikType(string fullName)
    {
        return typeof(AppSettings).Assembly.GetType(fullName) ??
            throw new XunitException($"Expected type '{fullName}' to exist in the Bisik assembly.");
    }

    private sealed class FakeRegistrar : Bisik.Core.IHotkeyRegistrar
    {
        public bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey)
        {
            return true;
        }

        public bool UnregisterHotKey(IntPtr windowHandle, int id)
        {
            return true;
        }
    }
}
