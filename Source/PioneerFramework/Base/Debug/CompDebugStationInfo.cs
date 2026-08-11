using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    public class CompProperties_DebugStationInfo : CompProperties
    {
        public CompProperties_DebugStationInfo()
        {
            compClass = typeof(CompDebugStationInfo);
        }
    }

    /// <summary>
    /// 空间站信息台（调试）：选中时在检视面板显示所属地图 SpaceMapComp 概况
    /// （站名 / 归属实体 / 工质向量 / dV），并提供 Gizmo 把概况打印到游戏日志。
    /// </summary>
    public class CompDebugStationInfo : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "打印空间站概况",
                defaultDesc = "把当前地图的空间站概况打印到日志。",
                icon = TexButton.Info,
                action = delegate { Log.Message(BuildSummary()); }
            };
        }

        public override string CompInspectStringExtra() => BuildSummary();

        /// <summary>组装当前地图 SpaceMapComp 概况文本。</summary>
        private string BuildSummary()
        {
            var map = parent.Map;
            if (map == null)
                return "未在任何地图上";

            var mc = map.GetComponent<SpaceMapComp>();
            if (mc == null)
                return "当前地图无 SpaceMapComp（非空间站地图）";

            var sb = new StringBuilder();
            sb.AppendLine("站名: " + mc.StationName);
            sb.AppendLine("ownerEntityId: " + mc.ownerEntityId + "  linkedPartId: " + mc.linkedPartId);

            // 所属空间实体摘要（dV 调试）
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame != null && mc.ownerEntityId >= 0)
            {
                var entity = spaceGame.GetEntityById(mc.ownerEntityId);
                if (entity != null)
                {
                    sb.AppendLine("实体: entity " + entity.EntityId + "  group " + entity.GroupId +
                        "  dV " + entity.RemainingDeltaV.ToString("0.#") + " / " + entity.MaxDeltaV.ToString("0.#") + " m/s");
                }
                else
                {
                    sb.AppendLine("实体: entity " + mc.ownerEntityId + " 未找到");
                }
            }

            // 工质向量（工质 → 质量 kg）
            float total = 0f;
            bool any = false;
            foreach (var kv in mc.PropellantSnapshot)
            {
                sb.AppendLine("工质 " + (kv.Key != null ? kv.Key.label : "?") + ": " + kv.Value.ToString("0.#") + " kg");
                total += kv.Value;
                any = true;
            }
            if (!any)
                sb.AppendLine("工质: (无)");
            sb.Append("工质总质量: " + total.ToString("0.#") + " kg");
            return sb.ToString();
        }
    }
}
