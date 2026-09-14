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

        public static TimeScaleCommand ToCombat() => new TimeScaleCommand
        {
            TargetLevel = TimeScaleLevel.Combat,
            RequestTick = Find.TickManager.TicksGame
        };

        public static TimeScaleCommand ToNormal() => new TimeScaleCommand
        {
            TargetLevel = TimeScaleLevel.Normal,
            RequestTick = Find.TickManager.TicksGame
        };

        public static TimeScaleCommand ToFast() => new TimeScaleCommand
        {
            TargetLevel = TimeScaleLevel.Fast,
            RequestTick = Find.TickManager.TicksGame
        };
    }

    public class TimeScaleManager : GameComponent
    {
        private const int UPDATE_INTERVAL_TICKS = 2;
        private const int MAX_WAIT_TICKS = 600;

        private TimeScaleLevel currentLevel = TimeScaleLevel.Normal;
        private Queue<TimeScaleCommand> commandQueue = new Queue<TimeScaleCommand>();
        private bool isProcessing = false;
        private int combatStartTick = 0;
        private int combatSubTick = 0;
        private double calendarBaseTick = 0;
        private int switchStartTick = 0;
        private double gameStartAbsTickCapture = 0;
        private bool calendarInitialized = false;

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

        private static Game cachedGame;
        private static TimeScaleManager cachedTsm;

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
        {
            switch (currentLevel)
            {
                case TimeScaleLevel.Combat: return 1.0 / 60.0;
                case TimeScaleLevel.Fast: return 8.0;
                default: return 1.0;
            }
        }

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

        private bool IsInCombatAction()
        {
            var combatManager = Current.Game.GetComponent<Combat.CombatManager>();
            if (combatManager != null && combatManager.IsBattleActive) return true;
            // 导弹 Attack 终末自动段与射弹同级（2026-09-07 用户定稿）：存在即视为战斗动作，
            // 阻止"回 Normal"命令执行（Combat 档维持由 SpaceGameComp 240-tick 检查负责）
            var avm = Current.Game.GetComponent<AutonomousVehicleManager>();
            return avm != null && avm.HasActiveAttack;
        }

        private void ExecuteCommand(TimeScaleCommand command)
        {
            isProcessing = true;

            double prevDisplayTicksAbs = GetDisplayTicksAbs();

            TimeScaleLevel oldLevel = currentLevel;
            currentLevel = command.TargetLevel;

            calendarBaseTick = prevDisplayTicksAbs;
            switchStartTick = Find.TickManager.TicksGame;

            Log.Message(string.Format("[TimeScale] Switched from {0} to {1} | baseTicksAbs={2:F2} startTick={3}",
                oldLevel, currentLevel, calendarBaseTick, switchStartTick));

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

            Messages.Message(string.Format("等待时间同步... ({0:F1}s)", waitSeconds), MessageTypeDefOf.NeutralEvent);
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