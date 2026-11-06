using Bisik.Core;
using Bisik.Models;
using Bisik.UI;

namespace Bisik.Tests;

public class ExperimentOwnershipTests
{
    [Fact]
    public void LeaseIsAtomicAndIdempotent()
    {
        var gate = new OperationGate(); var lease = gate.TryEnter(); Assert.NotNull(lease);
        Assert.True(gate.IsActive); Assert.Null(gate.TryEnter()); lease.Dispose(); lease.Dispose();
        Assert.False(gate.IsActive); using var next = gate.TryEnter(); Assert.NotNull(next);
    }

    [Fact]
    public Task CloseHoldsGateUntilLateCompletionIsRejectedAndCheckpointDrains() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.OwnershipTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { PostProcessor = TranscriptionServiceTextTests.Settings() };
            var definition = ExperimentRunnerTests.Definition(settings.PostProcessor);
            var store = new ExperimentStore(root);
            var prior = new ExperimentRun(definition, new(Guid.NewGuid(), "raw", 0, null, definition.InputIdentity), ExperimentState.Cancelled, 1, []);
            await store.SaveSetupAsync(definition, settings, CancellationToken.None); await store.SaveAsync(prior, CancellationToken.None);
            var gate = new OperationGate();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((_, _, _) => { started.TrySetResult(); return pending.Task; }));
            using var form = new ExperimentsForm(settings, _ => service, (_, _, _, _) => throw new Exception("No capture"), store, gate.TryEnter);
            form.Show(); await form.LoadAsync(definition.Id);
            var run = form.StartWorkAsync(false, true); await started.Task;
            form.Close(); Assert.True(gate.IsActive); Assert.False(form.IsDisposed);
            pending.SetResult("late"); await run; await form.DrainAsync();
            await ExperimentsFormTests.WaitUntilAsync(() => form.IsDisposed);
            Assert.False(gate.IsActive);
            var saved = await store.LoadAsync(definition.Id, CancellationToken.None);
            Assert.Equal(ExperimentState.Cancelled, saved.State);
            Assert.DoesNotContain(saved.Trials, t => t.Output == "late");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
