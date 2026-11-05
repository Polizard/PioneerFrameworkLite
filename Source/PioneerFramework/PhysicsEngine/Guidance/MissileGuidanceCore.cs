using System;

namespace PioneerFramework
{
    /// <summary>
    /// 导弹终末制导参数（纯数据，Def 可配；默认 = 2026-09-08 定稿初值）。
    /// 单位口径：距离 m、速度 m/s、加速度 m/s²、时间 s。物理步长无关——每步只输出 dv 需求，
    /// 由 D15 物理按 ve·ṁ 缓释（步长细化时行为不变，见设计 §0.3-8）。
    /// </summary>
    public struct MissileGuidanceParams
    {
        /// <summary>横向相对速度归零增益（每步 dv = −vPerp·gain；物理侧饱和即满推力）。</summary>
        public double lateralGain;
        /// <summary>接近率阈值（m/s）：接近率已达此值不再补（"只加不减"的入口条件）。</summary>
        public double closingThreshold;
        /// <summary>终末目标接近率（m/s，冲刺巡航值；原 MissileDef.terminalSpeed 重定义口径）。</summary>
        public double cruiseClosing;
        /// <summary>单步接近率补量上限（m/s，防 dv 需求爆表）。</summary>
        public double boostMax;
        /// <summary>点火死区（m/s）：dv 低于此值熄火惯性逼近。</summary>
        public double deadband;
        /// <summary>可用横向加速度（m/s²，主推 F/m 湿质量口径）：用于"剩余时间够不够修横差"约束。</summary>
        public double lateralAccel;
        /// <summary>转向率（rad/s，导弹 group.MaxAngularRate）：速度方向与视线夹角 → 转向耗时，
        /// 计入"剩余时间够不够修横差"约束（主推未对准时只有 RCS 出力，转向期是真实的时间开销——
        /// 2026-09-08 游戏内队首擦过实证：0.88km 处 400m 横偏 + 冲刺 450 → 转向 1.35s + 修横差 0.85s
        /// 超过 t_go → 以 0.3km 擦过）。</summary>
        public double turnRateRad;
        /// <summary>时间裕度：tGo 至少是（转向 + 横差修正）时间的此倍数，才允许补接近率（越大越保守）。</summary>
        public double timeMargin;
        /// <summary>几何丢失距离裕度（m）：最近点后表面距拉大超过此值 → 判定几何丢失（追丢中止）。
        /// 取代旧"接管点 +3km"（接管点常在 100km 外，近距滑脱根本触发不了）——以最近点为基准，
        /// 同时给掉头再捕获留出回旋余地（大横差滑脱的横向漂移可达十几 km）。</summary>
        public double lostMarginMeters;
    }

    /// <summary>
    /// 标准弹的制导参数（**唯一来源**）。
    /// 2026-09-14：此处原为 `MissileGuidanceParams.CreateDefault()`——它与 `MissileDef` 的 C#/XML 默认值
    /// 是**手抄的平行副本**，已经漂了两处（deadband 1.0 vs 游戏内 0.5、lateralAccel 83.0 vs 由推力/湿质量
    /// 推得的 ≈80.0826），导致"纯测试全绿"不代表游戏内行为。现改为：本类持有标准弹的全部物理/制导常数，
    /// `MissileDef` 只覆写真正需要调参的字段，且 `lateralAccel` 两侧共用同一算式（LateralAccelFor）。
    /// 任何一处漂移都会被纯测试与游戏内一致性断言同时抓住。
    /// </summary>
    public static class MissileGuidanceDefaults
    {
        // ── 标准弹物理常数（= PF_Missile_Standard 的 XML 取值）──
        public const float DryMass = 300f;
        public const float FuelMass = 2000f;
        public const float Ve = 3200f;
        public const float MassFlow = 60f;
        public const float RcsVe = 900f;
        public const float RcsMassFlow = 2f;
        public const float RcsFuelMass = 120f;

        // ── 制导常数（可调参项由 MissileDef 字段承载；不可调项在此单点定义）──
        public const float LateralGain = 0.8f;
        public const float ClosingThreshold = 300f;
        public const float CruiseClosing = 450f;
        public const double BoostMax = 600.0;
        /// <summary>点火死区（m/s）：dv 低于此值熄火惯性逼近。**0.5 是意图值**——同项目发射制导
        /// （LaunchGuidanceCore / RocketLaunchController）同为 0.5 且单一来源，旧副本的 1.0 是漂移。</summary>
        public const double Deadband = 0.5;
        public const float MaxAngularRateDegrees = 20f;
        public const float TimeMargin = 2f;
        public const float LostMarginMeters = 30000f;

        /// <summary>
        /// 横向加速度 = 主推推力 / 湿质量（K11 每枚同构口径）。
        /// `MissileDef.GuidanceParams` 与本类共用此算式——避免两处各写一遍再漂。
        /// </summary>
        public static double LateralAccelFor(double dryMass, double fuelMass, double ve, double massFlow,
            bool hasRcs, double rcsVe, double rcsMassFlow, double rcsFuelMass)
        {
            double thrust = ve * massFlow + (hasRcs ? rcsVe * rcsMassFlow : 0.0);
            double wetMass = dryMass + fuelMass + (hasRcs ? rcsFuelMass : 0.0);
            return wetMass > 1e-6 ? thrust / wetMass : 0.0;
        }

        /// <summary>标准弹的制导参数（纯测试与游戏内一致性断言共用）。</summary>
        public static MissileGuidanceParams Standard
        {
            get
            {
                return new MissileGuidanceParams
                {
                    lateralGain = LateralGain,
                    closingThreshold = ClosingThreshold,
                    cruiseClosing = CruiseClosing,
                    boostMax = BoostMax,
                    deadband = Deadband,
                    lateralAccel = LateralAccelFor(DryMass, FuelMass, Ve, MassFlow,
                        true, RcsVe, RcsMassFlow, RcsFuelMass),
                    turnRateRad = MaxAngularRateDegrees * (Math.PI / 180.0),
                    timeMargin = TimeMargin,
                    lostMarginMeters = LostMarginMeters
                };
            }
        }
    }

    /// <summary>
    /// 制导状态（跨步记忆；纯数据，调用方持有——AVM 存 VehicleRecord，测试存仿真器）。
    /// 全部由"最近点 + 远离计时"构成，无积分器/滤波器：载入存档重置即回到"重新观测"语义。
    /// </summary>
    public struct MissileGuidanceState
    {
        /// <summary>历史最近表面距（m）。追丢判定基准——取代旧"接管点 +3km"（对近距滑脱不敏感）。</summary>
        public double minSurfaceDist;
        /// <summary>
        /// 是否已记录过最近距。**不能用 `minSurfaceDist &lt;= 0` 当"未初始化"哨兵**：
        /// 贴表面（=0）与侵入目标球（&lt;0）都是**有效**的历史值（命中/穿透），
        /// 用 ≤0 会把它们误判为未初始化 → 之后一步把历史**重置成更大的值** →
        /// 追丢判据 `surfaceDist &gt; minSurfaceDist + lostMargin` 被抬高、提前 Abort。
        /// （2026-09-14 修；瞬态字段，不序列化）
        /// </summary>
        public bool minSurfaceInitialized;
    }

    /// <summary>制导动作（AVM 按此驱动 EndBurn/StartBurn/中止）。</summary>
    public enum MissileGuidanceAction
    {
        /// <summary>不点火（已达期望速度场 / 死区内）。</summary>
        Coast,
        /// <summary>点火（dv 为需求向量，物理按 ve·ṁ 缓释）。</summary>
        Burn,
        /// <summary>几何丢失：中止撞击回 Standby（K6/追丢语义，由调用方处置）。</summary>
        Abort
    }

    /// <summary>单步制导输出（dv + 诊断量 + 更新后的状态）。</summary>
    public struct MissileGuidanceStep
    {
        public DVector3 dv;
        public MissileGuidanceAction action;
        /// <summary>表面距（质心距 − 目标包围球半径，K4 口径）。</summary>
        public double surfaceDist;
        /// <summary>接近率（沿视线相对速度，&gt;0 接近）。</summary>
        public double closing;
        /// <summary>横向相对速度（⊥视线分量模长）。</summary>
        public double lateral;
        /// <summary>本步接近率补量（m/s，0 = 未补）。</summary>
        public double boostDemand;
        /// <summary>本步接近率需求上限（m/s，= min(cruiseClosing, 修正能力上限)）。</summary>
        public double closingDemand;
        /// <summary>正在远离（掉头再捕获中）。</summary>
        public bool reacquiring;
        /// <summary>更新后的跨步状态（调用方写回）。</summary>
        public MissileGuidanceState state;
    }

    /// <summary>
    /// 导弹终末制导纯数学核心（《导弹系统设计》§7 终末段，2026-09-08 抽纯）。
    /// 无记忆输入输出：每步只由 (弹位/弹速, 瞄准点/目标速度, 目标半径, 参数, 状态) 决定，
    /// 不碰 Verse/UnityEngine/Game——可在纯测试里用迷你飞行仿真迭代控制律，游戏内只做验收。
    ///
    /// 控制律（2026-09-08 弹群场景暴露"飞越滑脱"后重写，纯测试仿真定标）：
    ///   ① 横向相对速度按 lateralGain 归零（横向是制导误差主项）。
    ///   ② 接近率补量双门控：接近率不足（&lt;closingThreshold）**且**剩余时间够修横差
    ///      （tGo ≥ timeMargin·lateral/lateralAccel）——接近率上限随横差/距离自适应，不再"无脑补到
    ///      巡航值"把 tGo 压到修不完（"速度超目标仍加"的过冲根因）。横差大/距离近时需求被压到很低，
    ///      dv 方向自然退化为纯横向、主推全力修横差（独立"横差门"经 09-08 仿真证伪：强制 90°
    ///      姿态摆动反而多耗 RCS 修正时间）。
    ///   ③ 远离（closing&lt;0）= 掉头再捕获：允许沿视线反向补量（火炬 180° 掉头 ~9s 可行），
    ///      直到几何丢失判定（最近点 +lostMargin 被拉大）→ Abort。
    /// 收敛形态 = 横差归零 → 速度指向瞄准点 → 视线不再旋转 → 直线命中（无过冲）。
    /// </summary>
    public static class MissileGuidanceCore
    {
        /// <summary>
        /// 计算本物理步的制导输出。调用方负责：表面距 &gt; 接管边界时不调用（巡航段），
        /// Abort 时 EndBurn + 回 Standby + 上报，Burn 时 EndBurn→StartBurn(dv)，Coast 时 EndBurn。
        /// </summary>
        /// <param name="pos">导弹位置（世界 m）。</param>
        /// <param name="vel">导弹速度（世界 m/s）。</param>
        /// <param name="aimPoint">瞄准点（世界 m；含弹着偏置，随目标姿态换算）。</param>
        /// <param name="targetVel">目标速度（世界 m/s）。</param>
        /// <param name="targetRadius">目标包围球半径（m，表面距口径）。</param>
        /// <param name="p">制导参数。</param>
        /// <param name="stateIn">跨步状态（调用方持有）。</param>
        /// <param name="allowBoost">false = 禁止接近率补量（错峰点火时隙未到：只修横向、不冲刺）——
        /// 弹群解离后按队列深度错峰，保证到达间隔 ≥ 间距/巡航接近率（2026-09-08 三轮实测：各弹
        /// 收敛 closing 因对准 luck 不同（310 vs 476）→ 队列间隔在到达前坍缩 → 后弹爆炸杀死先到兄弟）。</param>
        public static MissileGuidanceStep ComputeStep(DVector3 pos, DVector3 vel,
            DVector3 aimPoint, DVector3 targetVel, double targetRadius,
            in MissileGuidanceParams p, in MissileGuidanceState stateIn, bool allowBoost = true)
        {
            var st = stateIn;
            var step = new MissileGuidanceStep { action = MissileGuidanceAction.Coast, state = st };

            DVector3 toTarget = aimPoint - pos;
            double dist = toTarget.magnitude;
            if (dist < 1e-9) return step; // 与瞄准点重合：交给物理碰撞结算
            DVector3 los = toTarget / dist;

            double surfaceDist = dist - targetRadius;
            step.surfaceDist = surfaceDist;
            if (!st.minSurfaceInitialized || surfaceDist < st.minSurfaceDist)
            {
                st.minSurfaceDist = surfaceDist;
                st.minSurfaceInitialized = true;
            }

            DVector3 vRel = vel - targetVel;
            double closing = DVector3.Dot(vRel, los);
            DVector3 vPerp = vRel - los * closing;
            double lateral = vPerp.magnitude;
            step.closing = closing;
            step.lateral = lateral;
            step.reacquiring = closing < 0.0;

            // ── 几何丢失判定（追丢中止，②）：最近点基准（对近距滑脱敏感）──
            if (closing < 0.0 && surfaceDist > st.minSurfaceDist + p.lostMarginMeters)
            {
                step.action = MissileGuidanceAction.Abort;
                step.state = st;
                return step;
            }

            // ── ① 横向归零（主项）──
            DVector3 dv = -vPerp * p.lateralGain;

            // ── ② 接近率补量：阈值 + 修正能力约束（横差速度归零时间 + 转向时间）──
            double closingDemand = p.cruiseClosing;
            if (p.lateralAccel > 1e-6)
            {
                // 修正耗时 = 转向（速度方向与视线夹角 / 转向率）+ 横差速度归零（lateral / 横向加速度）
                double tNull = lateral / p.lateralAccel;
                double tRot = 0.0;
                if (p.turnRateRad > 1e-6 && vel.magnitude > 1e-6)
                {
                    double cosA = DVector3.Dot(vel.normalized, los);
                    if (cosA > 1.0) cosA = 1.0;
                    if (cosA < -1.0) cosA = -1.0;
                    tRot = Math.Acos(cosA) / p.turnRateRad;
                }
                double tNeed = p.timeMargin * (tRot + tNull);
                if (tNeed > 1e-9)
                {
                    double bound = Math.Max(0.0, surfaceDist) / tNeed;
                    if (bound < closingDemand) closingDemand = bound;
                }
            }
            step.closingDemand = closingDemand;

            // 补量门控：远离（掉头再捕获）恒允许；接近时只在未达阈值时补（只加不减）；
            // 错峰时隙未到（allowBoost=false）→ 不补（横向修正照常，保持碰撞线）
            if (allowBoost && (closing < 0.0 || closing < p.closingThreshold))
            {
                double boost = closingDemand - closing;
                if (boost < 0.0) boost = 0.0;
                if (boost > p.boostMax) boost = p.boostMax;
                dv += los * boost;
                step.boostDemand = boost;
            }

            if (dv.magnitude > p.deadband)
                step.action = MissileGuidanceAction.Burn;
            step.dv = dv;
            step.state = st;
            return step;
        }
    }

    /// <summary>
    /// 弹群几何/弹头数学（纯）：发射深度队列间距与弹头致死带——两条不变量可单测：
    /// ① 队列间距 &gt; 弹头爆炸冲量作用半径（兄弟自伤/队形扰动防护，§5 2026-09-08 弹群仿真教训：
    ///    致死带 1km 之外仍有冲量可达 1.5km，后弹被侧推数百 m/s 打乱错峰到达）；
    /// ② 致死带公式与 3D AoE 线性衰减同口径（D0·(1−d/R) ≥ HP 的最大 d）。
    /// </summary>
    public static class MissileVolleyMath
    {
        /// <summary>
        /// 深度队列间距（m）= max(min(安全格距, 总长上限/(N−1)), 爆炸半径下限)；
        /// N ≤ 1 时返回安全格距（无兄弟）。与 CombatManager.CreateMissileVolley 同式（防两处口径漂移）。
        /// minSpacingMeters = 弹头爆炸冲量作用半径 + 裕量（后弹落在半径内会被前弹爆炸侧推数百 m/s）。
        /// </summary>
        public static double ComputeSpacing(double safetySpacingMeters, double arraySideLimitMeters, int count,
            double minSpacingMeters = 0.0)
        {
            double s = count <= 1
                ? safetySpacingMeters
                : Math.Min(safetySpacingMeters, arraySideLimitMeters / (count - 1));
            return s < minSpacingMeters ? minSpacingMeters : s;
        }

        /// <summary>
        /// 弹头致死半径（m）：3D AoE 线性衰减 D0·(1−d/R) ≥ partHp 的最大距离。
        /// D0 ≤ HP（打不死）或 R ≤ 0 → 0。
        /// </summary>
        public static double BlastLethalRadius(double blastRadius, double blastCenterDamage, double partHp)
        {
            if (blastRadius <= 0.0 || blastCenterDamage <= 0.0) return 0.0;
            double f = 1.0 - partHp / blastCenterDamage;
            return f > 0.0 ? blastRadius * f : 0.0;
        }
    }
}
