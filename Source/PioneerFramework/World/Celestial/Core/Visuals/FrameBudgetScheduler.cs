// ─────────────────────────────────────────────────────────────────────────────
// 纯核（无 Verse / Unity / RimWorld —— 本文件被 PioneerFramework.Pure.Tests 通配链接，定义 PURE_MATH）。
//
// 为什么要有它：参考模组把所有主线程长任务（逐格上色、分块建 Mesh）都做成"每帧只花 4 ms"的
// Step() 状态机（CelestialLazySurfaceRenderer.cs 的 Advance / AdvancePreview：预算判定在 :88-89 /
// :108-109，do-while 的收尾条件在 :100 / :120）。不这么拆，进近景时一次构建会把主线程按住几百毫秒。
// 这里把那份状态机抽成**与引擎无关的通用调度器**：
//   · 参考模组用 Unity 的 Time.frameCount 判断"新的一帧"，这里改成"一次 Pump 调用 = 一帧"，
//     于是纯测试可以手推帧；预算按 Stopwatch 实测本次调用里的真实耗时累计（不估算、不依赖帧率）；
//   · 参考模组每帧对每个天体的缓存各调一次 Advance，天然公平；换成共享队列后必须显式做
//     round-robin 游标，否则一个吃满预算的任务永远堵在队首，后面的任务永远轮不到（饥饿）。
//
// 语义约定（测试钉住，改动前先看 Pure.Tests/Visual/FrameBudgetSchedulerTests.cs）：
//   · step 返回 true = 该任务已完成 ⇒ 移出队列并 CompletedCount++；返回 false = 下帧继续；
//   · step 抛异常 ⇒ 该任务移出队列、记录 LastFaultKey / LastFaultMessage、Pump 不向上抛
//     （否则一个坏任务会每帧把调用方整条渲染路径炸掉），抛异常的那次调用**计入 ran**（它确实执行了），
//     但**不计入 completed / CompletedCount**（没完成就不许算完成，否则诊断会骗人）；
//   · 每个 pending 任务在一次 Pump 里最多执行一次（Step 语义，与参考模组"每帧每个缓存一次"一致），
//     ⇒ "跑满一遍"即结束，不会在同一帧里把同一个任务反复推进；
//   · 预算判据用"已经花掉的时间 ≥ 预算"：预算 0 时仍保证至少跑一个（对应参考模组 do-while 的
//     "至少推进一次"），否则零预算会让加载永远停在第一格；
//   · Clear() 只清队列与游标；CompletedCount / LastFault* 是累积诊断，不清空。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PioneerFramework.Celestial;

/// <summary>
/// 主线程分帧状态机（参考模组"4 ms/帧"口径的纯托管版本）。
/// 每次 <see cref="Pump"/> = 游戏里的一帧：按插入顺序（round-robin 起点）各推进一步，直到预算耗尽。
/// </summary>
public sealed class FrameBudgetScheduler
{
    private sealed class Entry
    {
        public Entry(string key, Func<bool> step)
        {
            Key = key;
            Step = step;
        }

        public string Key { get; }

        // 同 key 重复入队只替换委托、保留原插入位置 ⇒ 队列顺序不会因"重建同一个任务"而抖动。
        public Func<bool> Step { get; set; }
    }

    private readonly List<Entry> entries = new();
    private readonly Dictionary<string, int> indexByKey = new(StringComparer.Ordinal);
    private readonly Stopwatch watch = new();

    private double budgetMilliseconds;

    // 下一帧从 entries 的哪个下标开始（0..entries.Count-1；空队列时为 0）。
    private int cursor;

    public FrameBudgetScheduler(double budgetMilliseconds = 4.0)
    {
        BudgetMilliseconds = budgetMilliseconds;
    }

    /// <summary>
    /// 本帧预算（毫秒）。负值 / NaN 夹到 0（此时每次 Pump 仍至少推进一个任务）；+∞ 视为不限。
    /// 之所以"夹取"而不是抛：调用方可能把用户可调的滑条直接接进来，热路径上抛异常没有意义。
    /// </summary>
    public double BudgetMilliseconds
    {
        get => budgetMilliseconds;
        set => budgetMilliseconds = NormalizeBudget(value);
    }

    /// <summary>还在队列里的任务数。</summary>
    public int PendingCount => entries.Count;

    /// <summary>累计完成的任务次数（step 返回 true 的次数；异常任务不计）。</summary>
    public long CompletedCount { get; private set; }

    /// <summary>最近一次抛异常的 key（没有异常时为 null；成功后不清空，便于事后定位）。</summary>
    public string? LastFaultKey { get; private set; }

    /// <summary>最近一次抛异常的 Exception.Message。</summary>
    public string? LastFaultMessage { get; private set; }

    /// <summary>
    /// 入队（或替换同 key 的 step）。同 key 覆盖并保留原位置。
    /// step 返回 true 表示该任务完成，会被移出队列。
    /// </summary>
    public void Enqueue(string key, Func<bool> step)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(step);
        if (indexByKey.TryGetValue(key, out int index))
        {
            entries[index].Step = step;
            return;
        }

        indexByKey.Add(key, entries.Count);
        entries.Add(new Entry(key, step));
    }

    /// <summary>移除一个任务；不存在返回 false。移除位置之前的游标会相应前移，避免跳过任务。</summary>
    public bool Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!indexByKey.TryGetValue(key, out int index)) return false;
        RemoveEntryAt(index);
        if (cursor > index) cursor--;
        cursor = NormalizeCursor(cursor);
        return true;
    }

    /// <summary>清空队列与游标（累积诊断 CompletedCount / LastFault* 不清空）。</summary>
    public void Clear()
    {
        entries.Clear();
        indexByKey.Clear();
        cursor = 0;
    }

    /// <summary>
    /// 推进一帧。按插入顺序从 round-robin 游标开始，每个任务最多跑一步；累计耗时达到预算即停
    /// （至少跑一个）。返回 true 表示队列里还有任务（下一帧要继续 Pump）。
    /// </summary>
    /// <param name="ran">本次实际执行了 step 的次数（含抛异常的那一次）。</param>
    /// <param name="completed">本次完成（step 返回 true）的任务数。</param>
    public bool Pump(out int ran, out int completed)
    {
        ran = 0;
        completed = 0;

        int pending = entries.Count;
        if (pending == 0)
        {
            cursor = 0;
            return false;
        }

        double budget = budgetMilliseconds;
        watch.Reset();
        watch.Start();

        int index = cursor;
        if (index < 0 || index >= pending) index = 0;

        // pending 是本次 Pump 开始时的人数；每个任务最多轮到一次（"跑满一遍"）。
        for (int i = 0; i < pending; i++)
        {
            // 队列可能被上一个 step 清空/改写（如 Clear()），先兜住再判断预算。
            if (entries.Count == 0) break;
            if (ran > 0 && watch.Elapsed.TotalMilliseconds >= budget) break;
            if (index >= entries.Count) index = 0;

            Entry entry = entries[index];
            bool done;
            try
            {
                done = entry.Step();
            }
            catch (Exception error)
            {
                ran++;
                LastFaultKey = entry.Key;
                LastFaultMessage = error.Message;
                // 坏任务直接摘除：留着会让同一个异常每帧重复抛、把日志刷满，且调度器无法区分"暂时失败"。
                // step 自己也可能已经 Remove/Clear 过队列 ⇒ 先确认它还在这个下标上。
                if (index < entries.Count && ReferenceEquals(entries[index], entry)) RemoveEntryAt(index);
                continue;   // 下标不动：后面的任务滑进同一个位置
            }

            ran++;
            if (done)
            {
                completed++;
                CompletedCount++;
                if (index < entries.Count && ReferenceEquals(entries[index], entry)) RemoveEntryAt(index);
            }
            else
            {
                index++;   // 未完成：下帧从下一个任务开始（本轮内继续按序推进）
            }
        }

        watch.Stop();
        cursor = NormalizeCursor(index);
        return entries.Count > 0;
    }

    private static double NormalizeBudget(double value)
    {
        if (double.IsNaN(value) || value < 0.0) return 0.0;
        if (double.IsPositiveInfinity(value)) return double.MaxValue;
        return value;
    }

    private int NormalizeCursor(int value)
    {
        if (entries.Count == 0) return 0;
        if (value < 0 || value >= entries.Count) return 0;
        return value;
    }

    private void RemoveEntryAt(int index)
    {
        indexByKey.Remove(entries[index].Key);
        entries.RemoveAt(index);
        for (int i = index; i < entries.Count; i++) indexByKey[entries[i].Key] = i;
    }
}
