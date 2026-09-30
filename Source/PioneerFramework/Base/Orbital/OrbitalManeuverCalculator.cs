using System;
using MechJebLib.Functions;
using MechJebLib.Lambert;
using MechJebLib.Maneuvers;

using MechJebLib.Primitives;
using MechJebLib.Utils;

namespace PioneerFramework
{
    /// <summary>
    /// "一个目标机动要多少 Δv"的计算器——MJ 的 `MechJeb2/OrbitalManeuverCalculator.cs` 的移植（KSP 层，语义移植）。
    ///
    /// **⚠ 本文件目前只移植了 `Simple` 支撑的那几个成员，不是整文件。** 逐条说明：
    /// · **可以移的**（走 `MechJebLib.Maneuvers.Simple`，无 alglib）：`DeltaVToCircularize` / `DeltaVToEllipticize` /
    ///   `DeltaVToChangeInclination` —— 分别支撑 `OperationCircularize` / `OperationEllipticize` / `OperationInclination`。
    /// · **移不了的**（走 `ChangeOrbitalElement`，**依赖 alglib** ⇒ 属 **P1**）：`DeltaVToChangeApoapsis` /
    ///   `DeltaVToChangePeriapsis` / `DeltaVToChangeSMA` / `DeltaVToChangeECC`。
    ///   ⇒ **`OperationApoapsis` / `OperationPeriapsis` / `OperationSemiMajor` / `OperationEccentricity`
    ///   这四个要等 P1 的 alglib 替换**（计划：网格扫描 + `BrentRoot`）。
    ///
    /// **两处语义偏离**（都写在各自成员的注释里）：
    /// ① **参考系**：上游每个函数结尾都做 `dv.V3ToWorld()`，那是"右手系 → KSP 左手世界系"的翻身。
    ///   **我们选定了一个右手惯性系（P-1 §1.3），所以这一步整体删掉**，直接返回右手系 Δv。
    ///   这不是适配，是上游那个 swizzle 的另一半。
    /// ② **`DeltaVToShiftLAN` 的退化域**：上游的"地面导航"模型在烧点落进赤道面内时给出无意义的 Δv
    ///   （详见该成员的注释）⇒ 我们改为精确平面解、并在解不存在时明确拒绝。
    /// </summary>
    public static class OrbitalManeuverCalculator
    {
        /// <summary>在 <paramref name="ut" /> 时刻圆化。<paramref name="o" /> 的单位与 `mu` 必须自洽。</summary>
        public static V3 DeltaVToCircularize(OrbitView o, double ut)
        {
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            return Simple.DeltaVToCircularize(o.mu, r, v);
        }

        /// <summary>把轨道改成指定的近/远心点距离（保持当前平面与时刻）。</summary>
        public static V3 DeltaVToEllipticize(OrbitView o, double ut, double newPeR, double newApR)
        {
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            return Simple.DeltaVToEllipticize(o.mu, r, v, newPeR, newApR);
        }

        /// <summary>
        /// 改倾角（在给定时刻、以当前半径为支点）。
        ///
        /// ## ⚠ 与上游的偏离（治本，2026-09-19）：**两支都试、取省的那支**
        /// 上游（KSP 层 `:123`）是 `Simple.DeltaVToChangeInclination(r, v, Deg2Rad(newInc))` **单支**。
        /// 而那条路最终落到 `Astro.VelocityForInclination`：它把**水平速度的航向**按新倾角重摆，
        /// 而航向有**两个解**（北向 / 南向，符号由 `AngleForInclination` 里 `inc < 0` 那一行决定）
        /// —— **两支给出同一个倾角，但交点线差 180°**。单支碰巧取到"远支"时，
        /// 烧的是绕极轴多转一圈的那个平面，代价与节点都错。
        ///
        /// **实测（游戏内一键验收，2026-09-19）**：受控轨道（倾角 30°、e=0.3、在远端点火）
        /// 请求"直觉 45°"（= 适配层 135°，Δi = 105°）时，
        /// 单支给出 **6507 m/s、方向 T=−6451 / N=+849**（对应 Δ≈165° = 30+135 ⇒ 节点被翻）；
        /// 而闭式下界是 `2v·sin(105°/2) = 5207 m/s`。修好后应当拿回 5207 那一支。
        ///
        /// 依据：目标倾角给定、节点线不指定时，两个候选平面正是"绕**交点线**倾斜 |Δi|"与
        /// "绕极轴多转一圈（= i + i₀）" ⇒ **取更小的那个旋转 = 保住交点线**，两者在此重合。
        /// 这也正是上游自己在**上升段**那条路径里的做法（`Simple.DeltaVForLaunchInclination`：
        /// "picks the least expensive north/south option"）——它只是**没把同一条道理用到普通改倾角上**。
        /// </summary>
        public static V3 DeltaVToChangeInclination(OrbitView o, double ut, double newInc)
        {
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);

            V3 dvNorth = Simple.DeltaVToChangeInclination(r, v, newInc);
            V3 dvSouth = Simple.DeltaVToChangeInclination(r, v, -newInc);

            return dvSouth.magnitude < dvNorth.magnitude ? dvSouth : dvNorth;
        }

        // ── 下面四个走 `ChangeOrbitalElement`（上游用 alglib，我们重写为两层一维；见那个文件头）──
        // **入参清洗逐字照搬上游**（那是"用户输入越界时的兜底"，不是可以随手改的口径）。

        public static V3 DeltaVToChangeApoapsis(OrbitView o, double ut, double newApR)
        {
            double radius = o.Radius(ut);

            // sanitize input（上游同款：只对正值做下限保护，留下"远心点无穷远 = 逃逸"的口子）
            if (newApR > 0) newApR = Math.Max(newApR, radius + 1);

            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            return ChangeOrbitalElement.ChangeApoapsis(o.mu, r, v, newApR);
        }

        public static V3 DeltaVToChangePeriapsis(OrbitView o, double ut, double newPeR)
        {
            double radius = o.Radius(ut);

            // sanitize input（上游同款：夹在 (0, radius) 开区间内）
            newPeR = Statics.Clamp(newPeR, 1.0, radius - 1.0);

            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            return ChangeOrbitalElement.ChangePeriapsis(o.mu, r, v, newPeR);
        }

        public static V3 DeltaVToChangeEccentricity(OrbitView o, double ut, double newEcc)
        {
            // sanitize input（上游同款）
            if (newEcc < 0) newEcc = 0;

            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            return ChangeOrbitalElement.ChangeECC(o.mu, r, v, newEcc);
        }

        /// <summary>
        /// **匹配速度**：`目标速度 − 自身速度`（上游是一行）。
        /// ⚠ 上游用的是**世界系**速度、两者必须同系；我们这边两者都在**适配层参考系**里，
        /// 而"两个同系矢量之差"在换系下是协变的 ⇒ 这个 Δv 本身**与参考系无关** ✓
        /// （与 `GetInclination` 那种"方向量"不同，差矢量不需要换系）。
        /// </summary>
        public static V3 DeltaVToMatchVelocities(OrbitView o, double ut, OrbitView target) =>
            target.RightHandedOrbitalVelocityAtUT(ut) - o.RightHandedOrbitalVelocityAtUT(ut);

        /// <summary>
        /// **进入共振轨道**：把周期改成当前周期的 `f` 倍（`f = 分子/分母`）。
        ///
        /// ## ⚠ 与上游**做法不同**，理由有实测支撑（见下）
        /// 上游：先用闭式算 `x = f^(2/3)·(ApR+PeR) − ApR`，再调 `ChangePeriapsis(x)` / `ChangeApoapsis(x)`。
        /// **那个组合只在一种情况下精确**：当"改这个拱点"的**最小范数 Δv 恰好是纯横推**
        /// （纯横推才保持**另一个**拱点，而 `x` 正是从"另一个拱点不变"推出来的）。
        ///
        /// **实测（`Diag_WhichMember`，2026-09-18）**——同一烧点（远心点）、同一个目标 `ApR × 1.016`：
        /// · 最小范数解 = **370 m/s，径向 −355 / 横向 106** ⇒ 它把 **PeR 从 1.0000 改成 1.0943R₀** ✗
        /// · 纯横推解 = 656 m/s ⇒ 保持 PeR ✓（但**贵 77%**）
        /// ⇒ 最小范数**并不是**纯横推，于是"另一个拱点不变"的前提**不成立** ⇒ 周期比实测 `1.0656 ≠ 1.5`，**且无任何提示**。
        /// · 而在 `ApR × 1.442`（f=2）与 `PeR × 0.323`（f=2/3）两个目标上，最小范数解**恰好就是纯横推**
        ///   （径向分量 = 0）⇒ 上游组合**精确命中**（0.6667 / 2.0000）✓。
        /// ⇒ 上游不是"错"，而是**正确性依赖一个不普遍成立的前提**；漂不漂取决于 f 落在哪一段。
        /// （alglib 解的是**同一个 NLP**，所以这不是我们的求解器差异——370 &lt; 656 说明我们找到了**更省**的点。）
        ///
        /// ## 我们的做法：约束**周期本身**
        /// `T ∝ sma^(3/2)` ⇒ 令 **`sma_new = f^(2/3)·sma_old`**，周期比就**精确**等于 `f`，
        /// 而 `DeltaVForSemiMajorAxis` / `ChangeSMA` 求的正是**它的**最小范数脉冲。
        /// ⇒ **没有"另一个拱点必须不变"这个前提**，因此对**所有** f 都成立（不可行时抛异常，而不是静默零脉冲）。
        /// L2 oracle 断言 `T_new/T_old == f`（容差 1e-8），三个 f 全绿。
        /// </summary>
        /// <summary>上游 `:283` 一族：进入 (f) 共振轨道 —— **直接约束半长轴**：`a_new = a·f^{2/3}`。
        /// ⚠⚠ **2026-09-20 两次试错的结论（引以为戒）**：
        ///   ① 我一度改成"上游的闭式拱点解" —— **错**，而且**上游那一支本身就有问题**：
        ///      `x = f^{2/3}(a+p) − a` 是"改**近点**"的解（a=ApR、p=PeR），而它对 `f>1` 也拿去改**远点** ✗；
        ///      更根本的是：改拱点**必须烧在被改那一端的对侧拱点**（纯横推才保持另一个拱点）⇒ 上游那个
        ///      `f>1 ? Ap : Pe` 的选法是拿 `f` 当烧点的替身 ✗。实测（远心点烧、f=1.5）周期比只到 1.066/1.482 ✗。
        ///   ② 它同时**破坏了本类的契约**：`OperationTests` 的注释写着"我们的实现（直接约束 sma）**没有**
        ///      （烧点）前提" —— 即**这个契约本身就是被测试钉住的**，我不该改语义去换实现手法。
        ///   ⇒ 定案：**语义保持"约束 sma"（无烧点前提）**，但把内部**从数值搜索换成闭式**（见
        ///     `DeltaVForSemiMajorAxis` 的注释：沿速度方向给幅值 ⇒ vis-viva 精确命中）。
        ///     这样既去掉"偶发不收敛"（flaky 的根），也不引入烧点前提 ✓。
        /// </summary>
        public static V3 DeltaVToResonantOrbit(OrbitView o, double ut, double f)
        {
            double smaNew = o.semiMajorAxis * Math.Pow(f, 2.0 / 3.0);
            return DeltaVForSemiMajorAxis(o, ut, smaNew);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 地表朝向那一族（`LAN` / `Longitude` / `Stationary`）—— 需要"烧点下方的经纬度"。
        //
        // ⚠ **口径映射（2026-09-18 结清，见 SurfaceFrame 的类注释）**：
        //   · 上游写 `GetLongitude(pos) + referenceBody.rotationAngle`；那个 `+rotationAngle`
        //     是"惯性经度 → 地表经度"的换算，而我们的 `SurfaceFrame.EciToLongLat` **内部已经做了**
        //     `Ry(+ωt)` 逆变换 ⇒ **不要再加**（加了就是重复计一次自转）。
        //   · 上游的 `GetLatitude/GetLongitude` 给**度**，`EciToLongLat` 给**弧度** ⇒ 这里转度。
        //   · 经纬度函数吃的是**游戏系**位置 ⇒ 批4 P4 起适配层位置**就是**游戏系位置（两系同轴），
        //     直接喂入即可；跨系只剩 `OrbitView` 的进/出两个口（批6 S2）。
        // ═══════════════════════════════════════════════════════════════════════

        private static double Clamp360(double deg)
        {
            double m = deg % 360.0;
            return m < 0.0 ? m + 360.0 : m;
        }

        /// <summary>球面两点的大圆角距（**度**）。逐字移植上游（`Maneuver/OrbitalManeuverCalculator.cs:307`）。</summary>
        public static double Distance(double latA, double lonA, double latB, double lonB)
        {
            const double d2r = Math.PI / 180.0;
            double latAr = d2r * latA, latBr = d2r * latB, dLonR = d2r * (lonB - lonA);

            return (180.0 / Math.PI) * Math.Atan2(
                Math.Sqrt(Math.Pow(Math.Cos(latBr) * Math.Sin(dLonR), 2)
                          + Math.Pow(Math.Cos(latAr) * Math.Sin(latBr)
                                     - Math.Sin(latAr) * Math.Cos(latBr) * Math.Cos(dLonR), 2)),
                Math.Sin(latAr) * Math.Sin(latBr) + Math.Cos(latAr) * Math.Cos(latBr) * Math.Cos(dLonR));
        }

        /// <summary>球面初始航向（**度**，0..360，罗盘式：0 = 正北、顺时针增）。逐字移植上游（`:323`）。</summary>
        public static double Heading(double latA, double lonA, double latB, double lonB)
        {
            const double d2r = Math.PI / 180.0;
            double latAr = d2r * latA, latBr = d2r * latB, dLonR = d2r * (lonB - lonA);

            return Clamp360((180.0 / Math.PI) * Math.Atan2(
                Math.Sin(dLonR),
                Math.Cos(latAr) * Math.Tan(latBr) - Math.Sin(latAr) * Math.Cos(dLonR)));
        }

        // ── 改升交点经度（LAN） ────────────────────────────────────────────────

        /// <summary>赤道面内、惯性经度 = <paramref name="lonDeg" /> 的**单位方向**。</summary>
        /// <remarks>
        /// 与 `SurfaceFrame` 共用同一份经度约定：经度 λ 的赤道点是 `(sinλ, 0, −cosλ)`
        /// （见 `SurfaceFrame.SurfaceState` 的 `p = R·(cosφ·sinλ, sinφ, −cosφ·cosλ)`）。
        /// ⚠ 批4 P4 极轴统一后适配层与游戏世界同系 ⇒ **不再需要任何换系**（旧写法 `(sinλ, cosλ, 0)`
        /// 是 +z 适配层的产物，留着会让 LAN 目标静默偏 90°）。
        /// </remarks>
        private static V3 EquatorialDirection(double lonDeg)
        {
            double lam = lonDeg * (Math.PI / 180.0);
            return new V3(Math.Sin(lam), 0.0, -Math.Cos(lam));
        }

        /// <summary>位置（**游戏系**）⇒ **固连系经度（度）**（"此刻这一点正下方是哪条经线"）。
        ///
        /// ⚠ 批4 P4（2026-09-28）起适配层与游戏世界同轴 +Y ⇒ 适配层算出来的位置**本身就在游戏系**，
        /// 这里直接喂给 `SurfaceFrame`；原先那次 `AdapterToGameWorld` 是恒等且已收进 `OrbitView` 私有（批6 S2）。
        /// </summary>
        private static double FixedLongitudeDeg(V3 posGame, OrbitView o, double ut)
        {
            var latLon = SurfaceFrame.EciToLongLat(posGame, o.angularVelocity, ut);
            return latLon.lonRad * 180.0 / Math.PI;
        }

        /// <summary>**惯性系经度（度）**（入参同 <see cref="FixedLongitudeDeg" />，游戏系）。与 `EquatorialDirection` 同口径。</summary>
        private static double InertialLongitudeDeg(V3 posGame, OrbitView o, double ut)
        {
            // `EciToLongLat` 给的是**固连系**经度 ⇒ 加回 `ω·ut` 才是惯性系（= 上游的 `+ rotationAngle`）
            return FixedLongitudeDeg(posGame, o, ut) + o.angularVelocity * ut * (180.0 / Math.PI);
        }

        /// <summary>
        /// **改"下一圈拱点所在的地面经度"**——上游的机制很取巧：**不动拱点位置，改周期**。
        /// 在拱点做切向燃烧只改 SMA（拱点位置不变），而轨道周期决定"回到这一点时天体自转了多少"
        /// ⇒ 让 `周期 = 自转周期 × (经度差/360 + N)` 就能把拱点压到目标经线上。
        ///
        /// ## 与上游的偏离：**删掉 KSP 专有的时间加速约束**（2026-09-19）
        /// 上游用 `o.referenceBody.timeWarpAltitudeLimits[4]` 当循环条件（"别让对面拱点低到只能 5 倍时间加速"）
        /// —— 那是 KSP 的**按高度分档的时间加速**规则，**我们的宿主没有这个东西**（RimWorld 的速度档与高度无关）
        /// ⇒ 如实剔除，只保留唯一的物理约束：**对面那个拱点不能钻进天体里**。
        /// 另外上游的 `LongitudeOffset` 不取模、靠 `N` 循环兜住负值（`T²` 把符号吃掉）；我们取模到 `[0,360)`，
        /// 语义相同且不必依赖这个巧合。
        ///
        /// ⚠ 上游自己的注释也承认："This may place the apside out the sphere of influence"（有可能把拱点甩出 SOI）。
        /// 我们没有在这一层拒绝它（那是个"贵但不违法"的解），改由 `OperationLongitude` 在结果上给一句**非致命**提示。
        /// </summary>
        public static V3 DeltaVToShiftNodeLongitude(OrbitView o, double ut, double newNodeLongDegrees)
        {
            if (!(o.angularVelocity > 0.0))
                throw new OperationException("改拱点地面经度需要天体的自转数据，但这颗天体的 angularVelocity ≤ 0。");

            (V3 r, V3 _) = o.RightHandedStateVectorsAtUT(ut);
            double burnRadius = r.magnitude;
            double rotationPeriod = 2.0 * Math.PI / o.angularVelocity;

            // 点火时刻该点正下方的**固连系**经度 = 上游的 `NodeLongitude`
            double nodeLongitude = FixedLongitudeDeg(r, o, ut);
            double longitudeOffset = Clamp360(nodeLongitude - newNodeLongDegrees);

            int n = -1;
            double targetSma;
            double oppositeRadius;
            do
            {
                n++;
                if (n > 20)
                    throw new OperationException(
                        "改拱点地面经度：找不到可行的周期（N > 20）——目标经度与当前经度之间的差距太大。");
                double targetPeriod = rotationPeriod * (longitudeOffset / 360.0 + n);
                targetSma = Math.Pow(o.mu * targetPeriod * targetPeriod / (4.0 * Math.PI * Math.PI), 1.0 / 3.0);
                oppositeRadius = 2.0 * targetSma - burnRadius;
            } while (oppositeRadius < o.bodyRadius);

            return DeltaVForSemiMajorAxis(o, ut, targetSma);
        }

        /// <summary>
        /// **改升交点经度（LAN）**：求把轨道搬到"赤道升交点落在 <paramref name="newLANDegrees" /> 经度"
        /// 所需的**最小 Δv**（保持 `|v|` 与径向分量 ⇒ 只改轨道平面）。
        ///
        /// ## 与上游的偏离：把退化域显式化（2026-09-19，治本）
        /// 上游（在 **KSP 层** `MechJeb2/OrbitalManeuverCalculator.cs:338`，`MechJebLib/` 里没有）用的是
        /// **地面导航**模型：把烧点与"赤道上目标经度的那个点"喂进大圆初航向 `Heading()`，让水平速度朝那个方位。
        ///
        /// 它在**烧点不在赤道面内**时与下面的精确解**逐位等价**（实测：`LAN=45°` 两边都给 2456.16 m/s，
        /// `LAN=120°` 都给 1497.26 m/s），但在烧点落进赤道面内时**整体退化**：
        /// `Heading(0, λa, 0, λb)` 的分母 `cos0·tan0 = 0` ⇒ 航向恒为正东/正西、**与目标经度无关**
        /// ⇒ 烧的是**倾角**而不是交点（实测：请求 45°/300°/0° 都给同一个 2766.89 m/s；
        /// 燃后 AN 与 DN 落在同一经度 = 轨道被烧成了赤道面）。
        ///
        /// 那不是"精度差"，是**解根本不存在**：新平面必须同时包含烧点位置 `r` 与目标节点方向 `d̂`，
        /// 而 `r` 与 `d̂` 都在赤道面内时 `span(r, d̂)` 只能是赤道面本身 ⇒ 节点线退化 ⇒ 指定 LAN 无从谈起。
        /// （几何上更强的说法：此时**可达到的升交点经度只有烧点经度或其对面**。）
        /// ⇒ 本实现**拒绝**这种请求并给出可读的理由（`OperationException`），而不是静默烧掉一大笔 Δv。
        ///
        /// ## 数学
        /// 节点线在平面内 ⇒ `n̂ ⊥ d̂`；平面含烧点 ⇒ `n̂ ⊥ r` ⇒ **`n̂ ∥ r × d̂`**（符号按"AN 落在 +d̂"选）。
        /// 燃烧 = 把 `v` 绕 `r̂` 转到 `r × v' ∥ n̂`：
        /// `v' = |v⊥|·(n̂ × r̂) + (v·r̂)·r̂`（由 `r×(n̂×r̂) ∝ n̂` 得）。
        /// 因此 `|v'| = |v|`（能量与 SMA 不变）且径向分量不变（拱线几何不变）⇒ **只改平面，且是该平面变更的最小 Δv**
        /// ⇒ 与"改动前后 `Δv` 逐位相同"的上游解一致（见上）。
        ///
        /// ⚠ **反直觉但正确**：即使烧点只**极度接近**赤道面，解依然**精确**（节点线恰为 `span(d̂)`，
        /// 与烧点纬度无关）——变大的只是**代价**（平面越接近赤道、要转的倾角越大）。
        /// 所以下面"退化"的门限是"恰好平行"级的，而不是"纬度小于某值"。
        /// </summary>
        public static V3 DeltaVToShiftLAN(OrbitView o, double ut, double newLANDegrees)
        {
            if (o.equatorialAxis.sqrMagnitude <= 0.0)
                throw new OperationException("DeltaVToShiftLAN：这条轨道没有赤道轴（equatorialAxis 未设）。");

            if (!o.AscendingNodeEquatorialExists() && !o.DescendingNodeEquatorialExists())
                throw new OperationException(
                    "轨道面与赤道面重合（或交点在轨道之外），升交点没有意义：请先改变倾角，再指定升交点经度。");

            V3 pole = o.equatorialAxis.normalized;
            V3 d = EquatorialDirection(newLANDegrees);
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);

            // 退化①：烧点恰在目标节点线上 ⇒ `span(r, d̂)` 退化成一条线、平面欠定
            V3 n = V3.Cross(r, d);
            if (n.magnitude <= 1e-9 * r.magnitude)
            {
                // 例外：如果升交点**已经**在请求的经度上，那这个请求本来就已满足 ⇒ 无需燃烧
                // 批6 S3-3a：交点位置改走 `Try*`（"不存在"不再用零矢量表示，故不再需要判长）
                if (o.TryAscendingNodePositionEquatorial(ut, out V3 anNow)
                    && LongitudeDiffDeg(InertialLongitudeDeg(anNow, o, ut), newLANDegrees) <= 1e-6)
                    return V3.zero;
                throw new OperationException(
                    "点火时刻恰好落在节点线上，且目标经度在对面：需要绕烧点轴翻面，本计划不做。" +
                    "请另选点火时刻或改为请求当前经度。");
            }

            n = n.normalized;

            // 退化②：新平面只能是赤道面 ⇒ 节点线退化、AN 无定义（**这就是那个 180°/定值 Δv 的真因**）
            if (V3.Cross(pole, n).magnitude <= 1e-12)
                throw new OperationException(
                    "点火时刻位于赤道面内：此时新平面只能是赤道面，升交点经度无法指定。" +
                    $"此刻可达到的经度只有 {InertialLongitudeDeg(r, o, ut):F3}° 或其对面。请另选点火时刻。");

            // 符号：让升交点落在 +d̂（`AN 方向 = pole × ĥ`，见 `OrbitView.AscendingNodeEquatorialTrueAnomaly`）
            if (V3.Dot(V3.Cross(pole, n), d) < 0.0) n = -n;

            V3 rhat = r.normalized;
            double vr = V3.Dot(v, rhat);
            V3 vPerp = v - vr * rhat;
            V3 vNew = vPerp.magnitude * V3.Cross(n, rhat) + vr * rhat;
            return vNew - v;
        }

        /// <summary>两个经度（度）之差，取 `[-180,180]`。</summary>
        private static double LongitudeDiffDeg(double a, double b) =>
            System.Math.Abs(Statics.ClampPi((a - b) * (Math.PI / 180.0))) * (180.0 / Math.PI);

        /// <summary>
        /// **霍曼（两冲量）转移**：返回两次点火各自的时刻与 Δv。上游 KSP 层 `:161` 的移植（去掉 `V3ToWorld`）。
        ///
        /// ⚠ **一处与上游不同：把 `fixedTime` 真的传下去**。上游把这个参数声明了却**没往下传**
        /// （`NextManeuver` 调用里没有它）⇒ 那个开关在上游是**死的**。我们的两个既有调用点都传 `false`
        /// ⇒ 转发它对行为零影响，只是让"声明与实现一致"。**留着上游那处漏传当反面教材。**
        /// </summary>
        public static (V3 dV1, double UT1, V3 dV2, double UT2) DeltaVAndTimeForHohmannTransfer(OrbitView o, OrbitView target, double ut,
            double lagTime = double.NaN, bool fixedTime = false, bool coplanar = true, bool rendezvous = true, bool capture = true)
        {
            (V3 r1, V3 v1) = o.RightHandedStateVectorsAtUT(ut);
            (V3 r2, V3 v2) = target.RightHandedStateVectorsAtUT(ut);

            // ⚠⚠ **退化守卫（2026-09-20，实测血泪）**：**近同轨**的两条轨道之间，"两冲量交会"是**病态**的 ——
            //   模式搜索的时间尺度来自会合周期 `T_syn = 1/|1/T1 − 1/T2|`，而它在近同轨上**趋于无穷**
            //   （同轨 ⇒ 严格无穷；Δa = 1 km 也有 3.8e7 s ≈ 一万小时）⇒ **搜索不返回**
            //   （实测：同轨目标上 180 s 没返回、测试宿主被打崩）。
            //   ⇒ 这里显式判"窗口远到没有意义"并返回 **NaN**（不抛：调用方按 `UT1` 非有限即可识别，
            //     `RendezvousLadder` 与 `PhaseWindow` 都是这么判的）。
            //   判据 = `T_syn > 200 × 转移时长`（转移时长 = 转移椭圆半个周期 `π√((a1+a2)³/(8μ))`）：
            //   系数 200 是**护栏量级**（"等 200 倍转移时长才有一个窗口"已经不该走这条路了），不是调参旋钮。
            double a1 = Astro.SmaFromStateVectors(o.mu, r1, v1);
            double a2 = Astro.SmaFromStateVectors(target.mu, r2, v2);
            double tHohmann = a1 > 0.0 && a2 > 0.0
                ? Math.PI * Math.Sqrt(Math.Pow(a1 + a2, 3.0) / (8.0 * o.mu)) : double.NaN;
            if (a1 > 0.0 && a2 > 0.0)
            {
                double t1 = Astro.PeriodFromStateVectors(o.mu, r1, v1);
                double t2 = Astro.PeriodFromStateVectors(target.mu, r2, v2);
                double invDiff = Math.Abs(1.0 / t1 - 1.0 / t2);
                double synodic = invDiff > 0.0 ? 1.0 / invDiff : double.PositiveInfinity;
                if (!(synodic <= 200.0 * tHohmann))
                {
                    // 没有可用窗口 ⇒ 报"解不出来"而不是磨着等（**这是本次唯一的行为改动**，
                    // 且只作用在原本"跑不完"的那片几何上 —— 非退化几何逐位不变）。
                    return (new V3(double.NaN, double.NaN, double.NaN), double.NaN,
                            new V3(double.NaN, double.NaN, double.NaN), double.NaN);
                }
            }

            (V3 dv1, double dt1, V3 dv2, double dt2) = TwoImpulseTransfer.NextManeuver(o.mu, r1, v1, r2, v2,
                lagTime: lagTime, coplanar: coplanar, rendezvous: rendezvous, capture: capture, fixedTime: fixedTime);

            return (dv1, ut + dt1, dv2, ut + dt2);
        }

        /// <summary>⚠ 上游这个名字**没有 `To`**（`DeltaVForSemiMajorAxis`）—— 照搬，别"顺手改整齐"（J1）。
        /// ⚠⚠ **2026-09-20 实现换成闭式（记下来）**：上游落到 `ChangeSMA`（我们那份是"模式搜索替代 alglib"）
        ///   ⇒ 收敛不到机器精度、而且**偶发不收敛**（`P1_OperationResonantOrbit_…AchievesExactPeriodRatio`
        ///   那条 1e-8 判据"单独必过、全量偶挂"的 flaky 根因就在这 ✗）。
        ///   而这件事**有闭式**：只约束半长轴 = 只约束能量 = 只约束速度**模长**
        ///   （`1/a = 2/r − v²/μ` ⇒ `|v'| = √(μ(2/r − 1/a'))`）；最小 Δv 即**沿速度方向改幅值**：
        ///   `Δv = (|v'| − |v|)·v̂` ⇒ 打完由 vis-viva **精确**落在目标 a 上（机器精度 ✓）。
        ///   ⚠ 与上游的语义差别：上游搜索解可能连形状一起动；我们这条是"最小实现"（只动能量）✓。
        ///   登记：`ChangeOrbitalElement.ChangeSMA` 仍是**搜索版**、仍被别的成员用 ⇒ 两处口径待收（J2）。</summary>
        public static V3 DeltaVForSemiMajorAxis(OrbitView o, double ut, double newSMA)
        {
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            if (!(newSMA > 0.0) || !(o.mu > 0.0)) return ChangeOrbitalElement.ChangeSMA(o.mu, r, v, newSMA);

            double rMag = r.magnitude;
            if (!(rMag > 1.0)) return ChangeOrbitalElement.ChangeSMA(o.mu, r, v, newSMA);

            double vTargetSq = o.mu * (2.0 / rMag - 1.0 / newSMA);
            if (!(vTargetSq > 0.0)) return V3.zero;        // 该点不可达（目标 a 太小 ⇒ 需要负速度）
            double vMag = v.magnitude;
            if (!(vMag > 0.0)) return V3.zero;

            double dvAlong = Math.Sqrt(vTargetSq) - vMag;  // 沿速度方向改幅值（最小 Δv）
            return (v / vMag) * dvAlong;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // 交会阶梯的积木（2026-09-20，B3）。**成员名与语义逐字对齐上游**
        // `MechJeb2/OrbitalManeuverCalculator.cs`（KSP 层 ⇒ 只搬名/语义，实现去 KSP 化，同 P-1 的处置）。
        // ⚠ **`DeltaVToMatchVelocities` 上面已经有了 —— 本轮差点写出第二份**（编译器 CS0111 当场拦下，
        //   J2"不许移植一份留一份"那条禁令正是为了这个）。阶梯里直接用那份，不要再写。
        // 其余动作我们**已有**（圆化/改近远点/霍曼/最近接近时刻/交点时刻），**只缺下面这两个**。
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>上游 `:135`：在**相对升交点**处把水平速度摆到两平面的交线上（纯平面改变 ⇒ 速度模长守恒）。
        /// <paramref name="burnUT"/> = 第一个升交点时刻（上游同款）。</summary>
        public static V3 DeltaVAndTimeToMatchPlanesAscending(OrbitView o, OrbitView target, double ut, out double burnUT) =>
            DeltaVToMatchPlanes(o, target, o.TimeOfAscendingNode(target, ut), out burnUT);

        /// <summary>上游 `:147`：同上升支，取**降交点**。（上游对双曲会抛；我们走 `TimeOfTrueAnomaly`
        /// ⇒ 双曲下给 NaN 而不是抛，调用方按 NaN 判"不可用"即可。）</summary>
        public static V3 DeltaVAndTimeToMatchPlanesDescending(OrbitView o, OrbitView target, double ut, out double burnUT) =>
            DeltaVToMatchPlanes(o, target, o.TimeOfDescendingNode(target, ut), out burnUT);

        private static V3 DeltaVToMatchPlanes(OrbitView o, OrbitView target, double nodeUt, out double burnUT)
        {
            burnUT = nodeUt;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(burnUT);
            V3 up = r.normalized;                                    // = 上游 `o.Up(burnUT)`
            V3 nodeLine = V3.Cross(target.OrbitNormal(), up);         // 两平面交线（交点处它 ⊥ 双方法向）
            V3 horizontal = v - up * V3.Dot(v, up);                   // 去掉径向分量
            // ⚠ **一处与上游不同（记下来，引以为戒）**：上游写成 `水平速度模长 × Cross(...)`，**没归一化交线**。
            //   两单位法向的叉积模长 = sin(夹角) = 1 − O(Δi²) ⇒ 近共面时与归一化只差 4e-7（0.05°），
            //   但**语义上必须归一化**：这一发是"把水平速度转到交线上"，模长守恒是判据。
            //   我们只在 relInc > 0.05° 时才走到这一级，此时归一化严格正确。
            return nodeLine.normalized * horizontal.magnitude - horizontal;
        }

        /// <summary>上游 `:181`：解"<c>t0 + dt</c> 时刻到达目标位置"的两冲量（Lambert 走移植的 `Gooding`）。
        /// <paramref name="offsetDistance"/> ≠ 0 ⇒ 把到达点从目标**中心**沿视线前移该距离再解一次
        /// （上游语义：停在目标前方而不是撞上去）。⚠ <paramref name="dt"/> 必须为正。
        ///
        /// ★★ **2026-10-01：试过把解算器 `Gooding` 换成 `Izzo`，当场撤了**（记下来，免得再试一次）。
        /// 起因：地球那条（5500→6300 km）**反复重规划到烧干**，而阶梯自己的 `独立积分到达偏差` 报 1–12 km。
        /// 台架（`InterceptSolverMissTests` / `CwVsLambertCloseRangeTests`）在同轨沿迹带上量到
        /// **`Izzo` 落点 1e-8 m 级、`Gooding` 偏 0.9–320 km（≈5.6× 距离）** ⇒ 当时判"换解算器"。
        /// **游戏内实跑（MVID `49f2d34e…`）否掉了它**：`独立积分到达偏差` 的分布**没变**（仍 67 条 ≥1 km），
        /// 规划量还更差（计划 Δv 累计 12827 → **20027 m/s**、`BLOCKED` 124 次）⇒ **撤**。
        /// ⇒ **推论（重要）**：那个 km 级读数**不是解算器精度造成的**（换成精确解算器读数不变）
        ///   ⇒ 要么**那条"独立复算"自身口径有问题**（纪元/系/参考轨道），要么病灶在别处。
        ///   ⇒ 在把那条仪器**验明**之前，不许再拿它当"计划是坏的"的证据（仪器必须被验证）。
        /// ⚠ 两件事分开记：① **`Gooding` 在该带确实偏 5.6× 距离**（台架实测，是真的；未接生产）；
        ///   ② **游戏内那条 km 读数另有来源**（待查）——别把①当成②的解释。</summary>
        public static (V3 v1, V3 v2) DeltaVToInterceptAtTime(OrbitView o, double t0, OrbitView target, double dt,
            double offsetDistance = 0.0, bool prograde = true)
        {
            (V3 ri, V3 vi) = o.RightHandedStateVectorsAtUT(t0);
            (V3 rf, V3 vf) = target.RightHandedStateVectorsAtUT(t0 + dt);

            var dir = prograde ? TransferGeometry.Prograde : TransferGeometry.Retrograde;
            DVector3 hRef = V3.Cross(ri, vi);          // 上游用"本轨道法向"作 h 参考

            (DVector3 ti, DVector3 tf) = Gooding.Solve(o.mu, ri, rf, dt, dir, 0, hRef);

            if (offsetDistance != 0.0)
            {
                // ⚠ **第二处与上游不同（记下来）**：上游写 `rf -= offsetDistance * Cross(vf, rf).normalized`，
                //   而 `cross(v, r) = −h` ⇒ 那是一根**轨道法向**轴 ⇒ 上游那个偏移把到达点挪到了**面外**，
                //   与"停在目标前方 d"这个名字/意图不符（阶梯调用时**不传** offset ⇒ 上游自己也从未用到它）。
                //   这里按**意图**实现：沿**视线**（本船→目标）后退 offset ⇒ 停短而不是撞上去。
                V3 rfOff = rf - (rf - ri).normalized * offsetDistance;
                (ti, tf) = Gooding.Solve(o.mu, ri, rfOff, dt, dir, 0, hRef);
            }

            return (ti - vi, vf - tf);
        }
    }
}
