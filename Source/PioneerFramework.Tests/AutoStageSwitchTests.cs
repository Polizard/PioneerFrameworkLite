using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using PioneerFramework;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **自动抛级开关（2026-10-28）**：`SpaceGameComp.SetAutoStage` 落组态 → 建/撤计划 → 读档重建。
    ///
    /// 为什么单独一条：这批改动落在 2026-10-28 01:09（全量闸门 00:43 已绿之后），
    /// 当时只过了确定性双构建 + 交付件 + 部署，**没有任何测试引用 `SetAutoStage`**。
    /// 判据 = 开关与计划表一一对应（开⇒恰一条、关⇒零条、重复开⇒不重复）、
    /// 读档语义（计划表不落盘，组上开关落盘）、以及手动抛级的拒绝理由。
    /// </summary>
    public class AutoStageSwitchTests
    {
        // ── 世界壳：与 CollisionPlanValidationBench / CollisionPairEnumerationTests 同款 ──
        // 组必须注册进 comp，`RebuildAutoStagePlans` 才扫得到它。
        private sealed class World : IDisposable
        {
            private readonly Game previous;
            public readonly Game Game;
            public readonly SpaceGameComp SpaceGame;

            public World()
            {
                previous = Current.Game;
                Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
                SpaceGame = new SpaceGameComp(Game);
                Game.components = new List<GameComponent> { SpaceGame };
                Current.Game = Game;
            }

            public void Dispose() => Current.Game = previous;
        }

        private static SpaceEntityGroup Group(int id) => new(id)
        {
            CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)DVector3.zero,
                velocitySystem = (SystemVec3)DVector3.zero,
                rotation = DQuaternion.identity,
            },
        };

        private const string GroupXmlRoot = "group";

        private static void SafeStop()
        {
            if (Scribe.mode != LoadSaveMode.Inactive)
                Scribe.ForceStop();
        }

        [Fact]
        public void Defaults_Off_WithRocketThresholds()
        {
            var g = Group(1);
            Assert.False(g.autoStageEnabled);
            Assert.Equal(0.02, g.autoStageFuelFraction, 12);
            Assert.Equal(20.0, g.autoStageMinSeconds, 12);
        }

        [Fact]
        public void SetAutoStage_On_CreatesFuelPlanFromGroupSettings()
        {
            var sg = new SpaceGameComp(null);
            var g = Group(1);
            g.autoStageFuelFraction = 0.05;
            g.autoStageMinSeconds = 7.5;

            sg.SetAutoStage(g, true);

            Assert.True(g.autoStageEnabled);
            var plan = Assert.Single(sg.autoStagePlans);
            Assert.Equal(1, plan.GroupId);
            Assert.Equal(0.05, plan.FuelFraction, 12);
            Assert.Equal(7.5, plan.MinSeconds, 12);
            Assert.Equal(99, plan.StagesLeft);
            Assert.Equal(0.0, plan.SecondsToStage, 12);   // 燃料模式，不走"到点抛"的计时分支
        }

        [Fact]
        public void SetAutoStage_Off_RemovesPlan_AndClearsFlag()
        {
            var sg = new SpaceGameComp(null);
            var g = Group(1);
            sg.SetAutoStage(g, true);
            Assert.Single(sg.autoStagePlans);

            sg.SetAutoStage(g, false);

            Assert.False(g.autoStageEnabled);
            Assert.Empty(sg.autoStagePlans);
        }

        [Fact]
        public void SetAutoStage_OnTwice_DoesNotDuplicate()
        {
            var sg = new SpaceGameComp(null);
            var g = Group(1);

            sg.SetAutoStage(g, true);
            sg.SetAutoStage(g, true);

            Assert.Single(sg.autoStagePlans);   // 先撤后建 ⇒ 幂等，不会叠加两条
        }

        [Fact]
        public void SetAutoStage_NullGroup_IsNoOp()
        {
            var sg = new SpaceGameComp(null);
            sg.SetAutoStage(null, true);        // 防御：面板可能传空选中
            Assert.Empty(sg.autoStagePlans);
        }

        [Fact]
        public void RebuildAutoStagePlans_RestoresOnlyEnabledGroups()
        {
            using var w = new World();
            var on = Group(11);
            var off = Group(12);
            w.SpaceGame.RegisterGroup(on);
            w.SpaceGame.RegisterGroup(off);

            w.SpaceGame.SetAutoStage(on, true);
            w.SpaceGame.SetAutoStage(off, false);

            // 读档语义：计划表本身不序列化 ⇒ 表空、开关在组上 ⇒ 重建只给"开"的组补计划。
            w.SpaceGame.CancelAutoStagePlan(on.GroupId);
            Assert.Empty(w.SpaceGame.autoStagePlans);

            w.SpaceGame.RebuildAutoStagePlans();

            var plan = Assert.Single(w.SpaceGame.autoStagePlans);
            Assert.Equal(11, plan.GroupId);
            Assert.True(on.autoStageEnabled);
            Assert.False(off.autoStageEnabled);
        }

        [Fact]
        public void ScribeRoundTrip_PersistsSwitchAndThresholds()
        {
            SafeStop();
            var src = Group(21);
            src.autoStageEnabled = true;
            src.autoStageFuelFraction = 0.075;
            src.autoStageMinSeconds = 33.0;

            string path = Path.Combine(Path.GetTempPath(), $"pfl_group_{Guid.NewGuid():N}.xml");
            Scribe.saver.InitSaving(path, GroupXmlRoot);
            src.ExposeData();
            Scribe.saver.FinalizeSaving();

            try
            {
                var dst = Group(22);
                Scribe.loader.InitLoading(path);
                dst.ExposeData();
                Scribe.ExitNode();
                Scribe.mode = LoadSaveMode.Inactive;

                Assert.True(dst.autoStageEnabled);
                Assert.Equal(0.075, dst.autoStageFuelFraction, 12);
                Assert.Equal(33.0, dst.autoStageMinSeconds, 12);
            }
            finally
            {
                SafeStop();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void SeparateStage_FailurePaths_ReportReason()
        {
            var sg = new SpaceGameComp(null);

            Assert.Null(sg.TrySeparateStage(null, out string r1));
            Assert.Equal("invalid-args", r1);

            // 空组（无实体 ⇒ 无零件 ⇒ MaxStage=0）：单级船没有可抛的，必须明确拒绝而不是静默成功。
            Assert.Null(sg.TrySeparateStage(Group(1), out string r2));
            Assert.Equal("single-stage", r2);
        }
    }
}
