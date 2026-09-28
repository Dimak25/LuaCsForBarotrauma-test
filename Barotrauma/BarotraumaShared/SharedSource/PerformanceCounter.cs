using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Barotrauma
{
    public class PerformanceCounter
    {
        private readonly object mutex = new object();

        public double AverageFramesPerSecond { get; private set; }
        public double CurrentFramesPerSecond { get; private set; }

        public double AverageFramesPerSecondInPastMinute { get; private set; }

        public const int MaximumSamples = 10;

        private readonly Queue<double> sampleBuffer = new Queue<double>();

        private readonly Queue<double> averageFramesPerSecondBuffer = new Queue<double>();

        private readonly Stopwatch timer = new Stopwatch();
        private long lastSecondMark = 0;
        private long lastMinuteMark = 0;

        public class TickInfo
        {
            public Queue<long> ElapsedTicks { get; set; } = new Queue<long>();
            public long AvgTicksPerFrame { get; set; }
        }

        private readonly Dictionary<string, Queue<long>> elapsedTicks = new Dictionary<string, Queue<long>>();
        private readonly Dictionary<string, long> avgTicksPerFrame = new Dictionary<string, long>();

#if CLIENT
        internal Graph UpdateTimeGraph = new Graph(500), DrawTimeGraph = new Graph(500);
#endif

        private readonly List<string> tempSavedIdentifiers = new List<string>();

        public IReadOnlyList<string> GetSavedIdentifiers
        {
            get 
            {
                lock (mutex)
                {
                    tempSavedIdentifiers.Clear();
                    tempSavedIdentifiers.AddRange(avgTicksPerFrame.Keys);
                }
                return tempSavedIdentifiers;
            }
        }

        /// <summary>
        /// Managed memory allocated by the calling thread (the main thread, when called from the game loop) per second,
        /// in kilobytes. Diagnostic only: shown in the `showperf` overlay to spot allocation-heavy code paths.
        /// </summary>
        public double AllocatedKilobytesPerSecond { get; private set; }
        public double Gen0CollectionsPerSecond { get; private set; }
        public double Gen1CollectionsPerSecond { get; private set; }
        public double Gen2CollectionsPerSecond { get; private set; }

        private long lastAllocatedBytes = -1;
        private int lastGen0Count, lastGen1Count, lastGen2Count;

        /// <summary>
        /// Call once per measurement interval from the thread whose allocations should be measured.
        /// </summary>
        /// <param name="elapsedSeconds">Time since the previous call.</param>
        public void SampleGcStats(double elapsedSeconds)
        {
            long allocatedBytes = System.GC.GetAllocatedBytesForCurrentThread();
            int gen0 = System.GC.CollectionCount(0), gen1 = System.GC.CollectionCount(1), gen2 = System.GC.CollectionCount(2);
            if (lastAllocatedBytes >= 0 && elapsedSeconds > 0.0)
            {
                AllocatedKilobytesPerSecond = (allocatedBytes - lastAllocatedBytes) / 1024.0 / elapsedSeconds;
                Gen0CollectionsPerSecond = (gen0 - lastGen0Count) / elapsedSeconds;
                Gen1CollectionsPerSecond = (gen1 - lastGen1Count) / elapsedSeconds;
                Gen2CollectionsPerSecond = (gen2 - lastGen2Count) / elapsedSeconds;
            }
            lastAllocatedBytes = allocatedBytes;
            lastGen0Count = gen0;
            lastGen1Count = gen1;
            lastGen2Count = gen2;
        }

        public PerformanceCounter()
        {
            timer.Start();
        }

        public void AddElapsedTicks(string identifier, long ticks)
        {
            lock (mutex)
            {
                if (!elapsedTicks.ContainsKey(identifier)) { elapsedTicks.Add(identifier, new Queue<long>()); }
                elapsedTicks[identifier].Enqueue(ticks);

                if (elapsedTicks[identifier].Count > MaximumSamples)
                {
                    elapsedTicks[identifier].Dequeue();
                    avgTicksPerFrame[identifier] = (long)elapsedTicks[identifier].Average(i => i);
                }
            }
        }

        public float GetAverageElapsedMillisecs(string identifier)
        {
            long ticksPerFrame = 0;
            lock (mutex)
            {
                avgTicksPerFrame.TryGetValue(identifier, out ticksPerFrame);
            }
            return ticksPerFrame * 1000.0f / Stopwatch.Frequency;
        }

        public bool Update(double deltaTime)
        {
            if (deltaTime == 0.0f) { return false; }

            CurrentFramesPerSecond = 1.0 / deltaTime;

            sampleBuffer.Enqueue(CurrentFramesPerSecond);
            if (sampleBuffer.Count > MaximumSamples)
            {
                sampleBuffer.Dequeue();
                AverageFramesPerSecond = sampleBuffer.Average();
            }
            else
            {
                AverageFramesPerSecond = CurrentFramesPerSecond;
            }

            long currentTime = timer.ElapsedMilliseconds;
            long currentSecond = currentTime / 1000;


            if (currentSecond > lastSecondMark)
            {
                averageFramesPerSecondBuffer.Enqueue(AverageFramesPerSecond);
                lastSecondMark = currentSecond;
            }

            if (currentTime - lastMinuteMark >= 60 * 1000 &&
                /* we don't need info of the FPS every minute, we can get a good sample size just by logging a small sample */
                GameAnalyticsManager.ShouldLogRandomSample())
            {
                //the FPS could be even higher than this on a high-end monitor, but let's restrict it to 144 to reduce the number of distinct event IDs
                const int MaxFPS = 144;
                AverageFramesPerSecondInPastMinute = averageFramesPerSecondBuffer.Average();
                GameAnalyticsManager.AddDesignEvent($"FPS:{MathHelper.Clamp((int)AverageFramesPerSecondInPastMinute, 0, MaxFPS)}");
                GameAnalyticsManager.AddDesignEvent($"FPSLowest:{MathHelper.Clamp((int)averageFramesPerSecondBuffer.Min(), 0, MaxFPS)}");
                averageFramesPerSecondBuffer.Clear();
                lastMinuteMark = currentTime;
            }

            return true;
        }
    }    
}
