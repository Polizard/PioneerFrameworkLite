using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.GroundAssembly;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 火箭发射方式。
    /// </summary>
    public enum RocketLaunchMode
    {
        /// <summary>原版 TransportPod：发射到世界地图地格，不创建 PFL 轨道实体。</summary>
        VanillaTransportPod,

        /// <summary>PFL 入轨：创建 SpaceEntity + 舱内口袋地图，进入轨道。</summary>
        PflOrbit
    }

    /// <summary>
    /// 统一发射上下文：一次发射所需的火箭、发射台、设计、舱内地图、原版发射组件引用。
    /// </summary>
    public class RocketLaunchContext
    {
        public Thing Rocket;
        public Building Pad;
        public CompAssemblyPad PadComp;
        public RocketDesignSpec Spec;
        public Map PocketMap;
        public CompLaunchable VanillaLaunchable;
        public CompRocketPocketMap PocketMapComp;
    }

    /// <summary>
    /// 统一发射管理器。PFL 火箭的发射只应从这里进入，
    /// 由玩家选择“原版 TransportPod”或“PFL 入轨”。
    /// </summary>
    public static class RocketLaunchManager
    {
        /// <summary>该火箭是否由 PFL 统一发射入口管理。</summary>
        public static bool IsManagedRocket(Thing rocket)
        {
            if (rocket == null)
                return false;

            // 只有带 PFL 舱内地图 Comp 的火箭才纳入统一管理；
            // 避免把普通原版运输舱误判为 PFL 火箭并隐藏其原版发射 Gizmo。
            return rocket.TryGetComp<CompRocketPocketMap>() != null;
        }

        /// <summary>
        /// 获取统一发射上下文。若无法识别为 PFL 管理火箭，返回 false。
        /// </summary>
        public static bool TryGetLaunchContext(Thing rocket, out RocketLaunchContext context)
        {
            context = null;
            if (!IsManagedRocket(rocket) || rocket == null)
                return false;

            context = new RocketLaunchContext
            {
                Rocket = rocket,
                PocketMapComp = rocket.TryGetComp<CompRocketPocketMap>(),
                VanillaLaunchable = rocket.TryGetComp<CompLaunchable>()
            };

            if (rocket is Building building && building.Spawned && building.Map != null)
            {
                IntVec3 center = building.OccupiedRect().CenterCell;
                foreach (Thing thing in building.Map.thingGrid.ThingsListAt(center))
                {
                    if (thing is Building padBuilding)
                    {
                        var pad = padBuilding.TryGetComp<CompAssemblyPad>();
                        if (pad != null)
                        {
                            context.Pad = padBuilding;
                            context.PadComp = pad;
                            break;
                        }
                    }
                }
            }

            context.Spec = context.PadComp?.Design;
            context.PocketMap = context.PocketMapComp?.GetPocketMap() ?? context.PadComp?.GetPocketMap();
            return true;
        }

        /// <summary>打开统一发射窗口。</summary>
        public static void OpenLaunchDialog(Thing rocket)
        {
            if (!IsManagedRocket(rocket))
            {
                Messages.Message("该火箭不是 PFL 统一管理的火箭。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            Find.WindowStack.Add(new Dialog_RocketLaunch(rocket));
        }

        /// <summary>查询指定发射方式的当前阻止原因。</summary>
        public static List<string> GetLaunchBlockReasons(RocketLaunchContext ctx, RocketLaunchMode mode)
        {
            var reasons = new List<string>();
            if (ctx == null)
            {
                reasons.Add("无法获取火箭发射上下文。");
                return reasons;
            }

            if (mode == RocketLaunchMode.VanillaTransportPod)
            {
                if (ctx.VanillaLaunchable == null)
                    reasons.Add("该火箭没有原版 TransportPod 发射组件。");
                else
                {
                    AcceptanceReport report = ctx.VanillaLaunchable.CanLaunch();
                    if (!report.Accepted)
                        reasons.Add(report.Reason);
                }
            }
            else
            {
                if (ctx.Spec == null)
                    reasons.Add("缺少火箭设计数据，无法进行 PFL 入轨。");
                if (ctx.PadComp == null)
                    reasons.Add("火箭不在 PFL 总装发射台上，无法进行 PFL 入轨。");
                if (ctx.PadComp != null && ctx.PadComp.State != LaunchPadState.Completed)
                    reasons.Add("发射台尚未处于已完工状态。");

                if (ctx.Spec != null && ctx.Spec.CabinType == RocketCabinType.Spacecraft)
                {
                    if (ctx.PocketMap == null)
                        reasons.Add("航天舱室尚未生成舱内地图，无法进行正式发射。");
                    else
                    {
                        var errors = RocketLaunchConditions.Validate(ctx.Spec, ctx.PocketMap, false);
                        reasons.AddRange(errors);
                    }
                }
                else if (ctx.Spec != null)
                {
                    // 普通运输仓：无舱内地图分支，仅校验推进/燃料等机制硬条件。
                    var errors = RocketLaunchConditions.Validate(ctx.Spec, null, false);
                    reasons.AddRange(errors);
                }
            }

            return reasons;
        }

        /// <summary>
        /// 调试用强制 PFL 发射：不依赖发射台状态/人员条件，直接按当前或调试设计入轨。
        /// 用于在发射台 UI 未完善时验证入轨链路。
        /// </summary>
        public static bool ForceLaunchPfl(Thing rocket, out string error)
        {
            error = string.Empty;
            if (rocket == null || !rocket.Spawned || rocket.Map == null)
            {
                error = "火箭不存在或不在当前地图。";
                return false;
            }

            TryGetLaunchContext(rocket, out var ctx);
            RocketDesignSpec spec = ctx?.PadComp?.Design;
            if (spec == null || string.IsNullOrEmpty(spec.rocketDefName))
            {
                bool recoverable = rocket.def != null
                    && (rocket.def.defName.Contains("Recoverable") || rocket.def.defName.Contains("CVThree"));
                spec = LaunchService.CreateDebugDesign(recoverable);
                spec.rocketDefName = rocket.def?.defName ?? spec.rocketDefName;
            }

            IntVec3 center = rocket is Building b ? b.OccupiedRect().CenterCell : rocket.Position;
            var entity = LaunchService.LaunchFromGround(rocket.Map, center, spec, true);
            if (entity == null)
            {
                error = "强制 PFL 发射失败。";
                return false;
            }

            ctx?.PadComp?.NotifyLaunched();
            return true;
        }

        /// <summary>
        /// 执行指定方式发射。原版模式只启动原版目标选择流程；
        /// PFL 模式立即执行入轨，并在成功后由调用方/发射台复位状态。
        /// </summary>
        public static bool TryLaunch(Thing rocket, RocketLaunchMode mode, out string error)
        {
            error = string.Empty;
            if (!TryGetLaunchContext(rocket, out var ctx))
            {
                error = "无法获取火箭发射上下文。";
                return false;
            }

            var reasons = GetLaunchBlockReasons(ctx, mode);
            if (reasons.Count > 0)
            {
                error = reasons.FirstOrDefault() ?? "发射条件不满足。";
                return false;
            }

            // 记住玩家选择的发射方式。
            ctx.PadComp?.SetLaunchMode(mode);

            if (mode == RocketLaunchMode.VanillaTransportPod)
            {
                ctx.VanillaLaunchable.StartChoosingDestination(ctx.VanillaLaunchable.TryLaunch);
                return true;
            }

            var entity = LaunchService.LaunchFromGround(
                ctx.Rocket.Map,
                ctx.Rocket is Building b ? b.OccupiedRect().CenterCell : ctx.Rocket.Position,
                ctx.Spec,
                false);
            if (entity == null)
            {
                error = "PFL 入轨发射失败。";
                return false;
            }

            ctx.PadComp?.NotifyLaunched();
            return true;
        }

    }
}
