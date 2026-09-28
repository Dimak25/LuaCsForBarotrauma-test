extern alias Client;

using Client::Barotrauma;
using Client::Barotrauma.LuaCs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TestProject.Performance;

/// <summary>
/// Micro-benchmarks for the suspected hot spots in PERFORMANCE_PLAN.md that can be measured without the game's
/// Content folder. They measure the real <see cref="EventService"/> and <see cref="PerformanceCounter"/>, and
/// reproduce the exact code pattern for the parts that need game objects (marked "pattern").
///
/// Run with:
///   BARO_BENCH=1 dotnet test Barotrauma/BarotraumaTest/LinuxTest.csproj -c Release /p:Platform=x64 \
///       --filter "FullyQualifiedName~HotPathBenchmarks" --logger "console;verbosity=detailed"
/// Optionally set BARO_BENCH_OUT=/path/to/results.md to also append a Markdown table.
///
/// Numbers depend on the machine. Compare runs from the same machine, and always use a Release build.
/// </summary>
public sealed class HotPathBenchmarks
{
    private readonly ITestOutputHelper output;
    private readonly List<(string Name, int Iterations, double NsPerOp, double BytesPerOp)> results = new();

    public HotPathBenchmarks(ITestOutputHelper output) => this.output = output;

    private void Measure(string name, int iterations, Action body)
    {
        for (int i = 0; i < Math.Max(1000, iterations / 5); i++) { body(); } // warm-up (JIT, caches)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) { body(); }
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        results.Add((name, iterations, elapsed * 1e9 / Stopwatch.Frequency / iterations, allocated / (double)iterations));
    }

    private void Report(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {title}");
        sb.AppendLine($"_{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical cores, " +
            $"{(IsOptimized() ? "Release" : "DEBUG BUILD - numbers are not meaningful")}_");
        sb.AppendLine();
        sb.AppendLine("| Scenario | ns/op | bytes/op |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {r.Name} | {r.NsPerOp:0.0} | {r.BytesPerOp:0.0} |");
        }
        output.WriteLine(sb.ToString());

        string? path = Environment.GetEnvironmentVariable("BARO_BENCH_OUT");
        if (!string.IsNullOrEmpty(path)) { File.AppendAllText(path, sb.ToString() + Environment.NewLine); }
    }

    private static bool IsOptimized()
    {
        var attr = (DebuggableAttribute?)Attribute.GetCustomAttribute(typeof(EventService).Assembly, typeof(DebuggableAttribute));
        return attr == null || !attr.IsJITOptimizerDisabled;
    }

    // ---------------------------------------------------------------------------------------------------------
    // H1: event layer. In the game, HarmonyEventPatchesService calls PublishEvent / Call from postfixes on
    // Affliction.Update (per affliction, per tick), Connection.SendSignal (per signal and wire) and others,
    // even when nothing subscribed.
    // ---------------------------------------------------------------------------------------------------------

    [BenchmarkFact]
    public void H1_EventService()
    {
        var (service, _, _) = EventServiceFactory.Create();
        var instance = new object(); // stands in for `__instance` captured by the closure in the Harmony patches
        int sink = 0;

        // Lower bound: what the same virtual call costs without any service around it.
        ITestEvent direct = new NoOpSubscriber();
        Measure("baseline: direct interface call, no service", 5_000_000, () => direct.OnTest(1));

        Measure("PublishEvent, 0 subscribers, capturing closure (as in the patches)", 1_000_000,
            () => service.PublishEvent<ITestEvent>(x => { sink += instance.GetHashCode(); x.OnTest(1); }));

        var one = new TestSubscriber();
        service.Subscribe<ITestEvent>(one);
        Measure("PublishEvent, 1 subscriber", 1_000_000, () =>
        {
            one.Received.Clear();
            service.PublishEvent<ITestEvent>(x => x.OnTest(1));
        });

        for (int i = 0; i < 9; i++) { service.Subscribe<ITestEvent>(new NoOpSubscriber()); }
        Measure("PublishEvent, 10 subscribers", 500_000, () => service.PublishEvent<ITestEvent>(x => x.OnTest(1)));

        // Connection_SendSignal_Post: string concat + Call with params object[] for every wire of every signal.
        string identifier = "smallpump";
        object signal = new object(), recipient = new object();
        Measure("Call(\"signalReceived.\" + id, ...), 0 subscribers (pattern of Connection.SendSignal patch)", 1_000_000,
            () => service.Call("signalReceived." + identifier, signal, recipient));

        GC.KeepAlive(sink);
        Report("H1: EventService (real code)");

        // Not asserting on timings, only on the sanity of the measurement itself.
        Assert.All(results, r => Assert.True(r.NsPerOp > 0));
    }

    private sealed class NoOpSubscriber : ITestEvent
    {
        public void OnTest(int value) { }
    }

    // ---------------------------------------------------------------------------------------------------------
    // H13: the built-in profiler. GameScreen.Update / GameMain.Update call AddElapsedTicks ~15-20 times per tick
    // whether or not `showperf` is on.
    // ---------------------------------------------------------------------------------------------------------

    [BenchmarkFact]
    public void H13_PerformanceCounter()
    {
        var counter = new PerformanceCounter();
        for (int i = 0; i < 100; i++) { counter.AddElapsedTicks("Update:Test", 1000); } // fill the sample queue

        Measure("PerformanceCounter.AddElapsedTicks (real code)", 2_000_000, () => counter.AddElapsedTicks("Update:Test", 1000));
        Report("H13: PerformanceCounter (real code)");
        Assert.All(results, r => Assert.True(r.NsPerOp > 0));
    }

    // ---------------------------------------------------------------------------------------------------------
    // H2: MapEntity.UpdateAll shuffles all gaps every tick with
    //     foreach (Gap gap in Gap.GapList.OrderBy(g => Rand.Int(int.MaxValue)))
    // "pattern": same expression on a list of stand-in objects, using the real Rand class.
    // "proposal": what a replacement could look like (an in-place shuffle on a reusable buffer).
    // ---------------------------------------------------------------------------------------------------------

    private sealed class Stub { public int Id; }

    [BenchmarkFact]
    public void H2_GapShuffle()
    {
        foreach (int count in new[] { 30, 150, 600 })
        {
            var gaps = Enumerable.Range(0, count).Select(i => new Stub { Id = i }).ToList();
            int sink = 0;
            int iterations = 200_000 / Math.Max(1, count / 30);

            Measure($"pattern: OrderBy(Rand.Int) over {count} gaps (current code)", iterations, () =>
            {
                foreach (var gap in gaps.OrderBy(g => Rand.Int(int.MaxValue))) { sink += gap.Id; }
            });

            var buffer = new List<Stub>(count);
            Measure($"proposal: in-place shuffle of a reused buffer, {count} gaps", iterations, () =>
            {
                buffer.Clear();
                buffer.AddRange(gaps);
                for (int i = buffer.Count - 1; i > 0; i--)
                {
                    int j = Rand.Int(i + 1);
                    (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
                }
                foreach (var gap in buffer) { sink += gap.Id; }
            });
            GC.KeepAlive(sink);
        }
        Report("H2: gap shuffle (pattern, real Rand)");
        Assert.All(results, r => Assert.True(r.NsPerOp > 0));
    }

    // ---------------------------------------------------------------------------------------------------------
    // H3: Character.UpdateDespawn runs, for every dead character in a sub, every tick:
    //     CharacterList.Count(c => c.IsDead && c.Submarine == Submarine)
    // "pattern": same expression on stand-in characters.
    // ---------------------------------------------------------------------------------------------------------

    private sealed class FakeCharacter { public bool IsDead; public object? Submarine; }

    [BenchmarkFact]
    public void H3_CorpseCount()
    {
        var sub = new object();
        foreach ((int characters, int dead) in new[] { (30, 5), (60, 20), (120, 60) })
        {
            var list = Enumerable.Range(0, characters)
                .Select(i => new FakeCharacter { IsDead = i < dead, Submarine = sub }).ToList();
            int sink = 0;

            Measure($"pattern: Count(...) per dead character, {characters} characters / {dead} dead (one tick)", 20_000, () =>
            {
                foreach (var c in list)
                {
                    if (!c.IsDead) { continue; }
                    sink += list.Count(o => o.IsDead && o.Submarine == c.Submarine);
                }
            });

            var perSub = new Dictionary<object, int>();
            Measure($"proposal: count once per tick, {characters} characters / {dead} dead", 20_000, () =>
            {
                perSub.Clear();
                foreach (var o in list)
                {
                    if (o.IsDead && o.Submarine != null) { perSub[o.Submarine] = perSub.GetValueOrDefault(o.Submarine) + 1; }
                }
                foreach (var c in list)
                {
                    if (!c.IsDead) { continue; }
                    sink += perSub[c.Submarine!];
                }
            });
            GC.KeepAlive(sink);
        }
        Report("H3: corpse count (pattern)");
        Assert.All(results, r => Assert.True(r.NsPerOp > 0));
    }
}
