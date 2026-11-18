using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 行星际航线演示（2026-11-05）：Rimata → 赭砂。
    ///
    /// 只铺**日心转移段**：从 Rimata 轨道位置（已推出 Rimata SOI）出发，用 Gooding Lambert
    /// 求日心转移速度，船以恒星帧自由飞行；抵达时若进赭砂 SOI，`CheckGroupSoi` 会自动换系。
    /// 星历用本地纯解析传播（与 `GetBodyPositionParentCore` 同公式），不查 runtime 的已提交时间线
    /// ——否则 `now+TOF` 会抛 `CelestialMotionUnavailable: TimeUnavailable`。
    /// </summary>
    internal static class InterplanetaryRouteDemo
    {
        public static void SpawnRimataToZheSha()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int rimata = FindBodyId(mgr, "Earth");     // Rimata 的 Def 仍是 Earth
            int zheSha = FindBodyId(mgr, "ZheSha");    // 赭砂
            var rimataDef = mgr.GetBodyById(rimata);
            var zheShaDef = mgr.GetBodyById(zheSha);
            if (rimataDef == null || zheShaDef == null || !rimataDef.HasOrbit || !zheShaDef.HasOrbit ||
                rimataDef.orbitPeriod <= 0 || zheShaDef.orbitPeriod <= 0)
            {
                Log.Error($"[行星际演示] 天体解析失败：Rimata(id={rimata}) 赭砂(id={zheSha})");
                return;
            }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            // 注意：GetReferenceFrameId(行星) 返回的是**行星自身**（帧根 = 自身），不是父恒星；
            // 日心转移必须用主恒星的 GM。
            var star = mgr.GetStar();
            if (star == null)
            {
                Log.Error("[行星际演示] 主恒星解析失败");
                return;
            }
            int starId = star.bodyId;
            double muStar = star.gravitationalParameter;

            (DVector3 r1, DVector3 v1) = Circular(rimataDef, now);
            double a1 = r1.magnitude;
            double a2 = Circular(zheShaDef, now).pos.magnitude;
            Log.Message($"[行星际演示·诊断] Rimata id={rimata} def={rimataDef.defName} type={rimataDef.bodyType} " +
                $"R={rimataDef.orbitRadius:E4} T={rimataDef.orbitPeriod:F0} ph={rimataDef.initialPhase:F4}" +
                $"｜赭砂 id={zheSha} def={zheShaDef.defName} type={zheShaDef.bodyType} " +
                $"R={zheShaDef.orbitRadius:E4} T={zheShaDef.orbitPeriod:F0} ph={zheShaDef.initialPhase:F4}" +
                $"｜a1={a1:E4} a2={a2:E4} now={now:F0}");
            if (a1 <= 0 || a2 <= 0)
            {
                Log.Error("[行星际演示] 位置非法");
                return;
            }

            // 霍曼半周期作为 TOF；起点推到 Rimata SOI 外 15%，避免一铺出来就被判"进 SOI"。
            double tof = Math.PI * Math.Sqrt(Math.Pow(0.5 * (a1 + a2), 3) / muStar);
            double soiRimata = OrbitAdapter.SoiRadiusApprox(rimataDef, mgr);
            if (!(soiRimata > 0)) soiRimata = 0.01 * a1;
            DVector3 r1Out = r1 * (1.0 + 1.15 * soiRimata / a1);

            (DVector3 r2, _) = Circular(zheShaDef, now + tof);
            (DVector3 vi, DVector3 vf) = Gooding.Solve(muStar, r1Out, r2, tof);
            double dv1 = (vi - v1).magnitude;

            var group = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, starId,
                r1Out, vi, out int _, overThrust: true, circularOrbit: false);
            if (group == null)
            {
                Log.Error("[行星际演示] 铺船失败");
                return;
            }
            group.CelestialBodyId = starId;

            Log.Message($"[行星际演示] Rimata → 赭砂｜TOF={tof:F0}s（{tof / 60000.0:F2} 游戏日）" +
                $"｜日心转移 v={vi.magnitude:F0} m/s，Δv≈{dv1:F0} m/s，到达 v={vf.magnitude:F0} m/s" +
                $"｜Rimata SOI≈{soiRimata / 1000.0:F0} km，起点推到 1.15×SOI 外" +
                $"｜G{group.GroupId} 已铺在恒星帧 r={r1Out.magnitude / 1e9:F3}e9 m" +
                $"｜出发 tick={now:F0}，预计到达 tick={now + tof:F0}。" +
                "开时间加速观察「进赭砂 SOI → 自动换系」。");
        }

        /// <summary>**1 天内到达**（2026-11-18 用户要求）：跳过扫描，直接指定 tof=1 天解 Lambert；
        /// Δv 超预算（ve·ln(湿/干)×0.9 ≈ 2,560 km/s）会被明确拒绝。</summary>
        public static void TorchTwoDayArrival() => SpawnTorchProbeAndPlan(1.0, 432_000.0);   // 1 g / 5 天（复刻台已验）

        public static void TorchOneDayArrival() => SpawnTorchProbeAndPlan(1.0, 259_200.0);   // 1 g / 3 天（复刻台已验）

        /// <summary>**6 小时到达**：更激进的一档，用来看 Δv 预算的上限在哪。</summary>
        public static void TorchSixHourArrival() => SpawnTorchProbeAndPlan(1.0, 21_600.0);
        /// <summary>火炬转移三档的菜单入口（0.3 g / 1 g / 3 g）。</summary>
        public static void TorchProbe03() => SpawnTorchProbeAndPlan(0.3);
        public static void TorchProbe1() => SpawnTorchProbeAndPlan(1.0);
        public static void TorchProbe3() => SpawnTorchProbeAndPlan(3.0);

        /// <summary>
        /// **火炬转移一键**（2026-11-18，用户定稿）：铺一艘**聚变船**（ve=1000 km/s）在恒星帧，
        /// 然后按选定的起始加速度直接规划"加速半程 + 自动翻转减速半程"。
        ///
        /// 与弹道版的区别：不等窗口、不算 Lambert、不算相位——直线过去，到点相对速度归零。
        /// </summary>
        public static void SpawnTorchProbeAndPlan(double accelerationInG, double forcedTimeOfFlightSeconds = 0.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int zheSha = FindBodyId(mgr, "ZheSha");
            int rimataId = FindBodyId(mgr, "Earth");
            var star = mgr.GetStar();
            var rimata = mgr.GetBodyById(rimataId);
            if (zheSha < 0 || star == null || rimata == null || !rimata.HasOrbit)
            {
                Log.Error("[火炬·一键] 天体解析失败（赭砂 / 主恒星 / Rimata）");
                return;
            }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 earthPosition = mgr.GetBodyPositionParent(rimataId, now);
            DVector3 earthVelocity = mgr.GetBodyVelocityParent(rimataId, now);
            if (!(earthPosition.magnitude > 0.0)) { Log.Error("[火炬·一键] Rimata 位置无效"); return; }

            double soi = OrbitAdapter.SoiRadiusApprox(rimata, mgr);
            if (!(soi > 0.0)) soi = 0.01 * earthPosition.magnitude;
            double earthRadius = earthPosition.magnitude;
            DVector3 position = earthPosition * (1.0 + 1.2 * soi / earthRadius);
            double radius = position.magnitude;
            double speed = Math.Sqrt(star.gravitationalParameter / radius);
            DVector3 direction = earthVelocity.magnitude > 1e-9 ? earthVelocity.normalized : position.normalized;
            DVector3 velocity = direction * speed;

            var group = SpawnFusionProbe(spaceGame, star.bodyId, position, velocity, accelerationInG);
            if (group == null) { Log.Error("[火炬·一键] 铺船失败（工质 Def 缺失？）"); return; }

            Log.Message($"[火炬·一键] 已铺聚变船 G{group.GroupId}（ve=1000 km/s，干重 5 t + 工质 60 t）");
            if (spaceGame.TryBeginTorchTransfer(group, zheSha, accelerationInG, out string reason, MainTabWindow_PFView.transferTimeBias, forcedTimeOfFlightSeconds))
            {
                Messages.Message($"火炬转移已规划（{accelerationInG:F1} g），两段节点已排队。",
                    RimWorld.MessageTypeDefOf.TaskCompletion, false);
            }
            else
            {
                Log.Error($"[火炬·一键] 铺船成功（G{group.GroupId}），但规划失败：{reason}");
                Messages.Message("火炬规划失败：" + reason, RimWorld.MessageTypeDefOf.RejectInput, false);
            }
        }

        /// <summary>
        /// **调试：把转移中的船瞬移到目标 SOI 外 5%**（2026-11-18）。调试一次转移要等十几天，
        /// 这个动作跳过等待 + 巡航，直接验证"进 SOI → 捕获"这一半链路。
        /// </summary>
        public static void JumpToTargetSoi()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            double now = TimeScaleManager.GetPhysicsTickSafe();
            SpaceEntityGroup? picked = null;
            var target = (CelestialBodyInfo?)null;
            foreach (var group in spaceGame.GetAllGroups())
            {
                if (group == null || group.interplanetaryTargetBodyId < 0) continue;
                if (group.interplanetaryPhase != SpaceGameComp.InterplanetaryPhaseOutbound) continue;
                var body = mgr.GetBodyById(group.interplanetaryTargetBodyId);
                if (body == null) continue;
                picked = group;
                target = body;
                break;
            }
            if (picked == null || target == null)
            {
                Log.Warning("[火炬·调试] 没有处于 Outbound 的转移船；先点一次火炬转移。");
                return;
            }

            DVector3 targetPosition = mgr.GetBodyPositionParent(target.bodyId, now);
            DVector3 targetVelocity = mgr.GetBodyVelocityParent(target.bodyId, now);
            DVector3 shipPosition = picked.CurrentOrbit.positionSystem.v;
            DVector3 away = shipPosition - targetPosition;
            if (!(away.magnitude > 1.0)) away = new DVector3(1, 0, 0);
            double soi = OrbitAdapter.SoiRadiusApprox(target, mgr);
            if (!(soi > 0.0)) soi = 1.0e8;

            DVector3 newPosition = targetPosition + away.normalized * (soi * 1.05);
            // 保留当前速度相对目标的分量，只把位置搬到 SOI 边缘（进 SOI 后由捕获钩子处理）
            picked.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)newPosition,
                velocitySystem = (SystemVec3)targetVelocity + (SystemVec3)(picked.CurrentOrbit.velocitySystem.v - targetVelocity),
                rotation = picked.CurrentOrbit.rotation,
            };
            Log.Message($"[火炬·调试] G{picked.GroupId} 已瞬移到 {target.label} SOI 外 5%（r={newPosition.magnitude / 1e9:F3}e9 m，SOI={soi / 1e6:F1}e6 m）；几秒内应触发换系 + 捕获。");
        }
        /// <summary>聚变调试船：干重 5 t、工质 12 t、ve=1e6 m/s（1000 km/s）；ṁ 按选定 g 配平（F = m₀·g）。</summary>
        private static SpaceEntityGroup? SpawnFusionProbe(SpaceGameComp spaceGame, int starId, DVector3 position,
            DVector3 velocity, double accelerationInG)
        {
            var hydrogen = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            if (hydrogen == null) return null;

            int factionId = Faction.OfPlayer?.loadID ?? 0;
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true, IsCollidable = true, CelestialBodyId = starId,
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)position, velocitySystem = (SystemVec3)velocity, rotation = DQuaternion.identity,
            };
            spaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false, IsMapArchived = false, IsCollidable = true, IsMovable = true,
                CanInteract = false, IsAffectedByGravity = true,
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.mainAxisShip = new DVector3(0, 0, -1);

            var part = new SpacePart
            {
                partId = 1, defName = "PF_ShipPart_Cube", positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(40.0, 20.0, 80.0), hp = 1000f, isIndestructible = false,
            };
            part.AddComponent(new HullMassComponent { mass = 5000f });
            entity.SetParts(new List<SpacePart> { part });

            // 聚变档：ve=1000 km/s；ṁ = m₀·g/ve ⇒ 起始加速度正好等于选定档位（之后随质量下降上升）。
            // 罐容 50 t：1 g 火炬走 22.6e9 m 约需 42 t 工质（恒加速模型，2026-11-18 实测口径）。
            const double dryMass = 5_000.0;
            const double propellantCapacity = 60_000.0;
            double massFlow = (dryMass + propellantCapacity) * (accelerationInG * 9.80665) / 1_000_000.0;
            entity.AddPropulsion(new PropulsionData
            {
                propellant = hydrogen, ve = 1_000_000f, massFlow = (float)massFlow,
                mass = (float)propellantCapacity, capacity = (float)propellantCapacity,
            });

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);
            return group;
        }

        /// <summary>
        /// **一键：铺一艘恒星帧的船 + 直接规划粗转移 → 赭砂**（2026-11-18）。
        ///
        /// 为什么要有：验证这条功能不该要求玩家先自己造船、再自己开出 SOI —— 一步到位，
        /// 点完就能在日志里看到"Δv / 等待 / 转移时长"，之后等点火、等进 SOI 看捕获。
        /// 船是**真船**（`CreateNoMapShipGroup`，带氢/肼工质与引擎），不是演示用的空壳。
        /// </summary>
        public static void SpawnTransferProbeAndPlan()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int zheSha = FindBodyId(mgr, "ZheSha");
            int rimataId = FindBodyId(mgr, "Earth");     // Rimata 的 Def 仍是 Earth
            var star = mgr.GetStar();
            var rimata = mgr.GetBodyById(rimataId);
            if (zheSha < 0 || star == null || rimata == null || !rimata.HasOrbit)
            {
                Log.Error("[粗转移·一键] 天体解析失败（赭砂 / 主恒星 / Rimata）");
                return;
            }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 earthPosition = mgr.GetBodyPositionParent(rimataId, now);
            DVector3 earthVelocity = mgr.GetBodyVelocityParent(rimataId, now);
            if (!(earthPosition.magnitude > 0.0)) { Log.Error("[粗转移·一键] Rimata 位置无效"); return; }

            // 起点：Rimata 日心位置再往外推 1.2 倍 SOI（确保一铺出来就在恒星帧、不在 SOI 内）。
            double soi = OrbitAdapter.SoiRadiusApprox(rimata, mgr);
            if (!(soi > 0.0)) soi = 0.01 * earthPosition.magnitude;
            double earthRadius = earthPosition.magnitude;
            DVector3 position = earthPosition * (1.0 + 1.2 * soi / earthRadius);

            // 速度：该半径的日心圆轨速度，方向取 Rimata 当前运动方向（顺行）。
            double radius = position.magnitude;
            double speed = Math.Sqrt(star.gravitationalParameter / radius);
            DVector3 direction = earthVelocity.magnitude > 1e-9 ? earthVelocity.normalized : position.normalized;
            DVector3 velocity = direction * speed;

            var group = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, star.bodyId, position, velocity,
                out int _, overThrust: true, circularOrbit: false);
            if (group == null) { Log.Error("[粗转移·一键] 铺船失败"); return; }

            Log.Message($"[粗转移·一键] 已铺 G{group.GroupId}：恒星帧 r={radius / 1e9:F3}e9 m"
                + $"、v={speed:F0} m/s（Rimata SOI≈{soi / 1000.0:F0} km，起点在其 1.2 倍外）");

            if (spaceGame.TryBeginInterplanetaryTransfer(group, zheSha, out string reason))
            {
                Log.Message($"[粗转移·一键] G{group.GroupId} 规划成功，等日志里的点火 tick。");
                Messages.Message("粗转移已规划：等窗口点火，进赭砂 SOI 自动减速捕获。", RimWorld.MessageTypeDefOf.TaskCompletion, false);
            }
            else
            {
                Log.Error($"[粗转移·一键] 船已铺（G{group.GroupId}），但规划失败：{reason}");
                Messages.Message("铺船成功但规划失败：" + reason, RimWorld.MessageTypeDefOf.RejectInput, false);
            }
        }

        /// <summary>
        /// **真船的粗转移**（2026-11-18）：对现有的、处于恒星帧的自由飞行玩家船排一次
        /// `TryBeginInterplanetaryTransfer(赭砂)`。与上面的演示不同：这里不铺新船，走正式入口
        /// （窗口扫描 → 排出发点火 → 进 SOI 时减速捕获）。
        /// </summary>
        public static void TransferExistingShipToZheSha()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int zheSha = FindBodyId(mgr, "ZheSha");
            var star = mgr.GetStar();
            if (zheSha < 0 || star == null)
            {
                Log.Error("[粗转移] 天体解析失败：赭砂或主恒星缺失");
                return;
            }

            var candidates = new List<SpaceEntityGroup>();
            foreach (var group in spaceGame.GetAllGroups())
            {
                if (group == null) continue;
                if (group.CelestialBodyId != star.bodyId) continue;          // 必须已在恒星帧（出了 SOI）
                if (group.interplanetaryPhase != SpaceGameComp.InterplanetaryPhaseNone) continue;
                if (group.landed || group.IsCircularOrbit || group.HasActiveBurn) continue;
                candidates.Add(group);
            }

            if (candidates.Count == 0)
            {
                Log.Warning("[粗转移] 没有可用的船：需要先把船开出地月系 SOI（处于恒星帧、自由飞行、非圆轨）。");
                Messages.Message("没有处于恒星帧的自由飞行船：先把船开出 SOI 再点。", RimWorld.MessageTypeDefOf.RejectInput, false);
                return;
            }

            int accepted = 0;
            foreach (var group in candidates)
            {
                if (spaceGame.TryBeginInterplanetaryTransfer(group, zheSha, out string reason))
                {
                    accepted++;
                }
                else
                {
                    Log.Warning($"[粗转移] G{group.GroupId} 规划失败：{reason}");
                }
            }

            Log.Message($"[粗转移] 已为 {accepted}/{candidates.Count} 艘恒星帧中的船排入赭砂转移"
                + "（看 [粗转移] 日志里的 Δv / 等待 / 转移时长；到点自动点火，进 SOI 自动捕获）。");
            Messages.Message($"粗转移已规划 {accepted}/{candidates.Count} 艘。", RimWorld.MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>快速 SOI 入射：铺在赭砂 SOI 外 1.1R，相对赭砂 10 km/s 向内，秒级进 SOI 触发换系。</summary>
        public static void SpawnZheShaSoiEntry()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int zheSha = FindBodyId(mgr, "ZheSha");
            var def = mgr.GetBodyById(zheSha);
            var star = mgr.GetStar();
            if (def == null || !def.HasOrbit || star == null)
            {
                Log.Error($"[SOI入射演示] 天体解析失败：赭砂(id={zheSha})");
                return;
            }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            (DVector3 pos, DVector3 vel) = Circular(def, now);
            double soi = OrbitAdapter.SoiRadiusApprox(def, mgr);
            if (!(soi > 0))
            {
                Log.Error("[SOI入射演示] 赭砂 SOI 半径无效");
                return;
            }

            DVector3 dir = pos.normalized;                 // 太阳 → 赭砂 径向
            DVector3 shipPos = pos + dir * (1.1 * soi);    // SOI 外 0.1R
            DVector3 shipVel = vel - dir * 10000.0;        // 相对赭砂 10 km/s 向内
            double eta = 0.1 * soi / 10000.0;

            var group = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, star.bodyId,
                shipPos, shipVel, out int _, overThrust: true, circularOrbit: false);
            if (group == null)
            {
                Log.Error("[SOI入射演示] 铺船失败");
                return;
            }
            group.CelestialBodyId = star.bodyId;

            Log.Message($"[SOI入射演示] 赭砂 SOI≈{soi / 1000.0:F0} km｜G{group.GroupId} 铺在 1.1×SOI" +
                $"（距赭砂 {1.1 * soi / 1000.0:F0} km）｜相对速度 10 km/s 向内" +
                $"｜预计 {eta:F0} 物理秒后进 SOI（加速后观察 [换系] 日志）");
        }

        /// <summary>
        /// 关键点探针：在 Rimata / 赭砂 周围撒 7 个组，逐个打印
        /// 帧根、|posBody|、源表成员、盒外 gap（验证“盒内恒星=0 / 盒外近旁带”）。
        /// A Rimata 低轨 · B Rimata 盒外近旁 · C Rimata 盒外远处 · D 日心中点 ·
        /// E 赭砂盒外近旁 · F 赭砂低轨 · G 赭砂表面 10 km（应触发撞击结算）。
        /// </summary>
        public static void SpawnKeyPointProbes()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;

            int rimataId = FindBodyId(mgr, "Earth");
            int zheShaId = FindBodyId(mgr, "ZheSha");
            var rimata = mgr.GetBodyById(rimataId);
            var zheSha = mgr.GetBodyById(zheShaId);
            var star = mgr.GetStar();
            if (rimata == null || zheSha == null || star == null) { Log.Error("[关键点] 天体缺失"); return; }

            double now = TimeScaleManager.GetPhysicsTickSafe();
            (DVector3 rE, DVector3 vE) = Circular(rimata, now);
            (DVector3 rZ, DVector3 vZ) = Circular(zheSha, now);
            double soiE = OrbitAdapter.SoiRadiusApprox(rimata, mgr);
            double soiZ = OrbitAdapter.SoiRadiusApprox(zheSha, mgr);
            double rELeo = rimata.radius + 500_000.0;
            double rZLeo = zheSha.radius + 200_000.0;
            var sources = new List<GravitySource>();

            void Place(string tag, int bodyId, DVector3 pos, DVector3 vel, bool circularOrbit)
            {
                var g = DebugShipSpawner.CreateNoMapShipGroup(spaceGame, bodyId, pos, vel, out int _,
                    overThrust: true, circularOrbit: circularOrbit);
                if (g == null) { Log.Error($"[关键点] {tag} 铺船失败"); return; }
                sources.Clear();
                mgr.CollectSystemSources(bodyId, now, sources);
                int frameRoot = mgr.GetReferenceFrameId(bodyId);
                double posBody = g.GetBodyPosition(now).v.magnitude;

                string gapText = "";
                if (mgr.GetBodyById(bodyId)?.bodyType == CelestialBodyType.Star)
                {
                    DVector3 relE = mgr.GetBodyPositionParent(rimataId, now);
                    DVector3 dE = pos - relE;
                    double gapE = Math.Max(Math.Abs(dE.x), Math.Max(Math.Abs(dE.y), Math.Abs(dE.z))) - soiE;
                    DVector3 relZ = mgr.GetBodyPositionParent(zheShaId, now);
                    DVector3 dZ = pos - relZ;
                    double gapZ = Math.Max(Math.Abs(dZ.x), Math.Max(Math.Abs(dZ.y), Math.Abs(dZ.z))) - soiZ;
                    gapText = $" gapE={gapE / 1000.0:F0}km(带 {soiE * 0.5 / 1000.0:F0}km) gapZ={gapZ / 1000.0:F0}km";
                }
                Log.Message($"[关键点] {tag} G{g.GroupId} body={bodyId} frame={frameRoot}｜|posBody|={posBody:E3} m" +
                    $"｜|posFrame|={g.CurrentOrbit.positionSystem.v.magnitude:E3} m｜源=[{string.Join(",", sources.Select(s => s.bodyId))}]" +
                    gapText);
            }

            Place("A Rimata低轨", rimataId, new DVector3(rELeo, 0, 0),
                new DVector3(0, 0, -Math.Sqrt(rimata.gravitationalParameter / rELeo)), circularOrbit: true);
            Place("B Rimata近旁", star.bodyId, rE * (1.0 + 1.2 * soiE / rE.magnitude), vE, circularOrbit: true);
            Place("C Rimata远处", star.bodyId, rE * (1.0 + 3.0 * soiE / rE.magnitude), vE, circularOrbit: true);
            Place("D 日心中点", star.bodyId, (rE + rZ) * 0.5, vE, circularOrbit: true);
            Place("E 赭砂近旁", star.bodyId, rZ * (1.0 + 1.2 * soiZ / rZ.magnitude), vZ, circularOrbit: true);
            Place("F 赭砂低轨", zheShaId, new DVector3(rZLeo, 0, 0),
                new DVector3(0, 0, -Math.Sqrt(zheSha.gravitationalParameter / rZLeo)), circularOrbit: true);
            Place("G 赭砂表面10km", zheShaId, new DVector3(zheSha.radius + 10_000.0, 0, 0), DVector3.zero, circularOrbit: false);
        }

        /// <summary>纯解析圆轨（与 CelestialBodyManager.GetBodyPositionParentCore 同公式）。</summary>
        private static (DVector3 pos, DVector3 vel) Circular(CelestialBodyInfo body, double tick)
        {
            double w = 2.0 * Math.PI / body.orbitPeriod;
            double phase = body.initialPhase + w * tick;
            body.GetOrbitFrame(out var refX, out var refY);
            double c = Math.Cos(phase), s = Math.Sin(phase);
            return (body.orbitRadius * (c * refX + s * refY),
                    (body.orbitRadius * w) * (-s * refX + c * refY));
        }

        private static int FindBodyId(CelestialBodyManager mgr, string defName)
        {
            foreach (var body in mgr.GetAllBodies())
                if (string.Equals(body.defName, defName, StringComparison.Ordinal)) return body.bodyId;
            return -1;
        }
    }
}
