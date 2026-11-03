// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>保留旧公开请求结构；请求进入统一仲裁，不再按历史命令排队。</summary>
    public struct TimeScaleCommand
    {
        public TimeScaleLevel TargetLevel;
        public int RequestTick;
        public int ExecuteAfterTick;
        public static TimeScaleCommand ToCombat() => new() { TargetLevel = TimeScaleLevel.Combat, RequestTick = Find.TickManager.TicksGame };
        public static TimeScaleCommand ToNormal() => new() { TargetLevel = TimeScaleLevel.Normal, RequestTick = Find.TickManager.TicksGame };
        public static TimeScaleCommand ToFast() => new() { TargetLevel = TimeScaleLevel.Fast, RequestTick = Find.TickManager.TicksGame };
    }

    /// <summary>
    /// 游戏速度意图、精度限制和物理时钟的统一入口。2026-10-22：取消偶数 tick 队列与超时绕闸；
    /// 物理调度器逐段提交切档区间，步内消费者读取同一不可变时刻。宿主倍率保持原版语义。
    /// </summary>
    public partial class TimeScaleManager : GameComponent
    {
        private TimeScaleLevel currentLevel = TimeScaleLevel.Normal;
        private TimeScaleLevel requestedLevel = TimeScaleLevel.Normal;
        private int combatStartTick;
        private double calendarBaseTick;
        private int switchStartTick;
        private double gameStartAbsTickCapture;
        private bool calendarInitialized;
        private PhysicsTimeLine? timeline;
        private PhysicsStep? activeStep;
        private bool batchActive;
        private bool resolving;
        private bool resolveAgain;
        private bool combatEntryRequested;
        private bool combatCacheDirty;
        private readonly GameComponentRef<Combat.CombatManager> combatMgrRef = new();
        private readonly GameComponentRef<AutonomousVehicleManager> avmRef = new();
        private readonly GameComponentRef<SpaceGameComp> spaceRef = new();
        private readonly GameComponentRef<CelestialBodyManager> celestialRef = new();

        public TimeScaleLevel CurrentLevel => activeStep?.Level ?? currentLevel;
        public TimeScaleLevel RequestedLevel => requestedLevel;
        public TimeScaleRestriction Restrictions { get; private set; }
        internal string StatusLabel
        {
            get
            {
                string scale = CurrentLevel switch { TimeScaleLevel.Combat => "1/60", TimeScaleLevel.Fast => "8", _ => "1" };
                string label = "PFV_TimeSpaceRate".Translate(scale);
                string? reason = RestrictionKey;
                return reason == null ? label : label + " · " + reason.Translate();
            }
        }
        private string? RestrictionKey => (Restrictions & TimeScaleRestriction.Combat) != 0 ? "PFV_TimeCombat"
            : (Restrictions & TimeScaleRestriction.TerminalPrecision) != 0 ? "PFV_TimeTerminal"
            : (Restrictions & TimeScaleRestriction.Maneuver) != 0 ? "PFV_TimeManeuver" : null;
        internal string StatusTip => "PFV_TimeStatusTip".Translate(StatusLabel,
            RestrictionKey == null ? "PFV_TimeUnrestricted".Translate().ToString() : RestrictionKey.Translate().ToString());
        public bool IsCombatMode => CurrentLevel == TimeScaleLevel.Combat;
        public float CalendarSpeedMultiplier => PhysicsSpeedMultiplier;
        public float PhysicsSpeedMultiplier => CurrentLevel switch
        {
            TimeScaleLevel.Combat => 1f / 60f,
            TimeScaleLevel.Fast => 8f,
            _ => 1f
        };
        public int SubSteps => activeStep?.SubSteps ?? (CurrentLevel == TimeScaleLevel.Fast ? 8 : 1);
        internal PhysicsStep? CurrentPhysicsStep => activeStep;
        internal bool IsPhysicsBatchActive => batchActive;
        internal int CommittedGameTick { get { EnsureTimeline(); return timeline!.CommittedGameTick; } }
        internal double CommittedPhysicsTick { get { EnsureTimeline(); return timeline!.CommittedPhysicsTick; } }
        public int GetCombatStartTick() => combatStartTick;
        public event Action<TimeScaleLevel, TimeScaleLevel>? OnTimeScaleChanged;

        public TimeScaleManager(Game game) { }

        private void EnsureCalendarBaseline()
        {
            if (calendarInitialized) return;
            // gameStartAbsTick 在新局早期可能仍为 0：捕获值必须同时用于基线与减法，保持旧存档锚点自洽。
            gameStartAbsTickCapture = Find.TickManager.gameStartAbsTick;
            calendarBaseTick = Find.TickManager.TicksGame + gameStartAbsTickCapture;
            switchStartTick = Find.TickManager.TicksGame;
            calendarInitialized = true;
        }

        private void EnsureTimeline()
        {
            EnsureCalendarBaseline();
            timeline ??= new PhysicsTimeLine(Find.TickManager.TicksGame,
                calendarBaseTick + (Find.TickManager.TicksGame - switchStartTick) * PhysicsTimeLine.Scale(currentLevel)
                    - gameStartAbsTickCapture, currentLevel);
        }

        public double GetPhysicsTick()
        {
            EnsureTimeline();
            if (activeStep.HasValue) return activeStep.Value.EndTick;
            return celestialRef.Get()?.TryGetCelestialRuntime(out _) == true ? timeline!.CommittedPhysicsTick : timeline!.At(Find.TickManager.TicksGame);
        }

        public double GetPhysicsTick(int realTick)
        {
            EnsureTimeline();
            return timeline!.At(realTick);
        }

        private static Game cachedGame = null!;
        private static TimeScaleManager cachedTsm = null!;
        public static double GetPhysicsTickSafe()
        {
            var game = Current.Game;
            if (game == null) return Find.TickManager.TicksGame;
            if (cachedTsm == null || !ReferenceEquals(cachedGame, game))
            {
                cachedTsm = game.GetComponent<TimeScaleManager>();
                cachedGame = game;
            }
            return cachedTsm != null ? cachedTsm.GetPhysicsTick() : Find.TickManager.TicksGame;
        }

        public double GetDisplayTicksAbs() => GetPhysicsTick() + gameStartAbsTickCapture;

        public void QueueCommand(TimeScaleCommand command) => SetTimeScale(command.TargetLevel);
        public void SetTimeScale(TimeScaleLevel level)
        {
            if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
            requestedLevel = level;
            RefreshConstraints();
        }
        public void SetCombatMode() => SetTimeScale(TimeScaleLevel.Combat);
        public void SetNormalMode() => SetTimeScale(TimeScaleLevel.Normal);
        public void SetFastMode() => SetTimeScale(TimeScaleLevel.Fast);

        /// <summary>立即记录精度要求；步间保留旧时间段，步内等全部消费者完成后提交。</summary>
        public void SetCombatModeImmediate()
        {
            combatEntryRequested = true;
            RefreshConstraints();
        }

        /// <summary>唯一战斗判据：射弹在飞或导弹 Attack 终末段。解析轨降级继续共用此入口。</summary>
        public bool IsInCombatAction()
            => combatMgrRef.Get()?.IsBattleActive == true || avmRef.Get()?.HasActiveAttack == true;

        internal void RefreshConstraints()
        {
            if (batchActive) return;
            if (resolving)
            {
                resolveAgain = true;
                return;
            }
            resolving = true;
            try
            {
                do
                {
                    resolveAgain = false;
                    ResolveCurrentPreference();
                }
                while (resolveAgain);
            }
            finally { resolving = false; }
        }

        private void ResolveCurrentPreference()
        {
            EnsureTimeline();
            var space = spaceRef.Get();
            Restrictions = TimeScaleRestriction.None;
            if (combatEntryRequested || IsInCombatAction()) Restrictions |= TimeScaleRestriction.Combat;
            if (space?.RequiresTerminalPrecision == true) Restrictions |= TimeScaleRestriction.TerminalPrecision;
            if (space?.HasTrackedManeuvers == true) Restrictions |= TimeScaleRestriction.Maneuver;
            var target = (Restrictions & (TimeScaleRestriction.Combat | TimeScaleRestriction.TerminalPrecision)) != 0
                ? TimeScaleLevel.Combat
                : requestedLevel == TimeScaleLevel.Fast && (Restrictions & TimeScaleRestriction.Maneuver) != 0
                    ? TimeScaleLevel.Normal : requestedLevel;
            if (target == currentLevel) return;
            int tick = Find.TickManager.TicksGame;
            double physicsTick = timeline!.At(tick);
            if (celestialRef.Get()?.TryGetCelestialRuntime(out _) == true) physicsTick = Math.Min(physicsTick, timeline.CommittedPhysicsTick);
            var previous = currentLevel;
            timeline.SetRate(tick, target);
            currentLevel = target;
            calendarBaseTick = physicsTick + gameStartAbsTickCapture;
            switchStartTick = tick;
            if (target == TimeScaleLevel.Combat)
            {
                combatStartTick = tick;
                combatCacheDirty = true;
            }
            OnTimeScaleChanged?.Invoke(previous, target);
        }

        // GameComponentTick 刻意不仲裁：调度器显式调用，组件列表前后顺序不会改变切档时刻。
        public override void GameComponentTick() { }

        internal void BeginPhysicsBatch()
        {
            if (batchActive) throw new InvalidOperationException("Nested physical world advancement is not allowed.");
            RefreshConstraints();
            batchActive = true;
        }

        internal bool TryBeginPhysicsStep(int endGameTick)
        {
            if (!batchActive || activeStep.HasValue) throw new InvalidOperationException("Unfinished physical interval.");
            activeStep = timeline!.NextStep(endGameTick);
            return activeStep.HasValue;
        }

        internal void CompletePhysicsStep()
        {
            if (!activeStep.HasValue) throw new InvalidOperationException("No physical interval to commit.");
            timeline!.Commit(activeStep.Value);
            activeStep = null;
        }

        internal void EndPhysicsBatch()
        {
            activeStep = null;
            batchActive = false;
            combatEntryRequested = false;
            RefreshConstraints();
            if (combatCacheDirty && currentLevel == TimeScaleLevel.Combat)
            {
                combatCacheDirty = false;
                spaceRef.Get()?.RebuildAllCombatCaches(Find.TickManager.TicksGame);
            }
        }

        internal double PhysicsStepSeconds => activeStep?.Duration ?? SpaceGameComp.UPDATE_INTERVAL_TICKS * PhysicsSpeedMultiplier;

        internal void MarkPhysicsEvent()
        {
            EnsureTimeline();
            if (!batchActive) timeline!.SplitAt(Find.TickManager.TicksGame);
        }

        public void SetGameSpeed(TimeSpeed speed)
        {
            var ticks = Find.TickManager;
            if (speed == TimeSpeed.Paused)
            {
                if (ticks.CurTimeSpeed != TimeSpeed.Paused) ticks.TogglePaused();
            }
            else ticks.CurTimeSpeed = speed;
            renderClockTicksGame = int.MinValue;
            renderClockFrac = 0;
            renderClockSecPerTick = 1.0 / (60 * (speed switch
            {
                TimeSpeed.Fast => 3,
                TimeSpeed.Superfast => 6,
                TimeSpeed.Ultrafast => 15,
                _ => 1
            }));
        }

        private int renderClockTicksGame = int.MinValue;
        private int renderClockFrame = int.MinValue;
        private double renderClockFrac;
        private double renderClockSecPerTick = 1.0 / 60;
        private double renderClockValue = double.NegativeInfinity;

        /// <summary>
        /// 表示层在前一个宿主 tick 与当前 tick 之间插值，最多延后一个宿主 tick；
        /// 不外推到尚未经过的物理时间。历史倍率由账本求值，跨档不会收回未来预测位置。
        /// 同一帧所有读者共享一次采样；暂停直接落到物理时刻。
        /// </summary>
        public double GetRenderTick()
        {
            EnsureTimeline();
            var bounds = GetCelestialRenderBounds();
            if (renderClockFrame == RealTime.frameCount) return renderClockValue = BoundRenderTime(renderClockValue, bounds);
            renderClockFrame = RealTime.frameCount;
            int tick = Find.TickManager.TicksGame;
            double physicsTick = timeline!.At(tick);
            if (Find.TickManager.CurTimeSpeed == TimeSpeed.Paused)
            {
                renderClockTicksGame = tick;
                renderClockFrac = 0;
                return renderClockValue = BoundRenderTime(physicsTick, bounds);
            }
            double dt = RealTime.deltaTime;
            if (dt > 0 && dt < 0.5) renderClockFrac += dt / renderClockSecPerTick;
            if (tick != renderClockTicksGame)
            {
                if (renderClockTicksGame != int.MinValue)
                {
                    int elapsed = tick - renderClockTicksGame;
                    if (elapsed > 0 && dt > 0 && dt < 0.5)
                        renderClockSecPerTick = renderClockSecPerTick * 0.75 + dt / elapsed * 0.25;
                    renderClockFrac -= elapsed;
                }
                else renderClockFrac = 0;
                renderClockTicksGame = tick;
            }
            renderClockFrac = Math.Clamp(renderClockFrac, 0, 1);
            double sample = timeline.At(Math.Max(timeline.OldestGameTick, tick - 1.0 + renderClockFrac));
            return renderClockValue = BoundRenderTime(Math.Min(physicsTick, Math.Max(renderClockValue, sample)), bounds);
        }

        private List<int> epochGameTicks = new();
        private List<double> epochPhysicsTicks = new();
        private List<TimeScaleLevel> epochLevels = new();
        private int savedCommittedGameTick;
        private double savedCommittedPhysicsTick;

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                EnsureTimeline();
                epochGameTicks.Clear(); epochPhysicsTicks.Clear(); epochLevels.Clear();
                foreach (var epoch in timeline!.Epochs)
                {
                    epochGameTicks.Add(epoch.GameTick);
                    epochPhysicsTicks.Add(epoch.PhysicsTick);
                    epochLevels.Add(epoch.Level);
                }
                savedCommittedGameTick = timeline.CommittedGameTick;
                savedCommittedPhysicsTick = timeline.CommittedPhysicsTick;
            }
            Scribe_Values.Look(ref currentLevel, "timeScaleLevel", TimeScaleLevel.Normal);
            Scribe_Values.Look(ref requestedLevel, "requestedTimeScale", TimeScaleLevel.Normal);
            Scribe_Values.Look(ref combatEntryRequested, "combatEntryRequested", false);
            Scribe_Values.Look(ref calendarBaseTick, "calendarBaseTick", 0.0);
            Scribe_Values.Look(ref switchStartTick, "switchStartTick", 0);
            Scribe_Values.Look(ref gameStartAbsTickCapture, "gameStartAbsTickCapture", 0.0);
            Scribe_Values.Look(ref calendarInitialized, "calendarInitialized", false);
            Scribe_Collections.Look(ref epochGameTicks, "physicsEpochGameTicks", LookMode.Value);
            Scribe_Collections.Look(ref epochPhysicsTicks, "physicsEpochTicks", LookMode.Value);
            Scribe_Collections.Look(ref epochLevels, "physicsEpochLevels", LookMode.Value);
            Scribe_Values.Look(ref savedCommittedGameTick, "physicsCommittedGameTick", 0);
            Scribe_Values.Look(ref savedCommittedPhysicsTick, "physicsCommittedTick", 0.0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                timeline = null;
                if (epochGameTicks != null && epochGameTicks.Count > 0)
                {
                    if (epochPhysicsTicks == null || epochLevels == null || epochGameTicks.Count != epochPhysicsTicks.Count
                        || epochGameTicks.Count != epochLevels.Count)
                        throw new InvalidOperationException("Incomplete saved physical clock history.");
                    var history = new List<PhysicsTimeEpoch>(epochGameTicks.Count);
                    for (int i = 0; i < epochGameTicks.Count; i++)
                        history.Add(new PhysicsTimeEpoch(epochGameTicks[i], epochPhysicsTicks[i], epochLevels[i]));
                    timeline = new PhysicsTimeLine(history, savedCommittedGameTick, savedCommittedPhysicsTick);
                }
                epochGameTicks ??= new(); epochPhysicsTicks ??= new(); epochLevels ??= new();
                combatCacheDirty = currentLevel == TimeScaleLevel.Combat;
            }
        }
    }
}
