using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaInfoKeeper.Services
{
    public static class RefreshTaskRunner
    {
        private static readonly object GateSync = new object();
        private static int configuredConcurrency;
        private static int activeCount;
        private static TaskCompletionSource<bool> availability =
            CreateAvailabilitySource();

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
            await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                ReleaseTurn();
            }
        }

        private static async Task WaitForTurnAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task waiter = null;
                lock (GateSync)
                {
                    var maxConcurrent = GetMaxConcurrent();
                    if (configuredConcurrency != maxConcurrent)
                    {
                        configuredConcurrency = maxConcurrent;
                        if (activeCount < configuredConcurrency)
                        {
                            SignalAvailability();
                        }
                    }

                    if (activeCount < configuredConcurrency)
                    {
                        activeCount++;
                        return;
                    }

                    waiter = availability.Task;
                }

                await waiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static void ReleaseTurn()
        {
            lock (GateSync)
            {
                if (activeCount > 0)
                {
                    activeCount--;
                }

                if (activeCount < configuredConcurrency)
                {
                    SignalAvailability();
                }
            }
        }

        private static void SignalAvailability()
        {
            var current = availability;
            availability = CreateAvailabilitySource();
            current.TrySetResult(true);
        }

        private static int GetMaxConcurrent()
        {
            return Math.Max(1, Plugin.Instance?.Options?.MainPage?.MaxConcurrentCount ?? 1);
        }

        private static TaskCompletionSource<bool> CreateAvailabilitySource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
