using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭口袋地图生成器。按 RocketDesignSpec 中的尺寸/型号模板生成舱内地图。
    /// 首发复用 PF_RoomPocket（地板 + 边界墙 + 解锁迷雾），后续按型号模板生成船体轮廓。
    /// </summary>
    public static class RocketPocketMapGenerator
    {
        /// <summary>
        /// 生成一张火箭口袋地图。
        /// </summary>
        public static Map GenerateRocketMap(RocketDesignSpec spec)
        {
            if (spec == null)
                return null;

            Map anchor = SpaceMapGenerator.GetAnchorMap();
            if (anchor == null)
                return null;

            return SpaceMapGenerator.GeneratePocketMap(
                spec.mapWidth,
                spec.mapHeight,
                anchor,
                "PF_RoomPocket",
                "Rocket_" + spec.rocketDefName);
        }
    }
}
