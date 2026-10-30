using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    public enum TimeScaleLevel
    {
        Combat = 0,
        Normal = 1,
        Fast = 2
    }

    [Flags]
    public enum TimeScaleRestriction
    {
        None = 0,
        Combat = 1,
        TerminalPrecision = 2,
        Maneuver = 4
    }

    /// <summary>不可变物理区间。时间单位沿用物理秒，游戏 tick 仅标记调度边界。</summary>
    internal readonly record struct PhysicsStep(int StartGameTick, int EndGameTick,
        double StartTick, double EndTick, TimeScaleLevel Level)
    {
        public double Duration => EndTick - StartTick;
        public int SubSteps => Level == TimeScaleLevel.Fast ? Math.Max(1, (EndGameTick - StartGameTick) * 4) : 1;
    }

    internal readonly record struct PhysicsTimeEpoch(int GameTick, double PhysicsTick, TimeScaleLevel Level);

    /// <summary>
    /// 分段线性时钟与已提交物理位置的共同时间账本。切档保留尚未结算的旧区间，
    /// 调度器逐段提交；保留最近两个游戏 tick 的历史供渲染插值，避免跨档重写旧相位。
    /// 无 Verse/Unity 依赖，游戏存档适配由 TimeScaleManager 负责。
    /// </summary>
    internal sealed class PhysicsTimeLine
    {
        private readonly List<PhysicsTimeEpoch> epochs;
        public IReadOnlyList<PhysicsTimeEpoch> Epochs => epochs;
        public int CommittedGameTick { get; private set; }
        public double CommittedPhysicsTick { get; private set; }
        public int OldestGameTick => epochs[0].GameTick;

        public PhysicsTimeLine(int gameTick, double physicsTick, TimeScaleLevel level)
            : this(new[] { new PhysicsTimeEpoch(gameTick, physicsTick, level) }, gameTick, physicsTick) { }

        public PhysicsTimeLine(IReadOnlyList<PhysicsTimeEpoch> history, int committedGameTick, double committedPhysicsTick)
        {
            if (history.Count == 0 || !double.IsFinite(committedPhysicsTick))
                throw new ArgumentException("Missing or invalid physical clock history.", nameof(history));
            epochs = new List<PhysicsTimeEpoch>(history);
            for (int i = 0; i < epochs.Count; i++)
            {
                if (!double.IsFinite(epochs[i].PhysicsTick) || !Enum.IsDefined(epochs[i].Level)
                    || (i > 0 && (epochs[i].GameTick <= epochs[i - 1].GameTick
                        || epochs[i].PhysicsTick != Evaluate(epochs[i - 1], epochs[i].GameTick))))
                    throw new ArgumentException("Physical clock epochs are not continuous.", nameof(history));
            }
            CommittedGameTick = committedGameTick;
            CommittedPhysicsTick = committedPhysicsTick;
            if (committedGameTick < OldestGameTick || At(committedGameTick) != committedPhysicsTick)
                throw new ArgumentException("Physical state does not match its committed clock.", nameof(committedPhysicsTick));
        }

        public static double Scale(TimeScaleLevel level) => level switch
        {
            TimeScaleLevel.Combat => 1.0 / 60.0,
            TimeScaleLevel.Fast => 8.0,
            _ => 1.0
        };

        private static double Evaluate(PhysicsTimeEpoch epoch, double gameTick)
            => epoch.PhysicsTick + (gameTick - epoch.GameTick) * Scale(epoch.Level);

        private int EpochIndex(double gameTick)
        {
            int index = epochs.Count - 1;
            while (index > 0 && epochs[index].GameTick > gameTick) index--;
            return index;
        }

        public double At(double gameTick) => Evaluate(epochs[EpochIndex(gameTick)], gameTick);

        public void SetRate(int gameTick, TimeScaleLevel level)
        {
            if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
            if (gameTick < epochs[^1].GameTick || gameTick < CommittedGameTick)
                throw new InvalidOperationException("Cannot change an already elapsed clock interval.");
            if (epochs[^1].Level == level) return;
            var epoch = new PhysicsTimeEpoch(gameTick, At(gameTick), level);
            if (epochs[^1].GameTick == gameTick) epochs[^1] = epoch;
            else epochs.Add(epoch);
        }

        public PhysicsStep? NextStep(int endGameTick)
        {
            if (endGameTick <= CommittedGameTick) return null;
            int index = EpochIndex(CommittedGameTick);
            int end = index + 1 < epochs.Count ? Math.Min(endGameTick, epochs[index + 1].GameTick) : endGameTick;
            return new PhysicsStep(CommittedGameTick, end, CommittedPhysicsTick, At(end), epochs[index].Level);
        }

        public void SplitAt(int gameTick)
        {
            if (gameTick < CommittedGameTick || gameTick < epochs[^1].GameTick)
                throw new InvalidOperationException("Cannot insert a physical event into committed history.");
            if (gameTick > epochs[^1].GameTick)
                epochs.Add(new PhysicsTimeEpoch(gameTick, At(gameTick), epochs[^1].Level));
        }

        public void Commit(PhysicsStep step)
        {
            if (step.StartGameTick != CommittedGameTick || step.StartTick != CommittedPhysicsTick
                || step.EndGameTick <= step.StartGameTick || step.EndTick != At(step.EndGameTick))
                throw new InvalidOperationException("Physical intervals must be committed once and in order.");
            CommittedGameTick = step.EndGameTick;
            CommittedPhysicsTick = step.EndTick;
            while (epochs.Count > 1 && epochs[1].GameTick <= CommittedGameTick - 2) epochs.RemoveAt(0);
        }
    }
}
