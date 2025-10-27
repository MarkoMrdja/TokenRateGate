using BenchmarkDotNet.Running;

namespace TokenRateGate.PerformanceTests;

public class Program
{
    public static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<ThroughputBenchmarks>();
    }
}
