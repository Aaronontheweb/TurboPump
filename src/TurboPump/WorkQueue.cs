namespace TurboPump
{
    /// <summary>
    /// An asynchronous operation will be executed by a thread.
    /// </summary>
    public interface IRunnable
    {
        /// <summary>
        /// Executes the action.
        /// </summary>
        void Run();
    }

    /// <summary>
    /// n^2 circular array that grows by doubling its size. Uses array-length modulo indexing to allow
    /// circular insert / fetch behavior. 
    /// </summary>
    /// <typeparam name="T">The type of item stored in the array.</typeparam>
    public sealed class CircularArray<T>
    {
        private readonly int _logSize;
        private readonly T[] _segment;

        public CircularArray(int logSize)
        {
            _logSize = logSize;
            _segment = new T[1<<logSize];
        }

        public long Size => 1 << _logSize;

        public T this[long i]
        {
            get => _segment[i % Size];
            set => _segment[i % Size] = value;
        }

        public CircularArray<T> Grow(long b, long t)
        {
            var a = new CircularArray<T>(_logSize + 1);
            for (long i = t; i < b; i++)
                a[i] = this[i];
            return a;
        }
    }
    
    public class WorkQueue
    {
    }
}
