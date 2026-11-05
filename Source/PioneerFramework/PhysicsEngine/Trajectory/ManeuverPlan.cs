using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>计划级选网口径（2026-10-27）。</summary>
    public enum ManeuverNetworkMode : byte
    {
        /// <summary>沿用玩家选网（机动队列/交会用）。</summary>
        InheritPlayer = 0,
        /// <summary>本次强制全网络（圆轨化/AI/导弹/登船等旧行为）。</summary>
        All = 1,
        /// <summary>计划显式指定网络。</summary>
        Explicit = 2,
    }

    /// <summary>
    /// **机动的唯一计划表示**（2026-10-27，控制层收敛第 1 步）。
    ///
    /// 目的：消掉"`plannedDvBody` / R/T/N 分量 / 执行时再分解"三种表示——
    /// 执行器只认 `dv`（平移不变 ⇒ 体心/世界同向）；分量只作**方向锚点/诊断**，不参与方向决策。
    /// 规划是低频对象；执行层每步不构造它。
    /// </summary>
    public struct ManeuverPlan
    {
        /// <summary>本段要交付的 Δv（m/s）。方向即执行方向（体心系与世界系只差平移 ⇒ 同向）。</summary>
        public DVector3 dv;

        /// <summary>节点时刻（physicsTick 秒轴；&lt;0 = 立即）。仅信息/对照，不参与方向。</summary>
        public int nodeTick;

        /// <summary>计划燃烧时长（s）。&gt;0 ⇒ 命令加速度 = |dv| / 本值；否则用 `commandAccel`。</summary>
        public double burnSeconds;

        /// <summary>`burnSeconds &lt;= 0` 时的命令加速度（0 = 全油门，保留旧路径语义）。</summary>
        public double commandAccel;

        /// <summary>选网口径。</summary>
        public ManeuverNetworkMode networks;

        /// <summary>`Explicit` 时的网络集合。</summary>
        public HashSet<PropellantDef>? useNetworks;

        /// <summary>是否走 RCS（显式执行机构；默认主网）。</summary>
        public bool useRcs;

        /// <summary>是否携带节点帧 R/T/N 分量（仅作方向锚点/诊断）。</summary>
        public bool hasSolution;
        public float radialDV;
        public float tangentialDV;
        public float normalDV;

        /// <summary>引导修正 burn（允许引导打断重烧）。</summary>
        public bool isCorrection;

        /// <summary>点火后挂交会引导。</summary>
        public bool armRendezvousGuidance;

        /// <summary>点火前退出圆轨解析模式（圆轨化用）。</summary>
        public bool clearCircularOrbit;
    }
}
