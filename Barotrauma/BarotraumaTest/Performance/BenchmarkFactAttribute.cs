using System;
using Xunit;

namespace TestProject.Performance;

/// <summary>
/// A test that only runs when the environment variable <c>BARO_BENCH=1</c> is set, so that regular test runs
/// (and CI) never spend time on, or fail because of, timing measurements.
/// </summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "BARO_BENCH";

    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Benchmark. Set {EnvironmentVariable}=1 to run.";
        }
    }
}
