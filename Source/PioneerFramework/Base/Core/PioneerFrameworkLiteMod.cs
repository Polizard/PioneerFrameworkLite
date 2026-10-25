using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **先驱框架Lite 的模组设置**（2026-10-11 用户口径：「搞一个开关，让玩家自己决定要不要替换星空和太阳（默认开启）」）。
    ///
    /// **一个开关管两头，缺一不可**：
    /// · **开（默认）**：世界视图（含含图编队地图的背景）里的太阳/星空用本模组补画的那套（`WorldSunBillboard` / `WorldStarfield`，
    ///   材质/朝向/视大小照原版）；原版 `GlobalDrawLayer_Sun` / `GlobalDrawLayer_Stars` **停用**（见 `VanillaSkySuppression`）。
    /// · **关**：一切照原版（原版那两层照画；我们补画的太阳/星空不画）。
    ///
    /// 为什么必须成对：原版那两套画在**球心外 20 单位**、平时靠**行星壳**挡，而行星表面 shader 全是**单面**（`cull=Back`）
    /// ⇒ **镜头一进壳（船落地/低空时背景相机在舱底外侧）就整片露出** ⇒ 只"补画"不"停用"会出现**两个太阳**（2026-10-11 用户实测报回）；
    /// 只"停用"不"补画"则天上什么都没有。⇒ 两条腿只有一个开关。
    /// </summary>
    public partial class PioneerFrameworkLiteSettings : ModSettings
    {
        /// <summary>是否用本模组的太阳/星空**替换**原版（**默认开**）。</summary>
        public bool replaceWorldSky = true;
        public bool spaceAiRandomEncounters;

        /// <summary>
        /// 双击/聚焦编队时的**默认镜头模式**（跨局保存在 Mod 配置中）：
        /// `Free` / `ThirdPersonHull`（船体正向）/ `ThirdPersonVelocity`（速度正向）。
        /// </summary>
        internal PFViewGroupCameraMode pfviewFocusCameraMode = PFViewGroupCameraMode.ThirdPersonHull;

        /// <summary>
        /// **旧版键**（1.7 及以前）：true = 聚焦时默认第三人称（那时只有"船体正向"一种），false = 自由视角。
        /// 现在只用于迁移 —— 新键 `pfviewFocusCameraMode` 缺席时按它推断；保存时仍写回，方便降级。
        /// </summary>
        private bool legacyThirdPersonOnFocus = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref replaceWorldSky, "replaceWorldSky", true);
            Scribe_Values.Look(ref spaceAiRandomEncounters, "spaceAiRandomEncounters");

            // 降级兼容：旧键只表达「是否仍默认第三人称」（船体/速度正向都算 true）。
            if (Scribe.mode == LoadSaveMode.Saving)
                legacyThirdPersonOnFocus = pfviewFocusCameraMode != PFViewGroupCameraMode.Free;
            Scribe_Values.Look(ref legacyThirdPersonOnFocus, "pfviewThirdPersonOnFocus", true);

            // 旧键先读（缺省 true = 旧默认）；新键缺席（默认 -1）时按旧键迁移。
            int storedMode = Scribe.mode == LoadSaveMode.LoadingVars ? -1 : (int)pfviewFocusCameraMode;
            Scribe_Values.Look(ref storedMode, "pfviewFocusCameraMode", -1);
            pfviewFocusCameraMode = PFViewGroupCameraCore.MigrateMode(storedMode, legacyThirdPersonOnFocus);
            ExposePilotSettings();
            ExposeAssetSettings();
        }
    }

    /// <summary>
    /// 模组入口（RimWorld 按"程序集里唯一的 `Mod` 子类"自动认领）。本模组此前没有 `Mod` 类；
    /// 2026-10-11 为"要不要替换世界视图的太阳与星空"这个玩家开关而加。
    /// 设置读写走标准通道：`GetSettings&lt;T&gt;()` / `WriteSettings()` ⇒ 存在 RimWorld 的配置里（跨局保留）。
    /// </summary>
    public partial class PioneerFrameworkLiteMod : Mod
    {
        /// <summary>设置实例（`Mod` 构造时建立）。headless/未构造时为 null ⇒ 各处一律按"默认开"处理
        /// （见 `VanillaSkySuppression.ReplaceEnabled`：`?? true`）。</summary>
        public static PioneerFrameworkLiteSettings? Settings;

        public PioneerFrameworkLiteMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PioneerFrameworkLiteSettings>();
        }

        public override string SettingsCategory() => "先驱框架Lite";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Settings == null) return;

            var content = new Rect(0, 0, inRect.width - 20f, pilotSettingsHeight);
            Widgets.BeginScrollView(inRect, ref pilotSettingsScroll, content);
            var listing = new Listing_Standard();
            listing.Begin(content);
            // ⚠ 只用最小且确定的 API（`CheckboxLabeled(2 参)` / `Label`）——不确定的签名不猜（仓库纪律）
            listing.CheckboxLabeled("替换世界视图的太阳与星空（默认开）", ref Settings.replaceWorldSky);
            listing.CheckboxLabeled("PF_AI_RandomEncounters".Translate(), ref Settings.spaceAiRandomEncounters);
            listing.Gap(6f);
            listing.Label("开（默认）：世界视图与含图编队地图背景里的太阳/星空用本模组补画的那套；");
            listing.Label("原版那两套画在行星中心附近、只在镜头进入行星壳（船落地/低空）时才会露出来，已停用。");
            listing.Label("关：一切照原版（含「镜头进壳时会看到原版太阳/星空」这条）。");
            listing.Gap(18f);
            listing.Label("PFV_SettingsCamera".Translate());
            string mode = ("PFV_Camera_" + Settings.pfviewFocusCameraMode).Translate();
            if (Widgets.ButtonText(listing.GetRect(30f), "PFV_SettingsFocusMode".Translate(mode)))
            {
                var options = new System.Collections.Generic.List<FloatMenuOption>();
                foreach (PFViewGroupCameraMode value in System.Enum.GetValues<PFViewGroupCameraMode>())
                {
                    PFViewGroupCameraMode captured = value;
                    options.Add(new(("PFV_Camera_" + value).Translate(), () => Settings.pfviewFocusCameraMode = captured));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Label("PFV_SettingsFocusModeTip".Translate());
            DrawPilotSettings(listing);
            DrawAssetSettings(listing);
            pilotSettingsHeight = listing.CurHeight + 12f;
            listing.End();
            Widgets.EndScrollView();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            Settings?.NormalizeAssetSettings();
            Settings?.Write();
        }
    }
}
