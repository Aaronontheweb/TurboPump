using BenchmarkDotNet.Attributes;

namespace TurboPump.Benchmarks.CircularWorkQueues
{
    [Config(typeof(MicroBenchmarkConfig))]
    public class CircularWorkQueueBenchmark
    {
        public const int InvokeCount = 10_000;

        private CircularWorkStealingQueue<int> _prePopulated;
        private CircularWorkStealingQueue<int> _unpopulated;

        [IterationSetup]
        public void PerIteration()
        {
            _unpopulated = new CircularWorkStealingQueue<int>();
            _prePopulated = new CircularWorkStealingQueue<int>();
            for(var i = 0; i < InvokeCount; i++)
                _prePopulated.PushBottom(i);
        }
        
        [Benchmark(OperationsPerInvoke = InvokeCount)]
        public CircularWorkStealingQueue<int> Push()
        {
            for(var i = 0; i < InvokeCount; i++)
                _unpopulated.PushBottom(i);

            return _unpopulated;
        }

        [Benchmark(OperationsPerInvoke = InvokeCount)]
        public CircularWorkStealingQueue<int> PopOnly()
        {
            for (var i = 0; i < InvokeCount; i++)
                _prePopulated.PopBottom();
            
            return _prePopulated;
        }
        
        [Benchmark(OperationsPerInvoke = InvokeCount)]
        public CircularWorkStealingQueue<int> StealOnly()
        {
            for (var i = 0; i < InvokeCount; i++)
                _prePopulated.Steal();
            
            return _prePopulated;
        }
    }
}