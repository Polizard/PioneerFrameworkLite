using LudeonTK;
using RimWorld;
using Verse;

namespace PioneerFramework.AutoTest
{
    /// <summary>
    /// 部件自动测试的 Debug 菜单入口。
    /// 用法：开发者模式（~）→ Debug Actions → "Pioneer Framework" 类别。
    /// </summary>
    public static class ShipPartsAutoTestDebugActions
    {
        private const string Category = "Pioneer Framework";

        [DebugAction(Category, "部件: 运行自动测试",
            allowedGameStates = AllowedGameStates.Playing,
            displayPriority = 100)]
        public static void RunAutoTest()
        {
            ShipPartsAutoTest.RunAllAndLog();
        }

        [DebugAction(Category, "部件: 切换自动测试开关",
            allowedGameStates = AllowedGameStates.Playing,
            displayPriority = 99)]
        public static void ToggleAutoTest()
        {
            ShipPartsAutoTest.Enabled = !ShipPartsAutoTest.Enabled;
            Log.Message("[ShipPartsAutoTest] 自动测试已" + (ShipPartsAutoTest.Enabled ? "启用" : "禁用"));
        }

        [DebugAction(Category, "部件: 查询自动测试状态",
            allowedGameStates = AllowedGameStates.Playing,
            displayPriority = 98)]
        public static void QueryAutoTestState()
        {
            Log.Message("[ShipPartsAutoTest] 当前状态: " + (ShipPartsAutoTest.Enabled ? "启用" : "禁用"));
        }
    }
}
