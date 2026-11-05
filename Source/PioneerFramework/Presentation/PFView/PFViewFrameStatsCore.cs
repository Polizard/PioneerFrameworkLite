using System;

namespace PioneerFramework
{
    /// <summary>
    /// P0 恒星性能探针的**纯统计核**（不依赖 Unity/Verse ⇒ headless 可测；2026-10-28）。
    ///
    /// 为什么要有它：恒星"拉近掉帧"的验收只能靠帧时数字（本机无 GPU 计时 API），
    /// 而帧时会抖、会有离群尖峰 ⇒ 只看单帧或平均值会误判。这里固定保存最近 N 帧，
    /// 对外只给**中位 / p99 / 均值 / 样本数**四个读数，让 A/B 比较有可比口径。
    ///
    /// 纪律：
    /// · 非法输入（NaN/Inf/负数）**丢弃而不污染统计**——否则一次坏采样会把 p99 永远钉死；
    /// · 容量固定、不分配：每帧只写一个 float + 做整数取模（探针关闭时不调用）；
    /// · 百分位用 nearest-rank：`index = ceil(p/100 * count) - 1`，p50 在偶数样本时取下中位
    ///   （与"中位数"通常定义一致；测试钉死这一口径，避免两处实现漂移）。
    /// </summary>
    internal sealed class PFViewFrameStatsCore
    {
        private readonly float[] samples;
        private readonly float[] sortBuffer;
        private int next;
        private int count;

        public PFViewFrameStatsCore(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            samples = new float[capacity];
            sortBuffer = new float[capacity];
        }

        /// <summary>环形缓冲容量（帧）。</summary>
        public int Capacity => samples.Length;

        /// <summary>当前保存的有效样本数（≤ Capacity）。</summary>
        public int Count => count;

        /// <summary>最近一次成功采样（毫秒）；无样本时为 0。</summary>
        public float Last => count == 0 ? 0f : samples[(next - 1 + samples.Length) % samples.Length];

        /// <summary>清空。切换距离档/开关时调用，避免把上一个样本段的帧时混进来。</summary>
        public void Reset()
        {
            next = 0;
            count = 0;
        }

        /// <summary>写入一帧毫秒；非法值返回 false 且不改变统计。超过容量时覆盖最旧样本。</summary>
        public bool Add(float milliseconds)
        {
            if (!float.IsFinite(milliseconds) || milliseconds < 0f) return false;
            samples[next] = milliseconds;
            next = (next + 1) % samples.Length;
            if (count < samples.Length) count++;
            return true;
        }

        /// <summary>算术平均（毫秒）；无样本时为 0。</summary>
        public float Average()
        {
            if (count == 0) return 0f;
            double sum = 0d;
            for (int i = 0; i < count; i++) sum += samples[i];
            return (float)(sum / count);
        }

        /// <summary>中位数（毫秒）；无样本时为 0。</summary>
        public float Median() => Percentile(50f);

        /// <summary>p99（毫秒）；无样本时为 0。样本少时等于最大值。</summary>
        public float P99() => Percentile(99f);

        /// <summary>nearest-rank 百分位（毫秒）。percentile 夹到 [0,100]；无样本时为 0。</summary>
        public float Percentile(float percentile)
        {
            if (count == 0) return 0f;
            if (!float.IsFinite(percentile)) return 0f;
            percentile = Math.Clamp(percentile, 0f, 100f);
            Array.Copy(samples, sortBuffer, count);
            Array.Sort(sortBuffer, 0, count);
            int rank = (int)MathF.Ceiling(percentile / 100f * count) - 1;
            if (rank < 0) rank = 0;
            else if (rank >= count) rank = count - 1;
            return sortBuffer[rank];
        }
    }
}
