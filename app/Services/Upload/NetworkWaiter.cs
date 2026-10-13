using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace BoxeZone.Services.Upload
{
    /// <summary>
    /// Waits before retrying an upload that failed for lack of network: until Windows reports
    /// a network change, or 15 s, 30 s, then 60 s, whichever comes first. No overall limit.
    /// </summary>
    public static class NetworkWaiter
    {
        public static async Task WaitAsync(int attempt, CancellationToken ct)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NetworkStatusChangedEventHandler handler = _ => changed.TrySetResult();
            NetworkInformation.NetworkStatusChanged += handler;
            try
            {
                TimeSpan delay = TimeSpan.FromSeconds(attempt switch { 0 => 15, 1 => 30, _ => 60 });
                await Task.WhenAny(changed.Task, Task.Delay(delay, ct));
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                NetworkInformation.NetworkStatusChanged -= handler;
            }
        }
    }
}
