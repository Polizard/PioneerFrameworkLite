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
        /// <summary>最后更新时刻（物理 tick）：过期判定与记忆窗口的基准。（设计稿字段 3）</summary>
        public int lastUpdateTick;
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

        /// <summary>过期判定：距最后更新超过 <paramref name="memoryTicks"/> 即失效（记忆的本质是信息缺失）。</summary>
        public bool IsStaleAt(int nowTick, int memoryTicks) => nowTick - lastUpdateTick > memoryTicks;
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
        {
            switch (source)
            {
                case ContactSource.OwnSensor: return 4;
                case ContactSource.Public: return 3;
                case ContactSource.FactionShare: return 3;
                case ContactSource.PassiveES: return 2;
                case ContactSource.Decoy: return 1;
                default: return 0;
            }
        }

        /// <summary>保真度 = 来源等级 ÷（1 + σ/σ_ref）。σ 越小越真，且**同来源下才比 σ**。</summary>
        public static double Fidelity(in Contact c)
        {
            double sigma = c.positionSigma > 0.0 && !double.IsNaN(c.positionSigma) ? c.positionSigma : 0.0;
            return SourceRank(c.source) / (1.0 + sigma / SigmaRefMeters);
        }

        /// <summary>
        /// 是否用新接触替换既有接触。**严格大于**才替换——相等不替换，避免两份等保真度报告
        /// 反复互相覆盖（抖动），也避免"共享来的接触每次都刷新 `lastUpdateTick`"把记忆变成永久全知。
        /// </summary>
        public static bool ShouldReplace(in Contact existing, in Contact incoming)
            => Fidelity(incoming) > Fidelity(existing);
    }


    // ⚠ [D13] 2026-09-16：反向索引（VisibleIndex）已取消——三个用途全部有更便宜的正向做法：
    //   开火揭示 = 事件触发"对开火者重判定一次"（同一份探测代码）；EW 通知 = 查自己阵营的视图；
    //   ARM 靶子 = 对视图正向筛 isRadiationSource。取消后同时消掉"读档重建"整类坑与一个泄漏面。
    //   裁定与理由见实施计划 §1.8 [E] / §11 D13。
}
