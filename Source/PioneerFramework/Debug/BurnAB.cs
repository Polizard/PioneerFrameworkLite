namespace PioneerFramework
{
    /// <summary>
    /// **对照验收的开关**（2026-09-19）：全部默认 false ⇒ **生产行为逐位不变**。
    /// 用途：一轮游戏内同时跑 5 条对照船（见 `BurnABAcceptanceRunner`），让"残差（0.25% 幅值 + −0.23° 滚转）"
    /// 自己指出是哪一段造成的 —— 而不是再一轮只测一个数。
    /// </summary>
    public static class BurnAB
    {
        /// <summary>冻结燃烧方向（关掉"按当前 RTN 重取"的律）。</summary>
        public static bool FreezeDirection;

        /// <summary>用全油门（关掉"命令恒定加速度"的油门政策）。</summary>
        public static bool FullThrottle;

        public static void Reset() { FreezeDirection = false; FullThrottle = false; }
    }
}
