using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **并联（2026-10-29 用户令）**：1…7 台的"推力对称"摆法 + 件数口径 + 引擎必须在级末端的门。
    ///
    /// 判据分三层：
    /// · 纯几何（`ParallelLayout`）：环上等半径均分 ⇒ 横向偏移和为零、相邻相切；
    /// · 装配（`DesignPartAssembler`）：同一站共享 z、横向落在环上、轴向只推进一次；
    /// · 物理（`SpaceEntity.GetEngineTorqueAboutCom`）：对称并联的合力矩为 0（偏心会逼动量轮一直顶）。
    /// </summary>
    public class RocketAssemblerParallelTests
    {
        private static ShipPartDef Engine(string name = "E", float mass = 500f, float stack = 3f, float width = 2f,
            float ve = 3000f, float flow = 200f)
            => new()
            {
                defName = name, label = name, category = "thruster", massKg = mass,
                boundsX = width, boundsZ = width, boundsY = stack, stackLengthMeters = stack,
                exhaustVelocity = ve, massFlowRate = flow,
            };

        private static ShipPartDef Tank(string name = "T", float mass = 2000f, float cap = 20000f,
            float stack = 10f, float width = 3f)
            => new()
            {
                defName = name, label = name, category = "tank", massKg = mass,
                tankCapacityKg = cap, tankInitialKg = cap,
                boundsX = width, boundsZ = width, boundsY = stack, stackLengthMeters = stack,
            };

        private static ShipPartDef Control(string name = "C")
            => new()
            {
                defName = name, label = name, category = "control", massKg = 100f,
                boundsX = 1f, boundsZ = 1f, boundsY = 2f, stackLengthMeters = 2f,
            };

        private static DesignPartSpec Entry(string def, string cat, int stations = 1, int parallel = 1,
            int stage = 0)
            => new()
            {
                defName = def, category = cat, quantity = stations, parallelCount = parallel, stageId = stage,
            };

        private static RocketDesignSpec Spec(params DesignPartSpec[] rows)
            => new() { parts = new List<DesignPartSpec>(rows) };

        private static System.Func<string, ShipPartDef?> Catalog(params ShipPartDef[] defs)
        {
            var map = new Dictionary<string, ShipPartDef>();
            foreach (var d in defs) map[d.defName] = d;
            return name => map.TryGetValue(name, out var d) ? d : null;
        }

        // ── 纯几何：1…7 的对称答案 ──────────────────────────────

        [Fact]
        public void Layout_OneToSeven_BalancedAboutAxis()
        {
            for (int n = ParallelLayout.MinCount; n <= ParallelLayout.MaxCount; n++)
            {
                var xs = new float[n];
                var ys = new float[n];
                ParallelLayout.Offsets(n, 2f, xs, ys);

                double sx = 0.0, sy = 0.0;
                for (int i = 0; i < n; i++) { sx += xs[i]; sy += ys[i]; }

                // 正多边形顶点和 = 0 ⇒ 质心在轴上。偶数环是逐位对消，奇数环只留浮点残差。
                Assert.True(System.Math.Abs(sx) < 1e-5, $"n={n} Σx={sx}");
                Assert.True(System.Math.Abs(sy) < 1e-5, $"n={n} Σy={sy}");

                Assert.Equal(n, ParallelLayout.CenterCount(n) + ParallelLayout.RingCount(n));
                Assert.True(ParallelLayout.SpanM(2f, n) >= 2f);
            }

            // 1 = 轴上单件；7 = 中心 + 六环（"简洁答案"的定型）
            Assert.Equal(1, ParallelLayout.CenterCount(1));
            Assert.Equal(0, ParallelLayout.RingCount(1));
            Assert.Equal(1, ParallelLayout.CenterCount(7));
            Assert.Equal(6, ParallelLayout.RingCount(7));
            for (int n = 2; n <= 6; n++)
            {
                Assert.Equal(0, ParallelLayout.CenterCount(n));
                Assert.Equal(n, ParallelLayout.RingCount(n));
            }
        }

        [Fact]
        public void Layout_NeighboursAreExactlyTangent()
        {
            // R = w / (2 sin(π/k)) ⇒ 最近两件间距 = w（相切但不相交）；7 的中心件到环件同样 = w
            for (int n = 2; n <= ParallelLayout.MaxCount; n++)
            {
                var xs = new float[n];
                var ys = new float[n];
                ParallelLayout.Offsets(n, 2f, xs, ys);

                float best = float.MaxValue;
                for (int a = 0; a < n; a++)
                    for (int b = a + 1; b < n; b++)
                    {
                        float dx = xs[a] - xs[b];
                        float dy = ys[a] - ys[b];
                        float d = (float)System.Math.Sqrt(dx * dx + dy * dy);
                        if (d < best) best = d;
                    }
                Assert.Equal(2f, best, 3);
            }
        }

        [Fact]
        public void Layout_TooManyOrTooFew_IsClamped()
        {
            Assert.Equal(ParallelLayout.MaxCount, ParallelLayout.Clamp(99));
            Assert.Equal(ParallelLayout.MinCount, ParallelLayout.Clamp(-3));
            Assert.Equal(0f, ParallelLayout.RadiusM(2f, 1), 6);
        }

        // ── 装配：同一站共享 z、横向在环上、轴向只推进一次 ──────────

        [Fact]
        public void BuildParts_ParallelStation_IsOneAxialSlice()
        {
            var spec = Spec(Entry("E", "thruster", stations: 1, parallel: 6), Entry("T", "tank", 1, 1));
            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _,
                Catalog(Engine(stack: 3f, width: 2f), Tank(stack: 10f)), includeUnknownDefs: false,
                includeCabin: false);

            Assert.Equal(7, parts.Count);                       // 6 台引擎 + 1 罐
            // 6 台引擎同一 z（−stack/2），偏移在环上（R = 2/(2 sin30°) = 2 m）
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(-1.5, parts[i].positionShip.z, 6);
                Assert.Equal(2.0, System.Math.Sqrt(parts[i].positionShip.x * parts[i].positionShip.x
                    + parts[i].positionShip.y * parts[i].positionShip.y), 3);
            }
            // 轴向只推进一次 ⇒ 罐中心 = −(3 + 10/2)，不是 −(6×3 + 5)
            Assert.Equal(-8.0, parts[6].positionShip.z, 6);
            Assert.Equal(0.0, parts[6].positionShip.x, 6);
        }

        [Fact]
        public void BuildParts_SinglePart_KeepsExactLegacyPose()
        {
            // parallelCount = 1 必须逐位等于旧口径（偏移恒 0）
            var spec = Spec(Entry("E", "thruster", 2, 1), Entry("T", "tank", 1, 1));
            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _,
                Catalog(Engine(stack: 3f), Tank(stack: 10f)), includeUnknownDefs: false, includeCabin: false);

            Assert.Equal(3, parts.Count);
            Assert.Equal(-1.5, parts[0].positionShip.z, 6);
            Assert.Equal(0.0, parts[0].positionShip.x, 6);
            Assert.Equal(0.0, parts[0].positionShip.y, 6);
            Assert.Equal(-4.5, parts[1].positionShip.z, 6);
            Assert.Equal(-11.0, parts[2].positionShip.z, 6);
        }

        [Fact]
        public void Schematic_ClusterCountsPartsOnce_AndWidensSpan()
        {
            var spec = Spec(Entry("E", "thruster", stations: 2, parallel: 6), Entry("T", "tank", 1, 1));
            var s = RocketSchematicBuilder.Build(spec, Catalog(Engine(stack: 3f, width: 2f), Tank(stack: 10f)));

            Assert.Equal(3, s.pieces.Count);                    // 2 站 + 1 站（不是 13 块）
            Assert.Equal(2 * 6, s.engineCount);                 // 件数按 ×并联
            Assert.Equal(2 * 3f + 10f, s.totalHeightM, 3);      // 高度不乘并联
            Assert.Equal(6f, s.pieces[0].widthM, 3);            // 环 6 ⇒ 跨度 = 2R + w = 2·2 + 2
            Assert.Equal(2f, s.pieces[0].partWidthM, 3);
            Assert.Equal(6, s.pieces[0].offsetX!.Length);
            Assert.Equal(2f * 6f * 500f + 2000f, s.dryMassKg, 3);

            // 单件站的偏移表为 null、跨度 = 件宽（旧口径）
            Assert.Null(s.pieces[2].offsetX);
            Assert.Equal(3f, s.pieces[2].widthM, 3);
        }

        [Fact]
        public void ThrustAndFuel_MultiplyParallelCount()
        {
            var spec = Spec(Entry("E", "thruster", 2, 6), Entry("T", "tank", 1, 4));
            var catalog = Catalog(Engine(), Tank(cap: 20000f));

            // 2 站 × 6 台 = 12 台 × 600 kN
            Assert.Equal(12f * 3000f * 200f, RocketSchematicBuilder.ThrustN(spec, catalog), 1);
            // 1 站 × 4 罐 = 4 × 20 t
            Assert.Equal(80000f, CompRocketAssembler.FuelForLaunch(spec, catalog), 3);
            // 工时 = 12 × (40 + 500/100) + 4 × (40 + 2000/100 + 20000/1000) = 540 + 320
            Assert.Equal(860f, CompRocketAssembler.WorkFor(spec, catalog), 3);
        }

        [Fact]
        public void CompCounts_MultiplyParallelCount()
        {
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(0);
            comp.AddPart(Tank(), 1);
            comp.AddPart(Control(), 1);
            comp.SetCurrentStage(1);
            comp.AddPart(Tank(), 1);
            comp.SetCurrentParallel(6);
            comp.AddPart(Engine(), 2);          // 2 站 × 并联 6 = 12 台

            Assert.Equal(12, comp.EngineCountInStage(1));
            Assert.Equal(1, comp.TankCountInStage(1));
            Assert.Equal(2 + 1 + 12, comp.TotalPartCount);      // 罐/控制件 3 + 引擎 12
            Assert.True(comp.ActiveStageReady);
        }

        // ── 物理：对称并联的合力矩为 0 ────────────────────────────

        [Fact]
        public void SymmetricCluster_ProducesNoEngineTorque()
        {
            foreach (int n in new[] { 3, 4, 5, 6, 7 })
            {
                var spec = Spec(Entry("E", "thruster", 1, n));
                var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _,
                    Catalog(Engine(stack: 3f, width: 2f)), includeUnknownDefs: false, includeCabin: false);

                var e = new SpaceEntity(1, 0);
                e.SetParts(parts);

                DVector3 torque = e.GetEngineTorqueAboutCom(DVector3.zero, out double thrust);
                double scale = System.Math.Max(1.0, thrust * 2.0);   // 参考量：F × R
                Assert.True(torque.magnitude / scale < 1e-6,
                    $"n={n} |τ|={torque.magnitude} 参考={scale}");
                Assert.True(thrust > 0.0);
            }
        }

        [Fact]
        public void OffAxisEngines_DoProduceTorque()
        {
            // 反证：两根都在 +X 的引擎 ⇒ τ 必须明显非零（探针本身不是恒零）
            var spec = Spec(Entry("E", "thruster", 2, 1));
            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _,
                Catalog(Engine(stack: 3f, width: 2f)), includeUnknownDefs: false, includeCabin: false);
            for (int i = 0; i < parts.Count; i++) parts[i].positionShip = new DVector3(1.0 + i, 0.0, -1.5);

            var e = new SpaceEntity(2, 0);
            e.SetParts(parts);

            DVector3 torque = e.GetEngineTorqueAboutCom(DVector3.zero, out double thrust);
            Assert.True(torque.magnitude > 1.0, $"|τ|={torque.magnitude}（应明显非零）");
            Assert.True(thrust > 0.0);
        }

        // ── 引擎必须在级末端（设计门）────────────────────────────

        [Fact]
        public void EnginePlacement_RejectsEngineNotAtStageEnd()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine(), 1);       // 先加引擎 ⇒ 排在级首
            comp.AddPart(Tank(), 1);
            comp.AddPart(Control(), 1);

            string err = comp.EnginePlacementError();
            Assert.NotEqual("", err);
            Assert.Contains("末端", err);

            var offenders = new List<int>();
            comp.EnginePlacementError(offenders);
            Assert.Equal(2, offenders.Count);       // 罐与控制核心都排在引擎之后

            Assert.False(comp.StartBuild(out string why));
            Assert.Contains("末端", why);
        }

        [Fact]
        public void EnginePlacement_MovePartToStageEnd_PassesTheGate()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine(), 1);
            comp.AddPart(Tank(), 1);
            comp.AddPart(Control(), 1);
            Assert.False(comp.StartBuild(out _));

            // ↑/↓ 的语义：把引擎（下标 0）挪到本行末尾
            Assert.True(comp.MovePart(0, +2));
            Assert.Equal("E", comp.Design.parts[2].defName);
            Assert.Equal("", comp.EnginePlacementError());
            Assert.True(comp.StartBuild(out string why));
            Assert.Equal("", why);
        }

        [Fact]
        public void EnginePlacement_IsPerStage_NotGlobal()
        {
            // 上面级的引擎在它自己那一级的末尾即可 —— 下面还有一级（级 1）不算违规
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(0);
            comp.AddPart(Control(), 1);
            comp.AddPart(Tank(), 1);
            comp.AddPart(Engine(), 1);
            comp.SetCurrentStage(1);
            comp.AddPart(Tank(), 1);
            comp.AddPart(Engine(), 1);

            Assert.Equal("", comp.EnginePlacementError());
            Assert.True(comp.StartBuild(out _));
        }

        [Fact]
        public void EnginePlacement_RcsIsNotAMainEngine()
        {
            // RCS 是姿态推力器，不受"必须在级末端"约束
            var spec = Spec(Entry("R", "rcs", 1, 1), Entry("T", "tank", 1, 1), Entry("C", "control", 1, 1));
            Assert.Equal("", RocketDesignRules.EnginePlacementError(spec));
        }

        // ── 并联是合并键的一部分 ──────────────────────────────────

        [Fact]
        public void AddPart_ParallelIsPartOfMergeKey()
        {
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(0);
            comp.SetCurrentParallel(4);
            comp.AddPart(Engine(), 1);
            comp.SetCurrentParallel(6);
            comp.AddPart(Engine(), 1);          // 不同并联 ⇒ 新行

            Assert.Equal(2, comp.Design.parts.Count);
            Assert.Equal(4, comp.Design.parts[0].parallelCount);
            Assert.Equal(6, comp.Design.parts[1].parallelCount);

            comp.SetCurrentParallel(4);
            comp.AddPart(Engine(), 2);          // 同并联 ⇒ 合并（站数累加）
            Assert.Equal(2, comp.Design.parts.Count);
            Assert.Equal(3, comp.Design.parts[0].quantity);
            Assert.Equal(12, RocketDesignRules.PartCountOf(comp.Design.parts[0]));
        }

        // ── 顺序移动（含 parentIndex 重映射）──────────────────────

        [Fact]
        public void MovePart_ClampsAtEnds_AndRejectsWhenNotEditable()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine(), 1);
            comp.AddPart(Tank(), 1);

            Assert.False(comp.MovePart(0, -1));      // 已在最前
            Assert.False(comp.MovePart(1, +1));      // 已在最后
            Assert.False(comp.MovePart(9, +1));      // 越界
            Assert.False(comp.MovePart(0, 0));       // 没动

            Assert.True(comp.MovePart(1, -1));
            Assert.Equal("T", comp.Design.parts[0].defName);
            Assert.Equal("E", comp.Design.parts[1].defName);
        }

        [Fact]
        public void MovePart_RemapsParentIndexReferences()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine(), 1);
            comp.AddPart(Tank(), 1);
            comp.AddPart(Control(), 1);
            comp.Design.parts[0].parentIndex = 0;
            comp.Design.parts[1].parentIndex = 0;   // 指向引擎
            comp.Design.parts[2].parentIndex = 1;   // 指向罐

            Assert.True(comp.MovePart(0, +1));      // E 与 T 互换 ⇒ E 到 1，T 到 0

            Assert.Equal("T", comp.Design.parts[0].defName);
            Assert.Equal("E", comp.Design.parts[1].defName);
            Assert.Equal(1, comp.Design.parts[1].parentIndex);   // 引擎自己（原 0）⇒ 新 1
            Assert.Equal(1, comp.Design.parts[0].parentIndex);   // 罐指向引擎 ⇒ 新 1
            Assert.Equal(0, comp.Design.parts[2].parentIndex);   // 控制核心指向罐 ⇒ 新 0
        }

        // ── 同级行必须连成一段（2026-10-30：抛级按级号整段切）──────────

        [Fact]
        public void StageContiguity_RejectsInterleavedStages()
        {
            // [T1, C0, E0, E1]：级 1 被级 0 隔开（行 0 与行 3）⇒ 抛级切出来的两半在 z 向互相穿插
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(1);
            comp.AddPart(Tank(), 1);
            comp.SetCurrentStage(0);
            comp.AddPart(Control(), 1);
            comp.AddPart(Engine(), 1);
            comp.SetCurrentStage(1);
            comp.AddPart(Engine(), 1);

            Assert.Equal("", comp.EnginePlacementError());       // 引擎位置本身合法
            string err = comp.StageContiguityError();
            Assert.NotEqual("", err);
            Assert.Contains("连成一段", err);

            var offenders = new List<int>();
            comp.StageContiguityError(offenders);
            Assert.NotEmpty(offenders);

            Assert.False(comp.StartBuild(out string why));
            Assert.Contains("连成一段", why);
        }

        [Fact]
        public void StageContiguity_MoveRowFixesIt()
        {
            var comp = new CompRocketAssembler();
            comp.SetCurrentStage(1);
            comp.AddPart(Tank(), 1);      // 行 0
            comp.SetCurrentStage(0);
            comp.AddPart(Control(), 1);   // 行 1
            comp.AddPart(Engine(), 1);    // 行 2
            comp.SetCurrentStage(1);
            comp.AddPart(Engine(), 1);    // 行 3
            Assert.NotEqual("", comp.StageContiguityError());

            Assert.True(comp.MovePart(0, +2));                   // [C0, E0, T1, E1]
            Assert.Equal("", comp.StageContiguityError());
            Assert.True(comp.StartBuild(out string why));
            Assert.Equal("", why);
        }

        [Fact]
        public void StageContiguity_TwoStageDemo_IsContiguous()
        {
            var spec = Spec(
                Entry("C", "control", 1, 1, 0), Entry("T", "tank", 1, 1, 0), Entry("E", "thruster", 1, 1, 0),
                Entry("T", "tank", 1, 1, 1), Entry("E", "thruster", 2, 6, 1));
            Assert.Equal("", RocketDesignRules.StageContiguityError(spec));
        }
    }
}
