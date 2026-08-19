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

            if (!LaunchService.SpawnDebugRocketFrame(parent.Map, parent.Position, design))
                return false;

            state = LaunchPadState.Building;
            builtRocket = parent.Map.thingGrid.ThingsListAt(parent.Position)
                .FirstOrDefault(t => t.def.defName == "PF_Building_RocketFrame_Basic");
            return true;
        }

        /// <summary>发射当前火箭。成功后回到空闲。</summary>
        public bool TryLaunch()
        {
            if (design == null || parent == null || parent.Map == null)
                return false;

            var entity = LaunchService.LaunchFromGround(parent.Map, parent.Position, design);
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
            string designText = design != null ? design.rocketDefName : "无设计";
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
                defaultLabel = "新建调试设计",
                defaultDesc = "为发射台创建一份 PFL 调试火箭设计。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    SetDesign(LaunchService.CreateDebugDesign());
                    Messages.Message("调试火箭设计已创建。", MessageTypeDefOf.SilentInput, false);
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
                            Messages.Message("未完工火箭框架已生成。", MessageTypeDefOf.TaskCompletion, false);
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
                        if (TryLaunch())
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
