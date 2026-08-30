using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public class CompProperties_ShuttleDock : CompProperties
    {
        public CompProperties_ShuttleDock()
        {
            compClass = typeof(CompShuttleDock);
        }
    }

    /// <summary>
    /// 穿梭机停靠 Comp（Map 建筑 Thing）。
    /// ShuttleId = 稳定身份，贯穿「停泊 Map Thing ⇄ 飞行 3D 实体」两态（设计 §10）：
    /// 起飞时 BoardingManager 记录 ShuttleId，降落生成停靠 Thing 时继承同一 id，撤离再起飞沿用。
    /// 飞行态由 BoardingManager 承载；本 Comp 负责停泊态身份与登机起飞交互（Gizmo）。
    /// </summary>
    public class CompShuttleDock : ThingComp
    {
        /// <summary>穿梭机稳定身份。-1 = 未分配（停靠时首次 PostSpawnSetup 自动分配）。</summary>
        public int shuttleId = -1;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!respawningAfterLoad && shuttleId < 0)
            {
                var boarding = Current.Game?.GetComponent<BoardingManager>();
                shuttleId = boarding != null ? boarding.AllocateShuttleId() : -1;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref shuttleId, "shuttleId", -1);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;
            if (parent.Faction != Faction.OfPlayer)
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "布置跳帮",
                defaultDesc = "打开布置跳帮界面：选择本穿梭机、乘客、目标空间站与落点后起飞（ShuttleId 贯穿停泊与飞行）。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    Find.WindowStack.Add(new Window_BoardingPlanner(null, new List<Thing> { parent }));
                }
            };
        }
    }
}
