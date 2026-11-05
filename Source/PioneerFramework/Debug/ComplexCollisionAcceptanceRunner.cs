using System;
using System.Collections.Generic;
using System.Globalization;
using PioneerFramework.Combat;
using PioneerFramework.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **多part 复杂实体 · 多角度碰撞一键验收**（2026-10-17，用户点名）。
    ///
    /// 一次铺 4 组**分开**的复杂多part 船对，从不同角度/偏置相撞；每组自动结算：
    /// · 是否走 part 窄相（不是组级 AABB 回退）；
    /// · 冲量公式 `j=-(1+e)vRel/(1/mA+1/mB)`、撞击后速度；
    /// · 角冲量 `Δω=I⁻¹(r×J)` 的**实际组角速度**与记录是否一致；
    /// · **总角动量守恒**（接触点一对等大反向冲量，绕质点系质心的 L_before/L_after）；
    /// · 头对头必须 Δω≈0；偏置撞击必须出现非零 Δω；且每组只撞一次（不粘/不反复炸）。
    ///
    /// "一键"= 按钮只负责布置；之后每物理步 `Tick()` 等 4 组都出现撞击记录（带超时），
    /// 自动跑判据并打印 `PflTestKit` 结论块。空闲时 `Tick()` 第一行返回，零成本。
    /// </summary>
    public static class ComplexCollisionAcceptanceRunner
    {
        private const int EarthBodyId = 1;
        private const double ApproachSpeed = 1.5;
        private const int RecordSettleTicks = 10;   // 撞击后等 pendingImpulse 落地
        private const int TimeoutTicks = 7200;      // 2 min（1×）还没撞完 ⇒ UNCHECKED

        private sealed class Scenario
        {
            public string name;
            public int aGroup, bGroup;
            public double massA, massB;
            public bool expectZeroSpin;
            public bool collided;
            public int firstRecordTick = -1;

            // 撞击后短窗口内采到的实际速度/角速度（不能等最终结算：重力会累积，S1 曾差 0.34m/s）
            public bool captured;
            public DVector3 actualVA, actualVB, actualWA, actualWB;
        }

        private static readonly List<Scenario> scenarios = new();
        private static readonly List<int> spawnedGroupIds = new();
        private static bool pending;
        private static int stageTick;
        private static bool savedDetailedCollision;

        public static bool IsRunning => pending;

        /// <summary>布置 4 组复杂多part 对撞；之后自动结算（无需再点检查）。</summary>
        public static void Stage()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var combat = Current.Game?.GetComponent<CombatManager>();
            if (spaceGame == null || combat == null)
            {
                Log.Warning("[复杂碰撞验收] 不在太空场景（缺 SpaceGameComp / CombatManager）");
                return;
            }
            if (pending)
            {
                Messages.Message("复杂碰撞验收已在跑：等它自动结算即可。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            CleanupPrevious(); // 上一轮若因异常未清干净，先按记录 id 清掉（避免同场叠两套船）
            PflTestKit.SnapshotGroups();
            scenarios.Clear();
            combat.ClearDebugCollisionRecords();
            CombatManager.CaptureCollisionRecords = true;
            savedDetailedCollision = CombatManager.DebugDetailedCollision;
            CombatManager.DebugDetailedCollision = false; // 验收走记录，不刷日志

            // 一键验收必须真的推进物理：用户可能是在暂停/0× 下点的按钮（2026-10-17 实测：
            // 布置后 60s 无任何步进、也无结算）；这里强制回 1×，与对撞演示同口径。
            spaceGame.DesiredTimeScale = TimeScaleLevel.Normal;
            Current.Game?.GetComponent<TimeScaleManager>()?.SetNormalMode();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;

            int faction = Faction.OfPlayer?.loadID ?? 0;
            DVector3 basePos = new(1.2e9, 0, 0);

            // S1：完全对称船，沿 X 正撞 ⇒ 接触点在质心连线上，Δω 必须 ≈ 0
            SpawnPair(spaceGame, faction, "S1 正撞（对称/应零转）", basePos,
                Quaternion.identity, Quaternion.identity,
                new DVector3(60, 0, 0), new DVector3(-ApproachSpeed, 0, 0),
                BuildSymmetricParts(), BuildSymmetricParts(), expectZeroSpin: true);

            // S2：Y 偏置 ⇒ 非共线接触，必须出现非零 Δω
            SpawnPair(spaceGame, faction, "S2 Y偏置", basePos + new DVector3(0, 2.0e8, 0),
                Quaternion.identity, Quaternion.identity,
                new DVector3(60, 6, 0), new DVector3(-ApproachSpeed, 0, 0),
                BuildAsymmetricParts(), BuildAsymmetricParts(), expectZeroSpin: false);

            // S3：两船不同姿态 + 斜向接近 ⇒ 三维杠杆，双轴角速度
            SpawnPair(spaceGame, faction, "S3 斜向/双旋转", basePos + new DVector3(0, 4.0e8, 0),
                Quaternion.Euler(0, 45, 0), Quaternion.Euler(10, 30, 0),
                new DVector3(42, 8, 42),
                new DVector3(-ApproachSpeed * 0.70710678, 0, -ApproachSpeed * 0.70710678),
                BuildAsymmetricParts(), BuildAsymmetricParts(), expectZeroSpin: false);

            // S4：长船侧擦（长轴沿 Z），不同角度 + Y 偏置 ⇒ 只有端部 part 接触
            SpawnPair(spaceGame, faction, "S4 长船侧擦", basePos + new DVector3(0, 6.0e8, 0),
                Quaternion.Euler(0, 20, 0), Quaternion.identity,
                new DVector3(60, 4, 0), new DVector3(-ApproachSpeed, 0, 0),
                BuildLongParts(), BuildAsymmetricParts(), expectZeroSpin: false);

            pending = true;
            stageTick = Find.TickManager?.TicksGame ?? 0;
            Log.Message($"[复杂碰撞验收] 已布置 {scenarios.Count} 组（各自独立场地）；等撞击记录后自动结算。");
            Messages.Message($"复杂碰撞验收：{scenarios.Count} 组已布置，等自动结算（约几秒）。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>每物理步调用（空闲零成本）：等 4 组都有撞击记录，再结算。</summary>
        public static void Tick()
        {
            if (!pending) return;
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var combat = Current.Game?.GetComponent<CombatManager>();
            if (spaceGame == null || combat == null) { Abort("场景不在了"); return; }

            int now = Find.TickManager?.TicksGame ?? 0;
            bool allCollided = true;
            bool allCaptured = true;
            int lastFirstTick = stageTick;
            for (int i = 0; i < scenarios.Count; i++)
            {
                var s = scenarios[i];
                if (!s.collided)
                {
                    int n = CountRecords(combat, s, out var rec);
                    if (n > 0)
                    {
                        s.collided = true;
                        s.firstRecordTick = rec.tick;
                    }
                    else
                    {
                        allCollided = false;
                    }
                }

                // 撞击后只等 10 tick 就采实际速度：等到最终结算时重力已累积（S1 实测差 0.34m/s）
                if (s.collided && !s.captured && now >= s.firstRecordTick + RecordSettleTicks)
                {
                    var a = spaceGame.GetGroupById(s.aGroup);
                    var b = spaceGame.GetGroupById(s.bGroup);
                    if (a != null && b != null)
                    {
                        s.actualVA = a.CurrentOrbit.velocitySystem.v;
                        s.actualVB = b.CurrentOrbit.velocitySystem.v;
                        s.actualWA = a.angularVelocity;
                        s.actualWB = b.angularVelocity;
                        s.captured = true;
                    }
                }

                if (!s.captured) allCaptured = false;
                if (s.firstRecordTick > lastFirstTick) lastFirstTick = s.firstRecordTick;
            }

            bool timeout = now - stageTick > TimeoutTicks;
            if (!timeout && (!allCollided || !allCaptured || now < lastFirstTick + RecordSettleTicks)) return;

            // 超时兜底：有记录的组也把当前值采下来（可能没赶上冲量落地，会如实写进判据）
            if (timeout)
            {
                for (int i = 0; i < scenarios.Count; i++)
                {
                    var s = scenarios[i];
                    if (!s.captured && s.collided)
                    {
                        var a = spaceGame.GetGroupById(s.aGroup);
                        var b = spaceGame.GetGroupById(s.bGroup);
                        if (a != null && b != null)
                        {
                            s.actualVA = a.CurrentOrbit.velocitySystem.v;
                            s.actualVB = b.CurrentOrbit.velocitySystem.v;
                            s.actualWA = a.angularVelocity;
                            s.actualWB = b.angularVelocity;
                            s.captured = true;
                        }
                    }
                }
            }

            Verify(spaceGame, combat);
            pending = false;
            CombatManager.CaptureCollisionRecords = false;
            CombatManager.DebugDetailedCollision = savedDetailedCollision;
            PflTestKit.CleanupSince();
            spawnedGroupIds.Clear();
        }

        private static void Abort(string why)
        {
            pending = false;
            CombatManager.CaptureCollisionRecords = false;
            CombatManager.DebugDetailedCollision = savedDetailedCollision;
            Log.Warning($"[复杂碰撞验收] 中止：{why}");
        }

        private static void CleanupPrevious()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) { spawnedGroupIds.Clear(); return; }
            int n = 0;
            for (int i = 0; i < spawnedGroupIds.Count; i++)
            {
                int id = spawnedGroupIds[i];
                if (sg.GetGroupById(id) == null) continue;
                sg.DestroyGroup(id);
                n++;
            }
            spawnedGroupIds.Clear();
            if (n > 0)
                Log.Message($"[复杂碰撞验收] 清理上一轮遗留 {n} 组（防止两套船叠在同场）");
        }

        private static void SpawnPair(SpaceGameComp sg, int faction, string name, DVector3 pos,
            Quaternion rotA, Quaternion rotB, DVector3 bOffset, DVector3 bVel,
            List<SpacePart> partsA, List<SpacePart> partsB, bool expectZeroSpin)
        {
            var a = CreateComplexShip(sg, faction, pos, rotA, name + "·A", DVector3.zero, partsA);
            var b = CreateComplexShip(sg, faction, pos + bOffset, rotB, name + "·B", bVel, partsB);
            spawnedGroupIds.Add(a.GroupId);
            spawnedGroupIds.Add(b.GroupId);
            scenarios.Add(new Scenario
            {
                name = name,
                aGroup = a.GroupId,
                bGroup = b.GroupId,
                massA = ShipMass(a),
                massB = ShipMass(b),
                expectZeroSpin = expectZeroSpin
            });
        }

        private static SpaceEntityGroup CreateComplexShip(SpaceGameComp sg, int factionId, DVector3 pos,
            Quaternion rot, string name, DVector3 vel, List<SpacePart> parts)
        {
            int groupId = sg.AllocGroupId();
            int entityId = sg.AllocEntityId();

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EarthBodyId
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)vel,
                rotation = (DQuaternion)rot
            };
            sg.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(rot);
            entity.SetParts(parts);
            sg.RegisterEntity(entity);
            group.AddEntity(entityId);
            return group;
        }

        private static double ShipMass(SpaceEntityGroup g)
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return 1.0;
            double m = 0.0;
            foreach (int eid in g.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null) continue;
                for (int i = 0; i < e.Parts.Count; i++)
                    if (e.Parts[i] != null) m += e.Parts[i].Mass;
            }
            return Math.Max(m, 1.0);
        }

        // ── 船型（全部是普适 SpacePart；只有质量/尺寸/位置不同）────────────────

        private static List<SpacePart> BuildSymmetricParts()
        {
            var p = new List<SpacePart>();
            // Y/Z 做得比一步 X 穿透量（约 6m）宽：保证正撞的首个接触法线是 X，
            // 不会被"深穿透 + 最小穿透轴"误判成 Y/Z 侧面。
            Add(p, new DVector3(0, 0, 0), new DVector3(6, 12, 12), 1500f);
            Add(p, new DVector3(0, 0, 9), new DVector3(4, 10, 4), 300f);
            Add(p, new DVector3(0, 0, -9), new DVector3(4, 10, 4), 300f);
            Add(p, new DVector3(6, 0, 0), new DVector3(3, 10, 12), 500f);
            Add(p, new DVector3(-6, 0, 0), new DVector3(3, 10, 12), 500f);
            Add(p, new DVector3(0, 8, 0), new DVector3(2, 2, 4), 200f);
            Add(p, new DVector3(0, -8, 0), new DVector3(2, 2, 4), 200f);
            return p;
        }

        private static List<SpacePart> BuildAsymmetricParts()
        {
            var p = new List<SpacePart>();
            Add(p, new DVector3(0, 0, 0), new DVector3(10, 7, 14), 2000f);
            Add(p, new DVector3(2, 0, 9), new DVector3(4, 4, 6), 500f);
            Add(p, new DVector3(-1, 0, -9), new DVector3(6, 4, 4), 700f);
            Add(p, new DVector3(8, 1, 0), new DVector3(3, 4, 10), 600f);
            Add(p, new DVector3(-6, -2, 0), new DVector3(2, 3, 6), 300f);
            Add(p, new DVector3(0, 6, -3), new DVector3(1, 3, 8), 200f);
            Add(p, new DVector3(5, 0, 4), new DVector3(1, 4, 3), 150f);
            return p;
        }

        private static List<SpacePart> BuildLongParts()
        {
            var p = new List<SpacePart>();
            Add(p, new DVector3(0, 0, 0), new DVector3(4, 8, 8), 800f);
            for (int i = -3; i <= 3; i++)
                Add(p, new DVector3(0, 0, i * 6), new DVector3(6, 5, 5), 450f);
            return p;
        }

        private static void Add(List<SpacePart> parts, DVector3 pos, DVector3 size, float mass)
        {
            var part = new SpacePart
            {
                partId = parts.Count + 1,
                defName = "PF_ShipPart_Cube",
                positionShip = pos,
                boundsSizeShip = size,
                hp = 10000f,
                isIndestructible = true
            };
            part.AddComponent(new HullMassComponent { mass = mass });
            parts.Add(part);
        }

        // ── 结算 ────────────────────────────────────────────────────────────

        private static void Verify(SpaceGameComp sg, CombatManager combat)
        {
            PflTestKit.Begin("多part 复杂实体 · 多角度碰撞 / 角动量", "多part");
            int hits = 0;
            int missing = 0;
            var missingNames = new System.Text.StringBuilder();
            for (int i = 0; i < scenarios.Count; i++)
            {
                var s = scenarios[i];
                var a = sg.GetGroupById(s.aGroup);
                var b = sg.GetGroupById(s.bGroup);
                if (a == null || b == null)
                {
                    missing++;
                    missingNames.Append(missingNames.Length == 0 ? "" : "、").Append(s.name).Append("(编队不存在)");
                    continue;
                }

                int n = CountRecords(combat, s, out var rec);
                if (n == 0)
                {
                    missing++;
                    missingNames.Append(missingNames.Length == 0 ? "" : "、").Append(s.name).Append("(无撞击记录)");
                    continue;
                }
                hits++;
                LogCollisionRecords(combat, s);

                PflTestKit.Check(PflTestKit.Kind.Invariant, rec.usedPartPair,
                    $"{s.name} 走 part 窄相（非组级回退）");
                PflTestKit.Check(PflTestKit.Kind.Invariant, rec.vRel > 0.0,
                    $"{s.name} 记录为接近速度", $"vRel={rec.vRel:F3}");

                double e = rec.vRel >= 1.0 ? 0.2 : 0.0;
                double jExp = -(1.0 + e) * rec.vRel / (1.0 / s.massA + 1.0 / s.massB);
                PflTestKit.Check(PflTestKit.Kind.Value,
                    Math.Abs(rec.j - jExp) <= 1e-6 * Math.Max(1.0, Math.Abs(jExp)),
                    $"{s.name} 冲量公式", $"j={rec.j:F3} exp={jExp:F3}");

                DVector3 actualVA = s.captured ? s.actualVA : a.CurrentOrbit.velocitySystem.v;
                DVector3 actualVB = s.captured ? s.actualVB : b.CurrentOrbit.velocitySystem.v;
                PflTestKit.Check(PflTestKit.Kind.Value,
                    (actualVA - rec.vaAfter).magnitude < 0.05 && (actualVB - rec.vbAfter).magnitude < 0.05,
                    $"{s.name} 撞击后速度", $"|ΔvA|={(actualVA - rec.vaAfter).magnitude:E2} |ΔvB|={(actualVB - rec.vbAfter).magnitude:E2}");

                PflTestKit.Check(PflTestKit.Kind.Invariant, n == 1,
                    $"{s.name} 只撞一次（不粘/不连撞）", $"记录数={n}");

                DVector3 actualWA = s.captured ? s.actualWA : a.angularVelocity;
                DVector3 actualWB = s.captured ? s.actualWB : b.angularVelocity;
                DVector3 expectedA = rec.wA0 + rec.dwA;
                DVector3 expectedB = rec.wB0 + rec.dwB;
                PflTestKit.Check(PflTestKit.Kind.Value,
                    (actualWA - expectedA).magnitude < 1e-9
                    && (actualWB - expectedB).magnitude < 1e-9,
                    $"{s.name} Δω 落到组角速度",
                    $"|ΔωA实际-记录|={(actualWA - expectedA).magnitude:E2} |ΔωB|={(actualWB - expectedB).magnitude:E2}");

                if (s.expectZeroSpin)
                {
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        rec.dwA.magnitude < 1e-7 && rec.dwB.magnitude < 1e-7,
                        $"{s.name} 头对头 Δω≈0",
                        $"|ΔωA|={rec.dwA.magnitude:E2} |ΔωB|={rec.dwB.magnitude:E2}");
                }
                else
                {
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        rec.dwA.magnitude + rec.dwB.magnitude > 1e-6,
                        $"{s.name} 偏置撞击产生非零 Δω",
                        $"|ΔωA|={rec.dwA.magnitude:E3} |ΔωB|={rec.dwB.magnitude:E3}");
                }

                DVector3 l0 = TotalAngularMomentum(a, b, rec, s.massA, s.massB, false);
                DVector3 l1 = TotalAngularMomentum(a, b, rec, s.massA, s.massB, true);
                double rel = (l1 - l0).magnitude / Math.Max(1.0, l0.magnitude);
                PflTestKit.Check(PflTestKit.Kind.Value, rel < 1e-6,
                    $"{s.name} 总角动量守恒（L_before≈L_after）",
                    $"|ΔL|={ (l1 - l0).magnitude:E3} 相对={rel:E2}");

                Log.Message($"[复杂碰撞验收] {s.name}: j={rec.j:F1} vRel={rec.vRel:F2} "
                    + $"ΔωA={rec.dwA.magnitude:E2} ΔωB={rec.dwB.magnitude:E2} "
                    + $"L相对误差={rel:E2} 记录数={n}");
            }

            if (missing > 0)
                PflTestKit.Unchecked($"{missing}/{scenarios.Count} 组未产生撞击记录", missingNames.ToString());
            else
                PflTestKit.End();
            Log.Message($"[复杂碰撞验收] 自动结算完成：{hits}/{scenarios.Count} 组有撞击记录；"
                + "判据见上方 PflTestKit 结论块。");
        }

        /// <summary>诊断：把匹配本场景的每条撞击记录逐条打出（多 contact 连撞定位用）。</summary>
        private static void LogCollisionRecords(CombatManager combat, Scenario s)
        {
            var list = combat.DebugCollisionRecords;
            var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                bool match = (r.aGroup == s.aGroup && r.bGroup == s.bGroup)
                          || (r.aGroup == s.bGroup && r.bGroup == s.aGroup);
                if (!match) continue;
                Log.Message("[复杂碰撞验收·记录] " + s.name
                    + " tick=" + r.tick.ToString(ci)
                    + " A=" + r.aGroup.ToString(ci) + " B=" + r.bGroup.ToString(ci)
                    + " vRel=" + r.vRel.ToString("F3", ci)
                    + " n=(" + r.n.x.ToString("F3", ci) + "," + r.n.y.ToString("F3", ci) + "," + r.n.z.ToString("F3", ci) + ")"
                    + " contact=(" + r.contact.x.ToString("F1", ci) + "," + r.contact.y.ToString("F1", ci) + "," + r.contact.z.ToString("F1", ci) + ")"
                    + " j=" + r.j.ToString("F1", ci)
                    + " partPair=" + r.usedPartPair
                    + " pa=(" + r.pa.x.ToString("F1", ci) + "," + r.pa.y.ToString("F1", ci) + "," + r.pa.z.ToString("F1", ci) + ")"
                    + " pb=(" + r.pb.x.ToString("F1", ci) + "," + r.pb.y.ToString("F1", ci) + "," + r.pb.z.ToString("F1", ci) + ")");
            }
        }

        private static int CountRecords(CombatManager combat, Scenario s,
            out CombatManager.CollisionDebugRecord first)
        {
            first = default;
            int n = 0;
            var list = combat.DebugCollisionRecords;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                bool match = (r.aGroup == s.aGroup && r.bGroup == s.bGroup)
                          || (r.aGroup == s.bGroup && r.bGroup == s.aGroup);
                if (!match) continue;
                if (n == 0)
                    first = r.aGroup == s.aGroup ? r : SwapRecord(r);
                n++;
            }
            return n;
        }

        /// <summary>把记录的 A/B 翻成场景的 A/B（枚举顺序不保证，S4 曾因顺序互换把 L 算成 2.39）。</summary>
        private static CombatManager.CollisionDebugRecord SwapRecord(in CombatManager.CollisionDebugRecord r)
        {
            return new CombatManager.CollisionDebugRecord
            {
                tick = r.tick,
                aGroup = r.bGroup,
                bGroup = r.aGroup,
                pa = r.pb,
                pb = r.pa,
                rotA = r.rotB,
                rotB = r.rotA,
                va = r.vb,
                vb = r.va,
                vaAfter = r.vbAfter,
                vbAfter = r.vaAfter,
                n = -r.n,
                contact = r.contact,
                j = r.j,
                vRel = r.vRel,
                wA0 = r.wB0,
                wB0 = r.wA0,
                dwA = r.dwB,
                dwB = r.dwA,
                usedPartPair = r.usedPartPair
            };
        }

        /// <summary>绕质点系质心的总角动量：轨道项 + 自旋项（自旋用记录姿态下的惯量张量）。</summary>
        private static DVector3 TotalAngularMomentum(SpaceEntityGroup a, SpaceEntityGroup b,
            in CombatManager.CollisionDebugRecord rec, double massA, double massB, bool after)
        {
            DVector3 comA = ComWorld(a, rec.pa, rec.rotA);
            DVector3 comB = ComWorld(b, rec.pb, rec.rotB);
            DVector3 systemCom = (comA * massA + comB * massB) / (massA + massB);

            DVector3 va = after ? rec.vaAfter : rec.va;
            DVector3 vb = after ? rec.vbAfter : rec.vb;
            DVector3 wa = after ? rec.wA0 + rec.dwA : rec.wA0;
            DVector3 wb = after ? rec.wB0 + rec.dwB : rec.wB0;

            return DVector3.Cross(comA - systemCom, va * massA)
                 + DVector3.Cross(comB - systemCom, vb * massB)
                 + SpinMomentum(a, wa, rec.rotA)
                 + SpinMomentum(b, wb, rec.rotB);
        }

        private static DVector3 ComWorld(SpaceEntityGroup g, DVector3 groupPos, Quaternion rot)
        {
            var r = g.InertiaResult;
            return groupPos + DVector3.Rotate(new DVector3(r.comX, r.comY, r.comZ), rot);
        }

        /// <summary>自旋角动量 H = R·I_p·Rᵀ·ω（与 InverseInertiaMultiply 同一份方向矩阵）。</summary>
        private static DVector3 SpinMomentum(SpaceEntityGroup g, DVector3 w, Quaternion rot)
        {
            var r = g.InertiaResult;
            if (!r.HasData || !(r.p1 > 0.0)) return DVector3.zero;

            DVector3 wb = DVector3.Rotate(w, Quaternion.Inverse(rot));
            double a = r.r00 * wb.x + r.r10 * wb.y + r.r20 * wb.z;
            double b = r.r01 * wb.x + r.r11 * wb.y + r.r21 * wb.z;
            double c = r.r02 * wb.x + r.r12 * wb.y + r.r22 * wb.z;
            double ha = r.p1 * a, hb = r.p2 * b, hc = r.p3 * c;
            DVector3 hBody = new(
                r.r00 * ha + r.r01 * hb + r.r02 * hc,
                r.r10 * ha + r.r11 * hb + r.r12 * hc,
                r.r20 * ha + r.r21 * hb + r.r22 * hc);
            return DVector3.Rotate(hBody, rot);
        }
    }
}
