namespace PioneerFramework
{
    /// <summary>
    /// 一份计划的**置信度**（《P-1适配层决策》§4.3）。
    ///
    /// **它存在的理由**：MJ 的 `Operation` 没有"输入不确定度"这个概念——它的入参是当真值用的 `Orbit`。
    /// 而我们的规划器**必须**能用认知对象（`Contact`：过期 + 有 σ）做决策。
    /// 若签名里看不出来，"一个用过期+低保真接触算出来的 Δv"会和真值算出来的长得一模一样——
    /// 那正是本项目反复咬人的"静默失效"形态。
    ///
    /// ⇒ 判据（P-1 §4.3，**P1 执行**）：**σ 递增 ⇒ 计划要么变保守、要么报不可行，不得纹丝不动。**
    ///
    /// ⚠ **这里不发明绝对常数**：`value` 的两个尺度由**调用方**给（`lengthScale` 通常是目标轨道半径、
    /// `timeScale` 通常是控制周期或计划视界）。上游的常数是按 KSP 的代价函数调的，
    /// 我们的不该由一个拍脑袋的魔法数决定（§12.2-b）。
    /// </summary>
    public readonly struct PlanConfidence
    {
        /// <summary>位置不确定度 (m)。0 = 精确已知。</summary>
        public readonly double positionSigma;

        /// <summary>数据年龄 (s)。"这份计划是不是建立在昨天的情报上"。</summary>
        public readonly double dataAgeSeconds;

        /// <summary>
        /// 单调不增的标量，∈ (0, 1]。`1 / (1 + σ/L + age/T)`。
        /// **单调性本身是要被测的**（P-1 判据 A5），不是"看起来像"。
        /// </summary>
        public readonly double value;

        public PlanConfidence(double positionSigma, double dataAgeSeconds, double value)
        {
            this.positionSigma = positionSigma;
            this.dataAgeSeconds = dataAgeSeconds;
            this.value = value;
        }

        /// <summary>
        /// 由 (σ, 年龄, 长度尺度, 时间尺度) 构造。<paramref name="lengthScale" /> / <paramref name="timeScale" />
        /// 必须 &gt; 0 且由调用方提供——**不设默认值**，因为"σ 多大算大"是场景相关的，藏一个默认值就是藏一个难度调节器。
        /// </summary>
        public static PlanConfidence From(double positionSigma, double dataAgeSeconds, double lengthScale, double timeScale)
        {
            double v = 1.0 / (1.0 + positionSigma / lengthScale + dataAgeSeconds / timeScale);
            return new PlanConfidence(positionSigma, dataAgeSeconds, v);
        }

        /// <summary>目标过期/不可解算时的置信度：`value` 为 0，且 `IsUsable` 为 false。</summary>
        public static readonly PlanConfidence Unusable = new PlanConfidence(double.PositiveInfinity, double.PositiveInfinity, 0.0);

        /// <summary>精确已知：σ = 0、年龄 = 0 ⇒ `value` = 1。</summary>
        public static readonly PlanConfidence Exact = new PlanConfidence(0.0, 0.0, 1.0);

        /// <summary>计划是否还建立在可用情报上。</summary>
        public bool IsUsable => value > 0.0;

        public override string ToString() =>
            IsUsable ? $"PlanConfidence(σ={positionSigma:F1} m, age={dataAgeSeconds:F1} s, value={value:F4})" : "PlanConfidence(不可用)";
    }

    /// <summary>
    /// 规划目标的**唯一允许进入规划器的接口**（《P-1适配层决策》§3.3）。
    ///
    /// 它**刻意不暴露"真值"这个概念**：调用方拿不到"目标真实在哪"，只能拿到"我认为它在哪、我有多确定"。
    /// 这样"决策不读真值"就不是一句口号，而是**签名层面**的事实。
    ///
    /// ⚠ **实现方必须携带中心天体假设**（`Contact` 没有中心天体字段，而两体问题必须先知道绕谁转）。
    /// 规划器内部**不许猜**——猜错就是把两体问题解成了另一个两体问题，且没有任何东西会报警。
    /// </summary>
    public interface ITargetView
    {
        /// <summary>
        /// 目标在 <paramref name="ut" /> 时刻的轨道视图。
        /// 返回 <c>false</c> = 给不出（接触过期 / 无解算中心天体）——
        /// **调用方必须当成"计划不可做"，不得回退到真值。**
        /// </summary>
        bool TryOrbit(double ut, out OrbitView orbit);

        /// <summary>位置不确定度 (m)。0 = 已知精确。</summary>
        double PositionSigma { get; }

        /// <summary>数据年龄 (s)。</summary>
        double DataAgeSeconds { get; }

        /// <summary>
        /// 身份：真接触的 group id；假接触给 <c>-1</c>。**刻意暴露**——
        /// 假目标起作用的方式就是"被选中"，所以规划器**不得**替玩家把假目标筛掉。
        /// </summary>
        int IdentityGroupId { get; }

        /// <summary>身份：假接触的 decoyId；真接触给 <c>-1</c>。</summary>
        int IdentityDecoyId { get; }

        /// <summary>视在半径 (m)。制导与瞄准需要。</summary>
        double ApparentRadius { get; }

        /// <summary>本目标的置信度（见 <see cref="PlanConfidence" />）。</summary>
        PlanConfidence Confidence { get; }
    }

    /// <summary>
    /// 精确已知的目标——**没有不确定度**。用于：调试场景、台架、以及"给自己排机动的辅助工具"。
    ///
    /// ⚠ 它**不是** `ContactTargetView`。用一个 `OrbitView` 直接充作目标，在认知层的语义里等于
    /// "你恰好精确知道对手的轨道"——**只允许在当真值为真的场景里用**（测试、单人调试）。
    /// </summary>
    public readonly struct FixedTargetView : ITargetView
    {
        private readonly OrbitView _orbit;

        /// <summary>调试/台架用的固定目标。
        /// ⚠ 批6 S3-3b 判定：`identityGroupId = -1` / `apparentRadius = 0.0` 是**语义值**（"不是某个 group 的接触"/
        /// "不知道视在半径"），与 `ITargetView` 的 `-1` 约定一致 ⇒ 不是 A5 哨兵。
        /// 另：`ApparentRadius`（以及 `IdentityGroupId`）**目前只有测试消费者** —— 它们是 EW/瞄准层等着用的
        /// **契约成员**，不是死代码（连写点也在测试里）；接上之后不要加"筛掉假目标"那种过滤。</summary>
        public FixedTargetView(OrbitView orbit, int identityGroupId = -1, double apparentRadius = 0.0)
        {
            _orbit = orbit;
            IdentityGroupId = identityGroupId;
            IdentityDecoyId = -1;
            ApparentRadius = apparentRadius;
            Confidence = PlanConfidence.Exact;
        }

        public bool TryOrbit(double ut, out OrbitView orbit)
        {
            orbit = _orbit;
            return true;
        }

        public double PositionSigma => 0.0;
        public double DataAgeSeconds => 0.0;
        public int IdentityGroupId { get; }
        public int IdentityDecoyId { get; }
        public double ApparentRadius { get; }
        public PlanConfidence Confidence { get; }
    }
}
