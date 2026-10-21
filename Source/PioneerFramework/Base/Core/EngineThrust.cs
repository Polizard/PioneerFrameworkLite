using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **B4b：推力作用点/偏心力矩的纯函数**（2026-10-17）。
    /// 引擎按 `massFlowRate` 分摊总推力，逐引擎 `F_i = F·(ṁ_i/Σṁ)`，力矩 `Σ r_i × F_i`（绕给定质心）。
    /// 姿态方向由调用方给（系统系转本体系后再传入）；魔法档当前不施加此力矩，仅作读数/物理档（批 2）前置。
    /// </summary>
    public static class EngineThrust
    {
        /// <summary>`points` = (相对质心的位置, 质量流率)，`direction` = 推力方向（任意长度，内部归一）。</summary>
        public static DVector3 Torque(IReadOnlyList<(DVector3 r, float flow)> points,
            DVector3 direction, double thrustN)
        {
            if (points == null || points.Count == 0 || !(thrustN > 0.0)) return DVector3.zero;
            if (direction.sqrMagnitude < 1e-12) return DVector3.zero;
            DVector3 dir = direction.normalized;

            double totalFlow = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                float f = points[i].flow;
                if (f > 0f) totalFlow += f;
            }
            if (!(totalFlow > 0.0)) return DVector3.zero;

            DVector3 torque = DVector3.zero;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (!(p.flow > 0f)) continue;
                double weight = p.flow / totalFlow;
                torque += DVector3.Cross(p.r, dir * (thrustN * weight));
            }
            return torque;
        }
    }
}
