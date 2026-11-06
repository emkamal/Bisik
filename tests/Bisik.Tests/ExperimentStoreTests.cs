using Bisik.Core;
using Bisik.Models;

namespace Bisik.Tests;

public sealed class ExperimentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Bisik.StoreTests", Guid.NewGuid().ToString("N"));
    private static ExperimentRun Run(PostProcessorSettings connection) {
        var definition = ExperimentRunnerTests.Definition(connection);
        return new(definition, new(Guid.NewGuid(), "raw Ελληνικά\n12", 100, null, definition.InputIdentity), ExperimentState.Generating, 1,
            [new() { Id = "trial", Candidate = ExperimentCandidate.Baseline, Generation = ExperimentGenerationStatus.Running }]);
    }

    [Fact]
    public async Task CheckpointsReopenInterruptedWithSeparateEncryptedConnectionAndSourceUnchanged()
    {
        var connection = TranscriptionServiceTextTests.Settings() with { Provider = TranscriptionServiceTextTests.Settings().Provider with
            { ApiKey = "private-key-value", ExtraHeaders = new() { ["X-Token"] = "private-header-value" } } };
        var run = Run(connection); var store = new ExperimentStore(_root);
        await store.SaveSetupAsync(run.Definition, new() { PostProcessor = connection }, CancellationToken.None);
        await store.SaveAsync(run, CancellationToken.None);
        var reopened = await store.LoadAsync(run.Definition.Id, CancellationToken.None);
        Assert.True(reopened.Interrupted); Assert.Equal(ExperimentState.Failed, reopened.State);
        Assert.Equal("interrupted", reopened.Trials[0].FailureCode);
        var settingsPath = Path.Combine(store.DirectoryFor(run.Definition.Id), "connection", "settings.json");
        var encrypted = await File.ReadAllTextAsync(settingsPath);
        Assert.DoesNotContain("private-key-value", encrypted); Assert.DoesNotContain("example.invalid", encrypted);
        Assert.DoesNotContain("private-header-value", encrypted);
        Assert.Equal("private-key-value", (await store.LoadConnectionAsync(run.Definition.Id, CancellationToken.None))!.PostProcessor.Provider.ApiKey);
        Assert.Equal(encrypted, await File.ReadAllTextAsync(settingsPath));
        var json = await File.ReadAllTextAsync(Path.Combine(store.DirectoryFor(run.Definition.Id), "run.json"));
        Assert.DoesNotContain("private", json); Assert.DoesNotContain("example.invalid", json);
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
    }

    [Fact]
    public async Task CorruptionCancelledWritesAndDiskFailureDoNotSilentlyComplete()
    {
        var run = Run(TranscriptionServiceTextTests.Settings()); var store = new ExperimentStore(_root);
        await store.SaveAsync(run, CancellationToken.None);
        var path = Path.Combine(store.DirectoryFor(run.Definition.Id), "run.json");
        var original = await File.ReadAllTextAsync(path);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(run with { State = ExperimentState.Completed }, cancelled.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, original.Replace("raw Ελληνικά", "tampered"));
        // Escaping varies with JsonSerializer; mutate a guaranteed plain field instead.
        await File.WriteAllTextAsync(path, original.Replace("\"OrderSeed\":1", "\"OrderSeed\":2"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(run.Definition.Id, CancellationToken.None));
        File.Delete(path); Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => store.SaveAsync(run, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryFor(run.Definition.Id), "*.tmp"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AudioRetentionIsExplicitAndDoesNotDeleteOtherPaths(bool retain)
    {
        Directory.CreateDirectory(_root);
        var audio = Path.Combine(_root, "owned.wav"); await File.WriteAllBytesAsync(audio, [1, 2, 3]);
        var run = Run(TranscriptionServiceTextTests.Settings()); run = run with { Definition = run.Definition with { RetainAudio = retain } };
        var store = new ExperimentStore(Path.Combine(_root, "runs"));
        await store.SaveAsync(run, CancellationToken.None, audio);
        Assert.Equal(retain, File.Exists(Path.Combine(store.DirectoryFor(run.Definition.Id), "recording.wav")));
        Assert.True(File.Exists(audio)); // Capture owner, not arbitrary import paths, performs deletion.
        Assert.Null(await store.LoadConnectionAsync(run.Definition.Id, CancellationToken.None));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
