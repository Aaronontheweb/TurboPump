using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TurboPump
{
    /// <summary>
    /// Work queue consists of two tiers:
    /// 1. A FIFO generalized work queue designed for work that is queued "from outside" the managed ThreadPool.
    /// 2. A set of thread-specific work-stealing LIFO dequeues that are used
    /// </summary>
    /// <remarks>
    /// Much of this design was inspired by https://github.com/dotnet/runtime/blob/415a41770bdf8efd4c3217e2c281233ee5cd03ea/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPoolWorkQueue.cs,
    /// although we have different requirements and queue structures at work.
    /// </remarks>
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

        internal readonly DedicatedThreadPool _threadPool;

        // External "general" queue - FIFO
        internal readonly ConcurrentQueue<IRunnable> PoolQueue = new ConcurrentQueue<IRunnable>();
        
        // List of all thread-specific work-stealing queues - LIFO
        internal readonly WorkQueueList ThreadQueues = new WorkQueueList();

        private int _hasOutstandingThreadRequest = 0;

        public ThreadPoolWorkQueue(DedicatedThreadPool threadPool)
        {
            _threadPool = threadPool;
        }

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
                PoolQueue.Enqueue(item);
            }
            
            EnsureThreadRequested();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnsureThreadRequested()
        {
            if (Interlocked.CompareExchange(ref _hasOutstandingThreadRequest, 1, 0) == 0)
            {
                // TODO: need to reference worker thread here
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MarkThreadRequestSatisfied()
        {
            _hasOutstandingThreadRequest = 0;
            Interlocked.MemoryBarrier();
        }

        internal ThreadPoolWorkerQueueLocals GetOrCreateLocals() =>
            ThreadPoolWorkerQueueLocals.ThreadLocals ?? new ThreadPoolWorkerQueueLocals(this);
        
        private const int ProcessorsPerAssignableWorkItemQueue = 16;
        
        // the amount of work a worker can pull before yielding
        private static readonly int s_assignableWorkItemQueueCount =
            Environment.ProcessorCount <= 32 ? 0 :
                (Environment.ProcessorCount + (ProcessorsPerAssignableWorkItemQueue - 1)) / ProcessorsPerAssignableWorkItemQueue;
        
        // Execution time quantum = same as the ThreadPool built into .NET
        public const uint DispatchQuantumMs = 30;

        internal (OpCode status, IRunnable item) Dequeue(ThreadPoolWorkerQueueLocals tl)
        {
            // Check for work in local queue first
            var t = tl.LocalQueue.PopBottom();
            if (t.status == OpCode.Success)
                return t;
            
            // check for items on the global queue - again
            if (PoolQueue.TryDequeue(out var item))
            {
                return (OpCode.Success, item);
            }
            
            // last chance - time to steal work from someone else
            var rValue = tl.Random.Next();
            var queues = ThreadQueues.ThreadQueues;
            var c = queues.Length;
            var maxIndex = c - 1;
            for (var i = (rValue % c); c > 0; i = i < maxIndex ? i + 1 : 0, c--)
            {
                var otherQueue = queues[i];
                if (otherQueue != tl.LocalQueue)
                {
                    var result = otherQueue.Steal();
                    if (result.status == OpCode.Success)
                        return result;
                }
            }

            return CircularWorkStealingQueue<IRunnable>.Empty;
        }

        /// <summary>
        /// Dispatches work items to the current worker thread.
        /// </summary>
        /// <remarks>
        /// This method is called by the actual worker thread itself.
        /// </remarks>
        /// <returns><c>true</c> if the thread was busy throughout its entire quantum, <c>false</c> if it finished early.</returns>
        internal bool Dispatch()
        {
            var tl = GetOrCreateLocals();
            var workQueue = tl.GlobalQueue;
            
            // Before we begin work, mark thread request as satisfied.
            workQueue.MarkThreadRequestSatisfied();

            IRunnable? workItem = null;
            {
                // check global queue for work first, so this
                workQueue.PoolQueue.TryDequeue(out workItem);

                if (workItem == null)
                {
                    // No work in local queue or global queue.
                    var (status, item) = workQueue.Dequeue(tl);
                    workItem = item;
                
                    /*
                     * we missed stealing / taking work from any other queue.
                     * Ask another thread to check for work.
                     */
                    if (status != OpCode.Success)
                    {
                        workQueue.EnsureThreadRequested();
                    }

                    // TODO: when we add hill-climbing, this needs to return true
                    // to signal a normal programmatic exit.
                    // In lieu of hill climbing, we need to use this as a signal that
                    // the current threadpool is oversubscribed
                    return false;
                }
                
                // we have found work, but more might be available
                // make sure that other threads are activated to attempt
                // to process that work in parallel
                workQueue.EnsureThreadRequested();
            }
            
            //
            // Save the start time
            //
            var startTickCount = Environment.TickCount;

            // Loop until our quantum expires or there is no work.
            while (true)
            {
                if (workItem == null)
                {
                    // No work in local queue or global queue.
                    var (status, item) = workQueue.Dequeue(tl);
                    workItem = item;
                
                    /*
                     * we missed stealing / taking work from any other queue.
                     * Ask another thread to check for work.
                     */
                    if (status != OpCode.Success)
                    {
                        workQueue.EnsureThreadRequested();
                    }
                    
                    return true;
                }
                
                ExecuteWork(workItem);
                
                // Release reference
                workItem = null;

                var currentTickCount = Environment.TickCount;
                
                // TODO: add hill-climbing support here to dynamically scale threads
                
                // Check if the dispatch quantum has expired
                if ((uint)(currentTickCount - startTickCount) < DispatchQuantumMs)
                {
                    continue;
                }

                // we've used or exceeded our compute quantum
                // need to yield from dispatch so other threads have an opportunity to trun
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ExecuteWork(IRunnable runnable)
        {
            runnable.Run();
        }
    }

    /// <summary>
    /// All of the local state a thread pool worker needs to interop with queues
    /// </summary>
    internal sealed class ThreadPoolWorkerQueueLocals
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
            globalQueue.ThreadQueues.Add(LocalQueue);
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
                    GlobalQueue.Enqueue(item, true);
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
            GlobalQueue.ThreadQueues.Remove(LocalQueue);
        }
    }
}