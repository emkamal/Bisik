using System.Reflection;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class SoundFeedbackTests
{
    [Fact]
    public void Play_success_should_load_success_resource_and_invoke_player_when_enabled()
    {
        var playedResources = new List<string>();
        var service = CreateService(
            soundEnabled: true,
            resourceLoader: name => CreateTaggedStream(name),
            player: stream => playedResources.Add(ReadTag(stream)));

        InvokeInstance(service, "PlaySuccess");

        playedResources.Should().ContainSingle().Which.Should().Be("Bisik.Resources.success.wav");
    }

    [Fact]
    public void Play_error_should_load_error_resource_and_invoke_player_when_enabled()
    {
        var playedResources = new List<string>();
        var service = CreateService(
            soundEnabled: true,
            resourceLoader: name => CreateTaggedStream(name),
            player: stream => playedResources.Add(ReadTag(stream)));

        InvokeInstance(service, "PlayError");

        playedResources.Should().ContainSingle().Which.Should().Be("Bisik.Resources.error.wav");
    }

    [Fact]
    public void Play_methods_should_do_nothing_when_sound_is_disabled()
    {
        var requestedResources = new List<string>();
        var playCount = 0;
        var service = CreateService(
            soundEnabled: false,
            resourceLoader: name =>
            {
                requestedResources.Add(name);
                return CreateTaggedStream(name);
            },
            player: _ => playCount++);

        InvokeInstance(service, "PlaySuccess");
        InvokeInstance(service, "PlayError");

        requestedResources.Should().BeEmpty();
        playCount.Should().Be(0);
    }

    private static object CreateService(bool soundEnabled, Func<string, Stream?> resourceLoader, Action<Stream> player)
    {
        var type = RequireBisikType("Bisik.Core.SoundFeedback");
        var constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(bool), typeof(Func<string, Stream?>), typeof(Action<Stream>)],
            modifiers: null);

        if (constructor is null)
        {
            throw new XunitException("Expected SoundFeedback constructor to accept (bool, Func<string, Stream?>, Action<Stream>).");
        }

        return constructor.Invoke([soundEnabled, resourceLoader, player]);
    }

    private static void InvokeInstance(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (method is null)
        {
            throw new XunitException($"Expected {instance.GetType().Name}.{methodName}().");
        }

        method.Invoke(instance, []);
    }

    private static Type RequireBisikType(string fullName)
    {
        return typeof(Bisik.Models.AppSettings).Assembly.GetType(fullName) ??
            throw new XunitException($"Expected type '{fullName}' to exist in the Bisik assembly.");
    }

    private static Stream CreateTaggedStream(string resourceName)
    {
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(resourceName));
    }

    private static string ReadTag(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
