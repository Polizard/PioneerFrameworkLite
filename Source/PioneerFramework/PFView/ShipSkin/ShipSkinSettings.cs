namespace PioneerFramework.ShipSkin
{
    /// <summary>
    /// 舰船蒙皮总开关（2026-10-19，用户裁定：**只加表面层、不改形体**；极简硬表面）。
    /// 调试窗可 A/B：关掉即回到改动前的纯色盒体/三色地图浮雕。
    /// </summary>
    public static class ShipSkinSettings
    {
        /// <summary>是否启用舰船蒙皮（Shader 取不到时自动回退旧材质，本开关保持开也无害）。</summary>
        public static bool Enabled = true;
    }
}
