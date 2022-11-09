using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;

namespace TurboPump.Benchmarks
{
    public class MicroBenchmarkConfig : ManualConfig
    {
        public MicroBenchmarkConfig()
        {
            Add(MemoryDiagnoser.Default);
            Add(MarkdownExporter.GitHub);
            AddLogger(ConsoleLogger.Default);
        }
    }
}