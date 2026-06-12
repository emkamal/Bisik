using System.Reflection;
using Bisik.Models;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class AudioRecorderTests
{
    [Fact]
    public void Captured_pcm16_buffer_should_raise_normalized_audio_level_event()
    {
        var recorderType = RequireBisikType("Bisik.Core.AudioRecorder");
        var eventArgsType = RequireBisikType("Bisik.Core.AudioLevelChangedEventArgs");
        var recorder = Activator.CreateInstance(recorderType) ??
            throw new XunitException("Expected AudioRecorder to expose a public parameterless constructor.");
        double? observedLevel = null;

        var handlerMethod = typeof(AudioRecorderTests).GetMethod(
            nameof(HandleAudioLevelChanged),
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new XunitException("Expected audio level handler method to exist.");
        _capturedLevel = null;

        var delegateType = typeof(EventHandler<>).MakeGenericType(eventArgsType);
        var handler = Delegate.CreateDelegate(delegateType, handlerMethod);
        var levelEvent = recorderType.GetEvent("AudioLevelChanged") ??
            throw new XunitException("Expected AudioRecorder.AudioLevelChanged event.");
        levelEvent.AddEventHandler(recorder, handler);

        InvokeRecorderInstanceMethod(
            recorder,
            "PublishAudioLevel",
            [typeof(byte[]), typeof(int)],
            [CreatePcm16Buffer(short.MaxValue, 0), 4]);

        observedLevel = _capturedLevel;
        observedLevel.Should().NotBeNull();
        observedLevel.Should().BeApproximately(1d, 0.001d);
    }

    private static double? _capturedLevel;

    [Fact]
    public void Calculate_level_should_return_zero_for_silent_pcm16_samples()
    {
        var level = InvokeCalculateLevel([0, 0, 0, 0]);

        level.Should().Be(0d);
    }

    [Fact]
    public void Calculate_level_should_normalize_peak_pcm16_samples_to_one()
    {
        var level = InvokeCalculateLevel([short.MaxValue, short.MinValue]);

        level.Should().BeApproximately(1d, 0.001d);
    }

    [Fact]
    public void Should_stop_for_silence_should_require_detected_speech_and_minimum_silence_window()
    {
        var settings = new AudioSettings
        {
            VadSilenceThreshold = 0.01,
            VadMinSpeechMs = 300,
        };

        var beforeSpeech = InvokeShouldStopForSilence(settings, currentLevel: 0.001d, silenceDurationMs: 500, speechDetected: false);
        var tooSoon = InvokeShouldStopForSilence(settings, currentLevel: 0.001d, silenceDurationMs: 250, speechDetected: true);
        var readyToStop = InvokeShouldStopForSilence(settings, currentLevel: 0.001d, silenceDurationMs: 300, speechDetected: true);

        beforeSpeech.Should().BeFalse();
        tooSoon.Should().BeFalse();
        readyToStop.Should().BeTrue();
    }

    [Fact]
    public void Voice_activity_tracker_should_request_start_after_minimum_speech_window()
    {
        var tracker = CreateVoiceActivityTracker(new AudioSettings
        {
            VadSilenceThreshold = 0.01,
            VadMinSpeechMs = 300,
        });

        var firstTick = InvokeVoiceActivityTracker(tracker, "ProcessLevel", [typeof(double), typeof(int)], [0.02d, 100]);
        var secondTick = InvokeVoiceActivityTracker(tracker, "ProcessLevel", [typeof(double), typeof(int)], [0.02d, 200]);

        GetEnumName(firstTick).Should().Be("None");
        GetEnumName(secondTick).Should().Be("StartRecording");
    }

    [Fact]
    public void Voice_activity_tracker_should_request_stop_after_silence_follows_detected_speech()
    {
        var tracker = CreateVoiceActivityTracker(new AudioSettings
        {
            VadSilenceThreshold = 0.01,
            VadMinSpeechMs = 300,
        });

        InvokeVoiceActivityTracker(tracker, "ProcessLevel", [typeof(double), typeof(int)], [0.02d, 300]);
        var firstSilenceTick = InvokeVoiceActivityTracker(tracker, "ProcessLevel", [typeof(double), typeof(int)], [0.001d, 100]);
        var secondSilenceTick = InvokeVoiceActivityTracker(tracker, "ProcessLevel", [typeof(double), typeof(int)], [0.001d, 200]);

        GetEnumName(firstSilenceTick).Should().Be("None");
        GetEnumName(secondSilenceTick).Should().Be("StopRecording");
    }

    [Fact]
    public void Build_output_file_path_should_use_timestamped_name_and_audio_format_extension()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bisik-audio-tests");
        var timestamp = new DateTimeOffset(2026, 6, 12, 7, 8, 9, TimeSpan.Zero);

        var path = InvokeBuildOutputFilePath(directory, AudioFormat.Ogg, timestamp);

        path.Should().Be(Path.Combine(directory, "bisik-20260612-070809.ogg"));
    }

    [Fact]
    public void Transition_should_follow_idle_recording_stopping_lifecycle()
    {
        var idle = ParseEnum("Bisik.Core.RecordingState", "Idle");
        var recording = InvokeTransition(idle, ParseEnum("Bisik.Core.AudioRecorderSignal", "Start"));
        var stopping = InvokeTransition(recording, ParseEnum("Bisik.Core.AudioRecorderSignal", "Stop"));
        var backToIdle = InvokeTransition(stopping, ParseEnum("Bisik.Core.AudioRecorderSignal", "FinalizeStop"));

        GetEnumName(recording).Should().Be("Recording");
        GetEnumName(stopping).Should().Be("Stopping");
        GetEnumName(backToIdle).Should().Be("Idle");
    }

    private static double InvokeCalculateLevel(short[] samples)
    {
        var result = InvokeAudioRecorderLogic("CalculateLevel", [typeof(short[])], [samples]);
        result.Should().BeOfType<double>();
        return (double)result!;
    }

    private static bool InvokeShouldStopForSilence(
        AudioSettings settings,
        double currentLevel,
        int silenceDurationMs,
        bool speechDetected)
    {
        var result = InvokeAudioRecorderLogic(
            "ShouldStopForSilence",
            [typeof(AudioSettings), typeof(double), typeof(int), typeof(bool)],
            [settings, currentLevel, silenceDurationMs, speechDetected]);
        result.Should().BeOfType<bool>();
        return (bool)result!;
    }

    private static string InvokeBuildOutputFilePath(string directory, AudioFormat format, DateTimeOffset timestamp)
    {
        var result = InvokeAudioRecorderLogic(
            "BuildOutputFilePath",
            [typeof(string), typeof(AudioFormat), typeof(DateTimeOffset)],
            [directory, format, timestamp]);
        result.Should().BeOfType<string>();
        return (string)result!;
    }

    private static object InvokeTransition(object currentState, object signal)
    {
        var result = InvokeAudioRecorderLogic(
            "Transition",
            [currentState.GetType(), signal.GetType()],
            [currentState, signal]);
        result.Should().NotBeNull();
        return result!;
    }

    private static object CreateVoiceActivityTracker(AudioSettings settings)
    {
        var trackerType = RequireBisikType("Bisik.Core.VoiceActivityTracker");
        return Activator.CreateInstance(trackerType, settings) ??
            throw new XunitException("Expected VoiceActivityTracker to expose a public constructor accepting AudioSettings.");
    }

    private static object InvokeVoiceActivityTracker(object instance, string methodName, Type[] parameterTypes, object?[] arguments)
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

        var result = method.Invoke(instance, arguments);
        result.Should().NotBeNull();
        return result!;
    }

    private static void InvokeRecorderInstanceMethod(object instance, string methodName, Type[] parameterTypes, object?[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        if (method is null)
        {
            throw new XunitException($"Expected {instance.GetType().Name}.{methodName}({string.Join(", ", parameterTypes.Select(type => type.Name))}).");
        }

        method.Invoke(instance, arguments);
    }

    private static object? InvokeAudioRecorderLogic(string methodName, Type[] parameterTypes, object?[] arguments)
    {
        var logicType = RequireBisikType("Bisik.Core.AudioRecorderLogic");
        var method = logicType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        if (method is null)
        {
            throw new XunitException($"Expected AudioRecorderLogic.{methodName}({string.Join(", ", parameterTypes.Select(type => type.Name))}).");
        }

        return method.Invoke(null, arguments);
    }

    private static object ParseEnum(string fullName, string memberName)
    {
        var enumType = RequireBisikType(fullName);
        if (!enumType.IsEnum)
        {
            throw new XunitException($"Expected '{fullName}' to be an enum.");
        }

        return Enum.Parse(enumType, memberName, ignoreCase: false);
    }

    private static string GetEnumName(object value)
    {
        return Enum.GetName(value.GetType(), value) ??
            throw new XunitException("Expected a named enum value.");
    }

    private static void HandleAudioLevelChanged(object? sender, object eventArgs)
    {
        var levelProperty = eventArgs.GetType().GetProperty("Level") ??
            throw new XunitException("Expected AudioLevelChangedEventArgs.Level property.");
        _capturedLevel = (double?)levelProperty.GetValue(eventArgs);
    }

    private static byte[] CreatePcm16Buffer(params short[] samples)
    {
        var buffer = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);
        return buffer;
    }

    private static Type RequireBisikType(string fullName)
    {
        return typeof(AppSettings).Assembly.GetType(fullName) ??
            throw new XunitException($"Expected type '{fullName}' to exist in the Bisik assembly.");
    }
}
