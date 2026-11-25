using System.Collections.Generic;
using System.Linq;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **火箭组装器第一步**（2026-10-28）：侧视图几何（纯核）+ 工时口径 + 设计增删。
    /// 只测不依赖 `DefDatabase`/`Thing` 的部分 —— 目录查询一律注入，窗口排版不进测试。
    /// </summary>
    public class RocketAssemblerTests
    {
        private static ShipPartDef Engine(string name = "E", float mass = 500f, float stack = 3f, float width = 2f,
            float ve = 3000f, float flow = 200f)
            => new() { defName = name, label = name, category = "thruster", massKg = mass,
                       boundsX = width, boundsY = stack, stackLengthMeters = stack,
                       exhaustVelocity = ve, massFlowRate = flow };

        private static ShipPartDef Tank(string name = "T", float mass = 2000f, float cap = 20000f,
            float stack = 10f, float width = 3f)
            => new() { defName = name, label = name, category = "tank", massKg = mass,
                       tankCapacityKg = cap, tankInitialKg = cap,
                       boundsX = width, boundsY = stack, stackLengthMeters = stack };

        /// <summary>控制核心件（2026-10-28：纯零件火箭必须有它才能点火）。</summary>
        private static ShipPartDef Control(string name = "C")
            => new() { defName = name, label = name, category = "control", massKg = 100f,
                       boundsX = 1f, boundsY = 2f, stackLengthMeters = 2f };

        private static RocketDesignSpec Spec(params (string def, string cat, int qty)[] rows)        {
            var spec = new RocketDesignSpec();
            foreach (var (def, cat, qty) in rows)
                spec.parts.Add(new DesignPartSpec { defName = def, category = cat, quantity = qty });
            return spec;
        }

        // ── 侧视图几何 ──────────────────────────────────────────

        [Fact]
        public void Schematic_StacksTopToBottom_AndSumsHeight()
        {
            var spec = Spec(("E", "thruster", 2), ("T", "tank", 1));
            var map = new Dictionary<string, ShipPartDef> { ["E"] = Engine(stack: 3f), ["T"] = Tank(stack: 10f) };

            var s = RocketSchematicBuilder.Build(spec, n => map.TryGetValue(n, out var d) ? d : null);

            Assert.Equal(3, s.pieces.Count);                 // quantity 已展开：2 引擎 + 1 罐
            Assert.Equal(16f, s.totalHeightM, 3);            // 3 + 3 + 10
            Assert.Equal(3f, s.maxWidthM, 3);
            Assert.Equal(2, s.engineCount);
            Assert.Equal(1, s.tankCount);
            Assert.Equal(SchematicKind.Engine, s.pieces[0].kind);   // spec 顺序 = 自上而下
            Assert.Equal(SchematicKind.Tank, s.pieces[2].kind);
        }

        [Fact]
        public void Schematic_UnknownDef_FallsBackToPlaceholder()
        {
            var spec = Spec(("NOPE", "hull", 1));

            var s = RocketSchematicBuilder.Build(spec, _ => null);

            Assert.True(s.hasPlaceholder);
            var p = Assert.Single(s.pieces);
            Assert.Equal(RocketSchematicBuilder.PlaceholderSideM, p.heightM, 3);
            Assert.Equal(RocketSchematicBuilder.PlaceholderSideM, p.widthM, 3);
            Assert.Equal("NOPE", p.label);                   // 目录查不到时用 defName 顶
            Assert.Equal(SchematicKind.Other, p.kind);
        }

        [Fact]
        public void Schematic_Masses_MatchDefsTimesQuantity()
        {
            var spec = Spec(("E", "thruster", 9), ("T", "tank", 1));
            var map = new Dictionary<string, ShipPartDef>
            {
                ["E"] = Engine(mass: 500f),
                ["T"] = Tank(mass: 2000f, cap: 20000f),
            };

            var s = RocketSchematicBuilder.Build(spec, n => map.TryGetValue(n, out var d) ? d : null);

            Assert.Equal(9 * 500f + 2000f, s.dryMassKg, 3);
            Assert.Equal(20000f, s.propellantKg, 3);         // 只有罐带推进剂
            Assert.Equal(9 * 500f + 2000f + 20000f, s.TotalMassKg, 3);
        }

        [Fact]
        public void Schematic_EmptyDesign_IsZeroNotThrow()
        {
            var s = RocketSchematicBuilder.Build(new RocketDesignSpec(), _ => null);
            Assert.Empty(s.pieces);
            Assert.Equal(0f, s.totalHeightM, 3);
            Assert.False(s.hasPlaceholder);
        }

        // ── 工时口径 ────────────────────────────────────────────

        [Fact]
        public void Work_IsPerPartMassAndCapacity()
        {
            var spec = Spec(("E", "thruster", 2), ("T", "tank", 1));
            var map = new Dictionary<string, ShipPartDef> { ["E"] = Engine(mass: 500f), ["T"] = Tank(mass: 2000f, cap: 20000f) };

            // E: 40 + 500/100 = 45 ×2 = 90；T: 40 + 2000/100 + 20000/1000 = 80
            float work = CompRocketAssembler.WorkFor(spec, n => map.TryGetValue(n, out var d) ? d : null);
            Assert.Equal(170f, work, 3);
        }

        [Fact]
        public void Work_IsClampedToRange()
        {
            var tiny = Spec(("E", "thruster", 1));
            Assert.Equal(60f, CompRocketAssembler.WorkFor(tiny, n => new ShipPartDef
            { defName = n, category = "thruster", massKg = 0f }), 3);

            var huge = Spec(("T", "tank", 1000));
            Assert.Equal(12000f, CompRocketAssembler.WorkFor(huge, n => new ShipPartDef
            { defName = n, category = "tank", massKg = 20000f, tankCapacityKg = 400000f }), 3);

            Assert.Equal(0f, CompRocketAssembler.WorkFor(new RocketDesignSpec(), _ => null), 3);
        }

        // ── 设计增删 / 状态机（不碰 Thing）─────────────────────────

        [Fact]
        public void AddPart_MergesSameDef_AndEntersDesigning()
        {
            var comp = new CompRocketAssembler();
            ShipPartDef def = Engine();

            Assert.True(comp.AddPart(def, 2));
            Assert.Equal(AssemblerState.Designing, comp.State);
            Assert.Single(comp.Design.parts);

            Assert.True(comp.AddPart(def, 3));
            Assert.Single(comp.Design.parts);                 // 合并，不新增行
            Assert.Equal(5, comp.Design.parts[0].quantity);
            Assert.Equal("thruster", comp.Design.parts[0].category);
        }

        [Fact]
        public void RemovePart_DropsRowAtZero_AndReturnsToIdle()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine(), 2);

            Assert.True(comp.RemovePartAt(0, 1));
            Assert.Equal(1, comp.Design.parts[0].quantity);

            Assert.True(comp.RemovePartAt(0, 5));
            Assert.Empty(comp.Design.parts);
            Assert.Equal(AssemblerState.Idle, comp.State);
        }

        [Fact]
        public void StartBuild_RequiresEngineAndTank()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Tank(), 1);                          // 只有罐
            Assert.False(comp.StartBuild(out string why1));
            Assert.Contains("推进器", why1);
            Assert.Equal(AssemblerState.Designing, comp.State);

            comp.AddPart(Control(), 1);
            Assert.False(comp.StartBuild(out string whyC));
            Assert.Contains("推进器", whyC);                 // 有控制核心也还缺引擎

            comp.AddPart(Engine(), 1);                        // 引擎最后加 ⇒ 顺序合法（2026-10-29 判据）
            Assert.True(comp.StartBuild(out string why2));
            Assert.Equal("", why2);
            Assert.Equal(AssemblerState.Building, comp.State);
            Assert.True(comp.WorkRequired > 0f);

            // 建造中不许改设计
            Assert.False(comp.CanEditDesign);
            Assert.False(comp.AddPart(Engine(), 1));

            comp.CancelBuild();
            Assert.Equal(AssemblerState.Designing, comp.State);
            Assert.Equal(0f, comp.WorkDone, 3);
            Assert.True(comp.CanEditDesign);
        }

        [Fact]
        public void StartBuild_RejectsDesignWhoseActiveStageHasNoEngine()
        {
            // 用户 2026-10-28 实测：上面级编成 1、一级编成 0（或罐单独占最大级）⇒ 起飞推力 0。
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(0);
            comp.AddPart(Control(), 1);     // 控制核心（缺它就轮不到"活动级"这条判据）
            comp.AddPart(Engine(), 1);      // 上面级：有引擎，且在级末（2026-10-29 顺序判据）
            comp.SetCurrentStage(1);
            comp.AddPart(Tank(), 1);        // 最大级：只有罐 ⇒ 活动级没引擎

            Assert.Equal(1, comp.ActiveStage);
            Assert.Equal(0, comp.EngineCountInStage(1));
            Assert.False(comp.ActiveStageHasEngine);
            Assert.False(comp.StartBuild(out string why));
            Assert.Contains("不能自己点火", why);
            Assert.Contains("引擎 0", why);

            // 对称情形：活动级只有引擎、没有罐（有推力没油）
            var comp2 = new CompRocketAssembler();
            comp2.SetCurrentStage(0);
            comp2.AddPart(Tank(), 1);
            comp2.AddPart(Control(), 1);
            comp2.SetCurrentStage(1);
            comp2.AddPart(Engine(), 9);
            Assert.False(comp2.ActiveStageReady);
            Assert.False(comp2.StartBuild(out string why2));
            Assert.Contains("工质箱 0", why2);
        }

        [Fact]
        public void ReverseStages_FixesReversedTwoStageDesign()
        {
            // 设计 [E(0) → 级 1: T]，顺序上引擎在级首 —— 2026-10-29 起先过"引擎必须在级末端"这条。
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(0);
            comp.AddPart(Engine(), 1);      // 一级引擎，被错编到 0
            comp.SetCurrentStage(1);
            comp.AddPart(Tank(), 1);        // 一级罐在 1，但 1 里没有引擎 ⇒ 活动级推力 0

            Assert.False(comp.ActiveStageHasEngine);
            Assert.Equal("", comp.EnginePlacementError());   // 本测试只针对级号，顺序本身合法

            Assert.True(comp.ReverseStages());

            Assert.Equal(1, comp.ActiveStage);
            Assert.Equal(1, comp.Design.parts.Single(p => p.category == "thruster").stageId);
            Assert.Equal(0, comp.Design.parts.Single(p => p.category == "tank").stageId);
            Assert.True(comp.ActiveStageHasEngine);
        }

        [Fact]
        public void StartBuild_RejectsEmptyDesign()
        {
            var comp = new CompRocketAssembler();
            Assert.False(comp.StartBuild(out string why));
            Assert.False(string.IsNullOrEmpty(why));
        }

        [Fact]
        public void StartNewRocket_ResetsDesign_ButNotWhileBuilding()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine(), 1);
            comp.AddPart(Tank(), 1);
            Assert.True(comp.StartNewRocket());          // 设计中：允许（等于清空）
            Assert.Empty(comp.Design.parts);
            Assert.Equal(AssemblerState.Idle, comp.State);

            comp.AddPart(Tank(), 1);                      // 顺序：罐 → 控制核心 → 引擎（引擎在级末）
            comp.AddPart(Control(), 1);
            comp.AddPart(Engine(), 1);
            Assert.True(comp.StartBuild(out _));
            Assert.False(comp.StartNewRocket());         // 建造中：拒绝
            Assert.Equal(AssemblerState.Building, comp.State);
            Assert.NotEmpty(comp.Design.parts);
        }

        [Fact]
        public void Thrust_SumsEnginesOnly_AndIgnoresTanks()
        {
            var spec = Spec(("E", "thruster", 3), ("T", "tank", 1));
            var map = new Dictionary<string, ShipPartDef> { ["E"] = Engine(), ["T"] = Tank() };

            float n = RocketSchematicBuilder.ThrustN(spec, n2 => map.TryGetValue(n2, out var d) ? d : null);

            Assert.Equal(3f * 3000f * 200f, n, 1);      // 3 × 600 kN
            Assert.Equal(0f, RocketSchematicBuilder.ThrustN(new RocketDesignSpec(), _ => null), 1);
        }

        // ── 分级（2026-10-28：级号必须能分开，不能全扎堆）────────────

        [Fact]
        public void AddPart_PerStage_KeepsRowsSeparate()
        {
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(1);
            comp.AddPart(Engine(), 9);
            comp.SetCurrentStage(0);
            comp.AddPart(Engine(), 1);       // 同 def、不同级 ⇒ 不许合并
            comp.AddPart(Tank(), 1);

            Assert.Equal(3, comp.Design.parts.Count);
            Assert.Equal(1, comp.Design.parts[0].stageId);
            Assert.Equal(0, comp.Design.parts[1].stageId);
            Assert.Equal(2, comp.StageCount);
        }

        [Fact]
        public void ShiftPartStage_ClampsBothEnds()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine(), 1);                    // 默认 stage 0
            Assert.True(comp.ShiftPartStage(0, +2));
            Assert.Equal(2, comp.Design.parts[0].stageId);
            Assert.True(comp.ShiftPartStage(0, -9));
            Assert.Equal(0, comp.Design.parts[0].stageId);
            Assert.True(comp.ShiftPartStage(0, +99));
            Assert.Equal(CompRocketAssembler.MaxStageId, comp.Design.parts[0].stageId);

            // 建造中不许挪级（先把罐/控制件插到引擎**之前**，再把引擎挪到本级最后一行）
            comp.SetCurrentStage(CompRocketAssembler.MaxStageId);
            comp.AddPart(Tank(), 1);
            comp.AddPart(Control(), 1);
            Assert.True(comp.MovePart(0, +2));            // [E, T, C] ⇒ [T, C, E]（引擎在级末）
            Assert.True(comp.StartBuild(out _));
            Assert.False(comp.ShiftPartStage(0, -1));
        }

        [Fact]
        public void Thrust_UsesOnlyActiveStage_AndSchematicCountsStages()
        {
            var spec = new RocketDesignSpec();
            spec.parts.Add(new DesignPartSpec { defName = "E", category = "thruster", quantity = 2, stageId = 1 });
            spec.parts.Add(new DesignPartSpec { defName = "E", category = "thruster", quantity = 1, stageId = 0 });
            spec.parts.Add(new DesignPartSpec { defName = "T", category = "tank", quantity = 1, stageId = 1 });
            var map = new Dictionary<string, ShipPartDef> { ["E"] = Engine(), ["T"] = Tank() };
            ShipPartDef? Lookup(string n) => map.TryGetValue(n, out var d) ? d : null;

            Assert.Equal(1, RocketSchematicBuilder.ActiveStage(spec));
            Assert.Equal(2f * 600_000f, RocketSchematicBuilder.ThrustN(spec, Lookup, 1), 1);
            Assert.Equal(600_000f, RocketSchematicBuilder.ThrustN(spec, Lookup, 0), 1);
            Assert.Equal(1_800_000f, RocketSchematicBuilder.ThrustN(spec, Lookup), 1);   // 不筛 = 全部

            var s = RocketSchematicBuilder.Build(spec, Lookup);
            Assert.Equal(2, s.stageCount);
            Assert.Equal(1, s.pieces[0].stageId);
            Assert.Equal(0, s.pieces[2].stageId);
        }

        // ── 发射倒计时（用户令：10 秒是**游戏秒**，不是物理秒）───────────

        [Fact]
        public void LaunchCountdown_IsGameTicks_NotPhysicsSeconds()
        {
            // 10 游戏秒 = 600 game tick；与 TimeScaleManager 的物理/日历倍率无关，
            // 所以这里只该出现 60 这个常量（3 倍速由 TickManager 自己决定 tick 的实际时长）。
            Assert.Equal(600, CompRocketAssembler.TicksForGameSeconds(10f));
            Assert.Equal(150, CompRocketAssembler.TicksForGameSeconds(2.5f));
            Assert.Equal(0, CompRocketAssembler.TicksForGameSeconds(0f));
            Assert.Equal(0, CompRocketAssembler.TicksForGameSeconds(-5f));
        }

        [Fact]
        public void FuelForLaunch_IsSumOfTankCapacity()
        {
            var spec = Spec(("E", "thruster", 1), ("T", "tank", 2));
            var map = new Dictionary<string, ShipPartDef> { ["E"] = Engine(), ["T"] = Tank(cap: 20000f) };

            float fuel = CompRocketAssembler.FuelForLaunch(spec, n => map.TryGetValue(n, out var d) ? d : null);

            Assert.Equal(40000f, fuel, 3);               // 2 罐 × 20 t
            Assert.Equal(0f, CompRocketAssembler.FuelForLaunch(new RocketDesignSpec(), _ => null), 3);
        }

        [Fact]
        public void BeginLaunch_RequiresComplete()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Tank(), 1);                        // 顺序：罐 → 控制 → 引擎（引擎在级末）
            comp.AddPart(Control(), 1);
            comp.AddPart(Engine(), 1);
            Assert.False(comp.BeginLaunch(out string why1));   // 还在设计阶段
            Assert.Contains("完成", why1);

            comp.StartBuild(out _);
            Assert.False(comp.BeginLaunch(out _));             // 建造中也不行
            Assert.Equal(AssemblerState.Building, comp.State);
        }

        // ── 完工 → 编辑设计（2026-11-24，C+ UI 转生产） ──────────

        /// <summary>
        /// 造一台**已经完工**的组装器：罐 → 控制 → 引擎（引擎在级末，顺序合法）。
        /// `ForceComplete()` 是不建 Thing 就能进 `Complete` 的唯一入口（`BeginLaunch` 要求 `Complete`，
        /// `CancelLaunch` 要求 `Launching`，两者互为前提，绕不过去）。
        ///
        /// **宿主限制**：测试没加载 Core 的 `MessageTypeDef` ⇒ `MessageTypeDefOf.TaskCompletion` 为 null，
        /// `Messages.Message` 在 `msg.def.sound` 处 NRE。`FinishBuild` 是**先提交状态再弹消息**
        /// （state / workDone / fuelAmount 都在消息之前），所以只吞"来自 Verse.Messages 的那一个 NRE"，
        /// 其余异常照抛 —— 不掩盖产品缺陷，也不必为测试给后端开洞。
        /// </summary>
        private static CompRocketAssembler CompletedRocket()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Tank(), 1);
            comp.AddPart(Control(), 1);
            comp.AddPart(Engine(), 1);
            Assert.True(comp.StartBuild(out string why), why);
            try
            {
                comp.ForceComplete();
            }
            catch (System.NullReferenceException ex) when (ex.StackTrace != null
                && ex.StackTrace.Contains("Verse.Messages.Message"))
            {
                // 见上：只有宿主缺 MessageTypeDef 时才会到这里，状态已提交。
            }
            Assert.Equal(AssemblerState.Complete, comp.State);
            return comp;
        }

        [Fact]
        public void ReopenDesign_FromComplete_KeepsDesignAndClearsWork()
        {
            var comp = CompletedRocket();
            string[] before = comp.Design.parts.Select(p => p.defName).ToArray();
            Assert.Equal(3, comp.Design.parts.Count);
            Assert.True(comp.WorkDone > 0f);                   // 完工时 workDone = workRequired
            Assert.False(comp.CanEditDesign);

            // 宿主里查不到 ShipPartDef("E"/"T") ⇒ FinishBuild 加的是 0 t 油，
            // 这里手工放一个非零值，"回设计态清油"这条才算真的有判据（否则 0→0 是空断言）。
            comp.Design.fuelAmount = 5000f;

            Assert.True(comp.ReopenDesign());

            Assert.Equal(AssemblerState.Designing, comp.State);
            Assert.True(comp.CanEditDesign);
            Assert.Equal(0f, comp.WorkDone, 3);
            Assert.Equal(0f, comp.WorkRequired, 3);
            Assert.Equal(0, comp.LaunchTicksLeft);
            Assert.Equal(0f, comp.Design.fuelAmount, 3);        // 完工时加过油，回设计态要清

            // 设计原样保留：行数与 defName 顺序都不变（这是"不用快照"的判据）
            Assert.Equal(3, comp.Design.parts.Count);
            Assert.Equal(before, comp.Design.parts.Select(p => p.defName).ToArray());

            // 回到设计态后必须重新建造：Designing 不是可发射态
            Assert.True(comp.StartBuild(out string why2), why2);
            Assert.Equal(AssemblerState.Building, comp.State);
            Assert.True(comp.WorkRequired > 0f);                // 工时在 StartBuild 里重算
        }

        [Fact]
        public void ReopenDesign_RejectsNonCompleteStates()
        {
            // Idle：空设计
            var idle = new CompRocketAssembler();
            Assert.False(idle.ReopenDesign());
            Assert.Equal(AssemblerState.Idle, idle.State);

            // Designing：只加了件、还没开工
            var designing = new CompRocketAssembler();
            designing.AddPart(Tank(), 1);
            Assert.Equal(AssemblerState.Designing, designing.State);
            Assert.False(designing.ReopenDesign());
            Assert.Equal(AssemblerState.Designing, designing.State);

            // Building：建造中不许跳回设计（必须先 CancelBuild）
            var building = new CompRocketAssembler();
            building.AddPart(Tank(), 1);
            building.AddPart(Control(), 1);
            building.AddPart(Engine(), 1);
            Assert.True(building.StartBuild(out string why), why);
            Assert.False(building.ReopenDesign());
            Assert.Equal(AssemblerState.Building, building.State);
            Assert.True(building.WorkRequired > 0f);            // 被拒时不能顺手清工时
        }

        [Fact]
        public void ReopenDesign_ThenBeginLaunchIsRejected()
        {
            var comp = CompletedRocket();
            Assert.True(comp.ReopenDesign());

            Assert.False(comp.BeginLaunch(out string why));
            Assert.Contains("完成", why);
            Assert.Equal(AssemblerState.Designing, comp.State);
            Assert.Equal(0, comp.LaunchTicksLeft);              // 没被误开倒计时
        }

        [Fact]
        public void BuildParts_StacksAlongMinusZ_WithLengthOnZ()
        {
            // 2026-10-28（用户："横着飞的，改吧"）：零件沿 **−Z** 堆，长度落在 Z 上 ⇒ 与 +Z 机头同轴。
            var spec = Spec(("E", "thruster", 1), ("T", "tank", 1));
            var map = new Dictionary<string, ShipPartDef>
            {
                ["E"] = Engine(stack: 3f, width: 2f),
                ["T"] = Tank(stack: 10f, width: 3f),
            };
            ShipPartDef? Lookup(string n) => map.TryGetValue(n, out var d) ? d : null;

            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _,
                Lookup, includeUnknownDefs: false, includeCabin: false);

            Assert.Equal(2, parts.Count);
            Assert.Equal(-1.5, parts[0].positionShip.z, 6);     // −stack/2
            Assert.Equal(-8.0, parts[1].positionShip.z, 6);     // −(3 + 10/2)
            Assert.Equal(0.0, parts[0].positionShip.y, 6);      // 不再往 Y 上堆

            Assert.Equal(3f, parts[0].boundsSizeShip.z, 6);     // 长度在 Z
            Assert.Equal(2f, parts[0].boundsSizeShip.x, 6);     // 宽度仍在 X
            Assert.Equal(10f, parts[1].boundsSizeShip.z, 6);
        }

        [Fact]
        public void BuildParts_NoCabin_StartsAtOrigin()
        {
            var spec = Spec(("E", "thruster", 2));
            var map = new Dictionary<string, ShipPartDef> { ["E"] = Engine(stack: 4f) };
            ShipPartDef? Lookup(string n) => map.TryGetValue(n, out var d) ? d : null;

            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _,
                Lookup, includeUnknownDefs: false, includeCabin: false);

            Assert.Equal(2, parts.Count);
            Assert.Equal(-2.0, parts[0].positionShip.z, 6);
            Assert.Equal(-6.0, parts[1].positionShip.z, 6);
        }

        [Fact]
        public void ControlCore_IsRequiredForPurePartRocket()
        {
            // 顺序合法（罐 → 引擎）但缺控制核心 ⇒ 只该报"控制核心"
            var comp = new CompRocketAssembler();
            comp.AddPart(Tank(), 1);
            comp.AddPart(Engine(), 1);
            Assert.Equal(0, comp.ControlCount);
            Assert.False(comp.StartBuild(out string why));
            Assert.Contains("控制核心", why);

            // 控制核心必须在引擎**之前**（引擎在级末端）⇒ 另起一份设计验证放行
            var comp2 = new CompRocketAssembler();
            comp2.AddPart(Tank(), 1);
            comp2.AddPart(Control(), 1);
            comp2.AddPart(Engine(), 1);
            Assert.Equal(1, comp2.ControlCount);
            Assert.True(comp2.StartBuild(out _));
        }
    }
}
