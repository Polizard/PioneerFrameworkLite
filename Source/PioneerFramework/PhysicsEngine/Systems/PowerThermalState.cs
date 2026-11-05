// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// **电/热闭环状态**（2026-10-31 类重组批 3b：从 `SpaceEntityGroup` 的散字段收成一个物理概念）。
    ///
    /// 分工：规则本体是纯函数 <see cref="PowerThermalCore" />；本类型只持有**状态**——
    /// 电池存量、四通道欠压、超温、推进供电门、上一步读数与输入快照。
    ///
    /// **全字段都是值类型**（含 `PowerThermalCore.Inputs` 这个纯值输入），
    /// ⇒ 预演副本的 `MemberwiseClone` 天然深拷贝，不需要额外 Clone 机制。
    /// 若将来有人往这里加引用类型字段，`ConceptStateCopySafetyTests` 会红。
    /// </summary>
    public struct PowerThermalState
    {
        /// <summary>**电池当前存量**（J，持久化）。容量 = `AggregatedStats.powerStorage`（Σ 组件容量）。
        /// 放在群级而不是逐件：存量每步都变，逐件存就要每步扫组件（§0-1）。</summary>
        public float StoredEnergyJ;

        /// <summary>**电闭环总系数**（0–1；1 = 全供，0 = 完全饿死）。消费端只乘这个数（§5）。</summary>
        public float Brownout;
        public float BrownoutPropulsion;
        public float BrownoutShield;
        public float BrownoutWeapon;
        public float BrownoutSensor;

        /// <summary>**热闭环系数**（0–1；1 = 不超温）。默认公式见 `PowerThermalCore.OverheatFactor`。</summary>
        public float Overheat;

        /// <summary>上一步读数（W，不持久化；调试/验收读它）。</summary>
        public float PowerSupplyW, PowerDemandW, PowerUsedW;

        /// <summary>本群是否有任何电/热输入（重算时定）：false ⇒ 每步**跳过**规则（老船零成本路径）。</summary>
        internal bool HasPowerOrHeat;

        /// <summary>上一步的电/热输入快照（供 `CanSupplyPropulsionNow` 在**点火前**问"够不够"）。</summary>
        internal PowerThermalCore.Inputs lastPowerInputs;

        /// <summary>本步**推进通道是否被全额供给**（欠电 ⇒ false ⇒ 推力门关闭）。
        /// 由 `StepPowerThermal` 每步写；并行区只读。</summary>
        public bool PropulsionPowered;

        /// <summary>最近一次点火被**欠电**拒绝（读数，不持久化）。用户 2026-10-26 裁定：欠电 ⇒ 拒绝点火。</summary>
        public bool BurnIgnitionBlockedByPower;

        /// <summary>最近一次燃烧因**欠电**被熄火（读数，不持久化；与"工质尽"同口径）。</summary>
        public bool BurnEndedByPower;

        /// <summary>与旧字段声明时的默认值完全一致：全供 / 不超温 / 推进有电。</summary>
        public static PowerThermalState CreateDefault() => new PowerThermalState
        {
            Brownout = 1f,
            BrownoutPropulsion = 1f,
            BrownoutShield = 1f,
            BrownoutWeapon = 1f,
            BrownoutSensor = 1f,
            Overheat = 1f,
            PropulsionPowered = true
        };
    }
}
