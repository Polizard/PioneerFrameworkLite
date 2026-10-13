using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// Mach 折线的一个采样点（`mach` 无量纲、`factor` 无量纲）。
    ///
    /// **为什么是 class 而不是 struct**：它同时要当 **Def 数据**用（`CompProperties_Thruster.machCurve`
    /// 由 XML 填充 —— RimWorld 用无参构造 + 逐字段赋值 ⇒ 必须有 `public` 字段与无参构造）
    /// 和**纯测试的构造需要**（带参构造）。**一套类型**，不做 Def/运行时的两套镜像（D13 的"不做第二套类型系统"同一条）。
    /// ⚠ 只在**装配/建网**时读，不在每步热路径上分配（消费者按数组索引读字段）。
    /// </summary>
    public class MachPoint
    {
        public double mach;
        public double factor;
        public MachPoint() { }
        public MachPoint(double mach, double factor) { this.mach = mach; this.factor = factor; }
    }

    /// <summary>
    /// **[A4] 包线项**：网络中"**同一种包线形状**的一组引擎"的聚合，`veMf` = 该组的 Σ(ve·ṁ)。
    ///
    /// 为什么不给整网一个 φ：网络的推力是 `Σ(ve·ṁ)`（`PropellantNetwork.sumVeMf`），而 φ **逐引擎**不同
    /// ⇒ 必须回到逐组加权才不丢信息（一台火箭 + 一台吸气式同网、或同网的引擎 a 不同时尤其明显）。
    /// 由 `PropellantNetwork.RegisterEnvelope` 在**建网时**填，运行期只读（并行区安全）。
    /// ⚠ 与 `MachPoint` 一样是**顶层类型**（不是 `AirBreathingCore` 的嵌套类）：
    ///   `PropellantNetwork`（有 Map 侧）与 `PropulsionData`（无 Map 侧）都要用它，嵌套名会到处写成
    ///   `AirBreathingCore.EnvelopeTerm`（MachPoint 就是这么定下来的）。
    /// </summary>
    public class EnvelopeTerm
    {
        /// <summary>本组的 Σ(ve·ṁ)（推力贡献权重）。</summary>
        public double veMf;
        /// <summary>密度指数 a（0 = 不随 ρ 变）。</summary>
        public float densityExponent;
        /// <summary>Mach 折线（空 = 不折）。</summary>
        public IReadOnlyList<MachPoint>? machCurve;
    }

    /// <summary>
    /// **吸气式推进的纯核心（[A3]+[A4]+[A5]，2026-10-06）** —— 三件事的唯一算式：
    /// ① **进气充填**（D4：`k·A_进气·ρ(h)·|v_air|`，封顶由容量给）；
    /// ② **发动机包线**（D5：`F = ve_eff·ṁ·油门·(ρ/ρ0)^a·f(M)`；**火箭 = a=0、无折线的退化情形**）；
    /// ③ **混合比 + 熄火门**（D4：空气免费但发动机仍烧燃料；**抽干 ⇒ 熄火：推力 0 且该步燃料不扣**）。
    ///
    /// ## 为什么全放这里
    /// 本库的教训（`BurnStep` 的类注释）：执行器与台架各写一份 ⇒ 两份必然漂移，每个"缺陷"都是复刻漂移。
    /// ⇒ 进气/包线/混合比也一样：**只一份**，台架（`Pure.Tests`）与生产同调。
    ///
    /// ## 退化要求（"一切新能力 = 既有机制的数据退化情形"）
    /// **火箭 = `densityExponent == 0` 且折线为空 ⇒ `ThrustFactor` 恰返回 1.0**（逐位；乘法恒等）。
    /// 这条由纯测试钉住——现有四台火箭引擎因此**零改动、零浮点差**。
    ///
    /// 【边界】本文件**纯**（只 using System）⇒ 链入 `Pure.Tests`，可单测。
    /// </summary>
    public static class AirBreathingCore
    {
        /// <summary>
        /// **进气充填速率**（kg/s）：`k·A·ρ·|v_air|`（D4）。任一项非正 ⇒ 精确 0（"缺席即零"）。
        /// ⚠ 封顶不在这里——封顶属**容量**（调用方用 <see cref="FillStep"/> 按罐余量截断）。
        /// </summary>
        public static double IntakeRateKgPerSec(double coefficient, double intakeArea, double rho, double airSpeedMps)
        {
            if (!(coefficient > 0.0) || !(intakeArea > 0.0) || !(rho > 0.0) || !(airSpeedMps > 0.0))
                return 0.0;
            return coefficient * intakeArea * rho * airSpeedMps;
        }

        /// <summary>
        /// **一步充填量**（kg）：`min(罐余量, 速率·dt)`，且不为负。返回**实际充入量**。
        /// </summary>
        public static double FillStep(double rateKgPerSec, double dt, double storedKg, double capacityKg)
        {
            if (!(rateKgPerSec > 0.0) || !(dt > 0.0) || !(capacityKg > storedKg)) return 0.0;
            double room = capacityKg - storedKg;
            double want = rateKgPerSec * dt;
            return want < room ? want : room;
        }

        /// <summary>
        /// **Mach 折线求值**：按 mach 线性插值；**两端平化**（低于首点取首点值、高于末点取末点值）。
        /// 空折线 ⇒ 1.0（火箭退化）。折线按 mach 升序给出（调用方保证；乱序不做排序——热路径不排序）。
        /// ⚠ 形参是 `IReadOnlyList`（不是数组）：Def 侧存的是 `List&lt;MachPoint&gt;`，**不为求值再拷一份数组**
        /// （[A4] 接线时改的；数组照样传得进来，纯测试零改动）。
        /// </summary>
        public static double EvaluateMach(IReadOnlyList<MachPoint>? curve, double mach)
        {
            if (curve == null || curve.Count == 0) return 1.0;
            if (curve.Count == 1) return curve[0].factor;
            if (mach <= curve[0].mach) return curve[0].factor;
            if (mach >= curve[curve.Count - 1].mach) return curve[curve.Count - 1].factor;
            for (int i = 0; i + 1 < curve.Count; i++)
            {
                double m0 = curve[i].mach, m1 = curve[i + 1].mach;
                if (mach >= m0 && mach <= m1)
                {
                    double span = m1 - m0;
                    if (!(span > 0.0)) return curve[i + 1].factor;   // 重合点
                    double t = (mach - m0) / span;
                    return curve[i].factor + (curve[i + 1].factor - curve[i].factor) * t;
                }
            }
            return curve[curve.Count - 1].factor;   // 理论不可达
        }

        /// <summary>
        /// **包线因子** `φ = (ρ/ρ0)^a · f(M)`（D5）。
        /// **火箭退化**：`a == 0` 且折线为空 ⇒ **恰 1.0**（逐位；消费者乘它就是恒等）。
        /// `ρ0 ≤ 0`/`ρ ≤ 0` ⇒ 也返回 0（但那只在"密度模型缺席"时出现，火箭退化分支先短路）。
        /// </summary>
        public static double ThrustFactor(double rho, double rho0, double densityExponent,
            IReadOnlyList<MachPoint>? machCurve, double mach)
        {
            if (densityExponent == 0.0 && (machCurve == null || machCurve.Count == 0))
                return 1.0;                                   // 火箭：与既有算式逐位一致
            if (!(rho > 0.0) || !(rho0 > 0.0)) return 0.0;

            double densityFactor = densityExponent == 0.0
                ? 1.0
                : Math.Pow(rho / rho0, densityExponent);      // 只在非火箭路径上出现（a 通常 1）
            double machFactor = EvaluateMach(machCurve, mach);
            return densityFactor * machFactor;
        }

        /// <summary>
        /// **海平面音速**（m/s，[A4]）：L1 **没有**温度/音速剖面模型 ⇒ 取常数 —— 与 ρ₀/H 同一处境
        /// （机制先落、数值待标定）。由 <see cref="MachFromAirSpeed"/> 换算 Mach。
        /// ⚠ 只影响**声明了 Mach 折线**的发动机；火箭（无折线）看都不看它 ⇒ 这条常数不参与任何既有算式。
        /// </summary>
        public const double SpeedOfSoundAtSeaLevelMps = 340.0;

        /// <summary>Mach 数 = `|v_air| / 音速`（任一非正 ⇒ 0 = "无空速"）。</summary>
        public static double MachFromAirSpeed(double airSpeedMps,
            double speedOfSoundMps = SpeedOfSoundAtSeaLevelMps)
            => airSpeedMps > 0.0 && speedOfSoundMps > 0.0 ? airSpeedMps / speedOfSoundMps : 0.0;

        /// <summary>
        /// **[A4] 全推续航（秒）** = 主工质存量 / ṁ —— 面板对**吸气式**该显示的口径。
        /// 为什么不是 Δv：Tsiolkovsky 的数字对喷气没有指挥意义（`ve_eff` 是**包线给定**的等效值，
        /// 随 ρ 与 Mach 变，"还能变多少速"由当时的空气决定 ⇒ 能答的只有"这么开还能开多久"）。
        /// ⚠ 这是**全油门**口径（油耗率 ṁ 恒定）⇒ 名字里带 FullThrottle；非正输入 ⇒ 0。
        /// </summary>
        public static double FullThrottleEnduranceSeconds(double propellantKg, double massFlowKgPerSec)
            => propellantKg > 0.0 && massFlowKgPerSec > 0.0 ? propellantKg / massFlowKgPerSec : 0.0;

        /// <summary>
        /// **网络包线因子（[A4]，唯一算式）**：`φ_net = [Σ_入表 (ve·ṁ)_j·φ_j + Σ_未入表 (ve·ṁ)·1] / Σ_全体 (ve·ṁ)`。
        ///
        /// · `terms` 空/null ⇒ **恰 1.0**（纯火箭 ⇒ 不进除法、不进超越函数 ⇒ 既有算式逐位不变）；
        /// · **真空（ρ ≡ 0）**：`a &gt; 0` 的项 φ = 0 ⇒ 加权结果 0 ⇒ 调用方据此**不路由该网络**
        ///   （D5 的"真空里不得把燃烧路由到吸气式网络"）；**整网全是火箭项** ⇒ 仍恰 1.0（火箭在真空照烧，既有行为）。
        /// · ⚠ **分母是全体权重**、不是入表权重：火箭项 φ≡1 但**权重照占**（同网混装时，不能被吸气式那一项拉低）。
        ///   未入表部分的权重 = `totalVeMf − Σ_入表`（入表时已排除火箭项，见 `RegisterEnvelope`）。
        /// ⚠ 调用方只需拿到 φ 后决定"跳不跳"，**φ 只乘推力、不乘流率** —— 密度低 = 同样的油少出力，
        ///   这正是包线的物理含义（流率由"油门"管，见 §2.4 的自由油门）。
        /// </summary>
        public static double EnvelopeFactor(IReadOnlyList<EnvelopeTerm>? terms, double totalVeMf,
            double rho, double rho0, double mach)
        {
            if (terms == null || terms.Count == 0) return 1.0;
            double wEnv = 0.0, weighted = 0.0;
            for (int i = 0; i < terms.Count; i++)
            {
                var t = terms[i];
                if (!(t.veMf > 0.0)) continue;
                wEnv += t.veMf;
                weighted += t.veMf * ThrustFactor(rho, rho0, t.densityExponent, t.machCurve, mach);
            }
            double wRocket = totalVeMf - wEnv;              // 未入表的那部分（火箭/退化项）φ ≡ 1
            if (wRocket < 0.0) wRocket = 0.0;               // 保守：表权重不该超过总权重
            double wSum = wEnv + wRocket;
            return wSum > 0.0 ? (weighted + wRocket) / wSum : 1.0;
        }

        /// <summary>**燃气混合比**：烧 <paramref name="fuelKg"/> 需要多少空气（kg）：`fuelKg · airPerFuel`。</summary>
        public static double AirRequired(double fuelKg, double airPerFuel)
            => fuelKg > 0.0 && airPerFuel > 0.0 ? fuelKg * airPerFuel : 0.0;

        /// <summary>
        /// **点火门（"抽干 ⇒ 熄火"）**：本步打算烧 <paramref name="fuelWantedKg"/> kg 燃料，
        /// 需要的空气 = `fuelWantedKg·airPerFuel`；**空气不足 ⇒ 不能烧**（返回 false ⇒ 调用方给 0 推力、
        /// **且该步燃料不扣**，D4/§2.3 的判据）。燃料不足同样 false（想烧的量都拿不出来）。
        /// ⚠ 调用方应当先把"想烧的量"按燃料存量截断（既有执行器就是这么做的），本门再查空气。
        /// </summary>
        public static bool CanRun(double fuelAvailableKg, double airAvailableKg, double fuelWantedKg, double airPerFuel)
        {
            if (!(fuelWantedKg > 0.0)) return false;
            if (fuelAvailableKg < fuelWantedKg) return false;
            double airNeed = AirRequired(fuelWantedKg, airPerFuel);
            return airAvailableKg >= airNeed;
        }

        /// <summary>
        /// **[A5] 进气限流（唯一算式）**：本步的空气存量撑得起主工质**全流量的几分之几**（0…1）。
        ///
        /// 与 <see cref="CanRun"/>（硬门：够/不够）**不是**一回事 —— 两者判据不同、各有其位：
        ///   · `CanRun` 用在"想烧 X kg"**已知**的场合（计划层/台架）；
        ///   · 本式用在**切片构造**处：那里还不知道本步要烧多少（那是 `BurnStep.Compute` 的输出，
        ///     而它又依赖本切片给的推力）⇒ 只能反过来问"空气支持多大流率"：
        ///     `cap = 空气存量 / (全流量一步所需的空气)`，把切片的 F 与 ṁ 同乘 cap
        ///     ⇒ 推力 ∝ 流率（`F = ve·ṁ`）⇒ **抽多少空气就烧多少燃料**，绝不过抽。
        ///
        /// 语义（D4/§2.3）：**够全流量 ⇒ 1（不受限；火箭走这条 ⇒ 逐位不变）；不够 ⇒ 按比例降流；
        /// 一点都没有 ⇒ 0 = 熄火** —— 调用方见 0 就**不建这个切片**（推力 0、主工质不扣 =
        /// "熄火期间燃料不扣"）。
        /// ⚠ 无进气需求（`airFlowKgPerSec ≤ 0` = 纯火箭）恒返回 1.0 ⇒ 该路径不进任何除法。
        /// </summary>
        public static double IntakeThrottleCap(double airAvailableKg, double dtSeconds,
            double airFlowKgPerSec, double primaryFlowKgPerSec)
        {
            if (!(airFlowKgPerSec > 0.0) || !(primaryFlowKgPerSec > 0.0)) return 1.0;  // 纯火箭
            if (!(dtSeconds > 0.0)) return 1.0;
            double airNeedFullStep = airFlowKgPerSec * dtSeconds;                      // 全流量烧一步的耗气
            if (!(airNeedFullStep > 0.0)) return 1.0;
            double cap = airAvailableKg / airNeedFullStep;
            if (cap >= 1.0) return 1.0;
            return cap > 0.0 ? cap : 0.0;
        }
    }
}
