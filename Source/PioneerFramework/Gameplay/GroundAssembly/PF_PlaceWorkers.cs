using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// PFL 自定义 PlaceWorker（2026-10-15，"反射清理"批 A-6：替代原先挂在
    /// `PlaceWorker_NeedsFuelingPort` / `PlaceWorker_FuelingPort` 上的 Harmony 补丁）。
    ///
    /// 机制：`BuildableDef.placeWorkers` 是 `List&lt;Type&gt;`（def 字段）⇒ 我们自己的 def 直接引用
    /// 下面两个子类即可；判据与旧补丁**同一套** `LaunchPadUtility`，且旧补丁的守卫本就只改**我方 def**
    /// 的结论（`IsRocket`/`IsReturnedBooster`/`IsAssemblyPad`）⇒ 语义 1:1。
    /// （另一处 `PlaceWorker_NotUnderRoof` 的基类补丁经核实为**死补丁**：其守卫要求
    /// "带该 worker 且是 returned booster"的 def，而全部 returned booster 都没有 placeWorkers ⇒ 已整删归档。）
    /// </summary>
    // 2026-11-17（用户授权）：有静态 Material 字段（FuelingPortCellMaterial 内联初始化）⇒ 标记静态构造，
    // 让材质在启动时的主线程创建，消掉 "needs a StaticConstructorOnStartup attribute" 警告。
    [Verse.StaticConstructorOnStartup]
    public class PF_PlaceWorker_NeedsFuelingPort : PlaceWorker_NeedsFuelingPort
    {
        private static readonly Color ValidColor = new(0.2f, 1f, 0.2f, 0.5f);
        private static readonly Color InvalidColor = new(1f, 0.2f, 0.2f, 0.5f);
        private static readonly Material FuelingPortCellMaterial =
            MaterialPool.MatFrom("UI/Overlays/FuelingPort", ShaderDatabase.Transparent);

        /// <summary>PFL 火箭/返回舱必须放在总装发射台可建造区域内（其余情形交还原版判断）。</summary>
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center, Rot4 rot, Map map,
            Thing? thingToIgnore = null, Thing? thing = null)
        {
            if (def is ThingDef thingDef)
            {
                Building fuelingPortGiver = FuelingPortUtility.FuelingPortGiverAtFuelingPortCell(center, map);
                if (fuelingPortGiver != null)
                {
                    if (LaunchPadUtility.IsVanillaPodLauncher(fuelingPortGiver.def))
                    {
                        if (LaunchPadUtility.IsRocket(thingDef) || LaunchPadUtility.IsReturnedBooster(thingDef))
                            return "PFL 火箭/回收第一级不能放在原版运输舱发射器上。";
                    }
                    else if (LaunchPadUtility.IsAssemblyPad(fuelingPortGiver.def))
                    {
                        if (LaunchPadUtility.IsReturnedBooster(thingDef))
                        {
                            if (!LaunchPadUtility.IsWithinBuildableArea(fuelingPortGiver, center, thingDef, rot))
                                return "回收第一级必须放在总装发射台可建造区域内。";
                            if (!LaunchPadUtility.CanRebuildOnPad(thingDef, fuelingPortGiver.def))
                                return "该总装发射台不支持重建此返回舱。";
                            if (IsOccupied(map, fuelingPortGiver, thingDef))
                                return "总装发射台已被占用。";
                            return true;
                        }

                        if (LaunchPadUtility.IsRocket(thingDef))
                        {
                            if (!LaunchPadUtility.IsWithinBuildableArea(fuelingPortGiver, center, thingDef, rot))
                                return "火箭必须放在总装发射台可建造区域内。";
                            if (!LaunchPadUtility.CanLaunchFromPad(thingDef, fuelingPortGiver.def))
                                return "该总装发射台不支持此火箭。";
                            if (IsOccupied(map, fuelingPortGiver, thingDef))
                                return "总装发射台已被占用。";
                            return true;
                        }
                    }
                }
            }

            return base.AllowsPlacing(def, center, rot, map, thingToIgnore, thing);
        }

        /// <summary>放置幽灵：PFL 火箭/返回舱走自家绘制（所有总装台中心画燃料口 + 中心格示意），不再落原版幽灵。</summary>
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing? thing = null)
        {
            if (!LaunchPadUtility.IsRocket(def) && !LaunchPadUtility.IsReturnedBooster(def))
            {
                base.DrawGhost(def, center, rot, ghostCol, thing);
                return;
            }

            Map map = Find.CurrentMap;
            if (map == null)
            {
                base.DrawGhost(def, center, rot, ghostCol, thing);
                return;
            }

            // 在所有 PFL 总装发射台中心绘制真实燃料口图标，固定不偏移。
            if (map.listerBuildings?.allBuildingsColonist != null)
            {
                for (int i = 0; i < map.listerBuildings.allBuildingsColonist.Count; i++)
                {
                    Building pad = map.listerBuildings.allBuildingsColonist[i];
                    if (pad == null || pad.Destroyed || !LaunchPadUtility.IsAssemblyPad(pad.def))
                        continue;

                    Vector3 iconPos = pad.OccupiedRect().CenterCell.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                    if (FuelingPortCellMaterial != null)
                        Graphics.DrawMesh(MeshPool.plane10, iconPos, Quaternion.identity, FuelingPortCellMaterial, 0);
                }
            }

            Building? launchpad = null;
            List<Thing> things = map.thingGrid.ThingsListAt(center);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Building b && LaunchPadUtility.IsAssemblyPad(b.def))
                {
                    launchpad = b;
                    break;
                }
            }

            if (launchpad == null)
                return;   // 中心不压台：只留上面那圈燃料口图标（同旧补丁：此时落原版幽灵反而误导）

            bool canPlace = LaunchPadUtility.IsWithinBuildableArea(launchpad, center, def, rot)
                && (LaunchPadUtility.IsReturnedBooster(def)
                    ? LaunchPadUtility.CanRebuildOnPad(def, launchpad.def)
                    : LaunchPadUtility.CanLaunchFromPad(def, launchpad.def));

            Color color = canPlace ? ValidColor : InvalidColor;
            Vector3 drawPos = center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
            Material mat = SolidColorMaterials.SimpleSolidColorMaterial(color);
            Graphics.DrawMesh(MeshPool.plane10, drawPos, Quaternion.identity, mat, 0);
        }

        private static bool IsOccupied(Map map, Building pad, ThingDef placingDef)
        {
            CellRect area = LaunchPadUtility.GetBuildableArea(pad);
            foreach (IntVec3 cell in area.Cells)
            {
                List<Thing> thingsAtCell = map.thingGrid.ThingsListAt(cell);
                for (int i = 0; i < thingsAtCell.Count; i++)
                {
                    Thing t = thingsAtCell[i];
                    if (t is Building && t.def != placingDef &&
                        (LaunchPadUtility.IsRocket(t.def) || LaunchPadUtility.IsReturnedBooster(t.def)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>总装发射台的燃料口幽灵：画在台中心（同旧补丁语义）。</summary>
    [Verse.StaticConstructorOnStartup]
    public class PF_PlaceWorker_FuelingPort : PlaceWorker_FuelingPort
    {
        private static readonly Material FuelingPortCellMaterial =
            MaterialPool.MatFrom("UI/Overlays/FuelingPort", ShaderDatabase.Transparent);

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing? thing = null)
        {
            if (!LaunchPadUtility.IsAssemblyPad(def))
            {
                base.DrawGhost(def, center, rot, ghostCol, thing);
                return;
            }

            Vector3 drawPos = center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
            if (FuelingPortCellMaterial != null)
            {
                Graphics.DrawMesh(MeshPool.plane10, drawPos, Quaternion.identity, FuelingPortCellMaterial, 0);
            }
        }
    }
}
