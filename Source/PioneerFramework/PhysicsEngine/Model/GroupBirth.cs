namespace PioneerFramework
{
    /// <summary>
    /// **组的出生种类**（2026-10-31 后端裁定；契约见 `DesignNotes/交接-20261031-出生协议定案.md`）。
    ///
    /// ## 为什么需要它
    /// "出生"有两种本质不同的情形，混在一起写就会出现两类 bug：
    /// · 同体切分（抛级/载荷释放）——子体状态是母体**已提交态**的恒等式切分，可以当帧入世界；
    /// · 从运动平台发射（导弹/弹丸）——子体出生在物理区间中间，母体当前状态还没提交，
    ///   直接起算 = 一帧瞬移 + 速度双计，必须等母体提交后由"局部系偏移 + 相对速度"反推。
    ///
    /// 种类由**出生点**声明，不由调用位置决定 —— 这样"抛级不该等一拍"和"弹丸必须先等"是同一张表的两行，
    /// 而不是散在各个 if 里。远端的 `AnchorGroupBirth` / `PrepareProjectileBirth` 是这套规则的
    /// 两份手工实现（规则对、但代码重复且策略写在调用点）⇒ 合并后收进这一个入口。
    /// </summary>
    public enum GroupBirthKind
    {
        /// <summary>
        /// 新建：发射建组、空间站、调试场景、AI 遭遇。状态自持（出生时刻即已提交态）。
        /// </summary>
        Instantiated = 0,

        /// <summary>
        /// 同体切分：抛级、载荷舱释放。子体状态由 <c>source</c> 的**已提交态**切出
        /// （质量/位置/速度是切分恒等式）⇒ 当帧入世界、当帧可积分、当帧可上屏。
        /// </summary>
        Continuation = 1,

        /// <summary>
        /// 从运动平台发射：导弹、弹丸。出生位姿只能由母体"局部系偏移 + 相对速度"在**母体状态提交后**反推；
        /// 对账完成前不得进入世界视图（并应在合并后实现为"先入 pendingBirths，对账后才进 groups"）。
        /// </summary>
        Emitted = 2,
    }

    /// <summary>
    /// 出生策略表（纯函数、无 Verse/Unity 依赖 ⇒ 被 `<c>PioneerFramework.Pure.Tests</c>` 链入并钉住）。
    /// 判定只有四处要问：要不要母体 / 要不要对账 / 当帧能不能入世界 / 种类是否合法。
    /// </summary>
    internal static class GroupBirthPolicy
    {
        /// <summary>是否是已定义的出生种类（未知枚举值一律拒绝，不做"默认当新建"的兜底）。</summary>
        internal static bool IsDefined(GroupBirthKind kind)
            => kind is GroupBirthKind.Instantiated or GroupBirthKind.Continuation or GroupBirthKind.Emitted;

        /// <summary>是否必须提供母体 <c>source</c>（切分与发射都必须能指出状态出处）。</summary>
        internal static bool RequiresSource(GroupBirthKind kind)
            => kind is GroupBirthKind.Continuation or GroupBirthKind.Emitted;

        /// <summary>是否需要"出生对账"（等母体状态提交后再反推位姿）。</summary>
        internal static bool RequiresReconciliation(GroupBirthKind kind)
            => kind == GroupBirthKind.Emitted;

        /// <summary>出生当帧是否就能进入世界（<c>groups</c> / 碰撞 / 传感器 / 渲染都看得见）。</summary>
        internal static bool EntersWorldAtBirth(GroupBirthKind kind)
            => IsDefined(kind) && !RequiresReconciliation(kind);

        /// <summary>日志用的一行描述；未知值也给出可读文本（便于报错时不吞掉原始数值）。</summary>
        internal static string Describe(GroupBirthKind kind) => kind switch
        {
            GroupBirthKind.Instantiated => "Instantiated(新建/自持态)",
            GroupBirthKind.Continuation => "Continuation(同体切分)",
            GroupBirthKind.Emitted => "Emitted(运动平台发射/需对账)",
            _ => $"Unknown({(int)kind})",
        };
    }
}
