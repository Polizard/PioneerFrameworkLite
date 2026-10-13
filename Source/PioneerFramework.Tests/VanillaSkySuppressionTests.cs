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
            Assert.True(VanillaSkySuppression.ShouldSuppress(typeof(GlobalDrawLayer_Sun)),
                "默认开 ⇒ 原版太阳层必须被跳过（否则低空/落地时会出现第二个太阳）");
            Assert.True(VanillaSkySuppression.ShouldSuppress(typeof(GlobalDrawLayer_Stars)),
                "默认开 ⇒ 原版星空层必须被跳过（同一处病根：进壳后原版 1500 颗星一起露出）");
        }

        [Fact]
        public void ReplaceOn_SuppressesOnlyThoseTwoLayers()
        {
            // 地形/云/发光/世界对象一律照原样画 —— 关错一个就是"星球没了/没有大气辉光"
            Assert.False(VanillaSkySuppression.ShouldSuppress(typeof(WorldDrawLayer_Terrain), true),
                "地形层绝不能被跳过");
            Assert.False(VanillaSkySuppression.ShouldSuppress(typeof(WorldDrawLayer_Clouds), true),
                "云层绝不能被跳过");
            Assert.False(VanillaSkySuppression.ShouldSuppress(typeof(WorldDrawLayer_Glow), true),
                "大气辉光层绝不能被跳过");
            Assert.False(VanillaSkySuppression.ShouldSuppress(typeof(WorldDrawLayer_WorldObjects), true),
                "世界对象层绝不能被跳过");
        }

        // ── 关（玩家选择照原版）──────────────────────────────────

        [Fact]
        public void ReplaceOff_KeepsVanillaSky()
        {
            // 关 ⇒ 原版两层照画（我们补画的那两个 `Draw()` 也据此早退，见 WorldSkyBillboard/WorldStarfield）
            Assert.False(VanillaSkySuppression.ShouldSuppress(typeof(GlobalDrawLayer_Sun), false),
                "关 ⇒ 原版太阳层必须照画");
            Assert.False(VanillaSkySuppression.ShouldSuppress(typeof(GlobalDrawLayer_Stars), false),
                "关 ⇒ 原版星空层必须照画");
        }

        [Fact]
        public void NullType_IsSafe()
        {
            Assert.False(VanillaSkySuppression.ShouldSuppress(null, true));
            Assert.False(VanillaSkySuppression.ShouldSuppress(null, false));
            Assert.False(VanillaSkySuppression.ShouldSuppress(null));
        }
    }
}
