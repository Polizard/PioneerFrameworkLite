using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 回收第一级助推器 Comp：提供“重建火箭”Gizmo。
    /// 取代 DMS:NF 的 RocketReturnedRebuildPatch，改为数据驱动。
    /// </summary>
    public class CompProperties_ReturnedBooster : CompProperties
    {
        public CompProperties_ReturnedBooster()
        {
            compClass = typeof(CompReturnedBooster);
        }
    }

    public class CompReturnedBooster : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            if (!LaunchPadUtility.IsReturnedBooster(parent.def))
                yield break;

            RocketRecoveryDef recovery = FindRecovery(parent.def);
            if (recovery == null)
                yield break;

            Designation designation = parent.Map?.designationManager?.DesignationOn(parent, PFLaunchDefOf.PF_DesignateRebuildRocket);
            if (designation == null)
            {
                Command_Action rebuild = new Command_Action
                {
                    defaultLabel = "重建火箭",
                    defaultDesc = GetDescription(recovery),
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", false) ?? BaseContent.BadTex,
                    action = delegate
                    {
                        Map map = parent.Map;
                        if (map == null || !CanRebuildNow(out _))
                            return;

                        if (map.designationManager.DesignationOn(parent, PFLaunchDefOf.PF_DesignateRebuildRocket) == null)
                            map.designationManager.AddDesignation(new Designation(parent, PFLaunchDefOf.PF_DesignateRebuildRocket));
                    }
                };

                if (!CanRebuildNow(out string disableReason))
                    rebuild.Disable(disableReason);

                yield return rebuild;
            }
            else
            {
                yield return new Command_Action
                {
                    defaultLabel = "取消重建",
                    defaultDesc = "取消当前重建指令。",
                    icon = TexCommand.ClearPrioritizedWork,
                    action = delegate
                    {
                        parent.Map?.designationManager?.RemoveDesignation(designation);
                    }
                };
            }
        }

        private bool CanRebuildNow(out string disableReason)
        {
            disableReason = string.Empty;

            if (parent.Map == null)
            {
                disableReason = "需要地图。";
                return false;
            }

            Building launchpad = parent.Map.thingGrid.ThingsListAt(parent.Position)
                .OfType<Building>()
                .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
            if (launchpad == null)
            {
                disableReason = "不在发射台上。";
                return false;
            }

            if (parent.Position != launchpad.Position)
            {
                disableReason = "不在发射台中心。";
                return false;
            }

            if (!LaunchPadUtility.CanRebuildOnPad(parent.def, launchpad.def))
            {
                disableReason = "该发射台不支持重建此返回舱。";
                return false;
            }

            return true;
        }

        private string GetDescription(RocketRecoveryDef recovery)
        {
            string desc = "将返回舱转换为火箭框架，随后由殖民者按原版建造流程施工。";
            if (parent.Map != null && RocketRebuildResourceUtility.TryGetMissingSummary(parent.Map, recovery, out string missing))
                desc += "\n\n缺少材料:\n" + missing;
            return desc;
        }

        private static RocketRecoveryDef FindRecovery(ThingDef returnedDef)
        {
            if (returnedDef == null)
                return null;

            foreach (RocketRecoveryDef recovery in DefDatabase<RocketRecoveryDef>.AllDefsListForReading)
            {
                if (recovery.returnedThingDef == returnedDef)
                    return recovery;
            }

            return null;
        }
    }
}
