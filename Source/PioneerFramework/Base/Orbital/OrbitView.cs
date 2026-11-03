using System.Runtime.CompilerServices;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using MechJebLib.Utils;

namespace PioneerFramework
{
    /// <summary>
    /// 一个天体参考系下的**二体轨道视图**——MJ 的 `Operation.MakeNodes(Orbit o, ...)` 里那个 `Orbit` 的等价物。
    ///
    /// 设计依据：《P-1适配层决策》§3.1。三条不变量：
    /// ① **不可变值类型**（`readonly struct`），零堆分配、可上线程；
    /// ② **与 MJ 的 `Orbit` 同名成员**（小写开头，如 `semiMajorAxis`/`period`）⇒
    ///    15 个 `MakeNodesImpl` 的函数体一行不用改（这是 P-1 §0.1 的核心结论）；
    /// ③ **它是一座薄适配层，不是新数学**。全部要素提取走移植来的 `Astro.KeplerianFromStateVectors`，
    ///    全部传播走移植来的 `Shepperd`，全部近点角换算走移植来的 `Angles`。
    ///    ⇒ 真数学都在**已被上游 4989 条测试验收**的那一层；本类型只负责"把状态摆到 MJ 期望的成员名下"。
    ///
    /// ⚠ **向量类型已统一**（批5 U2/U3，2026-09-28）：全库只剩一个双精度三矢量类型 `DVector3`，
    /// `V3` 只是移植面沿用的**别名**（`ThirdParty/MechJebLib/_PFL/GlobalAlias.cs`）⇒
    /// P-1 §3.1「日后把 `V3` 换成 `DVector3`」这条迁移债**已结清**，
    /// 那句"`V3` 只许出现在本类型及其构造工厂里"随之作废（现在写 `V3` 就是写 `DVector3`）。
    /// ⚠ **但"系"那一维的歧义仍在**：本类型的 `r`/`v` 是**天体参考系**，宿主侧另有一套游戏系语义，
    /// 今天两者恰好同轴（批4 P4 换系恒等）⇒ 归属批6 的 S1（命名）/ S2（封边界），**不要在这里自行换算**。
    ///
    /// ⚠ **无缓存重构的余地**：要素在构造时算一次并缓存（与 MJ 的 `Orbit` 一致）。
    /// 传播不使用要素，直接用原始 `(r, v)`，所以 `NextClosestApproachTime` 那种 160 次采样的循环不会被放大。
    ///
    /// **本类型必须是纯的**（不引 UnityEngine/Verse）—— 它被链接进 net48 的 `MechJebLibTest` 做互证。
    /// </summary>
    public readonly struct OrbitView
    {
        // —— 二体状态（唯一真值来源）——
        public readonly V3 r; // 天体参考系位置 (m)
        public readonly V3 v; // 天体参考系速度 (m/s)
        public readonly double mu; // 中心天体 GM (m³/s²)
        public readonly double epoch; // 上面 r/v 所在的绝对时刻

        /// <summary>
        /// 参考系的**赤道轴**（= 中心天体自转轴 / 北极方向）。**只服务 `*Equatorial*` 与 `North/East` 那一族**；
        /// 完全不参与两体计算。对应 MJ 那边的 `referenceBody.transform.up`。
        /// 之所以放在这里而不是 `BodyView`：MJ 的 `MakeNodesImpl` 直接调 `o.AscendingNodeEquatorialExists()`，
        /// 要把它们留在 `OrbitView` 上、签名才不用改（P-1 §0.1）。
        /// </summary>
        public readonly V3 equatorialAxis;

        /// <summary>
        /// 中心天体**平均半径** (m)。对应 MJ 那边的 `o.referenceBody.Radius`——
        /// `TimeSelector` 的 `ALTITUDE` 分支与 `OperationApoapsis` 一类都要"高度 ↔ 半径"换算。
        /// **0 = 调用方未提供**（该情形下用到它的成员会给出无意义结果，属调用方的错）。
        ///
        /// 与 <see cref="equatorialAxis" /> 同理：MJ 的 `Orbit` 自带 `referenceBody`，
        /// 所以"轨道知道自己绕谁"是**上游本来的架构**；我们只是把它压平成两个字段，以免改掉一串签名（J1）。
        /// </summary>
        public readonly double bodyRadius;

        /// <summary>
        /// 中心天体的 **SOI 半径（近似）**。对应 MJ 那边的 `referenceBody.sphereOfInfluence`。
        /// ⚠ **我们是按 Hill 半径近似的**（P-1 §6-R3：`CelestialBodyDef` **没有**这个字段，而参考系是**显式跟踪**的
        /// ⇒ 我们从不拿 SOI 判定参考系）。**它只用于"健全性提醒"这类非致命用途**——
        /// 目前唯一消费者是 `OperationSemiMajor` 的"新半长轴过大，可能变成双曲轨道"那句话。
        /// `NaN` = 未提供。
        /// </summary>
        public readonly double soiRadiusApprox;

        /// <summary>
        /// 中心天体的**自转角速度** (rad/s)。对应 MJ 那边的 `referenceBody.angularVelocity`。
        /// 服务"地表朝向"那一族的 Operation（`LAN`/`Longitude`/`Stationary`）——
        /// 它们要算"烧点下方的经纬度"，而经度是相对**自转中**的天体定义的。
        /// `0` = 未提供（那三个 Operation 会给出无意义结果，属调用方的错）。
        /// </summary>
        public readonly double angularVelocity;

        // —— 要素（构造时提取一次并缓存；走移植的 Astro）——
        private readonly double _sma, _ecc, _inc, _lan, _argp, _nu, _slr;

        private OrbitView(V3 r, V3 v, double mu, double epoch, V3 equatorialAxis, double bodyRadius,
            double soiRadiusApprox, double angularVelocity,
            double sma, double ecc, double inc, double lan, double argp, double nu, double slr)
        {
            this.r = r;
            this.v = v;
            this.mu = mu;
            this.epoch = epoch;
            this.equatorialAxis = equatorialAxis;
            this.bodyRadius = bodyRadius;
            this.soiRadiusApprox = soiRadiusApprox;
            this.angularVelocity = angularVelocity;
            _sma = sma;
            _ecc = ecc;
            _inc = inc;
            _lan = lan;
            _argp = argp;
            _nu = nu;
            _slr = slr;
        }

        /// <summary>
        /// **唯一的构造入口**（R5 要求的"构造点唯一"）：只从当真值用的 `(r, v)` 出发，
        /// 绝不去读"圆轨解析缓存"那第三种状态——否则规划器会看到与实际飞行不同的轨道。
        /// </summary>
        /// <param name="r">位置，**适配层参考系**（见下）。</param>
        /// <param name="v">速度，**适配层参考系**。</param>
        /// <param name="mu">中心天体 GM。</param>
        /// <param name="epoch">`r`/`v` 所在的绝对时刻。</param>
        /// <param name="equatorialAxis">赤道轴，**适配层参考系**（= +z）。</param>
        public static OrbitView FromStateVectors(V3 r, V3 v, double mu, double epoch,
            V3 equatorialAxis = default, double bodyRadius = 0.0, double soiRadiusApprox = double.NaN,
            double angularVelocity = 0.0)
        {
            (double sma, double ecc, double inc, double lan, double argp, double nu, double l) =
                Astro.KeplerianFromStateVectors(mu, r, v);

            if (ecc < NearCircularEcc)
                (lan, argp, nu) = NearCircularAngles(r, v);

            return new OrbitView(r, v, mu, epoch, equatorialAxis, bodyRadius, soiRadiusApprox, angularVelocity,
                sma, ecc, inc, lan, argp, nu, l);
        }

        /// <summary>
        /// **近圆阈值**：`ecc` 低于它就改走标准约定（近心点置于升交点，`ν` = 纬度幅角）。
        ///
        /// **为什么需要它**（2026-09-18 实测抓到的 R6，见 `DesignNotes/P-1适配层决策` §6.1）：
        /// `Astro.KeplerianFromStateVectors` 判"偏心是否可忽略"用的是
        /// `ecc > EPS`，而 **`EPS = 2.22e-16` 恰好落在双精度舍入噪声地板上**——
        /// 近圆轨道的 `ecc` 是算出来的，会因最后一次运算落在阈值**两侧**：
        /// 下侧走约定分支（良定）；上侧则 `Atan2(噪声, 噪声)` 返回**任意角**。
        /// 上游的测试输入是随机状态、落不到这个地板上；**而近圆恰是本 mod 的主流 regime**。
        ///
        /// **1e-6 这个值是从两端推出来的，不是拍的**：
        /// · 下端：近心点方向的误差 ≈ `1e-16 / ecc` ⇒ `ecc = 1e-6` 时方向已准到 ~1e-10 rad，远离噪声；
        /// · 上端：该处 `ω` 本身 ≤ 1e-6，故 `ν` 与纬度幅角 `u = ω + ν` 在此**连续衔接**，没有跳变。
        /// </summary>
        private const double NearCircularEcc = 1e-6;

        /// <summary>
        /// 升交点方向 `极轴(+Y) × ĥ` 的**退化阈值**：模长小于它（等价于 `|sin i| &lt; 1e-8`，即近赤道轨道）时，
        /// 升交点方向在数值上已无意义，按标准约定取 **+x**（于是 `LAN = 0`）。
        /// 与 <see cref="NearCircularEcc" /> 同源：一个方向只有在"定义它的那个量的模长远高于 1e-16 噪声地板"时才有意义。
        /// </summary>
        private const double DegenerateNodeMag = 1e-8;

        /// <summary>
        /// 近圆轨道的 `(LAN, argP, ν)`，走**标准约定**：近心点置于升交点（`argP = 0`），
        /// 于是 `ν` 就是**纬度幅角** `u = atan2(r̂·(ĥ×n̂), r̂·n̂)`，`n̂` 为升交点方向。
        ///
        /// 这是 `e → 0` 时"真近点角"的标准定义，**不是第二个求解器**——
        /// 它只用两个叉乘与一个 `atan2`，且仅在 `Astro` 的要素在该区退化时接管。
        /// </summary>
        private static (double lan, double argp, double nu) NearCircularAngles(V3 r, V3 v)
        {
            V3 hhat = V3.Cross(r, v).normalized;
            V3 node = V3.Cross(V3.northpole, hhat); // 极轴 = +Y（批4 P4 统一；`V3.northpole` 的唯一权威）

            if (node.magnitude < DegenerateNodeMag)
                node = new V3(1, 0, 0); // 赤道轨道：升交点取 +x ⇒ LAN = 0
            else
                node = node.normalized;

            V3 rhat = r.normalized;

            // ⚠ LAN 必须与 `Astro` 的节点线口径一致：`PerifocalToECIMatrix` 用 `node=(cosλ, 0, −sinλ)`
            //   ⇒ LAN 从 **+x 量向 −z**。极轴换成 +Y 之后节点线整条落在 x–z 平面里（`node.y ≡ 0`），
            //   旧写法 `Atan2(node.y, node.x)` 会恒给 0 或 180（LAN 静默失效）。
            double lan = Statics.Clamp2Pi(System.Math.Atan2(-node.z, node.x));
            double nu = Statics.Clamp2Pi(System.Math.Atan2(V3.Dot(rhat, V3.Cross(hhat, node)), V3.Dot(rhat, node)));

            return (lan, 0.0, nu);
        }

        /// <summary>
        /// ⚠⚠ **参考系约定（2026-09-28 更定；极轴统一后务必先读）**
        ///
        /// **适配层与游戏世界共用同一个参考系：极轴 = +Y**（`DVector3.up`）。
        /// 2026-09-18 起曾让适配层跟着上游走 +z、由下面那对函数换系；**批4 的 P1–P3 把上游那一整族
        /// 公式（`KeplerianFromStateVectors` 的 `hhat[1]` 判顺逆、`LatitudeFromBCI` 取 `r.y`、
        /// `RhatFromLatLng`/`ENU` 一族的基向量…）全部改写成 +Y 口径，并由上游 6207 条测试逐条钉住**
        /// ⇒ 两个系已经是同一个系，换系**退化为恒等**。
        ///
        /// ⚠ **但"直觉倾角 → 180° − i"那一类换算并没有消失（别误删）**：它换的不是极轴，而是**自转方向**。
        /// 游戏侧天体是 `Ry(−ωt)` 自转（见 `SurfaceFrame.SurfaceState` 的 `v = ω(p×up)`，注释点明
        /// "与 ω×p **反向**"）⇒ 游戏里"沿自转方向的顺行轨道"其 `h ∥ −Y`，而上游 `inc = 0` 的定义是
        /// `h ∥ +极轴` ⇒ 面向玩家的直觉倾角与适配层倾角差 `180° − i`（换算在 `OperationInclination` 里），
        /// **这与本次极轴统一无关，也不随统一而失效**。
        /// </summary>
        public static OrbitView FromGameStateVectors(DVector3 r, DVector3 v, double mu, double epoch,
            DVector3 equatorialAxis, double bodyRadius, double soiRadiusApprox = double.NaN,
            double angularVelocity = 0.0)
        {
            return FromStateVectors(
                GameWorldToAdapter(r),
                GameWorldToAdapter(v),
                mu,
                epoch,
                GameWorldToAdapter(equatorialAxis),
                bodyRadius,
                soiRadiusApprox,
                angularVelocity);
        }

        /// <summary>
        /// **出边唯一口**：`ut` 时刻的**游戏世界系（系统系）**状态（P-1 §11.3 S2，批6，2026-09-30）。
        ///
        /// ⚠ 批4 P4（2026-09-28）极轴统一后适配层与游戏世界同轴 +Y ⇒ 本次跨越是**恒等**。
        /// 与 <see cref="FromGameStateVectors" />（进边）一起构成本类型**唯二的两个跨系口**；
        /// 宿主侧要"游戏系状态"一律走这里，**不要再在别处自己拼参考系状态**。
        /// </summary>
        public (DVector3 pos, DVector3 vel) GameFrameStateAtUT(double ut)
        {
            (V3 r, V3 v) = RightHandedStateVectorsAtUT(ut);
            return (AdapterToGameWorld(r), AdapterToGameWorld(v));
        }

        /// <summary>
        /// ⚠ **已退化为恒等**（批4 P4，2026-09-28）：适配层与游戏世界同轴 +Y ⇒ 不再有换系。
        ///
        /// 批6 S2（2026-09-30）起**降为 `private`**：本类型之外再无调用点 —— **状态**的跨系只经两个口
        /// （**进** <see cref="FromGameStateVectors" /> / **出** <see cref="GameFrameStateAtUT" />），
        /// 将来两系若再分离，**状态**只改这两个口即可。
        ///
        /// ⚠⚠ **这两句只保证"状态"**：Δv / 方向 / 位置这类**非状态**向量在 S2 之后是**直传**的
        /// （各调用点有就地注释）。若两系将来真的分离，**那些点必须重新引入转换** ——
        /// 这一步的机械护栏"**类型承载系**"归 S3（§11.3 Stage 3）：在那之前，两系分离会让 Δv 静默偏掉。
        /// </summary>
        private static V3 GameWorldToAdapter(V3 game) => game;

        /// <summary>恒等；见 <see cref="GameWorldToAdapter" /> 的说明。</summary>
        private static V3 AdapterToGameWorld(V3 adapter) => adapter;

        // 批5 U4（2026-09-28）：`DVector3` 与移植面三矢量之间的**两个恒等转换函数已删除**
        // （类型统一 U2/U3 之后移植面那个类型就是 `DVector3` ⇒ 它们退化成原样搬运三个分量）。
        // 「`DVector3`→移植面类型的咽喉只在一处」这个形状随类型合并自然消失；调用点直接写变量本身。

        // ── 轨道标量与要素（MJ 的 Orbit 同名成员） ────────────────────────────────

        public double semiMajorAxis => _sma;
        public double eccentricity => _ecc;
        public double inclination => _inc;
        public double LAN => _lan;
        public double argumentOfPeriapsis => _argp;
        public double trueAnomaly => _nu;
        public double semiLatusRectum => _slr;

        public double PeR => Astro.PeriapsisFromKeplerian(_sma, _ecc);
        public double ApR => Astro.ApoapsisFromKeplerian(_sma, _ecc);

        /// <summary>
        /// 轨道周期。**本类型是周期算式的唯一权威**（《大修计划》§14.3『周期』行：
        /// 原先 `2π√(a³/μ)` 在 6 处各写一遍，收敛到这里）。
        ///
        /// ⚠ MJ 的 `PeriodFromStateVectors` **对双曲轨道抛异常**，而 MJ 的 `NextClosestApproachTime`
        /// 是**先读 `period` 再判 `eccentricity > 1`**。KSP 的 `Orbit.period` 在那种情形返回 NaN。
        /// ⇒ 这里对 `a < 0` 显式给 NaN，**保持上游调用点的控制流成立**。（这是一条守卫，不是第二个算式。）
        /// </summary>
        public double period => _sma < 0.0 ? double.NaN : Astro.PeriodFromStateVectors(mu, r, v);

        /// <summary>平均角速度。双曲轨道也要给正值（上游用它算"大块双曲弧"的时间跨度）。</summary>
        public double meanMotion => Astro.MeanMotion(mu, _sma);

        public double radius => r.magnitude;

        public double PeA => PeR; // MJ 的两个别名：PeA/ApA（高度口径由调用方自己减半径）
        public double ApA => ApR;

        /// <summary>本天体段 = 全体（P-1 的 R4：`StartUT`/`EndUT` 在活路径上只 2 处，且都在我们跳过的路径里）。</summary>
        public double StartUT => double.NegativeInfinity;
        public double EndUT => double.PositiveInfinity;

        // ── 传播：`Shepperd` 是本体 ──────────────────────────────────────────────

        /// <summary>
        /// `ut` 时刻的（天体参考系）状态。**这是 MJ 那边
        /// `getRelativePositionAtUT` / `getOrbitalVelocityAtUT` / `RightHandedStateVectorsAtUT` 的统一替身**——
        /// 那三个成员在 MJ 里是三份写法，在这里是一个。
        ///
        /// 用普适变量法（`Shepperd`）⇒ **椭圆 / 抛物 / 双曲全域有效**，顺带根治 Q11
        /// （我们原来的 `OrbitMath.GetPositionAtTimeDelta` 在 `e ≥ 1` 时静默 no-op）。
        /// </summary>
        public (V3 pos, V3 vel) RightHandedStateVectorsAtUT(double ut) =>
            Shepperd.Solve(mu, ut - epoch, r, v);

        public V3 RightHandedBCIPositionAtUT(double ut) => RightHandedStateVectorsAtUT(ut).pos;

        public V3 RightHandedOrbitalVelocityAtUT(double ut) => RightHandedStateVectorsAtUT(ut).vel;

        public double Radius(double ut) => RightHandedBCIPositionAtUT(ut).magnitude;

        /// <summary>轨道法向。约定：**沿着它向下看，卫星逆时针转**（与 MJ 的注释一致，也与 R1 的判据一致）。</summary>
        public V3 OrbitNormal() => V3.Cross(r, v).normalized;

        /// <summary>
        /// **原始状态的两体传播**（`DVector3` 进、`DVector3` 出）——给"只有 (pos, vel, Δt, μ)、
        /// 并不想建 `OrbitView`"的调用方用（22 处）。**唯一实现仍是 `Shepperd`**，与
        /// `RightHandedStateVectorsAtUT` 是同一句，只是不做"建 `OrbitView` 再取回"那趟空转。
        ///
        /// 为什么把它放在这里而不放在 `OrbitMath`：**`V3` 只许出现在本类型及其构造工厂里**
        /// （用户 2026-09-18 裁定，见本文件顶部）。放这里 ⇒ 宿主层永远看不到 `V3`/`Shepperd`。
        /// **参考系无关**：二体传播对旋转不变 ⇒ 游戏系 `DVector3` 可直接喂入（`epoch` 取 0）。
        /// </summary>
        public static (DVector3 pos, DVector3 vel) Propagate(DVector3 r, DVector3 v, double mu, double deltaSeconds)
        {
            (V3 p, V3 q) = Shepperd.Solve(mu, deltaSeconds, r, v);
            return (p, q);
        }

        /// <summary>径向单位矢量（背离中心天体）。</summary>
        public V3 Up(double ut) => RightHandedBCIPositionAtUT(ut).normalized;

        /// <summary>顺行单位矢量。</summary>
        public V3 Prograde(double ut) => RightHandedOrbitalVelocityAtUT(ut).normalized;

        public V3 RadialPlus(double ut, V3 up, V3 prograde) => (up - V3.Dot(prograde, up) * prograde).normalized;

        /// <summary>
        /// **正北**：切平面内指向**参考系北极**的方向（上游 `OrbitExtensions.North` 的语义）。
        ///
        /// ⚠ 与上游的**一处实现差异**（结果等价到二阶）：上游写的是
        /// `Exclude(Up(ut), referenceBody.transform.up * Radius − WorldBCIPositionAtUT(ut))`
        /// —— 即"朝**极轴在地表的那个点**"，用的是极轴上距中心一个半径的点。
        /// 我们直接用**极轴本身**的切向投影 `pole − (pole·r̂)r̂`（标准做法，也是同一方向）。
        /// 两者在切平面意义下同向，差异是曲率的二阶量。
        /// </summary>
        public V3 North(double ut) => North(ut, equatorialAxis);

        /// <summary><see cref="North(double)" /> 的显式极轴版（供 `BodyView` 复用同一算式）。</summary>
        public V3 North(double ut, V3 pole)
        {
            V3 up = Up(ut);
            V3 n = pole - V3.Dot(pole, up) * up;
            return n.sqrMagnitude > 0.0 ? n.normalized : V3.zero;
        }

        /// <summary>**正东**：`Up × North`（上游 `OrbitExtensions.East` 逐字同式）。</summary>
        public V3 East(double ut)
        {
            V3 n = North(ut);
            V3 up = Up(ut);
            V3 e = V3.Cross(up, n);
            return e.sqrMagnitude > 0.0 ? e.normalized : V3.zero;
        }

        // ── 两体间几何（语义逐字对齐 MJ 的 OrbitExtensions） ─────────────────────

        /// <summary>`a` 与 `b` 在 `ut` 时刻的距离。**两者必须绕同一个中心天体**（MJ 亦然）。</summary>
        public double Separation(OrbitView b, double ut) =>
            (RightHandedBCIPositionAtUT(ut) - b.RightHandedBCIPositionAtUT(ut)).magnitude;

        /// <summary>
        /// `a` 与 `b` 下一次最接近的时刻（MJ 的 8 轮 × 20 分格网格细化，逐条照搬）。
        /// ⚠ **这是上游的实现，不是更好的实现**——它按"游戏时间贵"的代价函数调过、且对高偏心双曲可能漏判。
        /// 换成"对 d(separation)/dt 求根"属于 §12.2 意义上的**局部优化**，排在基线有读数之后（P4）。
        /// </summary>
        public double NextClosestApproachTime(OrbitView b, double ut)
        {
            double closestApproachTime = ut;
            double closestApproachDistance = double.MaxValue;
            double minTime = ut;

            double interval = period;
            if (_ecc > 1)
                interval = 100 / meanMotion; // 覆盖一大块双曲弧；极高偏心时仍可能漏掉真正的最接近

            double maxTime = ut + interval;
            const int NUM_DIVISIONS = 20;

            for (int iter = 0; iter < 8; iter++)
            {
                double dt = (maxTime - minTime) / NUM_DIVISIONS;
                for (int i = 0; i < NUM_DIVISIONS; i++)
                {
                    double t = minTime + i * dt;
                    double distance = Separation(b, t);
                    if (distance < closestApproachDistance)
                    {
                        closestApproachDistance = distance;
                        closestApproachTime = t;
                    }
                }

                minTime = Statics.Clamp(closestApproachTime - dt, ut, ut + interval);
                maxTime = Statics.Clamp(closestApproachTime + dt, ut, ut + interval);
            }

            return closestApproachTime;
        }

        public double NextClosestApproachDistance(OrbitView b, double ut) =>
            Separation(b, NextClosestApproachTime(b, ut));

        /// <summary>两轨道面的夹角 (rad)，0..π。同面反向运行 = π。</summary>
        public double RelativeInclination(OrbitView b) => Statics.SafeAcos(V3.Dot(OrbitNormal(), b.OrbitNormal()));

        /// <summary>
        /// 相位角的重复周期。只对"近圆、近共面"有意义（MJ 注释亦然）。
        /// ⚠ 上游用**法向点积的符号**判别相对逆行；点积恰为 0（垂直轨道面）时上游取 -1。
        /// </summary>
        public double SynodicPeriod(OrbitView b)
        {
            int sign = V3.Dot(OrbitNormal(), b.OrbitNormal()) > 0 ? 1 : -1;
            return System.Math.Abs(1.0 / (1.0 / period - sign * 1.0 / b.period));
        }

        /// <summary>相位角。只在两者绕同一中心天体时有意义。</summary>
        public double PhaseAngle(OrbitView b, double ut)
        {
            V3 normalA = OrbitNormal();
            V3 posA = RightHandedBCIPositionAtUT(ut);
            V3 projectedB = b.RightHandedBCIPositionAtUT(ut) - V3.Dot(normalA, b.RightHandedBCIPositionAtUT(ut)) * normalA;
            double angle = Statics.SafeAcos(V3.Dot(posA.normalized, projectedB.normalized));
            if (V3.Dot(V3.Cross(normalA, posA), projectedB) < 0)
                angle = Statics.TAU - angle;
            return angle;
        }

        // ── 交点（与 b 的、以及与赤道的） ───────────────────────────────────────

        /// <summary>在 `a` 的轨道上、`a` 经过与 `b` 的升交点时的真近点角（0..2π）。</summary>
        private double AscendingNodeTrueAnomaly(OrbitView b) =>
            TrueAnomalyFromVector(V3.Cross(OrbitNormal(), b.OrbitNormal()));

        private double DescendingNodeTrueAnomaly(OrbitView b) => Statics.Clamp2Pi(AscendingNodeTrueAnomaly(b) + System.Math.PI);

        private double AscendingNodeEquatorialTrueAnomaly() =>
            TrueAnomalyFromVector(V3.Cross(equatorialAxis, OrbitNormal()));

        private double DescendingNodeEquatorialTrueAnomaly() =>
            Statics.Clamp2Pi(AscendingNodeEquatorialTrueAnomaly() + System.Math.PI);

        /// <summary>双曲轨道的真近点角有上界，这里给出那个上界；椭圆为 π。</summary>
        private double MaximumTrueAnomaly() => _ecc < 1 ? System.Math.PI : System.Math.Acos(-1 / _ecc);

        public bool AscendingNodeExists(OrbitView b) =>
            System.Math.Abs(Statics.ClampPi(AscendingNodeTrueAnomaly(b))) <= MaximumTrueAnomaly();

        public bool DescendingNodeExists(OrbitView b) =>
            System.Math.Abs(Statics.ClampPi(DescendingNodeTrueAnomaly(b))) <= MaximumTrueAnomaly();

        /// <summary>
        /// **退化判据**：轨道面与赤道面**重合**时（`Cross(极轴, h) = 0`）升交点**根本没有定义**。
        ///
        /// ⚠ 没有这一条会静默给假答案：`TrueAnomalyFromVector(0)` 返回 `0.0`（零矢量分支）⇒
        /// `|ClampPi(0)| = 0 <= π` ⇒ 判成"交点存在、就在 ν=0 处" ✗ —— 而 `TimeSelector` 的 `EQ_*`
        /// 时间参考正是靠这个判据挡"赤道轨上没有意义的交点" ⇒ 漏了它就会拿一个编出来的交点去算计划。
        /// （2026-09-18 实测抓到：赤道轨上 `AscendingNodeEquatorialExists()` 曾返回 true。）
        /// </summary>
        private bool EquatorialNodeIsDefined() =>
            V3.Cross(equatorialAxis, OrbitNormal()).sqrMagnitude > 1e-24;

        public bool AscendingNodeEquatorialExists() =>
            EquatorialNodeIsDefined()
            && System.Math.Abs(Statics.ClampPi(AscendingNodeEquatorialTrueAnomaly())) <= MaximumTrueAnomaly();

        public bool DescendingNodeEquatorialExists() =>
            EquatorialNodeIsDefined()
            && System.Math.Abs(Statics.ClampPi(DescendingNodeEquatorialTrueAnomaly())) <= MaximumTrueAnomaly();

        public double TimeOfAscendingNode(OrbitView b, double ut) => TimeOfTrueAnomaly(AscendingNodeTrueAnomaly(b), ut);

        public double TimeOfDescendingNode(OrbitView b, double ut) => TimeOfTrueAnomaly(DescendingNodeTrueAnomaly(b), ut);

        public double TimeOfAscendingNodeEquatorial(double ut) => TimeOfTrueAnomaly(AscendingNodeEquatorialTrueAnomaly(), ut);

        public double TimeOfDescendingNodeEquatorial(double ut) => TimeOfTrueAnomaly(DescendingNodeEquatorialTrueAnomaly(), ut);

        /// <summary>
        /// **赤道升交点的位置**（该点处的参考系位置矢量）。返回 `false` 表示**该交点不存在**
        /// （双曲轨且交点在双曲线的开口里；或轨道面与赤道面重合 ⇒ 交点没有定义）。
        ///
        /// ⚠ **批6 S3-3a（2026-09-30）：从"返回零矢量 + 调用方自己判长"改成 `Try*`** —— 零矢量当
        /// "不存在"是 A6 类歧义（忘了判就拿零矢量继续算，且零矢量也可能是合法值）。
        /// 判据取自 <see cref="AscendingNodeEquatorialExists" />（它含"赤道轨上交点未定义"那条退化判据）。
        /// `false` 时 <paramref name="pos" /> **保证置零**（不留上一次的残留）。
        ///
        /// 【为什么加这一族】V2 收敛（2026-09-18）：`OrbitMath.GetNodePositions` 是同一能力的第二份实现
        /// （而它与我们的 `inclination`/`LAN` 口径还差一个参考系：它默认 +Y、适配层是 +z）。
        /// `OrbitView` 原先只有交点**时刻**、没有交点**位置** —— 这是真缺口，补在这里之后
        /// 那两份就可以删掉，交点族只剩一个权威。
        /// </summary>
        public bool TryAscendingNodePositionEquatorial(double ut, out V3 pos)
        {
            if (!AscendingNodeEquatorialExists())
            {
                pos = default;
                return false;
            }
            pos = RightHandedBCIPositionAtUT(TimeOfAscendingNodeEquatorial(ut));
            return true;
        }

        /// <summary>**赤道降交点的位置**。语义同 <see cref="TryAscendingNodePositionEquatorial" />。</summary>
        public bool TryDescendingNodePositionEquatorial(double ut, out V3 pos)
        {
            if (!DescendingNodeEquatorialExists())
            {
                pos = default;
                return false;
            }
            pos = RightHandedBCIPositionAtUT(TimeOfDescendingNodeEquatorial(ut));
            return true;
        }

        // 批6 S3-3a（2026-09-30）：`AscendingNodePosition(OrbitView other, ut)` / `DescendingNodePosition(other, ut)`
        // **已删除** —— 两者都是"相对另一条轨道"的交点位置，全库**零调用者**（U-8：零调用者归零）。
        // 要取回：它们各自是上面 Try 形态的"相对另一条轨道"版（判据换成 `AscendingNodeExists(other)`）。

        /// <summary>
        /// 把方向矢量投影到轨道面后、相对**近心点方向**的真近点角（0..2π）。
        ///
        /// 近心点方向**不用 `e⃗`**——那样在圆轨上退化成噪声（这正是我们自家 `OrbitMath.GetTrueAnomaly`
        /// 踩过的坑，见 `MechJebLibPortTests.KnownDefect_*`）。改走移植的
        /// `Astro.EvecFromKeplerian(ecc, inc, lan, argP)`：它由要素按闭式给出方向，**圆轨上依然良定**
        /// （MJ 的原注释明确说"比 `Orbit.eccVec` 好，后者在圆轨上是零"）。
        /// </summary>
        public double TrueAnomalyFromVector(V3 vec)
        {
            V3 oNormal = OrbitNormal();
            V3 projected = vec - V3.Dot(oNormal, vec) * oNormal;

            // ⚠ 近心点方向**必须传 ecc = 1**：`EvecFromKeplerian` 的实现是"单位方向 × ecc"，
            // 传真实 ecc 的话圆轨上得到的是**噪声方向的单位矢量**（ecc ~ 1e-16，归一化把噪声放大成单位长度）
            // ——那就是我们自家 `OrbitMath.GetTrueAnomaly` 栽的同一个坑。传 1.0 拿的是纯方向，
            // 它在圆轨上依然良定：`KeplerianFromStateVectors` 对退化情形有显式约定
            // （`lan = s > EPS ? ... : 0`、`argp = ecc > EPS ? ... : 0`）。
            V3 vectorToPe = Astro.EvecFromKeplerian(1.0, _inc, _lan, _argp);

            if (projected.sqrMagnitude <= 0.0 || vectorToPe.sqrMagnitude <= 0.0)
                return 0.0;

            double angleFromPe = Statics.SafeAcos(V3.Dot(vectorToPe, projected.normalized));

            // 若投影方向落在"飞离近心点那一侧"的补集，则取 2π − angle。
            // 判据：cross(oNormal, vectorToPe) 指向出射侧；投影与它夹角 > 90° ⇒ 在入射侧。
            // ⚠ 上游在**恰近心点**处会返回 2π（它算的是 `360 - 0`）。这里用 `Clamp2Pi` 归一化到 `[0, 2π)`——
            //    对外承诺的值域是 `[0, 2π)`，让"近心点"返回 2π 是个会咬人的边界（`2π ≠ 0` 的比较会红）。
            return Statics.Clamp2Pi(
                Statics.SafeAcos(V3.Dot(projected.normalized, V3.Cross(oNormal, vectorToPe).normalized)) < System.Math.PI / 2
                    ? angleFromPe
                    : Statics.TAU - angleFromPe);
        }

        // ── 平近点角 / 时间 ──────────────────────────────────────────────────────

        /// <summary>
        /// `ut` 时刻的平近点角。椭圆返回 [0, 2π)；双曲可为任意值。
        /// 走移植的 `Angles` 做 ν → (E|F) → M，**不再自己写 Kepler**。
        /// </summary>
        public double MeanAnomalyAtUT(double ut)
        {
            double m0 = _ecc < 1
                ? Angles.MFromE(Angles.EFromNu(_nu, _ecc), _ecc)
                : Angles.MFromF(Angles.FFromNu(_nu, _ecc), _ecc);

            double ret = m0 + (ut - epoch) * meanMotion;
            if (_ecc < 1) ret = Statics.Clamp2Pi(ret);
            return ret;
        }

        /// <summary>下一次到达给定平近点角的时刻。</summary>
        public double UTAtMeanAnomaly(double meanAnomaly, double ut)
        {
            double meanDifference = meanAnomaly - MeanAnomalyAtUT(ut);
            if (_ecc < 1) meanDifference = Statics.Clamp2Pi(meanDifference);
            return ut + meanDifference / meanMotion;
        }

        /// <summary>下一次到达给定真近点角的时刻。</summary>
        public double TimeOfTrueAnomaly(double tanom, double ut) => UTAtMeanAnomaly(GetMeanAnomalyAtTrueAnomaly(tanom), ut);

        /// <summary>真近点角 → 平近点角（经偏近点角/双曲近点角）。走移植的 `Angles`。</summary>
        public double GetMeanAnomalyAtTrueAnomaly(double tanom)
        {
            if (_ecc < 1)
                return Statics.Clamp2Pi(Angles.MFromE(Angles.EFromNu(tanom, _ecc), _ecc));

            return Angles.MFromF(Angles.FFromNu(tanom, _ecc), _ecc);
        }

        /// <summary>给定半径处的真近点角（幅值解；椭圆上一半径对应 ±两个真近点角）。</summary>
        public double TrueAnomalyAtRadius(double radius) => Astro.TrueAnomalyFromRadius(_slr, _ecc, radius);

        /// <summary>下一次达到给定半径的时刻。椭圆给 [ut, ut+period) 内的；双曲可能给出过去。</summary>
        public double NextTimeOfRadius(double ut, double radius)
        {
            if (radius < PeR || (_ecc < 1 && radius > ApR))
                throw new System.ArgumentException(
                    "OrbitView.NextTimeOfRadius: given radius of " + radius + " is never achieved: PeR = " + PeR + " and ApR = " + ApR);

            double trueAnomaly1 = TrueAnomalyAtRadius(radius);
            double trueAnomaly2 = System.Math.PI * 2 - trueAnomaly1;
            double time1 = TimeOfTrueAnomaly(trueAnomaly1, ut);
            double time2 = TimeOfTrueAnomaly(trueAnomaly2, ut);

            if (time2 < time1 && time2 > ut) return time2;
            return time1;
        }

        /// <summary>下一次经过近心点。/ 下一次经过远心点（双曲抛 `ArgumentException`，与 MJ 一致）。</summary>
        public double NextPeriapsisTime(double ut) =>
            _ecc < 1 ? TimeOfTrueAnomaly(0, ut) : ut - MeanAnomalyAtUT(ut) / meanMotion;

        public double NextApoapsisTime(double ut)
        {
            if (_ecc < 1)
                return TimeOfTrueAnomaly(System.Math.PI, ut);

            throw new System.ArgumentException("OrbitView.NextApoapsisTime cannot be called on hyperbolic orbits");
        }

        // ── 机动坐标系 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 把世界系 Δv 分解成 MJ 的机动节点坐标（径向 / **法向取负** / 顺行）。
        /// 法向取负是 MJ 的约定（`-NormalPlus`），不是笔误。
        /// </summary>
        public V3 DeltaVToManeuverNodeCoordinates(double ut, V3 dV)
        {
            V3 up = Up(ut);
            V3 prograde = Prograde(ut);
            V3 normal = OrbitNormal();

            return new V3(V3.Dot(RadialPlus(ut, up, prograde), dV), V3.Dot(-normal, dV), V3.Dot(prograde, dV));
        }

        public override string ToString() =>
            $"PeR:{PeR:F0} ApR:{ApR:F0} SMA:{_sma:F0} ECC:{_ecc:F6} INC:{_inc:F4} LAN:{_lan:F4} ArgP:{_argp:F4} TA:{_nu:F4}";
    }
}
