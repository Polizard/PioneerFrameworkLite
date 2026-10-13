using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **[A3]/[A5] 进气充填与附带进气的"账"**（2026-10-06 接线片）—— 不需要游戏就能验的那一半：
    ///   · 进气道部件的数据往返（`IntakeComponentData` 的 Clone 通道 = 模板→运行时那条路）；
    ///   · `SpaceEntity.TryFillPropellant`：**按容量截断**、跨同工质条目分摊、与 `TotalPropellantMass`
    ///     这个增量缓存**同步**（它不在读时重算 ⇒ 少维护一处就会与逐条之和对不上，进而喂错 `a = F/m`）；
    ///   · `PropellantNetwork.RegisterGasDraws`：N 台同型引擎 ≡ N 倍、同工质合并、**纯火箭恒为 null**
    ///     （那条"逐位不变"的红线全靠它：门控路径根本不进）。
    ///
    /// 游戏侧那一半（`ComputeGroupIntakeFill` 抠 ρ/空速、并行区只算不写、主线程入账）不在本文件里，
    /// 它由闸门的集成步 + 游戏内读数覆盖 —— **不许拿本条测试冒充"接线正确"**。
    /// </summary>
    public class IntakeFillTests
    {
        private static PropellantDef Def(string name) => new PropellantDef { defName = name };

        private static SpaceEntity EntityWithPropulsion(params PropulsionData[] entries)
        {
            var e = new SpaceEntity(1, 0) { HasMap = false };
            foreach (var d in entries) e.propulsion.Add(d);
            e.InvalidatePropulsion();
            return e;
        }

        // ── ① 进气道部件（数据往返）──

        [Fact]
        public void IntakeComponent_Clone_RoundTripsBothFields()
        {
            var src = new IntakeComponentData { intakeArea = 3.5f, coefficient = 0.8f };
            var copy = src.Clone();

            Assert.NotNull(copy);                     // Clone 走组件注册表 ⇒ 非 null 即"已注册"
            var typed = Assert.IsType<IntakeComponentData>(copy);
            Assert.Equal(3.5f, typed.intakeArea);
            Assert.Equal(0.8f, typed.coefficient);
            Assert.IsType<IntakeComponentData>(ComponentRegistry.Create(src.TypeId));
        }

        // ── ② 充填（按容量截断 / 跨条目分摊 / 缓存同步）──

        [Fact]
        public void TryFill_CapsAtCapacity_AndStopsWhenFull()
        {
            var air = Def("PF_Propellant_IntakeAir");
            var e = EntityWithPropulsion(new PropulsionData { propellant = air, mass = 0.0, capacity = 100.0 });

            Assert.Equal(60.0, e.TryFillPropellant(air, 60.0));
            Assert.Equal(40.0, e.TryFillPropellant(air, 60.0));   // 只灌得进余量那 40
            Assert.Equal(0.0, e.TryFillPropellant(air, 60.0));    // 满 ⇒ 一滴不进

            Assert.Equal(100.0, e.GetPropellantMass(air));
            Assert.Equal(100.0, e.TotalPropellantMass);
        }

        [Fact]
        public void TryFill_SpreadsAcrossAllEntriesOfSamePropellant()
        {
            // 同工质多罐：必须**跨全部条目**填（与消耗路径"只吃第一个条目"那个旧 bug 对称的坑）
            var air = Def("PF_Propellant_IntakeAir");
            var e = EntityWithPropulsion(
                new PropulsionData { propellant = air, mass = 0.0, capacity = 10.0 },
                new PropulsionData { propellant = air, mass = 0.0, capacity = 10.0 });

            Assert.Equal(15.0, e.TryFillPropellant(air, 15.0));
            Assert.Equal(10.0, e.propulsion[0].mass);
            Assert.Equal(5.0, e.propulsion[1].mass);
            Assert.Equal(15.0, e.TotalPropellantMass);
        }

        [Fact]
        public void TryFill_IgnoresOtherPropellants_AndNonPositiveRequest()
        {
            var air = Def("PF_Propellant_IntakeAir");
            var fuel = Def("PF_Propellant_Hydrogen");
            var e = EntityWithPropulsion(new PropulsionData { propellant = fuel, mass = 0.0, capacity = 100.0 });

            Assert.Equal(0.0, e.TryFillPropellant(air, 50.0));   // 没有空气罐 ⇒ 不进
            Assert.Equal(0.0, e.TryFillPropellant(fuel, 0.0));   // 0/负请求 ⇒ 不进
            Assert.Equal(0.0, e.TryFillPropellant(fuel, -5.0));
            Assert.Equal(0.0, e.TotalPropellantMass);
        }

        [Fact]
        public void TotalPropellantMass_TracksFillAndConsumeBothWays()
        {
            var air = Def("PF_Propellant_IntakeAir");
            var e = EntityWithPropulsion(new PropulsionData { propellant = air, mass = 0.0, capacity = 100.0 });

            e.TryFillPropellant(air, 80.0);
            Assert.Equal(80.0, e.TotalPropellantMass);
            e.TryConsumePropellant(air, 30.0);
            Assert.Equal(50.0, e.TotalPropellantMass);
            // 增量缓存必须与逐条之和对得上（两处维护，任一处漏一处就漂）
            Assert.Equal(e.propulsion[0].mass, e.TotalPropellantMass);
        }

        // ── ③ 附带进气注册（N 倍 / 合并 / 纯火箭恒 null）──

        [Fact]
        public void RegisterGasDraws_AggregatesByFlow_AndMergesSameGas()
        {
            var air = Def("PF_Propellant_IntakeAir");
            var intakes = new List<GasIntake> { new GasIntake { propellant = air, perUnitRatio = 15f } };
            var net = new PropellantNetwork { propellant = Def("PF_Propellant_Hydrogen") };
            net.RegisterEngine(9000f, 2f);
            net.RegisterGasDraws(intakes, 2f);      // 2 kg/s 主工质 × 15 = 30 kg/s 空气
            net.RegisterEngine(9000f, 1f);
            net.RegisterGasDraws(intakes, 1f);      // 再 1 kg/s × 15 = 15 ⇒ 合并成一条 45

            Assert.NotNull(net.gasDraws);
            Assert.Single(net.gasDraws!);
            Assert.Equal(45.0, net.gasDraws![0].flowKgPerSec, 9);
            Assert.Same(air, net.gasDraws![0].propellant);
        }

        [Fact]
        public void RegisterGasDraws_LeavesRocketPathNull()
        {
            var net = new PropellantNetwork { propellant = Def("PF_Propellant_Hydrogen") };
            net.RegisterEngine(9000f, 5f);
            Assert.Null(net.gasDraws);                              // 没注册过 ⇒ 纯火箭

            net.RegisterGasDraws(null, 5f);                         // 缺席
            net.RegisterGasDraws(new List<GasIntake>(), 5f);        // 空列表
            net.RegisterGasDraws(new List<GasIntake> { new GasIntake { propellant = null, perUnitRatio = 15f } }, 5f);
            net.RegisterGasDraws(new List<GasIntake> { new GasIntake { propellant = Def("X"), perUnitRatio = 0f } }, 5f);
            net.RegisterGasDraws(new List<GasIntake> { new GasIntake { propellant = Def("X"), perUnitRatio = 15f } }, 0f);
            Assert.Null(net.gasDraws);                              // 全部无效 ⇒ 仍然恒 null（逐位不变的红线）
        }

        [Fact]
        public void GasDrawRatio_MatchesMixingRuleUsedByConsumption()
        {
            // 消费端（`SpaceGameComp`）用的是 `perFuel = flowKgPerSec / Σṁ` —— 本用例把
            // "注册时按 ṁ 累" 与 "消费时按流量比折算" 这两步的**等价性**钉住：
            var air = Def("PF_Propellant_IntakeAir");
            var net = new PropellantNetwork { propellant = Def("PF_Propellant_Hydrogen") };
            net.RegisterEngine(9000f, 3f);
            net.RegisterEngine(9000f, 1f);                          // Σṁ = 4
            net.RegisterGasDraws(new List<GasIntake> { new GasIntake { propellant = air, perUnitRatio = 15f } }, 3f);
            net.RegisterGasDraws(new List<GasIntake> { new GasIntake { propellant = air, perUnitRatio = 15f } }, 1f);

            double perFuel = net.gasDraws![0].flowKgPerSec / net.MassFlow;   // = 60/4
            Assert.Equal(15.0, perFuel, 9);                                  // = 引擎配比本身
            Assert.Equal(7.5, AirBreathingCore.AirRequired(0.5, perFuel), 9);
        }

        // ── ④ 包线注册（[A4]：火箭项不入表 / 同形状合并 / 经生产路径可达）──

        [Fact]
        public void RegisterEnvelope_RocketEngines_LeaveTermsNull()
        {
            var net = new PropellantNetwork { propellant = Def("PF_Propellant_Hydrogen") };
            net.RegisterEngine(9000f, 5f);
            net.RegisterEnvelope(9000f, 5f, 0f, null);                 // 火箭：a=0 且无折线
            Assert.Null(net.envelopeTerms);

            net.RegisterEnvelope(9000f, 5f, 0f, new List<MachPoint>()); // 空折线 = 不折 = 火箭
            Assert.Null(net.envelopeTerms);
        }

        [Fact]
        public void RegisterEnvelope_MergesSameShape_SeparatesOthers()
        {
            var net = new PropellantNetwork { propellant = Def("PF_Propellant_Hydrogen") };
            net.RegisterEnvelope(9000f, 2f, 1f, null);     // 吸气式 α 2 台
            net.RegisterEnvelope(9000f, 2f, 1f, null);
            Assert.NotNull(net.envelopeTerms);
            Assert.Single(net.envelopeTerms!);
            Assert.Equal(36000.0, net.envelopeTerms![0].veMf, 6);        // (9000·2)·2 台

            net.RegisterEnvelope(9000f, 2f, 0.5f, null);   // 不同 a ⇒ 另一项
            Assert.Equal(2, net.envelopeTerms!.Count);
            net.RegisterEnvelope(9000f, 2f, 1f, null);     // 回到 α=1 那一项（合并）
            Assert.Equal(2, net.envelopeTerms!.Count);
            Assert.Equal(54000.0, net.envelopeTerms![0].veMf, 6);

            net.RegisterEnvelope(9000f, 5f, 0f, null);     // 火箭项：不入表（但权重在网络里照占）
            Assert.Equal(2, net.envelopeTerms!.Count);
        }

        [Fact]
        public void SpaceEntityNetworks_CarryEnvelopeAndGasDraws_ThroughProductionPath()
        {
            // 走**生产路径**（`SpaceEntity.Networks` 从 `propulsion` 建网），不是手工 RegisterXxx：
            var air = Def("PF_Propellant_IntakeAir");
            var fuel = Def("PF_Propellant_Hydrogen");
            var e = EntityWithPropulsion(new PropulsionData
            {
                propellant = fuel,
                ve = 30000f,
                massFlow = 2f,
                mass = 100.0,
                capacity = 100.0,
                densityExponent = 1f,
                gasIntakes = new List<GasIntake> { new GasIntake { propellant = air, perUnitRatio = 15f } },
            });

            var net = e.Networks[0];
            Assert.NotNull(net.envelopeTerms);
            Assert.Single(net.envelopeTerms!);
            Assert.Equal(60000.0, net.envelopeTerms![0].veMf, 6);         // ve·ṁ = 30000·2
            Assert.Equal(1f, net.envelopeTerms![0].densityExponent);
            // 真空（ρ=0）⇒ 这一网恰归零（调用方据此不路由）；大气里（ρ=ρ0）⇒ 恰 1.0
            Assert.Equal(0.0, AirBreathingCore.EnvelopeFactor(net.envelopeTerms, net.Thrust, 0.0, 1.2, 0.0));
            Assert.Equal(1.0, AirBreathingCore.EnvelopeFactor(net.envelopeTerms, net.Thrust, 1.2, 1.2, 0.0));
            Assert.NotNull(net.gasDraws);
            Assert.Equal(30.0, net.gasDraws![0].flowKgPerSec, 6);          // 15 × 2
        }

        // ── ④b 有 Map 侧包线的注册/注销对称（2026-10-07：`SpaceMapComp` 走的就是这两个调用）──

        [Fact]
        public void MapSideEnvelope_RegisterUnregister_IsSymmetric()
        {
            var fuel = Def("PF_Propellant_Hydrogen");
            var net = new PropellantNetwork { propellant = fuel };

            // 火箭（a=0、无折线）⇒ **不入表**（既有四台推进器逐位不变的根据）
            net.RegisterEngine(9000f, 5f, 0);
            net.RegisterEnvelope(9000f, 5f, 0f, null);
            Assert.Null(net.envelopeTerms);

            // 吸气式（a=1）⇒ 入表；再来一台同型 ⇒ 合并
            net.RegisterEngine(9000f, 5f, 0);
            net.RegisterEnvelope(9000f, 5f, 1f, null);
            Assert.NotNull(net.envelopeTerms);
            Assert.Single(net.envelopeTerms!);
            Assert.Equal(45000.0, net.envelopeTerms![0].veMf, 6);

            // **注销对称**（有 Map 侧推进器可被拆 ⇒ 不许留幽灵权重）
            net.UnregisterEnvelope(9000f, 5f, 1f, null);
            Assert.Equal(0, net.envelopeTerms!.Count);
            net.UnregisterEnvelope(9000f, 5f, 0f, null);   // 火箭项本就不在表里 ⇒ 无操作、不炸
            Assert.Equal(0, net.envelopeTerms!.Count);
        }

        [Fact]
        public void MapSideEnvelope_VacuumGatesTheWholeNetwork()
        {
            // 与无 Map 侧同一条判据：ρ=0 ⇒ φ=0 ⇒ 调用方**不路由**这条网（D5）
            var fuel = Def("PF_Propellant_Hydrogen");
            var net = new PropellantNetwork { propellant = fuel };
            net.RegisterEngine(30000f, 5f, 0);
            net.RegisterEnvelope(30000f, 5f, 1f, null);
            Assert.Equal(0.0, AirBreathingCore.EnvelopeFactor(net.envelopeTerms, net.Thrust, 0.0, 1.2, 0.0));
            Assert.Equal(1.0, AirBreathingCore.EnvelopeFactor(net.envelopeTerms, net.Thrust, 1.2, 1.2, 0.0));
        }

        // ── ④c [A8]/③ 面板口径：环境相关的网报"全推续航"而不是 Δv ──

        [Fact]
        public void Endurance_IsPropellantOverFlow_AndTheEnvFlagIsTheNetworkShape()
        {
            // 口径的唯一算式（面板消费方直接用它，别各写一份）
            Assert.Equal(100.0, AirBreathingCore.FullThrottleEnduranceSeconds(500.0, 5.0));
            // "环境相关" = 网络**有包线表 或 要另外抽气** ⇒ Δv 不可承诺（本判据的实质）
            var air = Def("PF_Propellant_IntakeAir");
            var fuel = Def("PF_Propellant_Hydrogen");
            var jet = new PropellantNetwork { propellant = fuel };
            jet.RegisterEngine(30000f, 5f);
            jet.RegisterEnvelope(30000f, 5f, 1f, null);
            jet.RegisterGasDraws(new List<GasIntake> { new GasIntake { propellant = air, perUnitRatio = 15f } }, 5f);
            Assert.NotNull(jet.envelopeTerms);
            Assert.NotNull(jet.gasDraws);          // ⇒ 面板该报续航（实测：这就是吸气式那条网）

            var rocket = new PropellantNetwork { propellant = fuel };
            rocket.RegisterEngine(9000f, 5f);
            rocket.RegisterEnvelope(9000f, 5f, 0f, null);      // 火箭项不入表
            Assert.Null(rocket.envelopeTerms);
            Assert.Null(rocket.gasDraws);          // ⇒ 面板照旧报 Δv
        }

        // ── ⑤ 无 Map 标准档（[A4] §3.3：一套数、一个出处）──

        [Fact]
        public void StandardPropulsion_AirSet_WiresAsIntended()
        {
            var air = Def("PF_Propellant_IntakeAir");
            var fuel = Def("PF_Propellant_Hydrogen");
            var engine = StandardPropulsion.AirBreathingEngine(fuel, 500.0, air);
            var tank = StandardPropulsion.AirTank(air, 500.0);

            // 空气罐 massFlow=0 ⇒ **不成网络**（否则它会被当"一台用空气的引擎"，凭空多出一条推进网）
            Assert.Equal(0f, tank.massFlow);
            var e = EntityWithPropulsion(engine, tank);
            Assert.Single(e.Networks);
            Assert.Same(fuel, e.Networks[0].propellant);

            // 引擎侧：包线项（a=1）+ 空气抽取（15 × ṁ5 = 75 kg/s）
            Assert.NotNull(e.Networks[0].envelopeTerms);
            Assert.Equal(1f, e.Networks[0].envelopeTerms![0].densityExponent);
            Assert.NotNull(e.Networks[0].gasDraws);
            Assert.Equal(75.0, e.Networks[0].gasDraws![0].flowKgPerSec, 6);

            // 空罐起步 ⇒ 有余量可充（进气道充填的入口条件）
            Assert.Equal(0.0, e.GetPropellantMass(air));
            Assert.Equal(500.0, e.TryFillPropellant(air, 500.0), 6);
            Assert.Equal(0.0, e.TryFillPropellant(air, 1.0));       // 满罐 ⇒ 一点不进

            // 进气道部件（面积/系数照给）
            var intake = StandardPropulsion.Intake(12f, 0.9f);
            Assert.Equal(12f, intake.intakeArea);
            Assert.Equal(0.9f, intake.coefficient);
        }
    }
}
