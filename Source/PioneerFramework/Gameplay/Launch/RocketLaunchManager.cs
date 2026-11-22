using Verse;
using PioneerFramework.GroundAssembly;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 发射方式。2026-11-21：旧链退役后**只保留类型**——`CompAssemblyPad` 的存档字段仍引用它
    /// （`launchMode`），故枚举不能删；发射入口已集中到链 B（`CompRocketAssembler.DoLaunch`）。
    /// </summary>
    public enum RocketLaunchMode
    {
        /// <summary>原版 TransportPod：发射到世界地图地格，不创建 PFL 轨道实体。</summary>
        VanillaTransportPod,

        /// <summary>PFL 入轨：创建 SpaceEntity + 舱内口袋地图，进入轨道。</summary>
        PflOrbit
    }

    /// <summary>
    /// 2026-11-21：旧链（发射台 `CompAssemblyPad` + `Window_SpacecraftManagement`）退役，
    /// 统一发射入口 `OpenLaunchDialog` / `TryLaunch` / `GetLaunchBlockReasons` /
    /// `ForceLaunchPfl` 一并删除；发射统一走链 B（`CompRocketAssembler.DoLaunch`）。
    ///
    /// 这里只保留一个判定：该火箭是否由 PFL 管理（判据 = 带 `CompRocketPocketMap`）。
    /// 唯一消费者是 <see cref="CompLaunchable_PF"/> —— 它据此隐藏原版 `CompLaunchable` 的发射 Gizmo。
    /// </summary>
    public static class RocketLaunchManager
    {
        /// <summary>该火箭是否由 PFL 管理（不再提供任何发射入口）。</summary>
        public static bool IsManagedRocket(Thing rocket)
        {
            if (rocket == null)
                return false;

            // 只有带 PFL 舱内地图 Comp 的火箭才纳入统一管理；
            // 避免把普通原版运输舱误判为 PFL 火箭并隐藏其原版发射 Gizmo。
            return rocket.TryGetComp<CompRocketPocketMap>() != null;
        }
    }
}
