using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 航天器地图内的返回出口：继承原版 PocketMapExit，复用原版转移/返回逻辑。
    /// </summary>
    public class Building_SpacecraftExit : PocketMapExit
    {
        public override string EnterString => "返回地面";

        public override Map GetOtherMap()
        {
            SpacecraftMapParent spacecraft = Map?.Parent as SpacecraftMapParent;
            SpacecraftBridge bridge = spacecraft?.GroundBridge;
            if (bridge == null || !bridge.Valid)
                return null;

            return SpaceMapGenerator.FindPocketMap(bridge.groundMapId);
        }

        public override bool IsEnterable(out string reason)
        {
            SpacecraftMapParent spacecraft = Map?.Parent as SpacecraftMapParent;
            SpacecraftBridge bridge = spacecraft?.GroundBridge;

            if (spacecraft == null || spacecraft.state != SpacecraftState.Ground)
            {
                reason = "航天器已离开地面，无法返回地面。";
                return false;
            }

            if (bridge == null || !bridge.Valid)
            {
                reason = "未连接地面出口。";
                return false;
            }

            return base.IsEnterable(out reason);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                if (g is Command_Action command
                    && command.defaultLabel == "CommandViewSurface".Translate())
                {
                    continue;
                }

                yield return g;
            }
        }

        public override IntVec3 GetDestinationLocation()
        {
            SpacecraftMapParent spacecraft = Map?.Parent as SpacecraftMapParent;
            SpacecraftBridge bridge = spacecraft?.GroundBridge;
            return bridge?.groundCell ?? IntVec3.Invalid;
        }
    }
}
