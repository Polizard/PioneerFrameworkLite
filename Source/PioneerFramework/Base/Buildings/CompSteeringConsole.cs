using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 操舵台建筑 Comp 的配置。挂在操舵台 ThingDef 的 comps 列表里。
    /// </summary>
    public class CompProperties_SteeringConsole : CompProperties
    {
        public CompProperties_SteeringConsole()
        {
            compClass = typeof(CompSteeringConsole);
        }
    }

    /// <summary>
    /// 操舵台：船上存在 ≥1 台完好操舵台 → 激活该船手动操舵（选中建筑 → Gizmo「打开操舵」→ Window_Steering）。
    /// 与推进器/储罐同模式：本身不存数据，功能由 SpaceMapComp + 空间层 SteeringController 驱动。
    /// </summary>
    public class CompSteeringConsole : ThingComp
    {
        /// <summary>目标地图上是否有完好操舵台（开窗条件 / 每帧有效性校验）。null 图 = 没有（无 Map 编队谈不上操舵台）。</summary>
        public static bool HasConsole(Map? map)
        {
            if (map == null) return false;
            var comps = map.GetComponent<SpaceMapComp>();
            if (comps == null) return false;
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                if (thing.Destroyed || thing.IsBrokenDown()) continue;
                if (thing.TryGetComp<CompSteeringConsole>() != null)
                    return true;
            }
            return false;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra()) yield return g;
            yield return new Command_Action
            {
                defaultLabel = "打开操舵",
                defaultDesc = "打开手动操舵界面：平移/旋转键控、动作组、姿态对准（KSP 式）。",
                // ⚠ 原路径 `Things/Building/Ship/ShipSensorCluster` **不存在** ⇒ 每画一次按钮吐一行红字
                // （2026-10-07 用户"看红字"：实测 871 行）。这里改用**已加载的引用**，不会再有不存在的路径。
                icon = TexCommand.Draft,
                action = OpenSteeringWindow
            };

            // [③ + A8] 船上控制台（2026-10-07 用户点名"UI 要在控制台上、别挂在罐子上"）：
            // 同一个 Comp 上再加一个入口（**新窗口在本线自己的文件里**，不动本窗口的布局）。
            var console = PropulsionGizmo.TryMake(this);
            if (console != null) yield return console;
        }

        private void OpenSteeringWindow()
        {
            var mapComp = parent?.Map?.GetComponent<SpaceMapComp>();
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (mapComp == null || spaceGame == null) return;

            int groupId = -1;
            var entity = spaceGame.GetEntityById(mapComp.ownerEntityId);
            if (entity != null)
                groupId = entity.GroupId;
            if (groupId < 0)
            {
                Messages.Message("操舵台未关联飞船（缺 ownerEntityId）。", MessageTypeDefOf.RejectInput);
                return;
            }

            var old = Find.WindowStack.WindowOfType<Window_Steering>();
            old?.Close();
            if (SteeringController.IsOpen) Window_Steering.StopControl();
            SteeringController.Open(groupId);
            Find.WindowStack.Add(new Window_Steering());
        }
    }
}
