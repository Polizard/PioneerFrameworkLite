using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// 飞船部件组件基类：纯数据 + 低频事件回调。
    /// 序列化双通道：IExposable（存档 Scribe）+ Write/Read（模板 .part 二进制）。
    /// TypeId 为注册表键（默认取类名）；组件类须在静态构造器调用 ComponentRegistry.Register。
    /// </summary>
    public abstract class PartComponentData : IExposable
    {
        /// <summary>注册表键。默认取类名；重名组件可覆写为自定义 id。</summary>
        public virtual string TypeId => GetType().Name;

        /// <summary>
        /// **部件角色**（§7 统一失效语义）：失效收口只认角色（`PartRoleTable`）。
        /// 注册进 `ComponentRegistry` 的组件**必须**声明（`ComponentRegistry.ValidateRoles` 启动期检查）；
        /// 未注册的测试探针可留 `None`。
        /// </summary>
        public virtual PartRole Role => PartRole.None;

        // ── 低频事件回调（不进 tick）──
        public virtual void OnAttached() { }
        /// <summary>组件随 part 被移除/替换时调用（对称于 <see cref="OnAttached"/>；不参与 tick）。</summary>
        public virtual void OnDetached() { }
        public virtual void OnDamaged(float damage) { }
        public virtual void OnDestroyed() { }
        /// <summary>**维修**回调（2026-10-21，机制缺口 R1）：与 <see cref="OnDamaged"/> 对称。
        /// 只在"真的修上了血"时调用一次，参数 = 实际回涨量（已夹在 `MaxHp` 内）。</summary>
        public virtual void OnRepaired(float amount) { }
        public virtual void PostLoad() { }

        /// <summary>
        /// 聚合进 ShipStats。每个组件贡献自重与产出/消耗。
        ///
        /// **`efficiency`（0–1，§11.4）** = 本件当前运转效率（`SpacePart.Efficiency`）：组件自己决定
        /// **哪些贡献乘效率**——推力/发电/散热/传感/干扰/挂点功耗乘；**质量绝不乘**（结构质量不因失修减少）。
        /// 未磨损时效率恰为 `1.0f` ⇒ `贡献 × 1.0` 逐位不变（确定性红线）。
        /// ⚠ 仍然是"对 ref 累加"：**不许改求和顺序**、不许在这里读别的件（§3 分桶只许在规则侧）。
        /// </summary>
        public virtual void AccumulateStats(ref ShipStats s, float efficiency) { }

        /// <summary>
        /// **目录预置参数解释器**（§4，2026-10-22）：`ShipPartDef.presets` 里每条 `key/value` 交到这里，
        /// 组件自己解析（`float.Parse` / 枚举 / 字符串），不认识的 key 返回 `false`（装配器报红，不静默）。
        /// 返回 `true` = 已消费。默认不认任何键（"本组件没有预置参数"）。
        /// </summary>
        public virtual bool ApplyPreset(string key, string value) => false;

        /// <summary>预置参数解析公共件：**固定 InvariantCulture**（XML 里的数值永远是 `1.5` 形态，
        /// 不能跟着玩家系统区域走——德语区的小数点是逗号，跟着走会把 1.5 解析成 15 或直接失败）。</summary>
        protected static bool TryParseFloat(string value, out float result)
            => float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result);

        /// <summary>预置参数解析公共件：整数（弹药量等）。</summary>
        protected static bool TryParseInt(string value, out int result)
            => int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out result);

        /// <summary>
        /// 深拷贝（模板 → 运行时）。默认走 Write/Read 二进制通道；
        /// 子类有非二进制字段时可覆写。
        /// </summary>
        public virtual PartComponentData? Clone()
        {
            var dst = ComponentRegistry.Create(TypeId);
            if (dst == null) return null;
            using (var ms = new MemoryStream())
            {
                // leaveOpen: BinaryWriter/Reader 不得关闭底层流
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                {
                    Write(w);
                }
                ms.Position = 0;
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
                {
                    dst.Read(r);
                }
            }
            return dst;
        }

        // ── 存档序列化（SpacePart.ExposeData → Scribe_Deep）──
        public abstract void ExposeData();

        // ── 模板文件二进制（.part v4+）──
        public abstract void Write(BinaryWriter w);
        public abstract void Read(BinaryReader r);
    }
}
