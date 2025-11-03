using BenchmarkDotNet.Running;

namespace TokenRateGate.PerformanceTests;

public class Program
{
    public static void Main(string[] args)
    {
        // Use BenchmarkSwitcher to allow running all benchmarks or selecting specific ones
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
