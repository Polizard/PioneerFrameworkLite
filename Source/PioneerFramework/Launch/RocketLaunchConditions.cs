using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.GroundAssembly;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// LA-14 发射条件校验器。
    /// 对应设计稿 §7：至少 1 个有人控制的控制台、房间封闭无缺口、燃料足够、气体系统可选。
    /// 调试模式（debug=true）跳过「有人控制台 / 房间封闭」两项人员相关校验，仅保留机制层硬条件。
    /// </summary>
    public static class RocketLaunchConditions
    {
        /// <summary>
        /// 校验一次发射是否允许进行。
        /// </summary>
        /// <param name="spec">火箭设计规格。</param>
        /// <param name="rocketMap">舱内口袋地图。调试直接发射且尚未生成地图时可为 null。</param>
        /// <param name="debug">调试模式跳过人员相关校验。</param>
        public static List<string> Validate(RocketDesignSpec spec, Map rocketMap, bool debug = false)
        {
            var errors = new List<string>();
            if (spec == null)
            {
                errors.Add("缺少火箭设计规格。");
                return errors;
            }

            if (rocketMap == null)
            {
                // 普通运输仓允许无地图 PFL 入轨；航天舱室若执行正式发射仍应先生成地图。
                if (!debug && spec.CabinType == RocketCabinType.Spacecraft)
                    errors.Add("航天舱室尚未生成舱内地图，无法进行正式发射。");

                if (!SpecHasThrusters(spec))
                    errors.Add("未安装推进器：至少需要 1 个推进器。");
                if (spec.fuelAmount <= 0.001f)
                    errors.Add("燃料不足：设计燃料为 0。");

                return errors;
            }

            var mapComp = rocketMap.GetComponent<SpaceMapComp>();
            if (mapComp == null)
            {
                errors.Add("舱内地图缺少 SpaceMapComp，无法进行发射。");
                return errors;
            }

            // 发射台预检时，虚拟推进可能尚未注入地图（注入发生在 LaunchService 发射流程内），
            // 因此先按设计规格做硬条件判断；若地图已配置虚拟推进，则按地图实际工质判断。
            bool mapHasPropulsion = (mapComp.Networks != null && mapComp.Networks.Count > 0)
                || mapComp.TotalPropellantCapacity > 0f;
            if (!mapHasPropulsion)
            {
                if (!SpecHasThrusters(spec))
                    errors.Add("未安装推进器：至少需要 1 个推进器。");
                if (spec.fuelAmount <= 0.001f)
                    errors.Add("燃料不足：设计燃料为 0。");
            }
            else
            {
                if (mapComp.Networks == null || mapComp.Networks.Count == 0)
                    errors.Add("未安装推进器：至少需要 1 个推进器。");
                if (mapComp.TotalPropellantMass <= 0.001f)
                    errors.Add(string.Format("燃料不足：当前工质 {0:0.#} kg。", mapComp.TotalPropellantMass));
            }

            if (!debug)
            {
                if (!HasControlConsole(rocketMap))
                    errors.Add("缺少有人控制的控制台。");
                if (!HasSealedRoom(rocketMap))
                    errors.Add("舱室未封闭：存在缺口。");
            }

            return errors;
        }

        private static bool SpecHasThrusters(RocketDesignSpec spec)
        {
            if (spec?.parts == null)
                return false;

            int count = 0;
            for (int i = 0; i < spec.parts.Count; i++)
            {
                if (spec.parts[i] == null || spec.parts[i].category != "thruster")
                    continue;
                count += Mathf.Max(1, spec.parts[i].quantity);
            }

            return count >= 1;
        }

        /// <summary>是否存在已挂载 <see cref="CompRocketConsole"/> 的可用控制台建筑。</summary>
        private static bool HasControlConsole(Map map)
        {
            if (map?.listerThings == null)
                return false;

            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is Building building && !building.Destroyed)
                {
                    var console = building.GetComp<CompRocketConsole>();
                    if (console != null && console.IsOperational)
                        return true;

                    // 兼容 DMS:NF 尚未挂 PFL Comp、但命名已表达控制台语义的正式 Def。
                    string defName = building.def?.defName ?? string.Empty;
                    string label = building.def?.label ?? string.Empty;
                    if (defName.IndexOf("Console", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || defName.IndexOf("Control", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || label.IndexOf("控制台", System.StringComparison.Ordinal) >= 0
                        || label.IndexOf("操作台", System.StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>是否存在一个不接触地图边缘的内部房间（即封闭无缺口的舱室）。</summary>
        private static bool HasSealedRoom(Map map)
        {
            if (map?.regionGrid == null)
                return false;

            var seen = new HashSet<Room>();
            foreach (Room room in map.regionGrid.AllRooms)
            {
                if (room == null || room.TouchesMapEdge || seen.Contains(room))
                    continue;
                seen.Add(room);
            }

            return seen.Count > 0;
        }
    }
}
