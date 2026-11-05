using System;

namespace PioneerFramework
{
    /// <summary>
    /// 观测方技术三分量（W2）：灵敏度 / 抗压制 / 抗欺骗。
    /// 三分量**不可合并为单标量**——单标量会让军备竞赛退化成一维比大小，
    /// 而三分量让"压制 ↔ 抗压制"、"欺骗 ↔ 反欺骗"成对（设计稿 §3.1 / §11.5）。
    /// 载体：`SpaceEntityGroup.SensorTech`（落盘；由部件组件与 EW 建筑投影推送，见实施计划 [V1]/[N7]）。
    /// 【边界】纯数据 + 数值语义。本文件不做：目标选择（决策层）、任何 group / 组件读写。
    /// </summary>
    public struct SensorTech
    {
        /// <summary>灵敏度（m）：有效探测距离的基数——静默基准目标恰好在灵敏度处被发现。吃 `ShipStats.sensorRange`。</summary>
        public float sensitivityMeters;

        /// <summary>抗压制（无量纲，≥0）：压制效力除以 (1 + ecmResist)；0 = 无防护，越大压制越无效。</summary>
        public float ecmResist;

        /// <summary>抗欺骗（无量纲，≥0）：> 欺骗强度则**该观测者**识破假接触（"深层提示"的唯一来源）。
        /// 注意粒度是观测者而非阵营——否则会得到"全星系一台好雷达免疫所有欺骗"（设计稿 §7.2 推论一）。</summary>
        public float eccmResist;

        /// <summary>**记忆时长**（s，物理秒轴；[V3] 的记忆窗口 T；2026-10-02 用户裁定 ②）。
        /// ⚠ 它不是"第四个对抗分量"（W2 的三分量仍是灵敏度/抗压制/抗欺骗那一对关系），
        /// 而是**观测者的处理/记忆属性** —— 与三分量同住一个结构只是因为它们**同源**（都从观测者的
        /// 传感器/EW 部件聚合而来、同一批失效点）；用户的"信息处理速度以后改为非无穷"就长在这一格。
        /// 0 = 未设 ⇒ 用 `DetectionCore.DefaultMemorySeconds`。</summary>
        public float memorySeconds;
    }

    /// <summary>
    /// 信号强度（W1 枢纽）的派生输入与算式——**探测方程两侧共用同一个量**：
    /// 目标侧"别人探测我时读它"，观测侧"我开主动探测/干扰/转发时它会升高"。
    ///
    /// 单位口径：无量纲，**标称 1.0 = 一艘静默的基准飞船**（R_REF 尺寸、无热无电）。
    /// 每一项都是"多少倍基准"，所以算式本身自解释，标定只集中在下方常数区。
    ///
    /// 冷发射的副产品（设计稿 §4.3）：`Burning` 项让**巡航段导弹低信号、终末点火段高信号**
    /// 自动成立——信号源是弹不是船，潜袭语义由此保住，不需要任何额外机制。
    /// </summary>
    public struct SignalInputs
    {
        /// <summary>**编队并集 AABB 半尺寸的模**（m，船体系、姿态无关；B8 定稿，与碰撞组并集盒同口径）。
        /// 不是“各实体半对角线取 max”，也不是“特征半径之和”。无实体/无几何时给 0。</summary>
        public double rcsRadiusMeters;
        /// <summary>Σ(heatProduction + heatDissipation)（W）。今天恒为 0（无组件写入），组件就位后自动生效。</summary>
        public double heatWatts;
        /// <summary>Σ(powerGen + powerDraw)（W）。同上。</summary>
        public double powerWatts;
        /// <summary>本组是否正在点火（主推燃烧）。导弹巡航段为 false、终末段为 true。</summary>
        public bool burning;
        /// <summary>本组是否主动探测开机（[E2]）。开机看得远但要付暴露代价。</summary>
        public bool activePing;
        /// <summary>**主动辐射增量**（[V7]/[E2]，2026-10-01）：开机的暴露量**由设备定**——
        /// 部件级（`ShipStats.activeRadiation`）+ 建筑级（`Group.RadarCap.radiation` 投影）之和。
        /// `SignalModel.Total` 把它加在 `ActivePingMultiplier` **基准之上**（没装设备也能开机 = 只剩基准）。</summary>
        public double activeRadiation;
        /// <summary>本组是否正在向外转发情报（[E8] 通信 EW 最小版：共享节点要辐射才能转发）。</summary>
        public bool sharingIntel;
        /// <summary>本组是否处于开火揭示窗口内（[N6]；导弹发射器开火不计，见 A4）。</summary>
        public bool revealedByFire;
        /// <summary>本组是否正在干扰（[E3] 压制式干扰开机）：干扰机必须辐射才能干扰（W1 的第二个开关项）。</summary>
        public bool jamming;
    }

    /// <summary>
    /// 信号强度派生算式（[V8]）——**必须唯一的入口**：任何地方要"我有多响"都走这里。
    /// 出现第二份算式即违反 [C7] 的对称性不变式（实施计划 §9.3）。
    ///
    /// S = (r/R_REF)² + heat/HEAT_REF + power/POWER_REF + 各开关项
    ///
    /// 参考值集中在下方常数区；`R_REF` 是**唯一需要游戏内定标的尺寸基准**（一艘静默基准飞船的
    /// AABB 特征半径）——定标后 S(基准船) 应恰为 1.0，纯测试 `Signal_ReferenceShipIsExactlyOne` 钉住这条。
    /// </summary>
    public static class SignalModel
    {
        // ── 标定常数（待游戏内定标；此处为量级合理的初值）──
        /// <summary>基准飞船的 AABB 特征半径（m）。定标目标：静默参考船的 S 恰为 1.0。</summary>
        public const double RRefMeters = 60.0;
        /// <summary>热特征参考功率（W）：热项 = Σ热功率 / 此值。</summary>
        public const double HeatRefWatts = 5.0e5;
        /// <summary>电特征参考功率（W）：电项 = Σ电功率 / 此值。</summary>
        public const double PowerRefWatts = 1.0e6;

        // ── 动作/状态项（均为"基准船的多少倍"）──
        /// <summary>主推点火：终末段导弹的辐射（相对基准船）。</summary>
        public const double BurnMultiplier = 20.0;
        /// <summary>主动探测开机（[E2]）：看得远的代价。</summary>
        public const double ActivePingMultiplier = 10.0;
        /// <summary>情报转发（[E8]）：最小版通信 EW 的暴露成本，刻意小于主动探测。</summary>
        public const double ShareMultiplier = 0.5;
        /// <summary>开火揭示（[N6]）：一次开火让自身信号抬升的幅度与时长由调用方给窗口。</summary>
        public const double RevealMultiplier = 5.0;
        /// <summary>干扰开机（[E3]）：干扰机必须辐射才能干扰 ⇒ 它自己也变响（W1 的第二个开关项）。</summary>
        public const double JamMultiplier = 8.0;
        /// <summary>开火揭示的窗口时长（物理秒；**待定标**）："打一枪就亮一会儿"。</summary>
        public const double RevealWindowSeconds = 10.0;

        /// <summary>尺寸项（唯一项）：半径相对基准的平方（球体 RCS 口径）。</summary>
        public static double RcsTerm(double radiusMeters)
            => radiusMeters <= 0.0 ? 0.0 : (radiusMeters / RRefMeters) * (radiusMeters / RRefMeters);

        /// <summary>热特征项：Σ热功率相对基准。</summary>
        public static double HeatTerm(double heatWatts)
            => heatWatts <= 0.0 ? 0.0 : heatWatts / HeatRefWatts;

        /// <summary>电特征项：Σ电功率相对基准。</summary>
        public static double PowerTerm(double powerWatts)
            => powerWatts <= 0.0 ? 0.0 : powerWatts / PowerRefWatts;

        /// <summary>总信号强度（无量纲，标称 1.0 = 静默基准船）。</summary>
        public static double Total(in SignalInputs x)
        {
            double s = RcsTerm(x.rcsRadiusMeters)
                     + HeatTerm(x.heatWatts)
                     + PowerTerm(x.powerWatts);
            if (x.burning) s += BurnMultiplier;
            if (x.activePing) s += ActivePingMultiplier + x.activeRadiation;   // [V7] 设备增量叠在基准之上（W1）
            if (x.sharingIntel) s += ShareMultiplier;
            if (x.revealedByFire) s += RevealMultiplier;
            if (x.jamming) s += JamMultiplier;
            return s;
        }
    }

    /// <summary>
    /// **[V7] 主动雷达能力**（部件/建筑投影出来的那一份；消费者 = [V3] 认知层与探针）。
    /// 口径（2026-10-01 用户裁定 + 设计稿 §11.2）：
    /// · **增益与辐射是"加成/增量"，可加**（与 `ShipStats` 同语义）；
    /// · **波束几何（半锥角 / 距离上限）取最大**——多台雷达共用一个"外沿"，最强的那个说了算（v1 的单波束模型）。
    /// </summary>
    public struct RadarCapability
    {
        /// <summary>主动增益加成：有效探测距离倍率 = `1 + 本值`（0 = 无增益）。</summary>
        public float activeGainBonus;
        /// <summary>主动开机时的信号增量（S_REF 倍数，加在基准之上）。</summary>
        public float activeRadiation;
        /// <summary>波束半锥角（度）。</summary>
        public float beamHalfAngleDeg;
        /// <summary>波束距离上限（m）。</summary>
        public float beamRangeMeters;
        /// <summary>扫掠角速度（deg/s，**physicsTick 轴**）——扫掠相位 = `physicsTick × 本值`，是**时间的纯函数**
        ///（所以相位不落盘、天然确定性；用户的"信息处理速度"以后就改这一格）。多台取最大。</summary>
        public float scanRateDegPerSec;
    }

    /// <summary>
    /// 探测判据纯核心（[V2]）。判据的**归一化形式**，与设计稿 §4.1 的 `信号 / 距离² > 阈值` 等价：
    ///
    ///     信号侧   S · (灵敏度 / d_target)²
    ///     阈值侧   1 + J_eff
    ///     可见 ⟺   S · (灵敏度 / d_target)² > 1 + J_eff
    ///     ⟺       d_target < 灵敏度 · √(S / (1 + J_eff))  ≡  EffectiveDetectRange
    ///
    /// 两式在 `阈值 = 1/灵敏度²` 时整理即得，所以是同一个判据；改用归一化形式只是为了避免
    /// 1e-12 量级（灵敏度 ~1e6 m，1/灵敏度² ~1e-12），并让单位全是米。
    ///
    /// **烧穿距离是推论，不是第二个算式**：`J_eff` 只依赖干扰机到观测方的距离，与目标距离无关，
    /// 所以目标一靠近，信号按平方增长而阈值侧不变 ⇒ 必然在 `EffectiveDetectRange` 处**恰好翻真一次**。
    /// 由此本文件**不提供** `BurnThroughRangeMeters` —— 它和 `EffectiveDetectRange` 是同一个函数，
    /// 提供两份正好违反 [C7]"禁止第二份算式"（实施计划 §9.3）。烧穿由纯测试断言唯一翻转点来证明。
    /// 【边界】纯数值。不做感知（写认知层）、不做目标选择（决策层）。
    /// </summary>
    public static class DetectionCore
    {
        /// <summary>干扰效力参考距离（m）：强度 1.0 的干扰机在此距离上令 J_eff = 1（即把有效信号砍半）。</summary>
        public const double JamRefMeters = 1.0e6;
        /// <summary>退化输入下限：低于此值的距离/灵敏度按"判不可见"处理，避免除零与放大噪声。</summary>
        private const double Eps = 1e-9;

        /// <summary>**记忆时长默认值**（s，物理秒轴）：观测者的 `SensorTech.memorySeconds` 为 0/未设时用它
        ///（[V3] 的记忆窗口 T 的唯一兜底处；**待定标**——与 `SigmaRefMeters` / 干扰参考距离同族）。
        /// 语义 = "最后一次刷新之后还能活多久"：短了目标一闪就丢，长了静默/关机没意义。</summary>
        public const double DefaultMemorySeconds = 30.0;

        /// <summary>
        /// 单台干扰机对某观测方的效力（平方反比衰减）。
        /// 干扰机的距离是**它到观测方的距离**，不是目标到观测方的距离——两者不同是物理事实，
        /// 也正是"干扰机必须靠近才有效、且靠近后自己更容易被反辐射找上门"（§11.6 的自平衡）的来源。
        /// </summary>
        public static double JamEffort(double jamStrength, double jammerDistanceMeters)
        {
            if (!(jamStrength > 0.0)) return 0.0;
            if (!(jammerDistanceMeters > Eps)) return 0.0;
            double k = JamRefMeters / jammerDistanceMeters;
            return jamStrength * k * k;
        }

        /// <summary>压制后的有效信号：S_eff = S / (1 + J_eff)。抗压制落在分母上，与压制成对。</summary>
        public static double EffectiveSignal(double signal, double jamEffort, double ecmResist)
        {
            if (!(signal > 0.0)) return 0.0;
            if (!(jamEffort > 0.0)) return signal;
            double denom = 1.0 + jamEffort / (1.0 + Math.Max(0.0, ecmResist));
            return signal / denom;
        }

        /// <summary>
        /// **观测者因子**（唯一算式的左半边，[V3] 热路径用）：`A_obs = sens² / (1 + J/(1+ecm))`。
        /// 只含**观测者侧**的量（灵敏度 / 抗压制 / 干扰效力）——目标侧只剩 `SignalModel.Total` 的 `S`（B_tgt）。
        /// 于是"可见"退化成一次乘法加一次比较（**无 sqrt、无除法**）：
        /// <code>d² ≤ A_obs · B_tgt</code>
        /// 这正是实施计划 §1.8 [L](a)/[S] 定下的形态：`EffectiveDetectRange = √(A·S)` 只是**派生视图**（给人看）。
        /// </summary>
        public static double ObserverFactor(in SensorTech observer, double jamEffort)
        {
            double sens = observer.sensitivityMeters;
            if (!(sens > 0.0)) return 0.0;
            double denom = 1.0 + (jamEffort > 0.0 ? jamEffort / (1.0 + Math.Max(0.0, observer.ecmResist)) : 0.0);
            return sens * sens / denom;
        }

        /// <summary>
        /// **可见 ⟺ `d² ≤ A_obs · S`**（[V3] 的唯一规范算式；热路径直接用这条，不经过开根）。
        /// 退化语义与 <see cref="IsVisible" /> **逐条一致**（信号 ≤ 0 / NaN 距离 / 负距离 / 零灵敏度 ⇒ 不可见），
        /// 由 `SensorCoreTests` 的等价性网格钉住。
        /// </summary>
        public static bool IsVisibleByProduct(double signal, double distanceSquaredMeters, in SensorTech observer, double jamEffort)
        {
            if (!(signal > 0.0)) return false;
            if (double.IsNaN(distanceSquaredMeters) || distanceSquaredMeters < 0.0) return false;
            double a = ObserverFactor(observer, jamEffort);
            if (!(a > 0.0)) return false;                       // 零灵敏度：看不见（与 IsVisible 的 `r > 0` 前置一致）
            return distanceSquaredMeters <= a * signal;
        }

        /// <summary>
        /// **派生视图**：有效探测距离 = `√(A_obs · S)`（**唯一一处开根**，给 UI/验收/读数用）。
        /// **判据本体不走这里**（[S] 五级反思：判据的原语是"阈值比较"，距离是被人为具象化的中间量）。
        /// 这就是烧穿距离 —— 二者是同一个函数，故不另设 `BurnThroughRangeMeters`（[C7] 禁止第二份算式）。
        /// </summary>
        public static double EffectiveDetectRange(double signal, in SensorTech observer, double jamEffort)
        {
            if (!(signal > 0.0)) return 0.0;
            double a = ObserverFactor(observer, jamEffort);
            if (!(a > 0.0)) return 0.0;
            return Math.Sqrt(a * signal);
        }

        /// <summary>可见性（**距离形态的兼容入口**）：内部转成平方判据 ⇒ 不再有第二份算式。
        /// `d_target = 0`（重合）视为可见（若信号 &gt; 0）；零灵敏度 ⇒ 不可见。</summary>
        public static bool IsVisible(double signal, double targetDistanceMeters, in SensorTech observer, double jamEffort)
        {
            if (!(signal > 0.0)) return false;
            if (double.IsNaN(targetDistanceMeters) || targetDistanceMeters < 0.0) return false;
            return IsVisibleByProduct(signal, targetDistanceMeters * targetDistanceMeters, in observer, jamEffort);
        }

        /// <summary>位置不确定度（m）：压制的作用对象之一（另一是有效探测距离）。制导精度直接吃它。
        /// σ = d · √(方位误差² + 测距相对误差² + 干扰项²) —— 三项都与距离成正比，
        /// 所以"越远越不准"是内建的，不需要额外开关。</summary>
        public static double PositionSigmaMeters(double targetDistanceMeters, double jamEffort,
            double bearingSigmaRad, double rangeSigmaRelative, double jamSigmaFactor)
        {
            if (!(targetDistanceMeters > 0.0)) return 0.0;
            double b = Math.Max(0.0, bearingSigmaRad);
            double r = Math.Max(0.0, rangeSigmaRelative);
            double j = Math.Max(0.0, jamEffort) * Math.Max(0.0, jamSigmaFactor);
            return targetDistanceMeters * Math.Sqrt(b * b + r * r + j * j);
        }
    }
}
