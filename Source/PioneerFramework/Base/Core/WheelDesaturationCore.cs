using System;

namespace PioneerFramework
{
    /// <summary>
    /// **动量轮 + RCS 卸载（纯函数）**（2026-10-26 用户裁定）。
    ///
    /// 模型：
    /// · 轮子吸收引擎偏心产生的力矩：`L ← L − τ·dt`，再按**可变容量**夹紧（超限部分丢弃）；
    /// · 连续比例卸载：每步想卸掉 `|L|·dt/T` 的角动量，受"罐里还能供多少"限制；
    /// · 工质消耗：`Δm = ΔL / (ve_rcs · 力臂 · η)`；`η ≤ 0` 或罐空 ⇒ 本步不卸载；
    /// · RCS 空 = 空（没有回退、没有减推力表）。
    ///
    /// 这是**纯数学**：无 Verse 依赖、无分配、可在无图/无游戏环境单测。
    /// 集成侧（缓存 COM、单位力矩、罐引用）只负责喂输入，不在这里做缓存。
    /// </summary>
    public static class WheelDesaturationCore
    {
        private const double Eps = 1e-9;

        public readonly struct Inputs
        {
            /// <summary>本步时长（s）。</summary>
            public readonly double Dt;
            /// <summary>本步外力矩（N·m，船体系；引擎偏心 `Σ(r×F)`）。</summary>
            public readonly DVector3 TorqueBody;
            /// <summary>轮子当前角动量（kg·m²/s，船体系）。</summary>
            public readonly DVector3 Momentum;
            /// <summary>轮子容量（可变；≤0 ⇒ 无轮子，不吸收也不卸载）。</summary>
            public readonly double Capacity;
            /// <summary>卸载时间常数 T（s；≤0 ⇒ 不卸载）。</summary>
            public readonly double DesatTimeSeconds;
            /// <summary>RCS 有效排气速度（m/s）。</summary>
            public readonly double RcsExhaustVelocity;
            /// <summary>RCS 力臂（m，约定 = 船长/2）。</summary>
            public readonly double RcsArmMeters;
            /// <summary>RCS 效率（0–1；≤0 ⇒ 视为无 RCS）。</summary>
            public readonly double RcsEfficiency;
            /// <summary>指定 RCS 罐当前可扣工质（kg）。</summary>
            public readonly double RcsAvailableKg;

            public Inputs(double dt, DVector3 torqueBody, DVector3 momentum, double capacity,
                double desatTimeSeconds, double rcsExhaustVelocity, double rcsArmMeters,
                double rcsEfficiency, double rcsAvailableKg)
            {
                Dt = dt;
                TorqueBody = torqueBody;
                Momentum = momentum;
                Capacity = capacity;
                DesatTimeSeconds = desatTimeSeconds;
                RcsExhaustVelocity = rcsExhaustVelocity;
                RcsArmMeters = rcsArmMeters;
                RcsEfficiency = rcsEfficiency;
                RcsAvailableKg = rcsAvailableKg;
            }
        }

        public readonly struct Outputs
        {
            /// <summary>本步结束后的轮子角动量（已夹紧、已卸载）。</summary>
            public readonly DVector3 Momentum;
            /// <summary>本步实际扣掉的 RCS 工质（kg，≥0）。</summary>
            public readonly double FuelKg;
            /// <summary>本步实际卸掉的角动量大小（kg·m²/s，≥0）。</summary>
            public readonly double DumpedMomentum;
            /// <summary>本步是否撞到容量上限（撞了 ⇒ 力矩不再被吸收）。</summary>
            public readonly bool Saturated;

            public Outputs(DVector3 momentum, double fuelKg, double dumpedMomentum, bool saturated)
            {
                Momentum = momentum;
                FuelKg = fuelKg;
                DumpedMomentum = dumpedMomentum;
                Saturated = saturated;
            }
        }

        public static Outputs Step(in Inputs x)
        {
            double cap = x.Capacity > 0.0 ? x.Capacity : 0.0;

            // ① 吸收：外力矩反向进轮子（反作用力矩）
            DVector3 L = x.Momentum - x.TorqueBody * x.Dt;
            bool saturated = false;
            double mag = L.magnitude;
            if (cap > 0.0 && mag > cap)
            {
                L = L * (cap / mag);
                mag = cap;
                saturated = true;
            }
            else if (cap <= 0.0)
            {
                L = DVector3.zero;   // 没有轮子 ⇒ 不存
                mag = 0.0;
            }

            if (mag <= Eps || x.DesatTimeSeconds <= 0.0)
                return new Outputs(L, 0.0, 0.0, saturated);

            double perKg = x.RcsExhaustVelocity * x.RcsArmMeters * x.RcsEfficiency;
            if (perKg <= Eps || x.RcsAvailableKg <= 0.0)
                return new Outputs(L, 0.0, 0.0, saturated);   // RCS 空/无效 ⇒ 就空着

            // ② 连续比例：想卸 |L|·dt/T；受罐里可提供的角动量上限约束
            double wanted = mag * x.Dt / x.DesatTimeSeconds;
            double available = x.RcsAvailableKg * perKg;
            double dump = Math.Min(wanted, available);
            if (dump <= Eps) return new Outputs(L, 0.0, 0.0, saturated);

            // ③ 结算：同方向按比例缩小 L；油耗 = ΔL / (ve·臂·η)
            L = L * (1.0 - dump / mag);
            double fuel = dump / perKg;
            return new Outputs(L, fuel, dump, saturated);
        }
    }
}
