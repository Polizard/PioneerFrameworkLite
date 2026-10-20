namespace PioneerFramework
{
    /// <summary>会话级仪表显示选择；只决定可见性，不改变宿主操作状态。</summary>
    internal enum PFViewPanelMode { Automatic, Expanded, Collapsed }

    internal readonly record struct PFViewUiVisibility(bool ShowNavball, bool ShowRadar, bool ShowSteeringStrip);

    internal static class PFViewUiPolicy
    {
        public static bool CanShowEntity(bool active, bool own, bool stealth, bool known)
            => active && known && (own || !stealth);

        public static PFViewUiVisibility Resolve(bool steeringActive, bool weaponsActive,
            PFViewPanelMode navballMode, PFViewPanelMode radarMode)
        {
            bool navball = Visible(navballMode, steeringActive);
            return new(navball, Visible(radarMode, weaponsActive), steeringActive && !navball);
        }

        private static bool Visible(PFViewPanelMode mode, bool automatic)
            => mode == PFViewPanelMode.Expanded || (mode == PFViewPanelMode.Automatic && automatic);
    }
}
