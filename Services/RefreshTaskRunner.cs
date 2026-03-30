using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaInfoKeeper.Services
{
    public static class RefreshTaskRunner
    {
        private static readonly object GateSync = new object();
        private static SemaphoreSlim gate;
        private static int configuredConcurrency;

        public static Task RunAsync(
            Func<Task> action,
            CancellationToken cancellationToken = default)
        {
            return WithConcurrencyLimitAsync(action, cancellationToken);
        }

        private static async Task WithConcurrencyLimitAsync(
            Func<Task> action,
            CancellationToken cancellationToken)
        {
            var semaphore = GetGate();
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private static SemaphoreSlim GetGate()
        {
            var maxConcurrent = Math.Max(1, Plugin.Instance.Options.MainPage.MaxConcurrentCount);
            lock (GateSync)
            {
                if (gate == null || configuredConcurrency != maxConcurrent)
                {
                    gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
                    configuredConcurrency = maxConcurrent;
                }

                return gate;
            }
        }
    }
}
