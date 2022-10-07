using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TurboPump
{
    /// <summary>
    /// Work queue consists of two tiers:
    /// 1. A FIFO generalized work queue designed for work that is queued "from outside" the managed ThreadPool.
    /// 2. A set of thread-specific work-stealing LIFO dequeues that are used
    /// </summary>
    public sealed class ThreadPoolWorkQueue
    {
        /// <summary>
        /// INTERNAL API - tracks list of available work queues.
        /// </summary>
        internal sealed class WorkQueueList
        {
            /*
             * Can't re-use something like a circular array here, because:
             * 1. Need to have random-access for work stealing
             * 2. Don't want to pad / over-allocate queue instances - should match
             *    active worker thread count exactly.
             */
            private volatile CircularWorkStealingQueue<IRunnable>[] _queues =
                Array.Empty<CircularWorkStealingQueue<IRunnable>>();

            public CircularWorkStealingQueue<IRunnable>[] ThreadQueues => _queues;

            /// <summary>
            /// Called when a new worker thread enters.
            /// </summary>
            /// <param name="threadQueue">A thread-specific worker queue.</param>
            public void Add(CircularWorkStealingQueue<IRunnable> threadQueue)
            {
                // attempt CAS operation
                while (true)
                {
                    var old = _queues;
                    var grownQueue = new CircularWorkStealingQueue<IRunnable>[old.Length + 1];
                    Array.Copy(old, grownQueue, old.Length);
                    grownQueue[old.Length + 1] = threadQueue;
                    if (Interlocked.CompareExchange(ref _queues, grownQueue, old) == old)
                        break; // successful CAS
                }
            }

            public void Remove(CircularWorkStealingQueue<IRunnable> threadQueue)
            {
                // attempt CAS operation
                while (true)
                {
                    var old = _queues;
                    if (old.Length == 0)
                    {
                        return;
                    }

                    var index = Array.IndexOf(old, threadQueue);
                    if (index < 0)
                        return; // could not find queue
                    
                    var shrinkQueue = new CircularWorkStealingQueue<IRunnable>[old.Length - 1];
                    
                    // try to be memory efficient with copying by checking contiguous edge cases first
                    if (index == 0)
                    {
                        // skip head of old array
                        Array.Copy(old, 1, shrinkQueue, 0, shrinkQueue.Length);
                    }
                    else if (index == old.Length - 1)
                    {
                        // skip tail of old array
                        Array.Copy(old, shrinkQueue, shrinkQueue.Length);
                    }
                    else
                    {
                        // least performant case - missing queue is in the middle of source array
                        // requires 2 copy operations
                        Array.Copy(old, shrinkQueue, index);
                        Array.Copy(old, index + 1, shrinkQueue, index, shrinkQueue.Length - index);
                    }

                    if (Interlocked.CompareExchange(ref _queues, shrinkQueue, old) == old)
                        break; // successful CAS
                }
            }
        }

        // External "general" queue - FIFO
        internal readonly ConcurrentQueue<IRunnable> _globalQueue = new ConcurrentQueue<IRunnable>();
        
        // List of all thread-specific work-stealing queues - LIFO
        internal readonly WorkQueueList _workQueueList = new WorkQueueList();

        /// <summary>
        /// Enqueue an item back into the work queue.
        /// </summary>
        /// <param name="item">The item to execute.</param>
        /// <param name="forceGlobal">If set to <c>true</c>, add item to global queue rather than thread-specific.</param>
        /// <typeparam name="T">The type of <see cref="IRunnable"/> to execute.</typeparam>
        public void Enqueue<T>(T item, bool forceGlobal) where T : IRunnable
        {
            ThreadPoolWorkerQueueLocals? l;
            if (!forceGlobal && (l = ThreadPoolWorkerQueueLocals.ThreadLocals) != null)
            {
                // we are running inside a worker thread in this threadpool,
                // so it's safe for us to insert into the local work queue.
                l.LocalQueue.PushBottom(item);
            }
            else
            {
                // have to push onto global queue
                _globalQueue.Enqueue(item);
            }
            
            EnsureThreadRequested();
        }

        public void EnsureThreadRequested()
        {
            
        }
    }

    /// <summary>
    /// All of the local state a thread pool worker needs to interop with queues
    /// </summary>
    public sealed class ThreadPoolWorkerQueueLocals
    {
        [ThreadStatic]
        public static ThreadPoolWorkerQueueLocals? ThreadLocals;

        public readonly ThreadPoolWorkQueue GlobalQueue;
        public readonly CircularWorkStealingQueue<IRunnable> LocalQueue;
        public readonly Thread CurrentThread;
        public readonly Random Random = new Random();

        public ThreadPoolWorkerQueueLocals(ThreadPoolWorkQueue globalQueue)
        {
            GlobalQueue = globalQueue;
            CurrentThread = Thread.CurrentThread;
            LocalQueue = new CircularWorkStealingQueue<IRunnable>();
            globalQueue._workQueueList.Add(LocalQueue);
        }

        /// <summary>
        /// Called when a thread is exiting - to move locally queued work back onto
        /// the main thread pool.
        /// </summary>
        public void TransferLocalWork()
        {
            while (true)
            {
                var (status, item) = LocalQueue.PopBottom();
                if (status == OpCode.Success)
                {
                    GlobalQueue.Enqueue(item);
                    continue;
                }

                // OpCode was not a success - local queue is empty.
                break;
            }
        }

        ~ThreadPoolWorkerQueueLocals()
        {
            // transfer work to other threads and remove local queue
            TransferLocalWork();
            GlobalQueue._workQueueList.Remove(LocalQueue);
        }
    }
}