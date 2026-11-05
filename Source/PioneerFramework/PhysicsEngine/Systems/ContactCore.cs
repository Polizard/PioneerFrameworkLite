using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// 认知对象的来源。设计稿 §5.1 字段 5。
    /// **关键**：认知层的输入有三个来源，其中两个不经过物理层——
    /// `Public`/`OwnSensor`/`PassiveES` 来自物理层（受限制的感知），`FactionShare` 来自同阵营的其它认知层
    /// （横向边 B2），`Decoy` 来自敌方决策层的注入。后两者**推不出来**，所以必须落盘（实施计划 §1.5）。
    /// </summary>
    public enum ContactSource : byte
    {
        /// <summary>公开投影：不匿踪组的自报坐标（应答机语义 A2）。精确但**未经自舰测量验证**。</summary>
        Public = 0,
        /// <summary>自舰探测（主动/被动雷达）。阵营内保真度最高的一类。</summary>
        OwnSensor = 1,
        /// <summary>被动侦听 / 测向（ES）：通常只有方位，位置不确定度由 `positionSigma` 表达。</summary>
        PassiveES = 2,
        /// <summary>同阵营共享的情报（B2 全共享）。保真度随来源观测者的 `positionSigma` 而定。</summary>
        FactionShare = 3,
        /// <summary>假接触（欺骗注入）。**没有真值对应物** ⇒ `targetGroupId` 必须为 -1。</summary>
        Decoy = 4,
    }

    /// <summary>
    /// 认知对象（设计稿 §5.1 的"接触"+ 本次下沉补的两字段）。
    /// 它是**每阵营一份的认知**：有身份、有位置、有速度、有精度，且**可以没有真值对应物**——
    /// 这正是"假目标不进 `groups`"的根本原因（认知对象不是实在物），也是落点判据"新东西是认知对象还是实在物"。
    ///
    /// 身份是它自己的一部分，**不从实在物继承**：真接触带 `targetGroupId`，假接触带 `decoyId`，二者互斥。
    /// 由此得到一条必须记住的后果：**"命中一个认知对象"与"摧毁一个实在物"是两回事**。
    ///
    /// 【边界】纯数据。本文件不做感知、不做融合决策之外的任何编排、不读写 group。
    /// </summary>
    public struct Contact
    {
        /// <summary>真值 group id；假接触为 -1。（设计稿字段 1）</summary>
        public int targetGroupId;
        /// <summary>假接触的合成 id；真接触为 -1。与 `targetGroupId` 互斥（见 `IsConsistent`）。（[N1]）</summary>
        public int decoyId;
        /// <summary>位置（世界系，m）。（设计稿字段 2）</summary>
        public DVector3 position;
        /// <summary>速度（世界系，m/s）。**必须带**——制导核心直接吃它做提前量。（设计稿字段 2）</summary>
        public DVector3 velocity;
        /// <summary>最后更新时刻（**physicsTick 轴 = 模拟秒**；2026-10-01 用户裁定）：过期判定与记忆窗口的基准。
        /// ⚠ **单位必须是模拟秒**：1 个游戏 tick = 多少模拟秒**随倍数档变**（Normal 1:1 / Fast ×8 = 8 s /
        /// Combat 1/60 = 1/60 s）⇒ 记 tick 计数会在高倍档下把"10 秒记忆"压成 1.25 秒（U-12 同族的双钟病）。</summary>
        public double lastUpdatePhysics;
        /// <summary>位置不确定度（m）：压制干扰的作用对象；制导精度直接吃它。（设计稿字段 4）</summary>
        public double positionSigma;
        /// <summary>来源（自舰 / 共享 / 被动侦听 / 公开）。（设计稿字段 5）</summary>
        public ContactSource source;
        /// <summary>方位精度（rad）：给 ES 留的口，否则只有距离判据、没有"大致在哪个方向"。（设计稿字段 6）</summary>
        public double bearingSigmaRad;
        /// <summary>辐射状态：**ARM 与 home-on-jam 的共同靶子筛选条件**——一个字段换来两种武器。（设计稿字段 7）</summary>
        public bool isRadiationSource;
        /// <summary>真伪标记：仅"真/假"一态，**无识别等级**（设计稿 §0.2 第 3 条）。由观测者粒度结算（[E5] 两趟流程）。</summary>
        public bool isDecoy;
        /// <summary>目标视在半径（m）：制导与瞄准需要。（[N1] 补的第 9 字段——设计稿八字段不含它，
        /// 而 `MissileGuidanceCore.ComputeStep` 的签名要 `targetRadius`；假接触填 Def 常量。）</summary>
        public float apparentRadius;

        /// <summary>不变式：真/假身份字段互斥。任何构造与注入路径都必须满足它。</summary>
        public bool IsConsistent => (targetGroupId >= 0) != (decoyId >= 0);

        /// <summary>本接触是否为假接触（按身份字段判断，不读 `isDecoy`——后者是"观测者是否识破"）。</summary>
        public bool IsForged => decoyId >= 0;

        /// <summary>过期判定：距最后更新超过 <paramref name="memorySeconds"/> **模拟秒**即失效（记忆的本质是信息缺失）。
        /// 时间量一律走 `physicsTick` 轴（见 <see cref="lastUpdatePhysics"/>）。
        /// 窗口由调用方给（**显式**口径）；带默认兜底的三态判据见 <see cref="ContactMemory.PhaseOf" />——
        /// 两者共用同一条"现在 − 最后更新"的龄口径（<see cref="ContactMemory.AgeSeconds" />）。</summary>
        public bool IsStaleAt(double nowPhysics, double memorySeconds)
            => ContactMemory.AgeSeconds(nowPhysics, lastUpdatePhysics) > memorySeconds;
    }

    /// <summary>
    /// 点亮/接触的**生命期相位**（2026-10-02 用户裁定"识破就贴一根有时间限制的标签"的形态；
    /// 设计稿 §5.1 的"记忆"）。**三态，而不是布尔**：
    /// · **Live（新鲜）**：本步判据成立、刚刷新过 ⇒ 位置/速度按实测更新；
    /// · **Shadow（影子）**：判据不再成立、但记忆窗口未满 ⇒ 用最后已知状态外推（σ 随时间涨），
    ///   **仍是可打击/可制导的目标**（"影子可被打击"是裁定的正文，也是压制式干扰的作用对象）；
    /// · **Lost（失联）**：连记忆也过期 ⇒ 从视图里消失（假目标/影子不再可用）。
    /// 三态都只由 `(现在, 最后更新, 窗口)` 决定 ⇒ **读时算、零扫描**（[M4]），且天然确定性。
    /// </summary>
    public enum TrackPhase : byte
    {
        /// <summary>本步刚刷新（判据成立）。</summary>
        Live = 0,
        /// <summary>判据已断、记忆未过期（影子期）。</summary>
        Shadow = 1,
        /// <summary>记忆也过期 ⇒ 失联。</summary>
        Lost = 2,
    }

    /// <summary>
    /// **记忆窗口（T）与影子期 σ 的纯核心**。唯一算式：状态与 σ 都只由 `(现在, 最后更新, T)` 决定。
    ///
    /// ⚠ **窗口的产权 = 观测者**（2026-10-02 用户裁定 ②）：取值来自 `SensorTech.memorySeconds`
    /// （部件组件 / 建筑 Def → 投影链）。用户将来把"信息处理速度"改成非无穷时，改的就是那一格——
    /// **别在感知代码里另写 T 的常数**；未设（≤0 / NaN）时用 <see cref="DetectionCore.DefaultMemorySeconds" />。
    /// </summary>
    public static class ContactMemory
    {
        /// <summary>影子期位置不确定度的增长速率（m/s）。**待定标**（与 `ContactFusion.SigmaRefMeters` 同族）。</summary>
        public const double SigmaGrowthMetersPerSecond = 100.0;

        /// <summary>σ 上限（m）：防止影子期退化成"完全随机"。**待定标**。</summary>
        public const double SigmaCapMeters = 5.0e5;

        /// <summary>接触龄（物理秒）：**唯一的**"现在 − 最后更新"口径。</summary>
        public static double AgeSeconds(double nowPhysics, double lastUpdatePhysics)
            => nowPhysics - lastUpdatePhysics;

        /// <summary>记忆窗口（带默认兜底）。</summary>
        public static double EffectiveMemorySeconds(double memorySeconds)
            => memorySeconds > 0.0 && !double.IsNaN(memorySeconds) ? memorySeconds : DetectionCore.DefaultMemorySeconds;

        /// <summary>
        /// 三态。**快进慢出**（用户裁定的去抖机制）：本步刷新 ⇒ 立即 Live；丢掉刷新后**留满整个 T** 才 Lost。
        /// 边界口径：`now == lastUpdate`（同刻）判 Live；**恰好等于窗口**（`age == T`）判 Shadow，超出才 Lost（严格大于）。
        /// </summary>
        public static TrackPhase PhaseOf(double nowPhysics, double lastUpdatePhysics, double memorySeconds)
        {
            double age = AgeSeconds(nowPhysics, lastUpdatePhysics);
            if (!(age > 0.0)) return TrackPhase.Live;                      // 本步刚刷新（含防御性的"未来时刻"）
            return age > EffectiveMemorySeconds(memorySeconds) ? TrackPhase.Lost : TrackPhase.Shadow;
        }

        /// <summary>
        /// 影子期 σ：`σ(t) = min(σ_cap, σ_刷新时 + g·Δt)`（刷新当刻取刷新值）。
        /// 既是物理正确的（预测误差随时间增长），也是"记忆不免费、干扰有持久价值"的来源——
        /// 拿一条旧接触去制导，精度按时间劣化。
        /// </summary>
        public static double SigmaAt(double nowPhysics, double lastUpdatePhysics, double sigmaAtRefresh)
        {
            double s0 = sigmaAtRefresh > 0.0 && !double.IsNaN(sigmaAtRefresh) ? sigmaAtRefresh : 0.0;
            double age = AgeSeconds(nowPhysics, lastUpdatePhysics);
            if (!(age > 0.0)) return s0;
            double s = s0 + SigmaGrowthMetersPerSecond * age;
            return s < SigmaCapMeters ? s : SigmaCapMeters;
        }

        /// <summary>还能不能拿它做事（**影子算能**、失联算不能）——索敌/制导的准入判据。</summary>
        public static bool IsUsable(TrackPhase phase) => phase != TrackPhase.Lost;

        /// <summary>
        /// 影子期的**外推位置**：`p + v·Δt`（Δt = 龄）——给**制导/瞄准**用（它要的是"目标大概在哪"）。
        /// ⚠ **不要在接触上就地外推**：画面上的接触点必须停在**最后已知位置**（配上涨大的 σ），
        /// 两者分开。物理含义 = "假定它没机动"；真机动了就打偏 —— 这正是 σ 增长要表达的不确定性。
        /// </summary>
        public static DVector3 CoastedPosition(in Contact c, double nowPhysics)
        {
            double age = AgeSeconds(nowPhysics, c.lastUpdatePhysics);
            if (!(age > 0.0)) return c.position;
            return c.position + c.velocity * age;
        }
    }

    /// <summary>
    /// 融合（设计稿 §7.1【定】：各自观测，阵营内共享情报时**留下更真的那一个**——
    /// 真实情报融合不会用好报告被坏报告覆盖）。
    ///
    /// 免疫是**局部的**（§7.2 推论一）："取更真"只在**确实持有该接触的观测者之间**成立。
    /// 本文件只提供保真度与替换判据两个纯函数；"谁持有"的结算属组件编排（[E5] 两趟流程）。
    /// </summary>
    public static class ContactFusion
    {
        /// <summary>保真度减半的不确定度尺度（m）。待定标。</summary>
        public const double SigmaRefMeters = 5000.0;

        /// <summary>
        /// 来源等级（越大越可信）。
        /// `Public` 与 `FactionShare` **并列**：前者是目标自报、后者是盟友实测，都是"非自舰测量"，
        /// 靠 `positionSigma` 分胜负。若日后"公开坐标造假"成为机制，把 `Public` 降到 `PassiveES` 之下即可
        /// （单点改动，勿散布到别处）。
        /// </summary>
        public static int SourceRank(ContactSource source)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 => source switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 {
            ContactSource.OwnSensor => 4,
            ContactSource.Public => 3,
            ContactSource.FactionShare => 3,
            ContactSource.PassiveES => 2,
            ContactSource.Decoy => 1,
            _ => 0,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 };

        /// <summary>保真度 = 来源等级 ÷（1 + σ/σ_ref）。σ 越小越真，且**同来源下才比 σ**。</summary>
        public static double Fidelity(in Contact c)
        {
            double sigma = c.positionSigma > 0.0 && !double.IsNaN(c.positionSigma) ? c.positionSigma : 0.0;
            return SourceRank(c.source) / (1.0 + sigma / SigmaRefMeters);
        }

        /// <summary>
        /// 是否用新接触替换既有接触。**严格大于**才替换——相等不替换，避免两份等保真度报告
        /// 反复互相覆盖（抖动），也避免"共享来的接触每次都刷新 `lastUpdatePhysics`"把记忆变成永久全知。
        /// </summary>
        public static bool ShouldReplace(in Contact existing, in Contact incoming)
            => Fidelity(incoming) > Fidelity(existing);
    }


    /// <summary>
    /// **目标列表（玩家向）的标注口径**（[V3] 收尾 · B1 交互装门，2026-10-11）——"相位 → 词"的**唯一一处**。
    /// 消费者：定位机「指定目标」/「精确炮击」· 发射器「指定目标」/「精确打击」· 无人机控制窗撞击目标 · 雷达「跟踪目标」。
    ///
    /// 规则：**只有匿踪组的影子期**才标 —— 标「影子」是在告诉玩家"这是**最后已知位置**，不是当前位置"。
    /// 公开组（应答机自报、无相位可言）与 Live（本步刚刷新）都不标（标了是噪音）；Lost 根本不会进列表
    /// （`SensorManager.PlayerCanSee` 已挡掉）。
    /// ⚠ 准入判据**不在本文件**：列表统一走 `SensorManager.PlayerCanSee`（与视图/开火同一条，别在这里另开一条）。
    /// </summary>
    public static class TargetListCore
    {
        /// <summary>影子期后缀（其余相位/公开组 ⇒ 空串）。</summary>
        public static string PhaseSuffix(bool isStealth, TrackPhase phase)
            => isStealth && phase == TrackPhase.Shadow ? " 影子（最后已知）" : "";
    }


    // ⚠ [D13] 2026-09-16：反向索引（VisibleIndex）已取消——三个用途全部有更便宜的正向做法：
    //   开火揭示 = 事件触发"对开火者重判定一次"（同一份探测代码）；EW 通知 = 查自己阵营的视图；
    //   ARM 靶子 = 对视图正向筛 isRadiationSource。取消后同时消掉"读档重建"整类坑与一个泄漏面。
    //   裁定与理由见实施计划 §1.8 [E] / §11 D13。
}
