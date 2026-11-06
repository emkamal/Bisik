using System.Windows.Forms;

namespace Bisik.Tests;

internal static class StaTestHost
{
    internal static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form { ShowInTaskbar = false, Opacity = 0 };
            host.Shown += async (_, _) =>
            {
                try { await action().WaitAsync(TimeSpan.FromSeconds(20)); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
