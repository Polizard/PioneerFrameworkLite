using System;
using System.Reflection;
using LudeonTK;
using PioneerFramework.Combat;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 「PFL 测试」页签（2026-09-10 规范化）：把原先挂在调试建筑 ThingComp 上的全部测试入口
    /// 收进**游戏自带 Debug 菜单**（dev mode 顶部页签）。
    ///
    /// 扩展方式 = 官方扩展点：<c>DebugTabMenuDef.menuClass</c> 指向本类（Def：<c>PF_DebugTab</c>），
    /// **不需要 Harmony 补丁**。写法照官方 <c>DebugTabMenu_Settings</c>：
    ///   · 普通动作 = <c>Action</c> 类型节点；
    ///   · 复选框 = <c>Action</c> 类型节点 + <c>settingsField</c>（框架只据此渲染成勾选框，
    ///     **切换动作由本类提供**——所以能一次联动多个字段，如"详细战斗日志"同时设/清射弹探针间隔）。
    ///   · 动态文案用 <c>labelGetter</c>（如"运行中…"、开关状态）。
    /// 场景实现全在 <see cref="DebugShipSpawner"/>（静态、自建地图，不依赖建筑）。
    /// </summary>
    public class DebugTabMenu_PFL : DebugTabMenu
    {
        public DebugTabMenu_PFL(DebugTabMenuDef def, Dialog_Debug dialog, DebugActionNode root)
            : base(def, dialog, root)
        {
        }

        public override DebugActionNode InitActions(DebugActionNode absRoot)
        {
            myRoot = new DebugActionNode("PFL 测试", DebugActionType.Action, null, null);
            absRoot.AddChild(myRoot);

            // ═══ 验收（性能优化 9 条 + 中低优先批）═══
            AddAction("一键验收", "00 验收", 100, PerfAcceptanceRunner.Run,
                () => PerfAcceptanceRunner.Running
                    ? "一键验收（运行中…）"
                    : "一键验收（引力自检电池 + 7 个场景 → 硬指标/值判据/覆盖度结论）");
            AddAction("中止验收（立即打印结论与计数）", "00 验收", 90, PerfAcceptanceRunner.Abort, null);
            AddCheckbox("性能优化验收探针（影子校验 + 周期汇总）", "00 验收", 80, typeof(PerfProbe), "Enabled");
            AddAction("自检进度窗口（实时：阶段进度 + 引力读数 + 失败清单）", "00 验收", 70,
                Window_PFLSelfTest.OpenIfClosed, null);

            // ═══ 场景 · 引力场（A0 回归；判据见 PerfProbe 汇总行 ㉑㉒）═══
            AddAction("引力场自检电池（即时：数据路径 + 场数学 + A1 参数）", "05 场景 · 引力场(A0)", 110,
                GravityFieldSelfTest.Run, null);
            AddAction("月球引力回归（月球附近非圆轨群组）", "05 场景 · 引力场(A0)", 100,
                DebugShipSpawner.SpawnMoonGravityTest, null);
            AddAction("漏靶弹头（A2：段内插值飞行 + 引力弯曲）", "05 场景 · 引力场(A0)", 90,
                DebugShipSpawner.SpawnMissedWarheadTest, null);

            // ═══ 场景 · 轨道 / 空间站 ═══
            AddAction("部署空间站", "10 场景 · 轨道/空间站", 100, DebugShipSpawner.SpawnTestStation, null);
            AddAction("双引擎测试（核热 + 离子，网络独立选择）", "10 场景 · 轨道/空间站", 90, DebugShipSpawner.SpawnDualEngineTest, null);
            AddAction("架构验证测试（burn/储罐差值/合冲量）", "10 场景 · 轨道/空间站", 80, BurnArchitectureTest.Run, null);

            // ═══ 场景 · 炮塔（动能）═══
            AddAction("对射", "20 场景 · 炮塔（动能）", 100, DebugShipSpawner.SpawnTurretTest, null);
            AddAction("全方位六方位", "20 场景 · 炮塔（动能）", 90, DebugShipSpawner.SpawnTurretOmniTest, null);
            AddAction("精确炮击", "20 场景 · 炮塔（动能）", 80, DebugShipSpawner.SpawnTurretPrecisionTest, null);

            // ═══ 场景 · 炮塔（定向能）═══
            AddAction("对射", "30 场景 · 炮塔（定向能）", 100, DebugShipSpawner.SpawnRayTurretTest, null);
            AddAction("遮挡验证", "30 场景 · 炮塔（定向能）", 90, DebugShipSpawner.SpawnRayOcclusionTest, null);
            AddAction("旋转命中", "30 场景 · 炮塔（定向能）", 80, DebugShipSpawner.SpawnRayRotatedTest, null);
            AddAction("多目标齐射", "30 场景 · 炮塔（定向能）", 70, DebugShipSpawner.SpawnRayMultiTest, null);

            // ═══ 场景 · 无 Map 实体 / 姿态 ═══
            AddAction("无 Map 碰撞测试（扫掠膨胀盒）", "40 场景 · 无Map/姿态", 100, DebugShipSpawner.SpawnCollisionTest, null);
            AddAction("主推轴向 + RCS 燃烧", "40 场景 · 无Map/姿态", 90, DebugShipSpawner.SpawnAxialThrustTest, null);
            AddAction("操舵测试场景", "40 场景 · 无Map/姿态", 80, DebugShipSpawner.SpawnSteeringTest, null);

            // ═══ 场景 · 导弹（自主武器平台）═══
            AddAction("发射测试（20km，Following 待命）", "50 场景 · 导弹", 100, DebugShipSpawner.SpawnMissileLaunchTest, null);
            AddAction("撞击测试（1.5km，单发）", "50 场景 · 导弹", 95, DebugShipSpawner.SpawnMissileImpactTest, null);
            AddAction("巡航→终末", "50 场景 · 导弹", 90, DebugShipSpawner.SpawnMissileCruiseTest, null);
            AddAction("拦截对撞（反导 3 波）", "50 场景 · 导弹", 85, DebugShipSpawner.SpawnMissileInterceptTest, null);
            AddAction("弹群齐射→解离（N=8）", "50 场景 · 导弹", 80, DebugShipSpawner.SpawnVolleyImpactTest, null);
            AddAction("弹群合并期被撞", "50 场景 · 导弹", 75, DebugShipSpawner.SpawnVolleyMergedHitTest, null);
            AddAction("长距制导（60km）", "50 场景 · 导弹", 70, DebugShipSpawner.SpawnMissileLongRangeTest, null);
            AddAction("销毁来袭弹（测中止）", "50 场景 · 导弹", 65, DebugShipSpawner.DestroyIncomingMissiles, null);
            AddAction("导弹控制面板", "50 场景 · 导弹", 60, () => Find.WindowStack.Add(new Window_MissileControl()), null);

            // ═══ 场景 · 跳帮 / 发射面板 ═══
            AddAction("单兵 EVA。跳帮测试", "60 场景 · 跳帮", 100, DebugShipSpawner.SpawnBoardingTest, null);
            AddAction("跳帮测试面板", "60 场景 · 跳帮", 90, () => Find.WindowStack.Add(new Window_BoardingTest()), null);
            AddAction("发射测试面板", "70 场景 · 发射", 100, () => Find.WindowStack.Add(new Window_LaunchTest()), null);

            // ═══ 场景 · 星座（组织层）═══
            AddAction("星座自检（API 全链）", "90 场景 · 星座", 100, ConstellationSelfTest.Run, null);

            // ═══ 调试日志开关（直接绑定现有 static 字段）═══
            AddCheckbox("详细战斗日志（含射弹探针 30t）", "80 调试日志", 100, typeof(CombatManager), "DebugDetailedCombat",
                afterToggle: () => CombatManager.DebugProjectileProbeInterval = CombatManager.DebugDetailedCombat ? 30 : 0);
            AddCheckbox("详细碰撞日志（扫掠盒/枚举）", "80 调试日志", 95, typeof(CombatManager), "DebugDetailedCollision");
            AddCheckbox("详细操舵日志（[Steer]/[Align]）", "80 调试日志", 90, typeof(SpaceGameComp), "DebugDetailedSteering");
            AddCheckbox("详细推进日志（无Map主推/RCS [Thrust]）", "80 调试日志", 85, typeof(SpaceGameComp), "DebugDetailedThrust");
            AddCheckbox("详细导弹制导日志", "80 调试日志", 80, typeof(AutonomousVehicleManager), "DebugDetailedGuidance");
            AddCheckbox("详细跳帮日志", "80 调试日志", 75, typeof(BoardingManager), "DebugDetailedBoarding");
            AddCheckbox("详细发射日志", "80 调试日志", 70, typeof(RocketLaunchController), "DebugDetailedLaunch");
            AddCheckbox("详细星座日志（入编/收尸/锚点）", "80 调试日志", 60, typeof(ConstellationGameComp), "DebugDetailedConstellation");
            AddAction("射弹飞行探针（30 tick / 关）", "80 调试日志", 65, ToggleProjectileProbe,
                () => CombatManager.DebugProjectileProbeInterval > 0
                    ? "射弹飞行探针：开（" + CombatManager.DebugProjectileProbeInterval + "t）"
                    : "射弹飞行探针：关");

            return myRoot;
        }

        private static void ToggleProjectileProbe()
        {
            CombatManager.DebugProjectileProbeInterval = CombatManager.DebugProjectileProbeInterval > 0 ? 0 : 30;
        }

        private DebugActionNode AddAction(string label, string category, int priority, Action action, Func<string>? labelGetter)
        {
            var node = new DebugActionNode(label, DebugActionType.Action, action, null);
            node.category = category;
            node.displayPriority = priority;
            if (labelGetter != null) node.labelGetter = labelGetter;
            myRoot.AddChild(node);
            return node;
        }

        /// <summary>复选框节点。settingsField 只影响渲染（框架画成勾选框），切换动作由本类给出，
        /// 因此 afterToggle 可做联动（如同时设/清射弹探针间隔）。</summary>
        private DebugActionNode? AddCheckbox(string label, string category, int priority, Type type, string fieldName, Action? afterToggle = null)
        {
            var fi = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null || fi.FieldType != typeof(bool))
            {
                Log.Error(string.Format("[PFL] 调试页签：{0}.{1} 不是可用的 static bool 字段，节点「{2}」已跳过",
                    type.Name, fieldName, label));
                return null;
            }

            var node = new DebugActionNode(label, DebugActionType.Action, delegate
            {
                bool now = (bool)fi.GetValue(null);
                fi.SetValue(null, !now);
                afterToggle?.Invoke();
            }, null);
            node.category = category;
            node.displayPriority = priority;
            node.settingsField = fi;
            myRoot.AddChild(node);
            return node;
        }
    }
}
