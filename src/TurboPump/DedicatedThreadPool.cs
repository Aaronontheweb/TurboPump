using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TurboPump
{
    /// <summary>
    /// Provides settings for a dedicated thread pool
    /// </summary>
    public sealed class DedicatedThreadPoolSettings
    {
        /// <summary>
        /// The minimum number of threads for this thread pool.
        ///
        /// Can be set to 0.
        /// </summary>
        public uint MinThreads { get; }

        /// <summary>
        /// The theoretical maximum number of threads for this pool.
        ///
        /// For best results, should be scaled relative to <see cref="Environment.ProcessorCount"/>
        /// </summary>
        public uint MaxThreads { get; }

        /// <summary>
        /// Interval to check for thread deadlocks.
        ///
        /// If a thread takes longer than <see cref="ThreadTimeout"/> it will be aborted
        /// and replaced.
        /// </summary>
        public TimeSpan ThreadTimeout { get; private set; }

        /// <summary>
        /// The name of this thread pool.
        /// </summary>
        public string Name { get; private set; }


        /// <summary>
        /// Gets the thread stack size, 0 represents the default stack size.
        /// </summary>
        public int ThreadMaxStackSize { get; private set; }
    }


    public sealed class DedicatedThreadPool : IDisposable
    {
        private ThreadPoolWorkQueue _queue;
        private readonly DedicatedThreadPoolSettings _settings;

        // The current number of worker requests outstanding on this threadpool
        private volatile int _numRequestedWorkers = 0;

        public DedicatedThreadPool(DedicatedThreadPoolSettings settings)
        {
            _settings = settings;
            _queue = new ThreadPoolWorkQueue(this);
        }

        private readonly UnfairSemaphore _semaphore = new UnfairSemaphore();

        public void Dispose()
        {
            // don't allow new items to be added to queue
            // kill all worker threads
        }
        
        #region UnfairSemaphore implementation

        // This class has been translated from:
        // https://github.com/dotnet/coreclr/blob/97433b9d153843492008652ff6b7c3bf4d9ff31c/src/vm/win32threadpool.h#L124

        // UnfairSemaphore is a more scalable semaphore than Semaphore.  It prefers to release threads that have more recently begun waiting,
        // to preserve locality.  Additionally, very recently-waiting threads can be released without an addition kernel transition to unblock
        // them, which reduces latency.
        //
        // UnfairSemaphore is only appropriate in scenarios where the order of unblocking threads is not important, and where threads frequently
        // need to be woken.

        [StructLayout(LayoutKind.Sequential)]
        private sealed class UnfairSemaphore
        {
            public const int MaxWorker = 0x7FFF;

            private static readonly int ProcessorCount = Environment.ProcessorCount;

            // We track everything we care about in a single 64-bit struct to allow us to
            // do CompareExchanges on this for atomic updates.
            [StructLayout(LayoutKind.Explicit)]
            private struct SemaphoreState
            {
                //how many threads are currently spin-waiting for this semaphore?
                [FieldOffset(0)]
                public short Spinners;

                //how much of the semaphore's count is available to spinners?
                [FieldOffset(2)]
                public short CountForSpinners;

                //how many threads are blocked in the OS waiting for this semaphore?
                [FieldOffset(4)]
                public short Waiters;

                //how much count is available to waiters?
                [FieldOffset(6)]
                public short CountForWaiters;

                [FieldOffset(0)]
                public long RawData;
            }

            [StructLayout(LayoutKind.Explicit, Size = 64)]
            private struct CacheLinePadding
            { }

            private readonly Semaphore m_semaphore;

            // padding to ensure we get our own cache line
#pragma warning disable 169
            private readonly CacheLinePadding m_padding1;
            private SemaphoreState m_state;
            private readonly CacheLinePadding m_padding2;
#pragma warning restore 169

            public UnfairSemaphore()
            {
                m_semaphore = new Semaphore(0, short.MaxValue);
            }

            public bool Wait()
            {
                return Wait(Timeout.InfiniteTimeSpan);
            }

            public bool Wait(TimeSpan timeout)
            {
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    // First, just try to grab some count.
                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        // No count available, become a spinner
                        ++newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            break;
                    }
                }

                //
                // Now we're a spinner.
                //
                int numSpins = 0;
                const int spinLimitPerProcessor = 50;
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        --newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        double spinnersPerProcessor = (double)currentCounts.Spinners / ProcessorCount;
                        int spinLimit = (int)((spinLimitPerProcessor / spinnersPerProcessor) + 0.5);
                        if (numSpins >= spinLimit)
                        {
                            --newCounts.Spinners;
                            ++newCounts.Waiters;
                            if (TryUpdateState(newCounts, currentCounts))
                                break;
                        }
                        else
                        {
                            //
                            // We yield to other threads using Thread.Sleep(0) rather than the more traditional Thread.Yield().
                            // This is because Thread.Yield() does not yield to threads currently scheduled to run on other
                            // processors.  On a 4-core machine, for example, this means that Thread.Yield() is only ~25% likely
                            // to yield to the correct thread in some scenarios.
                            // Thread.Sleep(0) has the disadvantage of not yielding to lower-priority threads.  However, this is ok because
                            // once we've called this a few times we'll become a "waiter" and wait on the Semaphore, and that will
                            // yield to anything that is runnable.
                            //
                            Thread.Sleep(0);
                            numSpins++;
                        }
                    }
                }

                //
                // Now we're a waiter
                //
                bool waitSucceeded = m_semaphore.WaitOne(timeout);

                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    --newCounts.Waiters;

                    if (waitSucceeded)
                        --newCounts.CountForWaiters;

                    if (TryUpdateState(newCounts, currentCounts))
                        return waitSucceeded;
                }
            }

            public void Release()
            {
                Release(1);
            }

            public void Release(short count)
            {
                while (true)
                {
                    SemaphoreState currentState = GetCurrentState();
                    SemaphoreState newState = currentState;

                    short remainingCount = count;

                    // First, prefer to release existing spinners,
                    // because a) they're hot, and b) we don't need a kernel
                    // transition to release them.
                    short spinnersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Spinners - currentState.CountForSpinners)));
                    newState.CountForSpinners += spinnersToRelease;
                    remainingCount -= spinnersToRelease;

                    // Next, prefer to release existing waiters
                    short waitersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Waiters - currentState.CountForWaiters)));
                    newState.CountForWaiters += waitersToRelease;
                    remainingCount -= waitersToRelease;

                    // Finally, release any future spinners that might come our way
                    newState.CountForSpinners += remainingCount;

                    // Try to commit the transaction
                    if (TryUpdateState(newState, currentState))
                    {
                        // Now we need to release the waiters we promised to release
                        if (waitersToRelease > 0)
                            m_semaphore.Release(waitersToRelease);

                        break;
                    }
                }
            }

            private bool TryUpdateState(SemaphoreState newState, SemaphoreState currentState)
            {
                if (Interlocked.CompareExchange(ref m_state.RawData, newState.RawData, currentState.RawData) == currentState.RawData)
                {
                    Debug.Assert(newState.CountForSpinners <= MaxWorker, "CountForSpinners is greater than MaxWorker");
                    Debug.Assert(newState.CountForSpinners >= 0, "CountForSpinners is lower than zero");
                    Debug.Assert(newState.Spinners <= MaxWorker, "Spinners is greater than MaxWorker");
                    Debug.Assert(newState.Spinners >= 0, "Spinners is lower than zero");
                    Debug.Assert(newState.CountForWaiters <= MaxWorker, "CountForWaiters is greater than MaxWorker");
                    Debug.Assert(newState.CountForWaiters >= 0, "CountForWaiters is lower than zero");
                    Debug.Assert(newState.Waiters <= MaxWorker, "Waiters is greater than MaxWorker");
                    Debug.Assert(newState.Waiters >= 0, "Waiters is lower than zero");
                    Debug.Assert(newState.CountForSpinners + newState.CountForWaiters <= MaxWorker, "CountForSpinners + CountForWaiters is greater than MaxWorker");

                    return true;
                }

                return false;
            }

            private SemaphoreState GetCurrentState()
            {
                // Volatile.Read of a long can get a partial read in x86 but the invalid
                // state will be detected in TryUpdateState with the CompareExchange.

                SemaphoreState state = new SemaphoreState();
                state.RawData = Volatile.Read(ref m_state.RawData);
                return state;
            }
        }

#endregion

        private sealed class PoolWorker
        {
            private readonly DedicatedThreadPool _pool;
            private readonly TaskCompletionSource<bool> _threadExit;
            private Thread _thread;

            public PoolWorker(DedicatedThreadPool pool, int workerId)
            {
                _pool = pool;
                _threadExit = new TaskCompletionSource<bool>();
                _thread = new Thread(ExecuteThread) { IsBackground = true };
                _thread.Name = $"{_pool._settings.Name}-{workerId}";
                _thread.Start();
            }

            private void ExecuteThread()
            {
                var semaphore = _pool._semaphore;
                var workQueue = _pool._queue;
                var timeout = _pool._settings.ThreadTimeout;
                while (true)
                {
                    while (semaphore.Wait(timeout))
                    {
                        var alreadyRemovedWorkingWorker = false;
                        while (TakeActiveRequest(_pool))
                        {
                            if (!workQueue.Dispatch())
                            {
                                // didn't have any work to do
                                alreadyRemovedWorkingWorker = true;
                                break;
                            }

                            if (_pool._numRequestedWorkers <= 0)
                            {
                                break;
                            }
                            
                            // In scenarios where there are short bursts of work, pause the thread 
                            // briefly and check again for work so we don't have threads
                            // constantly starting and shutting down over and over again
                            Thread.Sleep(1);
                            if (Environment.ProcessorCount > 1)
                            {
                                Thread.SpinWait(1);
                            }
                        }

                        if (!alreadyRemovedWorkingWorker)
                        {
                            // Need to notify that we are done working for now
                        }
                    }
                }
            }

            private static bool TakeActiveRequest(DedicatedThreadPool poolInstance)
            {
                var count = poolInstance._numRequestedWorkers;
                while (count > 0)
                {
                    var prevCount =
                        Interlocked.CompareExchange(ref poolInstance._numRequestedWorkers, count - 1, count);
                    if (prevCount == count)
                    {
                        return true;
                    }

                    count = prevCount;
                }

                return false;
            }
        }
    }
}