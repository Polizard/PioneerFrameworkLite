using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **构建身份自证**（2026-09-17 首版 → 2026-09-23 提为无条件）。
    ///
    /// ## 为什么必须有
    /// 本 mod 的"交付物路径只在 Release 生效"，而开发期每次重建都会覆盖它
    /// ⇒ **"日志里这些读数来自哪一版代码"**这个问题历史上反复浪费排查时间（前两次"探针找不到"的根因都是旧 DLL）。
    /// 把**内容身份**打进日志，任何一份日志都能自证版本，不用靠时间戳猜。
    ///
    /// ## 为什么从 `CelestialBodyManager` 搬到这里（2026-09-23，游戏内实测逼出来的）
    /// 首版把它放在 `CelestialBodyManager.EnsureInitialized()` 里，而那个方法是**懒调用**的
    /// （由各访问器触发）⇒ 在一局**没有任何编队**的太空场景里，物理步不会查任何天体 ⇒ **这一行根本不打** ✗。
    /// 实测：一份 489 行的日志里 `[步耗时]`/`[帧耗时]` 都在打（说明场景在跑），却**既没有构建自证、
    /// 也没有天体管理器的初始化行** —— 于是那份日志**无法自证版本**，只能去翻 `Player-prev.log`。
    ///
    /// ⇒ 本库纪律"**读数必须无条件**、不许用沉默冒充没问题"：版本自证必须是**加载即打**，
    /// 不能挂在"某个组件恰好被访问"上。故用 `[StaticConstructorOnStartup]` 在 mod 加载时无条件打一次。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class BuildIdentity
    {
        static BuildIdentity()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var loc = string.IsNullOrEmpty(asm.Location) ? null : asm.Location;
                string built = "?";
                if (loc != null && System.IO.File.Exists(loc))
                    built = System.IO.File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
                Log.Message(string.Format(
                    // ⚠ 2026-09-28 更正标签：这里原打"交会新律={2}"，而那套自制远场律已于 2026-09-22 作废
                    //   （远场段 + 5% CW 终端整段删除）。该开关现在 gate 的是"阶梯引导 vs 旧巡航 Lambert 路径"
                    //   ⇒ 标签照实写"阶梯"，否则日志会让人误判成"旧律还在跑"——正是本项目最忌讳的标签与内容矛盾。
                    "[PFL] 构建自证：MVID={0}｜DLL 构建时间={1}｜交会引导=阶梯({2})",
                    asm.ManifestModule.ModuleVersionId, built,
                    SpaceGameComp.UseNewRendezvousLaw ? "开" : "关（回退旧巡航 Lambert 路径）"));
            }
            catch (System.Exception e)
            {
                Log.Warning("[PFL] 构建自证失败（不影响运行）：" + e.Message);
            }
        }
    }
}
