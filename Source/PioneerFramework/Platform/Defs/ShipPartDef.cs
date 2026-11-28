using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **普适零件目录**（2026-10-17，用户裁定："不能定义专门的火箭零件，而是应该定义普适的 part"）。
    ///
    /// 这是**所有船/站/实体**共用的零件定义，不是火箭专用：
    /// · 类别（`category`）只是**词表**（thruster / tank / stage / hull / cabin / sensor / weapon / misc…），
    ///   新增类别不需要新 Def 类型；
    /// · 几何（`bounds` / `stackLengthMeters`）+ 质量（`massKg`）是任何 part 都需要的；
    /// · 推进元数据（ve / ṁ / 容量）是**可选**的——推进器/储罐填，其他件留 0/空。
    ///
    /// 刻意**不是 ThingDef**：目录条目只被装配器读来派生 `SpacePart`，不需要建造/贴图/生成/进建造菜单。
    /// 运行时的功能（引擎/储罐/武器）仍由组件与实体/地图级聚合承载（M2 甲），不把功能语义塞进 Def。
    ///
    /// 2026-10-22 部件功能化 §4：新增**通用** `presets` 表 —— 新角色（反应堆/散热器/货舱/武器挂点）
    /// 的参数走它，**不再往这个 Def 上加专用字段**（加角色 = 改 XML，不改 C#）。
    /// </summary>
    public class ShipPartDef : Def
    {
        /// <summary>零件类别（词表，不是类型）：thruster / tank / stage / hull / cabin / sensor / weapon / reactor / radiator / cargo / misc…</summary>
        public string category = "misc";

        /// <summary>总装一件所需的实物材料。省略时采用组装器的兼容报价。</summary>
        public List<ThingDefCountClass>? assemblyCost;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (assemblyCost == null) yield break;
            if (assemblyCost.Count == 0) yield return "assemblyCost must contain at least one material.";
            foreach (var cost in assemblyCost)
            {
                if (!IsValidAssemblyMaterial(cost))
                    yield return "assemblyCost requires a droppable item, a positive count and no stuff override.";
            }
        }

        internal static bool IsValidAssemblyMaterial(ThingDefCountClass? cost)
            => cost?.thingDef is { category: ThingCategory.Item, stackLimit: > 0, destroyOnDrop: false }
                && !string.IsNullOrEmpty(cost.thingDef.defName) && cost.count > 0 && cost.stuff == null;

        /// <summary>结构质量（kg）。≤0 ⇒ 装配器按类别用兜底值。</summary>
        public float massKg = 100f;

        public float boundsX = 2f;
        public float boundsY = 2f;
        public float boundsZ = 2f;

        /// <summary>沿堆叠主轴的安装长度（m）。0 ⇒ 用 `boundsY`。</summary>
        public float stackLengthMeters;

        /// <summary>B3：形状预设（Box/Cylinder/Sphere）；只影响惯量/渲染/OBB，不影响质量口径。</summary>
        public InertiaTensor.Shape shape = InertiaTensor.Shape.Box;

        // ── §11.1 装甲（2026-10-22）：只影响伤害计算，**不参与质量/几何/命中** ──
        /// <summary>装甲材料（Def 引用；null = 无装甲）。XML 写 `<armorMaterial>PF_Armor_Steel</armorMaterial>`。</summary>
        public ArmorMaterialDef? armorMaterial;

        /// <summary>装甲厚度（mm，0 = 无装甲）。实例可在装配后按件覆写。</summary>
        public float armorThicknessMm;

        // ── 可选推进元数据（0/空 = 不适用于本零件）──
        public float exhaustVelocity;
        public float massFlowRate;
        public bool isVectoredRcs;
        public string propellantDefName = "";
        public float tankCapacityKg;

        /// <summary>**装配时的初始装料**（kg，0 = 空罐，保持旧行为）。2026-10-27 为 RCS 小罐加：
        /// 不来这一下，目录件的 `massKg` 是 0 ⇒ `PartPropulsionBuilder` 合成出的池也是 0（点不着）。</summary>
        public float tankInitialKg;

        /// <summary>**点火时的标称功耗**（W，§5 电闭环"推进"通道；0 = 不用电）。
        /// 用户 2026-10-26 裁定：欠电时**拒绝点火**（不是按比例降推力）⇒ 这个字段决定推力门会不会关。
        /// ⚠ 默认 0 是刻意的：`LaunchService` 的默认设计用调试推进器、没带反应堆，填 0 才不会让既有发射全部点不着。</summary>
        public float powerDrawWatts;

        /// <summary>**运转时的标称产热**（W，§1.7 热通道；0 = 不产热）。用户 2026-10-26 选 A：
        /// 显式目录字段（不是"推力×系数"推导），与 `powerDrawWatts` 对称；装配器写进**同一个**
        /// `EngineComponentData`（不再靠 presets 补，避免同件双引擎组件）。</summary>
        public float heatProductionWatts;

        /// <summary>**被动散热能力**（W，2026-10-27 用户令：补上引擎的散热）。装配器在 thruster 件上
        /// 挂一个 `RadiatorComponentData`（`dissipationWatts`）——统计口径与散热器完全相同，
        /// 损毁时随件出局；不需要给引擎组件再加一份平行字段。</summary>
        public float heatDissipationWatts;

        /// <summary>
        /// **目录预置组件集**（§4，2026-10-22）：每条 = 一个组件 + 参数键值，由 `ApplyPreset` 解释。
        /// 装配顺序 = 书写顺序（追加在"结构质量 + 类别组件"之后）⇒ 聚合的求和顺序确定。
        /// 未写本元素 = 空表（旧目录条目零变化）。
        /// </summary>
        public List<PartComponentPreset> presets = new();
    }
}
