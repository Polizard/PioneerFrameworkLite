using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 2026-11-21：一级助推器回收/重建线已整体退役（用户裁定「回收线删掉」）。
    /// 但**类型必须保留**：`1.6` 冻结树的 Def XML 仍引用 `CompProperties_ReturnedBooster`
    /// （`1.6/Defs/Things/GroundAssembly/PF_Buildings_Basic.xml`），而 `1.6` 的 XML/DLL 均已冻结、
    /// 不许改动；`tools/validate_config.py` 会拿它对照当前 `Source/` 找类型。
    /// 因此这里提供一个**惰性替身**：类名/compClass 与原版一致，但不再有任何回收或重建行为。
    /// </summary>
    public class CompProperties_ReturnedBooster : CompProperties
    {
        public CompProperties_ReturnedBooster()
        {
            compClass = typeof(CompReturnedBooster);
        }
    }

    /// <summary>已退役的返回助推器 Comp（惰性替身，见 <see cref="CompProperties_ReturnedBooster"/>）。</summary>
    public class CompReturnedBooster : ThingComp
    {
        public override string CompInspectStringExtra()
        {
            return "回收/重建线已退役。";
        }
    }
}
