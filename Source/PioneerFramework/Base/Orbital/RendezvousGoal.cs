using System;

namespace PioneerFramework
{
    /// <summary>
    /// **交会的"目的"（到达判据）**（2026-09-21）。纯描述量，不碰 Verse/UnityEngine。
    ///
    /// ## 为什么需要它
    /// 上游（MJ）与我们的旧口径都只认**一种**交会：**精确贴靠**——`距离 ≤ 容差 且 相对速度 ≤ 容差`
    /// （我们现行 2 km / 1 m/s）。但交会在本库要服务的目的不止一种：
    /// · **战斗**：够近、相对速度小到能打/能登船就行，不需要贴着；
    /// · **相位共轨**：要的是"**同一条轨道、按需保持某个相位差**"——判据是**要素**（a/e/i）而不是距离；
    /// · **松条件**：只要求经过某处。
    /// 把这些写成"到处各写两个数"，就会出现"改了一处忘一处"（本会话已经吃过这个亏）。
    /// ⇒ 收成一个**判据对象**，执行器/阶梯/UI 都读它。
    ///
    /// ## 与"捷径"的关系（同批的另一个机制）
    /// 到达判据一旦成立，且有"一步就能吸收的残余相对速度"时，**按 `<see cref="Kind"/>` 直接抹平**
    /// （见 `SpaceGameComp` 的收尾捷径）——而不是再走"解 Δv → 选网 → 方向 → 居中"整条链。
    ///
    /// ## ⚠ 现状（明确登记，别误以为已完成）
    /// 本类型**目前只落实了 `Kind` 的数据面 + `IsMet` 的距离/速度判据**。
    /// `OrbitalElements` / `Phase` 两个目的**只登记了语义，尚未实现判据**——
    /// 它们要读轨道要素（a/e/i）与相位，而那是另一件事（步序：先让"目的可配"这条接口站住，
    /// 再逐个把判据补上）。**未实现的目的不得偷偷当成 `Precise` 用**——
    /// `IsMet` 遇到未实现的目的会**保守判为未达成**并留一行说明。
    /// </summary>
    public readonly struct RendezvousGoal
    {
        /// <summary>交会的目的。决定 <see cref="IsMet"/> 用哪一套判据。</summary>
        public enum Kind
        {
            /// <summary>精确贴靠（现行口径）：距离 + 相对速度双判据。**默认**。</summary>
            Precise = 0,
            /// <summary>战斗/接舷：够近 + 相对速度可接受即可，容差显著宽于精确贴靠。</summary>
            Combat = 1,
            /// <summary>相位共轨：同轨（要素）且相位差达标。**判据未实现**（见类型注释的现状说明）。</summary>
            PhaseSynced = 2,
            /// <summary>松条件：只要到过某处（只判距离）。</summary>
            Loose = 3,
        }

        public readonly Kind kind;
        /// <summary>距离容差（m）。</summary>
        public readonly double distanceTol;
        /// <summary>相对速度容差（m/s）。仅在需要速度判据的目的下被读。</summary>
        public readonly double speedTol;
        /// <summary>**期望的相位差**（度，**有符号**：正 = 目标在追击方**前方**）。
        /// 仅 `PhaseSynced` 读。0 = 希望贴到目标处。</summary>
        public readonly double phaseDeg;
        /// <summary>相位差容差（度）。仅 `PhaseSynced` 读。</summary>
        public readonly double phaseTolDeg;

        public RendezvousGoal(Kind kind, double distanceTol, double speedTol,
            double phaseDeg = 0.0, double phaseTolDeg = 0.0)
        {
            this.kind = kind;
            this.distanceTol = distanceTol;
            this.speedTol = speedTol;
            this.phaseDeg = phaseDeg;
            this.phaseTolDeg = phaseTolDeg;
        }

        /// <summary>**精确贴靠**（默认口径）。</summary>
        public static RendezvousGoal Precise(double distanceTol = 2000.0, double speedTol = 1.0)
            => new RendezvousGoal(Kind.Precise, distanceTol, speedTol);

        /// <summary>**战斗/接舷**：5 km 内、相对速度 ≤ 50 m/s 就够（能打/能登船，不需要贴靠）。
        /// ⚠ 这两个数是**本库的初始取值**，不是上游口径（上游没有这个目的）；要按战斗机制调就改这里。</summary>
        public static RendezvousGoal Combat()
            => new RendezvousGoal(Kind.Combat, 5_000.0, 50.0);

        /// <summary>**松条件**：只判距离（相对速度不设限）。</summary>
        public static RendezvousGoal Loose(double distanceTol = 10_000.0)
            => new RendezvousGoal(Kind.Loose, distanceTol, double.PositiveInfinity);

        /// <summary>★★ **相位共轨**（2026-09-22 实现）——"**同一条轨道、按需保持某个相位差**"。
        ///
        /// ## 判据（三条全中）
        /// ① **同轨**：`|a_chaser − a_target| ≤ aTol`（半长轴对上 ⇒ 同周期 ⇒ 相位差不再漂）；
        /// ② **相对速度** ≤ `speedTol`（共轨还得"同速"：同一条轨道上不同点速度方向不同，
        ///    <paramref name="speedTol"/> 实际卡的是"是否真的在同一轨道上巡航"）；
        /// ③ **相位差** `|signed(相位差) − phaseDeg| ≤ phaseTolDeg`。
        ///
        /// ## 为什么它是"战斗要用的那一种"
        /// 战斗/接舷关心的往往不是"贴着"，而是**相对位置关系**（我在你前方 N 度、或我在你后半球）。
        /// 旧口径只认距离，做不了这件事；`phaseDeg` 就是那个旋钮。
        ///
        /// ⚠ **有符号**：正 = 目标在追击方**前方**（沿轨道前进方向），负 = 后方。
        ///   判据用有符号量 ⇒ 交接双方能区分"我要追上去"与"我要等你过来"。
        /// </summary>
        /// <param name="phaseDeg">期望相位差（度，有符号；正 = 目标在前方）</param>
        /// <param name="phaseTolDeg">相位容差（度）</param>
        /// <param name="aTol">半长轴容差（m）</param>
        /// <param name="speedTol">相对速度容差（m/s）</param>
        public static RendezvousGoal PhaseSynced(double phaseDeg, double phaseTolDeg = 1.0,
            double aTol = 1000.0, double speedTol = 20.0)
            => new RendezvousGoal(Kind.PhaseSynced, aTol, speedTol, phaseDeg, phaseTolDeg);

        /// <summary>
        /// **沿轨有符号相位差**（度，−180..+180）：目标相对追击方的相位，**正 = 目标在前方**。
        /// `+Z` 参考 = 轨道法向（`Cross(r, v)`）。
        ///
        /// ⚠ 与 `RendezvousLadder.PhaseDifferenceDeg`（**无符号短弧 0..180**）是**两个量**，别混：
        ///   那个回答"要收掉多少角度"，这个回答"**该加速还是该等**"。
        ///   收角度用前者（阶梯里已经在用）；**做判据必须用有符号的这一个**
        ///   （否则"我在你前方 5°"与"我在你后方 5°"会被判成同一件事）。
        /// </summary>
        public static double SignedPhaseDeg(DVector3 rChaser, DVector3 vChaser, DVector3 rTarget)
        {
            double rn = rChaser.magnitude, tn = rTarget.magnitude;
            if (!(rn > 1.0) || !(tn > 1.0)) return double.NaN;
            DVector3 n = DVector3.Cross(rChaser, vChaser);
            if (!(n.sqrMagnitude > 0.0)) return double.NaN;
            DVector3 nn = n.normalized;
            double sin = DVector3.Dot(nn, DVector3.Cross(rChaser / rn, rTarget / tn));
            double cos = DVector3.Dot(rChaser / rn, rTarget / tn);
            return Math.Atan2(sin, cos) * 180.0 / Math.PI;
        }

        // 批5.B（2026-09-28，用户："相位让我们的 MJ 接管"）：此处原有从**已作废旧律**逐字搬来的
        // `PlaneBasis` / `PhaseErrorRadians`（平黄经口径）/ `WrapPi` 三件。面板的相位读数改用
        // `SignedPhaseDeg`（上面那条 —— 本类型自己的判据权威）之后它们**零调用者** ⇒ 一并退役。
        // 留个记号：**相位口径只有一份**（`SignedPhaseDeg`）；旧律那份平黄经版随律归档，
        // 要回看：`Source/已弃置/RendezvousGuidanceLaw.cs`（`PhaseErrorRadians`）。

        /// <summary>
        /// 到达判据（**距离 + 相对速度**两参版）。**唯一权威**——执行器与阶梯都调这里，
        /// 不许各自写"距离 < x 且 速度 < y"。
        /// ⚠ <see cref="Kind.PhaseSynced"/> 需要相位差作第三参 ⇒ **本重载对它保守返回 false**
        ///   （不得用"缺参"冒充已达成）；相位场景请调三参重载。
        /// </summary>
        public bool IsMet(double distance, double relativeSpeed)
            => IsMet(distance, relativeSpeed, phaseErrDeg: double.NaN);

        /// <summary>
        /// 到达判据（三参，**含相位**）。
        /// </summary>
        /// <param name="phaseErrDeg">**沿轨有符号相位差**（度；见 <see cref="SignedPhaseDeg"/>）。
        /// 只有 <see cref="Kind.PhaseSynced"/> 读它；其它目的传 NaN 即可。</param>
        public bool IsMet(double distance, double relativeSpeed, double phaseErrDeg)
        {
            switch (kind)
            {
                case Kind.Precise:
                case Kind.Combat:
                    return distance <= distanceTol && relativeSpeed <= speedTol;
                case Kind.Loose:
                    return distance <= distanceTol;
                case Kind.PhaseSynced:
                    // ⚠ `distance` 这一参在相位目的下被当作**半长轴差**用（调用方按目的传）
                    if (double.IsNaN(phaseErrDeg)) return false;
                    return distance <= distanceTol                      // ① 同轨（半长轴差）
                        && relativeSpeed <= speedTol                    // ② 相对速度
                        && Math.Abs(PhaseErrorDeg(phaseErrDeg)) <= phaseTolDeg;  // ③ 相位差
                default:
                    return distance <= distanceTol && relativeSpeed <= speedTol;
            }
        }

        /// <summary>与期望相位差的偏差（度，取 −180..+180 的短弧；正 = 目标比期望**更靠前**）。</summary>
        public double PhaseErrorDeg(double actualPhaseDeg)
        {
            double d = actualPhaseDeg - phaseDeg;
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        public override string ToString()
            => kind == Kind.PhaseSynced
                ? string.Format("{0}(相位 {1:+0.0;-0.0;0}°±{2:F1}°, Δa≤{3:F0} m, relVel≤{4:F1})",
                    kind, phaseDeg, phaseTolDeg, distanceTol, speedTol)
                : string.Format("{0}(dist≤{1:F0} m, relVel≤{2})", kind, distanceTol,
                    double.IsInfinity(speedTol) ? "∞" : speedTol.ToString("F1"));
    }
}
