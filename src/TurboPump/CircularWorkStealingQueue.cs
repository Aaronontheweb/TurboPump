using System;
using System.Threading;

namespace TurboPump
{
    /// <summary>
    /// INTERNAL API.
    /// 
    /// n^2 circular array that grows by doubling its size. Uses array-length modulo indexing to allow
    /// circular insert / fetch behavior. 
    /// </summary>
    /// <typeparam name="T">The type of item stored in the array.</typeparam>
    public sealed class CircularArray<T>
    {
        private readonly int _logSize;
        private readonly T[] _arr;

        public CircularArray(int logSize)
        {
            _logSize = logSize;
            _arr = new T[1<<logSize];
        }

        public long Size => 1 << _logSize;

        public T this[long i]
        {
            get => _arr[i % Size];
            set => _arr[i % Size] = value;
        }

        public CircularArray<T> Grow(long b, long t)
        {
            var a = new CircularArray<T>(_logSize + 1);
            for (var i = t; i < b; i++)
                a[i] = this[i];
            return a;
        }

        public CircularArray<T> Shrink(long b, long t)
        {
            var a = new CircularArray<T>(_logSize - 1);
            for (var i = t; i < b; i++)
                a[i] = this[i];
            return a;
        }
    }

    /// <summary>
    /// Used to signal whether a steal or pop operation was a success / failure / no result.
    /// </summary>
    public enum OpCode
    {
        Success,
        Empty,
        Abort
    }
    
    /// <summary>
    /// Lock-free work-stealing queue implementation.
    /// </summary>
    /// <typeparam name="T">The type of work managed by this queue.</typeparam>
    public sealed class CircularWorkStealingQueue<T>
    {
        public static readonly (OpCode status, T item) Empty = (OpCode.Empty, default)!;
        public static readonly (OpCode status, T item) Abort = (OpCode.Abort, default)!;
        
        private const int LogInitialSize = 16;
        private const int ShrinkThreshold = 4;

        private long _bottom = 0;
        private long _top = 0;
        private CircularArray<T> _active = new CircularArray<T>(LogInitialSize);

        /// <summary>
        /// Current work capacity of this queue.
        /// </summary>
        public long Capacity => Volatile.Read(ref _active).Size;

        /// <summary>
        /// Actively used size of this queue.
        /// </summary>
        public long Size => Volatile.Read(ref _bottom) - Volatile.Read(ref _top);

        private bool SwapTop(long oldVal, long newVal)
        {
            var originalValue = Interlocked.CompareExchange(ref _top, newVal, oldVal);
            return originalValue == oldVal;
        }

        private void PerhapsShrink(long b, long t)
        {
            var a = Volatile.Read(ref _active);
            
            // TODO: use watermarks to avoid successive shrinking
            if (b - t < a.Size / ShrinkThreshold)
            {
                var aa = a.Shrink(b, t);
                Volatile.Write(ref _active, aa);
            }
        }

        /// <summary>
        /// Pushes an item to the bottom of the work queue.
        /// </summary>
        /// <remarks>
        /// Only the thread-owner of this queue can push to it.
        /// </remarks>
        /// <param name="item">The work item to add.</param>
        public void PushBottom(T item)
        {
            var b = Volatile.Read(ref _bottom);
            var t = Volatile.Read(ref _top);
            var a = Volatile.Read(ref _active);
            var size = b - t;
            
            if (size >= a.Size - 1)
            {
                a = a.Grow(b, t);
                Volatile.Write(ref _active, a);
            }

            a[b] = item;
            Volatile.Write(ref _bottom, b + 1);
        }

        /// <summary>
        /// Pops an item off the bottom of the work queue.
        /// </summary>
        /// <remarks>
        /// Only the thread-owner of this queue can pop items from it.
        /// </remarks>
        /// <returns>A (OpsCode, T) tuple - if the OpCode is equal to <see cref="OpCode.Empty"/> or <see cref="OpCode.Abort"/>
        /// then there is no work to perform.</returns>
        public (OpCode status, T item) PopBottom()
        {
            var b = Volatile.Read(ref _bottom);
            var a = Volatile.Read(ref _active);
            b = b - 1;
            Volatile.Write(ref _bottom, b);
            
            var t = Volatile.Read(ref _top);
            var size = b - t;
            if (size < 0) // bottom is empty
            {
                Volatile.Write(ref _bottom, t);
                return Empty;
            }

            var item = a[b];
            if (size > 0)
            {
                // attempt to reclaim memory as the work queue empties
                PerhapsShrink(b,t);
                return (OpCode.Success, item);
            }

            if (!SwapTop(t, t + 1))
                item = default;
            
            Volatile.Write(ref _bottom, t);
            return (OpCode.Empty, item)!;
        }

        /// <summary>
        /// Steals an item from the front of the work queue. Any thread that has no work
        /// available in its local queue can steal from any other thread.
        /// </summary>
        /// <remarks>
        /// Only the thread-owner of this queue can push to it.
        /// </remarks>
        public (OpCode status, T item) Steal()
        {
            var b = Volatile.Read(ref _bottom);
            var t = Volatile.Read(ref _top);
            var a = Volatile.Read(ref _active);
            var size = b - t;
            if (size <= 0)
                return Empty;

            var item = a[t];
            if (!SwapTop(t, t + 1))
                return Abort;
            return (OpCode.Success, item);
        }
    }
}
