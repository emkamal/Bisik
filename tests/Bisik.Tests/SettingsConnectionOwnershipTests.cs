using Bisik.Core;
using Bisik.Models;
using Bisik.UI;

namespace Bisik.Tests;

public class SettingsConnectionOwnershipTests
{
    [Fact]
    public Task ClosingSettingsOwnsItsSeparateTestRecorderAndTask() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.SettingsOwnership", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new SettingsManager(root); manager.Save(new AppSettings());
            var wave = new ExperimentSessionTests.TestCapture();
            var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((_, _, _) => throw new Exception("No call expected")));
            using var form = new SettingsForm(null, service, manager, testRecorderFactory: settings => new AudioRecorder(settings, root, (_, _) => wave));
            form.Show(); var run = form.BeginConnectionTestAsync();
            Assert.False(run.IsCompleted); Assert.Same(run, form.BeginConnectionTestAsync());
            form.Close(); await form.CancelConnectionTestAsync();
            Assert.True(wave.Disposed); Assert.True(form.ConnectionTestTask.IsCompleted);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
