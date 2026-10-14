using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PioneerFramework
{
    /// <summary>引力场中的一个天体（纯数据）：**系统系内**的位置/速度 + 引力常数。
    /// 系统系 = 根天体（参考帧天体，行星或恒星）为中心、该根钉住；位置沿父链累加到根。</summary>
    public struct GravitySource
    {
        public DVector3 position;
        public DVector3 velocity;
        public double mu;
        /// <summary>天体 id（用于白名单过滤 / 祖先链查找）。-1 = 未标注。</summary>
        public int bodyId;

        public GravitySource(DVector3 position, DVector3 velocity, double mu)
        {
            this.position = position;
            this.velocity = velocity;
            this.mu = mu;
            this.bodyId = -1;
        }

        public GravitySource(DVector3 position, DVector3 velocity, double mu, int bodyId)
        {
            this.position = position;
            this.velocity = velocity;
            this.mu = mu;
            this.bodyId = bodyId;
        }
    }

    public static class NBodyIntegrator
    {
        /// <summary>
        /// 单体引力的加速度与急动度（共享原语：全库唯一的引力算式副本）。
        /// `AggressiveInlining`：热路径逐源调用（每步每群），调用开销与参数搬运占比可观 ⇒ 让 JIT 内联进循环。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BodyAccelJerkCore(
            double scX, double scY, double scZ,
            double scVx, double scVy, double scVz,
            double bX, double bY, double bZ,
            double bVx, double bVy, double bVz,
            double mu,
            out double ax, out double ay, out double az,
            out double jx, out double jy, out double jz)
        {
            double rx = bX - scX;
            double ry = bY - scY;
            double rz = bZ - scZ;
            double vx = bVx - scVx;
            double vy = bVy - scVy;
            double vz = bVz - scVz;

            double rSq = rx * rx + ry * ry + rz * rz;

            if (rSq < 0.01)
            {
                ax = ay = az = jx = jy = jz = 0.0;
                return;
            }

            double invR = 1.0 / Math.Sqrt(rSq);
            double invRSq = invR * invR;
            double invR3 = invRSq * invR;
            double muR3 = mu * invR3;

            ax = muR3 * rx;
            ay = muR3 * ry;
            az = muR3 * rz;

            double rDotV = rx * vx + ry * vy + rz * vz;
            double alpha = 3.0 * rDotV * invRSq;

            jx = muR3 * vx - alpha * ax;
            jy = muR3 * vy - alpha * ay;
            jz = muR3 * vz - alpha * az;
        }

        public static void ComputeBodyAccelJerk(
            DVector3 scPos, DVector3 scVel,
            DVector3 bodyPos, DVector3 bodyVel,
            double mu,
            out DVector3 acceleration, out DVector3 jerk)
        {
            BodyAccelJerkCore(
                scPos.x, scPos.y, scPos.z, scVel.x, scVel.y, scVel.z,
                bodyPos.x, bodyPos.y, bodyPos.z, bodyVel.x, bodyVel.y, bodyVel.z,
                mu,
                out double ax, out double ay, out double az,
                out double jx, out double jy, out double jz);

            acceleration = new DVector3(ax, ay, az);
            jerk = new DVector3(jx, jy, jz);
        }

        /// <summary>
        /// **引力场**：对给定天体集合做全量求和（无"环绕天体"参量）。
        /// pos/vel 与 sources 的位置/速度必须**同处系统系**——这是本方法唯一的约定。
        /// </summary>
        public static void FieldAccelJerk(
            DVector3 pos, DVector3 vel,
            List<GravitySource> sources,
            out DVector3 acceleration, out DVector3 jerk)
            => FieldAccelJerk(pos, vel, sources, null, out acceleration, out jerk);

        /// <summary>
        /// **引力场（带"不计算的天体"掩码，[A6]，2026-10-06）**：在**求和循环里**跳过
        /// `ignoredBodyIds` 中的天体（大气档 = "只算中心天体"；掩码极短 ⇒ 循环内线性扫，不用 HashSet）。
        ///
        /// ⚠ 两条纪律（都是既有事故换来的）：
        /// · **过滤必须在求和循环内**——**不许**给每组建一张"过滤后的源表"
        ///   （S-a 收尾刚删掉"每群每步拷表"，见 `SpaceGameComp.ResolveSystemSources` 的注释）；
        /// · **空掩码与无掩码逐位同路**：同一循环、同一累加顺序；`null` 只多一次空判断（不碰浮点算式）。
        /// </summary>
        public static void FieldAccelJerk(
            DVector3 pos, DVector3 vel,
            List<GravitySource> sources,
            List<int>? ignoredBodyIds,
            out DVector3 acceleration, out DVector3 jerk)
        {
            double ax = 0.0, ay = 0.0, az = 0.0;
            double jx = 0.0, jy = 0.0, jz = 0.0;

            // Span 视图直读：免"逐源整结构体拷贝"（`GravitySource` 7 个 double）。`AsSpan(null)` = 空 span，
            // 与原 `sources != null` 判断同路；值语义、算式、累加顺序全不变（逐位同结果）。
            var span = CollectionsMarshal.AsSpan(sources);
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly GravitySource s = ref span[i];
                if (ignoredBodyIds != null && ContainsId(ignoredBodyIds, s.bodyId)) continue;
                BodyAccelJerkCore(
                    pos.x, pos.y, pos.z, vel.x, vel.y, vel.z,
                    s.position.x, s.position.y, s.position.z,
                    s.velocity.x, s.velocity.y, s.velocity.z,
                    s.mu,
                    out double bx, out double by, out double bz,
                    out double bjx, out double bjy, out double bjz);
                ax += bx; ay += by; az += bz;
                jx += bjx; jy += bjy; jz += bjz;
            }

            acceleration = new DVector3(ax, ay, az);
            jerk = new DVector3(jx, jy, jz);
        }

        /// <summary>小数组线性查找（掩码 ≤3 项；求和循环里不引入 HashSet 的哈希/分配成本）。</summary>
        private static bool ContainsId(List<int> ids, int bodyId)
        {
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == bodyId) return true;
            return false;
        }

        /// <summary>
        /// **差分引力**（= "场 − 计划"）：对每个天体取「在 pos 处的全量项」减「在参考点 refPos 处的全量项」，
        /// 即该天体贡献中**超出二体计划轨道**的那部分（潮汐/间接项）。由线性性，结果等价于
        /// FieldAccelJerk(pos) − FieldAccelJerk(refPos) 对同一组天体。
        ///
        /// **约定（易错）**：sources 的位置/速度与 pos、refPos **必须同处一个参考系**。
        /// 在子天体局部系里用时，外部天体要写成它在该局部系里的位置（如恒星系里用「根在局部系的位置」），
        /// 把外部天体放在原点会让差分退化成全量项——正是本方法要消灭的那类参考系混用。
        /// 用途：轨道保持——抵消非白名单天体的扰动、同时保住计划轨道本身。
        /// </summary>
        public static void DifferentialAccelJerk(
            DVector3 pos, DVector3 vel,
            DVector3 refPos, DVector3 refVel,
            List<GravitySource> sources,
            out DVector3 acceleration, out DVector3 jerk)
        {
            double ax = 0.0, ay = 0.0, az = 0.0;
            double jx = 0.0, jy = 0.0, jz = 0.0;

            // Span 视图直读（同 `FieldAccelJerk` 的掩码重载）：免逐源结构体拷贝。
            var span = CollectionsMarshal.AsSpan(sources);
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly GravitySource s = ref span[i];

                BodyAccelJerkCore(
                    pos.x, pos.y, pos.z, vel.x, vel.y, vel.z,
                    s.position.x, s.position.y, s.position.z,
                    s.velocity.x, s.velocity.y, s.velocity.z,
                    s.mu,
                    out double bx, out double by, out double bz,
                    out double bjx, out double bjy, out double bjz);

                BodyAccelJerkCore(
                    refPos.x, refPos.y, refPos.z, refVel.x, refVel.y, refVel.z,
                    s.position.x, s.position.y, s.position.z,
                    s.velocity.x, s.velocity.y, s.velocity.z,
                    s.mu,
                    out double rx, out double ry, out double rz,
                    out double rjx, out double rjy, out double rjz);

                ax += bx - rx; ay += by - ry; az += bz - rz;
                jx += bjx - rjx; jy += bjy - rjy; jz += bjz - rjz;
            }

            acceleration = new DVector3(ax, ay, az);
            jerk = new DVector3(jx, jy, jz);
        }

        /// <summary>
        /// 三阶泰勒多项式在任意时刻 t 的取值（不含 dt 语义——t 是"相对段起点的秒数"）。
        /// 段内插值与段端点共用本式，是"2 秒一段 + 插值飞行"的唯一算式来源。
        /// </summary>
        public static void TaylorInterpolate(
            DVector3 pos, DVector3 vel,
            DVector3 acc, DVector3 jerk,
            double t,
            out DVector3 newPos, out DVector3 newVel)
        {
            double c1 = t;
            double c2 = 0.5 * t * t;
            double c3 = c2 * t / 3.0;

            double px = pos.x + c1 * vel.x + c2 * acc.x + c3 * jerk.x;
            double py = pos.y + c1 * vel.y + c2 * acc.y + c3 * jerk.y;
            double pz = pos.z + c1 * vel.z + c2 * acc.z + c3 * jerk.z;

            double vx = vel.x + c1 * acc.x + c2 * jerk.x;
            double vy = vel.y + c1 * acc.y + c2 * jerk.y;
            double vz = vel.z + c1 * acc.z + c2 * jerk.z;

            newPos = new DVector3(px, py, pz);
            newVel = new DVector3(vx, vy, vz);
        }

        /// <summary>三阶泰勒步（= TaylorInterpolate 在 t = dt 处的取值）。</summary>
        public static void ThirdOrderTaylorStep(
            DVector3 pos, DVector3 vel,
            DVector3 acc, DVector3 jerk,
            double dt,
            out DVector3 newPos, out DVector3 newVel)
        {
            TaylorInterpolate(pos, vel, acc, jerk, dt, out newPos, out newVel);
        }

        /// <summary>引力参数兜底：Def 未配 GM 时用全局主天体值（根的参考系锚点语义，唯一来源）。</summary>
        public static double EffectiveMu(double defMu) => defMu > 0.0 ? defMu : SpaceConstants.PrimaryGravitationalParameter;

        /// <summary>按 bodyId 在源列表里查找（-1 的源不参与匹配）。</summary>
        public static bool TryFindSource(List<GravitySource> sources, int bodyId, out GravitySource found)
        {
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    if (sources[i].bodyId == bodyId)
                    {
                        found = sources[i];
                        return true;
                    }
                }
            }
            found = default;
            return false;
        }

        /// <summary>
        /// **"场 − 计划"差分**（纯）：= 场(全部天体) − 中心天体二体项 − 中心天体自身受迫加速度。
        /// 物理含义：局部系随中心天体自由落体，故"外面那个天体"的均匀项被抵消，只剩**潮汐/间接项**。
        ///
        /// 这是全库唯一的差分口径（`CelestialBodyManager.ComputeFieldMinusPlan` 解析出 center/parent 后
        /// 委托到这里；探针、自检电池、轨道保持的抵消量都用它）。**写成"场 − 中心天体单独在场"是错的**：
        /// 那是外面天体的*全量*项（地球→月球实测 6.86%，应为 0.3%–0.6%）。
        /// 1 层层级（Earth→Moon）精确；更深的链需要逐级递归（PFL 数据无此情形）。
        /// </summary>
        public static void FieldMinusPlanAccelJerk(DVector3 pos, DVector3 vel,
            GravitySource center, bool hasCenter,
            GravitySource parent, bool hasParent,
            List<GravitySource> sources,
            out DVector3 acc, out DVector3 jerk)
        {
            FieldAccelJerk(pos, vel, sources, out var fieldAcc, out var fieldJerk);

            DVector3 twoBody = DVector3.zero, twoBodyJerk = DVector3.zero;
            if (hasCenter)
                ComputeBodyAccelJerk(pos, vel, center.position, center.velocity, center.mu,
                    out twoBody, out twoBodyJerk);

            DVector3 aCenter = DVector3.zero, jCenter = DVector3.zero;
            if (hasCenter && hasParent)
                ComputeBodyAccelJerk(center.position, center.velocity, parent.position, parent.velocity, parent.mu,
                    out aCenter, out jCenter);

            acc = fieldAcc - twoBody - aCenter;
            jerk = fieldJerk - twoBodyJerk - jCenter;
        }
    }

    /// <summary>
    /// 三阶泰勒传播段：在段起点 (startPos, startVel) 处采样一次 (acc, jerk)，此后段内任意时刻
    /// 都用同一个多项式求值——即"隔一段时间重算一次引力、其间插值飞行"。
    /// 段端点与段内取值共用 <see cref="NBodyIntegrator.TaylorInterpolate"/>，全库只此一份算式。
    /// 用途：群组战斗渲染插值（2s 段）与引力射弹弹道（2 物理秒段）。
    /// </summary>
    public struct TaylorSegment
    {
        public DVector3 startPos;
        public DVector3 startVel;
        public DVector3 acc;
        public DVector3 jerk;
        /// <summary>段起点时刻（physicsTick，游戏世界秒）。</summary>
        public double baseTick;
        /// <summary>段长（秒）；≤0 = 未初始化。</summary>
        public double length;

        public void Rebuild(DVector3 pos, DVector3 vel, DVector3 acc, DVector3 jerk, double baseTick, double length)
        {
            this.startPos = pos;
            this.startVel = vel;
            this.acc = acc;
            this.jerk = jerk;
            this.baseTick = baseTick;
            this.length = length;
        }

        public double Elapsed(double tick) => tick - baseTick;

        /// <summary>段已过期（需要重算引力再开一段）。未初始化恒为过期。</summary>
        public bool Expired(double tick) => length <= 0.0 || tick - baseTick > length;

        public DVector3 PositionAt(double t)
        {
            NBodyIntegrator.TaylorInterpolate(startPos, startVel, acc, jerk, t, out var p, out _);
            return p;
        }

        public DVector3 VelocityAt(double t)
        {
            NBodyIntegrator.TaylorInterpolate(startPos, startVel, acc, jerk, t, out _, out var v);
            return v;
        }

        public void Evaluate(double t, out DVector3 pos, out DVector3 vel)
        {
            NBodyIntegrator.TaylorInterpolate(startPos, startVel, acc, jerk, t, out pos, out vel);
        }
    }
}
