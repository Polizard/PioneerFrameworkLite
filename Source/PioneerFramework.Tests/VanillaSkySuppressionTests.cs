using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **「要不要替换世界视图的太阳与星空」这个玩家开关的钉子**（2026-10-11 用户口径：开关 + **默认开启**）。
    ///
    /// 开关必须**成对**生效：开 = 我们补画 + 停用原版那两层；关 = 原版照旧 + 我们不画。
    /// 背景：原版太阳/星空画在**球心外 20 单位**、靠行星壳挡；行星表面 shader 全是**单面**（`cull = Back`）
    /// ⇒ 镜头一进壳（船落地/低空时背景相机在舱底外侧）原版天空就整片露出来 ⇒ 只补画不停用 =「两个太阳」。
    ///
    /// ⚠ 2026-10-15 反射清理 A-14 改写：旧实现 = "按类型跳过 `Render()`"的 Harmony 补丁（判据 `ShouldSuppress`）；
    /// 新实现 = **把两个原版层的 def 类型换成我们的子类**（`PF_GlobalDrawLayer_Sun/Stars`，其 `Visible`
    /// 按开关动态决定画不画）+ 我们补画的那层走 def（`PF_WorldDrawLayer_SkyBillboards`）。
    /// ⇒ 判据跟着改写成：① 只换这两层（其余返回 null）；② 开关两条腿（`VanillaLayersVisible`）。
    /// </summary>
    public class VanillaSkySuppressionTests
    {
        // ── 开（默认）────────────────────────────────────────────

        [Fact]
        public void Default_IsReplaceOn()
        {
            // headless 里没有设置实例（`Mod` 未被构造）⇒ 一律按默认"开"：替换生效、原版停用
            Assert.True(VanillaSkySuppression.ReplaceEnabled,
                "默认必须是「替换」（未构造设置实例时也算开）");
            Assert.Equal(typeof(PF_GlobalDrawLayer_Sun),
                VanillaSkySuppression.ReplacementLayerTypeFor(typeof(GlobalDrawLayer_Sun)));
            Assert.Equal(typeof(PF_GlobalDrawLayer_Stars),
                VanillaSkySuppression.ReplacementLayerTypeFor(typeof(GlobalDrawLayer_Stars)));
        }

        [Fact]
        public void OnlyThoseTwoLayersAreRewritten()
        {
            // 地形/云/发光/世界对象一律不动 —— 换错一个就是"星球没了/没有大气辉光"
            Assert.Null(VanillaSkySuppression.ReplacementLayerTypeFor(typeof(WorldDrawLayer_Terrain)));
            Assert.Null(VanillaSkySuppression.ReplacementLayerTypeFor(typeof(WorldDrawLayer_Clouds)));
            Assert.Null(VanillaSkySuppression.ReplacementLayerTypeFor(typeof(WorldDrawLayer_Glow)));
            Assert.Null(VanillaSkySuppression.ReplacementLayerTypeFor(typeof(WorldDrawLayer_WorldObjects)));
            Assert.Null(VanillaSkySuppression.ReplacementLayerTypeFor(null));
        }

        // ── 关（玩家选择照原版）──────────────────────────────────

        [Fact]
        public void ReplaceOff_KeepsVanillaSky()
        {
            // 关 ⇒ 原版两层照画（子类 `Visible` 读的就是这条；我们补画那层也据此早退，见 WorldSunBillboard/WorldStarfield）
            Assert.True(VanillaSkySuppression.VanillaLayersVisible(false),
                "关 ⇒ 原版太阳/星空层必须照画");
            Assert.False(VanillaSkySuppression.VanillaLayersVisible(true),
                "开 ⇒ 原版那两层必须不画（否则低空/落地时会出现第二个太阳）");
        }
    }
}
