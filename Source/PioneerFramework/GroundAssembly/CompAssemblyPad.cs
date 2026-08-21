using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 发射台状态机。
    /// 空闲 -> 设计中 -> 建造中 -> 已完工 -> 发射后回空闲。
    /// 对应 v1 设计中的发射台状态机。
    /// </summary>
    public enum LaunchPadState
    {
        Idle,
        Designing,
        Building,
        Completed
    }

    public class CompProperties_AssemblyPad : CompProperties
    {
        public CompProperties_AssemblyPad()
        {
            compClass = typeof(CompAssemblyPad);
        }
    }

    public class CompAssemblyPad : ThingComp
    {
        private LaunchPadState state;

        private RocketDesignSpec design;

        private Thing builtRocket;

        public LaunchPadState State => state;

        public RocketDesignSpec Design => design;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state, "state", LaunchPadState.Idle);
            Scribe_Deep.Look(ref design, "design");
            Scribe_References.Look(ref builtRocket, "builtRocket");
            if (design == null)
                design = new RocketDesignSpec();
        }

        /// <summary>进入设计状态并保存当前设计。</summary>
        public void SetDesign(RocketDesignSpec newDesign)
        {
            design = newDesign ?? new RocketDesignSpec();
            state = LaunchPadState.Designing;
        }

        /// <summary>直接登记一座已完工火箭（调试重建用），让发射台可直接发射它。</summary>
        public void SetCompletedRocket(RocketDesignSpec newDesign, Thing rocket)
        {
            design = newDesign ?? new RocketDesignSpec();
            builtRocket = rocket;
            state = LaunchPadState.Completed;
        }

        /// <summary>框架施工完成/调试标记完工后，通知发射台登记已完工火箭。</summary>
        public void NotifyRocketCompleted(Thing rocket)
        {
            if (rocket == null || rocket.Destroyed)
                return;
            builtRocket = rocket;
            state = LaunchPadState.Completed;
        }

        /// <summary>确认设计，生成未完工火箭框架。</summary>
        public bool TryStartBuild()
        {
            string failReason = "";
            if (design == null || !design.IsValid(out failReason))
            {
                if (!failReason.NullOrEmpty())
                    Messages.Message(failReason, MessageTypeDefOf.RejectInput, false);
                return false;
            }

            if (parent == null || parent.Map == null)
                return false;

            IntVec3 center = parent.OccupiedRect().CenterCell;
            if (!LaunchService.SpawnDebugRocketFrame(parent.Map, center, design))
                return false;

            state = LaunchPadState.Building;
            builtRocket = parent.Map.thingGrid.ThingsListAt(center)
                .FirstOrDefault(t => LaunchPadUtility.IsRocket(t.def));
            return true;
        }

        /// <summary>发射当前火箭。成功后回到空闲。</summary>
        /// <param name="debug">true 时跳过「有人控制台 / 舱室封闭」人员校验。</param>
        public bool TryLaunch(bool debug = false)
        {
            if (design == null || parent == null || parent.Map == null)
                return false;

            IntVec3 rocketPos = builtRocket != null && builtRocket.Spawned
                ? builtRocket.OccupiedRect().CenterCell
                : parent.OccupiedRect().CenterCell;
            var entity = LaunchService.LaunchFromGround(parent.Map, rocketPos, design, debug);
            if (entity == null)
                return false;

            builtRocket = null;
            design = null;
            state = LaunchPadState.Idle;
            return true;
        }

        public override string CompInspectStringExtra()
        {
            string stateText = state switch
            {
                LaunchPadState.Idle => "空闲",
                LaunchPadState.Designing => "设计中",
                LaunchPadState.Building => "建造中",
                LaunchPadState.Completed => "已完工",
                _ => "未知"
            };
            string designText = design != null
                ? (design.rocketDefName == "PF_Building_Rocket_Recoverable_Basic" ? "可回收火箭"
                    : design.rocketDefName == "PF_Building_Rocket_Basic" ? "不可回收火箭"
                    : design.rocketDefName)
                : "无设计";
            return "发射台状态: " + stateText + "\n设计: " + designText;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "新建可回收火箭设计",
                defaultDesc = "创建一份可回收火箭设计，之后可确认并生成框架。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    SetDesign(LaunchService.CreateDebugDesign(true));
                    Messages.Message("可回收火箭设计已创建。", MessageTypeDefOf.SilentInput, false);
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "新建不可回收火箭设计",
                defaultDesc = "创建一份不可回收火箭设计，之后可确认并生成框架。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    SetDesign(LaunchService.CreateDebugDesign(false));
                    Messages.Message("不可回收火箭设计已创建。", MessageTypeDefOf.SilentInput, false);
                }
            };

            if (state == LaunchPadState.Designing)
            {
                yield return new Command_Action
                {
                    defaultLabel = "确认设计并生成框架",
                    defaultDesc = "在发射台中心生成未完工火箭框架。",
                    icon = TexCommand.Install,
                    action = delegate
                    {
                        if (TryStartBuild())
                            Messages.Message("火箭建造蓝图已生成，等待搬运建材。", MessageTypeDefOf.TaskCompletion, false);
                    }
                };
            }

            if (state == LaunchPadState.Building || state == LaunchPadState.Completed)
            {
                yield return new Command_Action
                {
                    defaultLabel = "直接发射 (调试)",
                    defaultDesc = "跳过施工/装修，把当前设计直接发射入轨。",
                    icon = TexCommand.FireAtWill,
                    action = delegate
                    {
                        if (TryLaunch(true))
                            Messages.Message("火箭已入轨。", MessageTypeDefOf.TaskCompletion, false);
                    }
                };
            }

            if (state != LaunchPadState.Idle)
            {
                yield return new Command_Action
                {
                    defaultLabel = "取消设计",
                    defaultDesc = "清空当前设计并回到空闲。",
                    icon = TexCommand.ClearPrioritizedWork,
                    action = delegate
                    {
                        design = null;
                        builtRocket = null;
                        state = LaunchPadState.Idle;
                    }
                };
            }
        }
    }
}
