using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    public enum TimeScaleLevel
    {
        Combat = 0,
        Normal = 1,
        Fast = 2
    }

    public struct TimeScaleCommand
    {
        public TimeScaleLevel TargetLevel;
        public int RequestTick;
        public int ExecuteAfterTick;

        public static TimeScaleCommand ToCombat() => new() {
            TargetLevel = TimeScaleLevel.Combat,
            RequestTick = Find.TickManager.TicksGame
        };

        public static TimeScaleCommand ToNormal() => new() {
            TargetLevel = TimeScaleLevel.Normal,
            RequestTick = Find.TickManager.TicksGame
        };

        public static TimeScaleCommand ToFast() => new() {
            TargetLevel = TimeScaleLevel.Fast,
            RequestTick = Find.TickManager.TicksGame
        };
    }

    public class TimeScaleManager : GameComponent
    {
        private const int UPDATE_INTERVAL_TICKS = 2;
        private const int MAX_WAIT_TICKS = 600;

        private TimeScaleLevel currentLevel = TimeScaleLevel.Normal;
        private Queue<TimeScaleCommand> commandQueue = new();
        private bool isProcessing ;
        private int combatStartTick ;
        private int combatSubTick ;
        private double calendarBaseTick ;
        private int switchStartTick ;
        private double gameStartAbsTickCapture ;
        private bool calendarInitialized ;

        public float CalendarSpeedMultiplier
        {
            get
            {
                switch (currentLevel)
                {
                    case TimeScaleLevel.Combat: return 1f / 60f;
                    case TimeScaleLevel.Normal: return 1f;
                    case TimeScaleLevel.Fast: return 8f;
                    default: return 1f;
                }
            }
        }

        public float PhysicsSpeedMultiplier
        {
            get
            {
                switch (currentLevel)
                {
                    case TimeScaleLevel.Combat: return 1f / 60f;
                    case TimeScaleLevel.Normal: return 1f;
                    case TimeScaleLevel.Fast: return 8f;
                    default: return 1f;
                }
            }
        }

        public int SubSteps
        {
            get
            {
                switch (currentLevel)
                {
                    case TimeScaleLevel.Combat: return 1;
                    case TimeScaleLevel.Normal: return 1;
                    case TimeScaleLevel.Fast: return 8;
                    default: return 1;
                }
            }
        }

        public bool IsCombatMode => currentLevel == TimeScaleLevel.Combat;

        public TimeScaleLevel CurrentLevel => currentLevel;

        public int GetCombatStartTick() => combatStartTick;

        /// <summary>
        /// 初始化历法锚点。核心修复：把 gameStartAbsTick 在首次初始化时**捕获一次并固定**，
        /// 而非每次调用都全新读取 Find.TickManager.gameStartAbsTick。
        /// 原因：Game.InitNewGame 在 (Game.cs L507) 才设置 gameStartAbsTick，Mod 的 GameComponent
        /// 首次调用本方法可能早于此（此时 gameStartAbsTick==0）→ 旧代码把 calendarBaseTick 锚定到
        /// TicksGame+0，之后再读到的真实 gSAT 相减 → physicsTick = 120 - 372500 = -372380（负且大），
        /// 污染自转相位/经度。捕获后减号与基线用同一锚点 → 自洽非负，且跨存档连续。
        /// </summary>
        private void EnsureCalendarBaseline()
        {
            if (calendarInitialized) return;
            gameStartAbsTickCapture = Find.TickManager.gameStartAbsTick;
            calendarBaseTick = Find.TickManager.TicksGame + gameStartAbsTickCapture;
            switchStartTick = Find.TickManager.TicksGame;
            calendarInitialized = true;
        }

        public double GetPhysicsTick()
        {
            EnsureCalendarBaseline();

            int realTicksGame = Find.TickManager.TicksGame;
            int deltaReal = realTicksGame - switchStartTick;
            double scale = GetCalendarScale();

            return calendarBaseTick + deltaReal * scale - gameStartAbsTickCapture;
        }

        private static Game cachedGame = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        private static TimeScaleManager cachedTsm = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>
        /// 获取当前 physicsTick（静态便捷入口）。缓存组件引用避免每 tick 几十次 GetComponent。
        /// 用 ReferenceEquals 校验缓存所属 Game，跨存档/新游戏时自动失效。
        /// </summary>
        public static double GetPhysicsTickSafe()
        {
            var game = Current.Game;
            if (game == null) return (double)Find.TickManager.TicksGame;
            if (cachedTsm == null || !ReferenceEquals(cachedGame, game))
            {
                cachedTsm = game.GetComponent<TimeScaleManager>();
                cachedGame = game;
            }
            return cachedTsm != null ? cachedTsm.GetPhysicsTick() : (double)Find.TickManager.TicksGame;
        }

        public double GetPhysicsTick(int realTick)
        {
            EnsureCalendarBaseline();

            int deltaReal = realTick - switchStartTick;
            double scale = GetCalendarScale();

            return calendarBaseTick + deltaReal * scale - gameStartAbsTickCapture;
        }

        // ── 帧级连续渲染时钟（2026-10-11，"别的航天器位置在抖"的治本）────────────────────────
        // 问题：`GetPhysicsTick()` 由**整数** `TicksGame` 推出 ⇒ 渲染时间轴是**阶梯**。两台站不同轨道时
        //       相对速度可达 km/s（每 tick 相对位移上千米）⇒ 渲染帧率高于 tick 率时，位置**一格一格跳**
        //       = 用户报的"别的航天器整体位置在抖"（大小形状不变，只有位置在跳）。
        // 修法：把物理 tick **连续化** —— 加上"当前真实 tick 内已过的比例 × 档位倍率"。
        //       消费侧本来就按"任意实数 tick"取位置：圆轨是解析函数（`GetCircularPositionAtTick(double)`
        //       的注释就写着"支持子tick"），积分轨按速度外推（`SpaceGameComp.LastPhysicsStepTick`）。
        // ⚠ 只做**相位平滑**，不改时间基准：平均值仍严格等于物理 tick（相位有界 [0, scale]）。
        private int renderClockTicksGame = int.MinValue;
        private double renderClockFrac;                      // 当前真实 tick 内已过的比例（[0,1]）
        private double renderClockSecPerTick = 1.0 / 60.0;   // 实测（滑均）：一个真实 tick ≈ 多少秒

        /// <summary>
        /// **帧级连续时间**（渲染专用）：物理 tick + 子 tick 相位。取任意实数 tick 的位置函数都能吃它。
        /// ⚠ 不用于物理结算（那边仍按物理步整数边界走）——这是**表示层**的时间。
        /// </summary>
        public double GetRenderTick()
        {
            EnsureCalendarBaseline();
            int tg = Find.TickManager.TicksGame;

            double dt = RealTime.deltaTime;   // 本帧真实时长（秒）；RimWorld 自己的帧钟，不必引 UnityEngine
            if (dt > 0.0 && dt < 0.5) renderClockFrac += dt / renderClockSecPerTick;

            if (tg != renderClockTicksGame)
            {
                if (renderClockTicksGame != int.MinValue)
                {
                    int dtTicks = tg - renderClockTicksGame;
                    // 实测"每 tick 秒数"（只吃合理样本：暂停/卡帧/换档跳变不吃）
                    if (dtTicks > 0 && dtTicks <= 8 && dt > 0.0)
                        renderClockSecPerTick = renderClockSecPerTick * 0.75 + (dt / dtTicks) * 0.25;
                    renderClockFrac -= dtTicks;   // 相位**减去**走过的 tick 数（保留小数部分 ⇒ 无跳变）
                }
                else renderClockFrac = 0.0;
                renderClockTicksGame = tg;
            }
            if (renderClockFrac < 0.0) renderClockFrac = 0.0;
            else if (renderClockFrac > 1.0) renderClockFrac = 1.0;   // 暂停/掉帧：夹住，等 tick 复位

            return GetPhysicsTick() + renderClockFrac * GetCalendarScale();
        }

        /// <summary>
        /// 获取用于历法显示的 TicksAbs（基于 TicksAbs，用于 DateReadout）。
        /// 注意：这是纯显示层时间，不做物理计算；内部不使用 TicksAbs 属性，避免与 TickManager_TicksAbs_Patch 递归。
        /// </summary>
        public double GetDisplayTicksAbs()
        {
            EnsureCalendarBaseline();

            int realTicksGame = Find.TickManager.TicksGame;
            int deltaReal = realTicksGame - switchStartTick;
            double scale = GetCalendarScale();

            return calendarBaseTick + deltaReal * scale;
        }

        private double GetCalendarScale()
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         => currentLevel switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         {
            TimeScaleLevel.Combat => 1.0 / 60.0,
            TimeScaleLevel.Fast => 8.0,
            _ => 1.0,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         };

        public event Action<TimeScaleLevel, TimeScaleLevel> OnTimeScaleChanged;

        public TimeScaleManager(Game game) { }

        public void QueueCommand(TimeScaleCommand command)
        {
            var temp = new Queue<TimeScaleCommand>();
            while (commandQueue.Count > 0)
            {
                var cmd = commandQueue.Dequeue();
                if (cmd.TargetLevel != command.TargetLevel)
                    temp.Enqueue(cmd);
            }
            commandQueue = temp;

            command.ExecuteAfterTick = GetNextStepBoundary(Find.TickManager.TicksGame);
            commandQueue.Enqueue(command);

            UpdateUIPending(command);
        }

        public void SetTimeScale(TimeScaleLevel level)
        {
            if (level == currentLevel) return;
            QueueCommand(new TimeScaleCommand { TargetLevel = level, RequestTick = Find.TickManager.TicksGame });
        }

        public void SetCombatMode() => SetTimeScale(TimeScaleLevel.Combat);
        public void SetNormalMode() => SetTimeScale(TimeScaleLevel.Normal);
        public void SetFastMode() => SetTimeScale(TimeScaleLevel.Fast);

        /// <summary>
        /// 立即切换 Combat 档（不排队、不弹"等待时间同步"消息）。
        /// 供射弹生成等需要瞬间进入子弹时间的路径调用——排队版本有 ≤2 tick 的边界延迟，
        /// Normal 档窗口内射弹会以 1s/tick 大步进瞬移（6.5km/步），观感上"物理断裂"。
        /// </summary>
        public void SetCombatModeImmediate()
        {
            if (currentLevel == TimeScaleLevel.Combat) return;
            ExecuteCommand(new TimeScaleCommand { TargetLevel = TimeScaleLevel.Combat, RequestTick = Find.TickManager.TicksGame });
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            if (IsCombatMode)
            {
                combatSubTick++;
                if (combatSubTick >= 60)
                    combatSubTick = 0;
            }

            ProcessCommands();
        }

        private void ProcessCommands()
        {
            if (isProcessing) return;

            int currentTick = Find.TickManager.TicksGame;

            while (commandQueue.Count > 0)
            {
                var command = commandQueue.Peek();

                if (currentTick - command.RequestTick > MAX_WAIT_TICKS)
                {
                    ExecuteCommand(command);
                    commandQueue.Dequeue();
                    continue;
                }

                if (currentTick < command.ExecuteAfterTick)
                    break;

                if (!CanExecuteNow(command))
                    break;

                ExecuteCommand(command);
                commandQueue.Dequeue();
            }
        }

        private bool CanExecuteNow(TimeScaleCommand command)
        {
            if (command.TargetLevel == TimeScaleLevel.Normal && IsInCombatAction())
                return false;

            return true;
        }

        /// <summary>
        /// **是否处于「战斗动作」**（本库唯一的战斗判据口径，原来叫 private `IsInCombatAction`）：
        /// ① **有射弹在飞**（`CombatManager.IsBattleActive` = `activeProjectiles.Count > 0`）；
        /// ② 或**导弹处于 Attack 终末自动段**（`AutonomousVehicleManager.HasActiveAttack`；2026-09-07 用户定稿：
        ///    与射弹同级 ⇒ 存在即视为战斗动作）。
        ///
        /// 读者（两处，**同一份实现、不各写一份**）：
        /// · 时间流速闸（禁止"有战斗动作时回 Normal"）；
        /// · **解析档降级 [D7②]**（交战 ⇒ 退出解析轨）—— 2026-10-08 用户裁定："**用现成那个就行，别新建交战触发**"。
        /// ⚠ 改公开的顺带修一处成本：原来每次**现查组件**（`Current.Game.GetComponent` = 组件表线性扫描），
        ///   而新读者是**每物理步**调用 ⇒ 现在两个组件都走 `GameComponentRef` 缓存（全局查询增量 = 0）。
        /// </summary>
        public bool IsInCombatAction()
        {
            var combatManager = combatMgrRef.Get();
            if (combatManager != null && combatManager.IsBattleActive) return true;
            var avm = avmRef.Get();
            return avm != null && avm.HasActiveAttack;
        }

        private readonly GameComponentRef<Combat.CombatManager> combatMgrRef = new();
        private readonly GameComponentRef<AutonomousVehicleManager> avmRef = new();

        private void ExecuteCommand(TimeScaleCommand command)
        {
            isProcessing = true;

            double prevDisplayTicksAbs = GetDisplayTicksAbs();

            TimeScaleLevel oldLevel = currentLevel;
            currentLevel = command.TargetLevel;

            calendarBaseTick = prevDisplayTicksAbs;
            switchStartTick = Find.TickManager.TicksGame;

            Log.Message($"[TimeScale] Switched from {oldLevel} to {currentLevel} | baseTicksAbs={calendarBaseTick:F2} startTick={switchStartTick}");

            if (currentLevel == TimeScaleLevel.Combat)
                combatSubTick = 0;

            InitializeCombatCacheIfNeeded(command.TargetLevel);

            OnTimeScaleChanged?.Invoke(oldLevel, currentLevel);

            isProcessing = false;
        }

        private void InitializeCombatCacheIfNeeded(TimeScaleLevel targetLevel)
        {
            if (targetLevel != TimeScaleLevel.Combat) return;

            int currentTick = Find.TickManager.TicksGame;
            combatStartTick = currentTick;
            var spaceGameComp = Current.Game.GetComponent<SpaceGameComp>();
            if (spaceGameComp == null) return;

            // 采样与插值算式的唯一实现在 SpaceGameComp（此前这里是一份平行副本，已废止）
            spaceGameComp.RebuildAllCombatCaches(currentTick);
        }

        private int GetNextStepBoundary(int currentTick)
        {
            int remainder = currentTick % UPDATE_INTERVAL_TICKS;
            return currentTick + (UPDATE_INTERVAL_TICKS - remainder);
        }

        private void UpdateUIPending(TimeScaleCommand command)
        {
            int currentTick = Find.TickManager.TicksGame;
            int waitTicks = command.ExecuteAfterTick - currentTick;
            double waitSeconds = waitTicks * 1.0;

            Messages.Message($"等待时间同步... ({waitSeconds:F1}s)", MessageTypeDefOf.NeutralEvent);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref currentLevel, "timeScaleLevel", TimeScaleLevel.Normal);
            Scribe_Values.Look(ref calendarBaseTick, "calendarBaseTick", 0.0);
            Scribe_Values.Look(ref switchStartTick, "switchStartTick", 0);
            Scribe_Values.Look(ref gameStartAbsTickCapture, "gameStartAbsTickCapture", 0.0);
            Scribe_Values.Look(ref calendarInitialized, "calendarInitialized", false);
        }
    }
}