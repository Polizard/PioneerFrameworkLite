using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// CompNozzle 的配置属性。
    /// 定义推力大小、方向、燃料消耗等。
    /// </summary>
    public class CompProperties_Nozzle : CompProperties
    {
        /// <summary>
        /// 推力大小（牛顿或自定义单位）。
        /// </summary>
        public float thrustForce = 100f;

        /// <summary>
        /// 推力方向，相对建筑朝向的角度（度）。
        /// 0 = 建筑正面，90 = 右，180 = 后，270 = 左。
        /// </summary>
        public float thrustAngle = 0f;

        /// <summary>
        /// 是否消耗燃料。
        /// </summary>
        public bool consumesFuel = true;

        /// <summary>
        /// 每次推动消耗的燃料单位。
        /// </summary>
        public float fuelPerPush = 0.1f;

        /// <summary>
        /// 最小点火间隔（tick），防止每tick都消耗。
        /// </summary>
        public int fireIntervalTicks = 60;

        /// <summary>
        /// 启动所需最低电力。
        /// </summary>
        public float minPowerForThrust = 0f;

        public CompProperties_Nozzle()
        {
            compClass = typeof(CompNozzle);
        }
    }

    /// <summary>
    /// 推进器组件。挂载到建筑上使其成为推进器，
    /// 可向空间站/飞船提供推力向量。
    /// </summary>
    public class CompNozzle : ThingComp
    {
        private CompProperties_Nozzle Props => (CompProperties_Nozzle)props;

        // 点火状态
        private bool firing;
        public bool IsFiring => firing;

        // 上次点火 tick（用于间隔控制）
        private int lastFireTick = -1;

        // ========== 外部接口 ==========

        /// <summary>
        /// 世界空间下的推力向量。
        /// 根据建筑朝向和 Prop.thrustAngle 计算。
        /// </summary>
        public Vector3 WorldThrustVector
        {
            get
            {
                float angleDeg = parent.Rotation.AsAngle + Props.thrustAngle;
                float rad = angleDeg * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * Props.thrustForce;
            }
        }

        /// <summary>
        /// 面向推进器喷射方向的 Rot4。
        /// 推进器向自己的正面喷射，列队箭头的朝向。
        /// </summary>
        public Rot4 ExhaustDirection
        {
            get
            {
                // thrustAngle 是推力方向，排气方向相反（推力朝前，排向后）
                float exhaustAngleDeg = (parent.Rotation.AsAngle + Props.thrustAngle + 180f) % 360f;
                return Rot4.FromAngleFlat(exhaustAngleDeg);
            }
        }

        /// <summary>
        /// 引擎当前是否可点火。
        /// </summary>
        public bool CanFire()
        {
            // 间隔检查
            if (Find.TickManager.TicksGame - lastFireTick < Props.fireIntervalTicks)
                return false;

            // 如果有 CompPowerTrader，检查电力
            var powerComp = parent.TryGetComp<CompPowerTrader>();
            if (powerComp != null && Props.minPowerForThrust > 0f && !powerComp.PowerOn)
                return false;

            // 如果有 CompRefuelable，检查燃料
            if (Props.consumesFuel)
            {
                var refuelable = parent.TryGetComp<CompRefuelable>();
                if (refuelable != null && refuelable.Fuel <= 0f)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 尝试点火。返回是否成功。
        /// </summary>
        public bool StartFiring()
        {
            if (firing)
                return true;

            if (!CanFire())
                return false;

            firing = true;
            lastFireTick = Find.TickManager.TicksGame;
            ConsumeFuel();
            return true;
        }

        /// <summary>
        /// 熄火。
        /// </summary>
        public void StopFiring()
        {
            if (!firing)
                return;
            firing = false;
        }

        /// <summary>
        /// 消耗燃料（直接调用）。
        /// </summary>
        public void ConsumeFuel()
        {
            if (!Props.consumesFuel)
                return;

            var refuelable = parent.TryGetComp<CompRefuelable>();
            if (refuelable != null)
            {
                refuelable.ConsumeFuel(Props.fuelPerPush);
            }
        }

        // ========== 生命周期 ==========

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref firing, "firing");
            Scribe_Values.Look(ref lastFireTick, "lastFireTick");
        }

        public override void CompTick()
        {
            base.CompTick();

            // 每 tick 检查是否需要熄火（燃料耗尽、断电等）
            if (firing)
            {
                if (!CanFire())
                {
                    StopFiring();
                    return;
                }

                // 每 fireIntervalTicks 消耗一次燃料
                if (Find.TickManager.TicksGame - lastFireTick >= Props.fireIntervalTicks)
                {
                    ConsumeFuel();
                    lastFireTick = Find.TickManager.TicksGame;
                }
            }
        }

        // ========== Gizmo ==========

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            // 调试：点火/熄火切换
            yield return new Command_Toggle
            {
                defaultLabel = "推进器: " + (firing ? "开" : "关"),
                defaultDesc = "切换推进器点火/熄火。\n" +
                              "推力: " + Props.thrustForce + "\n" +
                              "角度: " + Props.thrustAngle + "°\n" +
                              "世界推力: " + WorldThrustVector,
                icon = TexCommand.DesirePower,
                isActive = () => firing,
                toggleAction = delegate
                {
                    if (firing)
                        StopFiring();
                    else
                        StartFiring();
                }
            };
        }
    }
}