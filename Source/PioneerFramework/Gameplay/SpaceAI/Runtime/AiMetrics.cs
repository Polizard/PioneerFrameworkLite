using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Bounded main-thread samples; percentile allocation happens only on explicit report.</summary>
    public sealed class AiMetrics
    {
        private readonly Dictionary<AiMetricStage,AiMetrics> stages = new();
        private readonly double[] samples = new double[1024];
        private int count, cursor;
        private long allocated;
        public void Record(double milliseconds, long bytes)
        { samples[cursor] = milliseconds; cursor = (cursor + 1) % samples.Length; count = Math.Min(count + 1, samples.Length); allocated += bytes; }
        public AiMetricReport Report()
        {
            if (count == 0) return default;
            var copy = new double[count];
            Array.Copy(samples, copy, count); Array.Sort(copy);
            double total = 0; for (int i = 0; i < count; i++) total += copy[i];
            return new(count, total / count, copy[(int)Math.Ceiling(count * .95) - 1],
                copy[(int)Math.Ceiling(count * .99) - 1], copy[count - 1], allocated);
        }
        public void RecordStage(AiMetricStage stage,double milliseconds)
        {
            if (!stages.TryGetValue(stage,out var window)) stages[stage] = window = new();
            window.Record(milliseconds,0);
        }
        public AiMetricReport Report(AiMetricStage stage) => stages.TryGetValue(stage,out var window) ? window.Report() : default;
        public AiTimingScope Measure(AiMetricStage stage) => new(this,stage);
    }
    public enum AiMetricStage { Snapshot, Decision, Planning, Submission, Generation }
    public readonly struct AiTimingScope : IDisposable
    {
        private readonly AiMetrics owner;
        private readonly AiMetricStage stage;
        private readonly long start;
        internal AiTimingScope(AiMetrics owner,AiMetricStage stage) { this.owner=owner;this.stage=stage;start=Stopwatch.GetTimestamp(); }
        public void Dispose() => owner.RecordStage(stage,Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    public readonly record struct AiMetricReport(int Samples, double MeanMilliseconds, double P95Milliseconds,
        double P99Milliseconds, double MaxMilliseconds, long AllocatedBytes);
}
