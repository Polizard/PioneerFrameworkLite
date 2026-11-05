using System.Collections.Generic;
using PioneerFramework.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **来袭演示场景（[V7]+[V3] 的可视化验收，2026-10-02）** —— 一次点击铺好，肉眼就能看出认知层在挡什么。
    ///
    /// ## 阵型（全部相对我方观测者摆位；观测者 = 我方那艘船，灵 1000 km）
    /// | 棋子 | 距离 | 静默时 | 一键动作后 |
    /// |---|---|---|---|
    /// | **我方船**（观测者） | 0 | 可见 | — |
    /// | **我方匿踪弹** | 200 km | 可见（**自有遥测**：自己人不是"探测结果"） | — |
    /// | **敌船**（公开应答机） | 400 km | 可见（不匿踪 ⇒ 进所有阵营视图） | — |
    /// | **敌匿踪弹 A** | 200 km | 可见（真被判据探到） | — |
    /// | **敌匿踪弹 B** | 1,500 km | **看不见**（S≈0.077 ⇒ 有效距离 ≈277 km） | **开机（W1）⇒ S≈10 ⇒ 距离 ≈3,175 km ⇒ 现身** |
    /// | **敌匿踪弹 C** | 3,000 km | **看不见** | **点火 ⇒ S ≈ 20 ⇒ 距离 ≈4,480 km ⇒ 现身** |
    ///
    /// 于是"开机即暴露、点火即暴露"这条 W1 枢纽不用讲，点一下就看见。
    /// 想看**没被挡掉**的样子：勾「80 调试日志」→「显示真实棋子（绕过认知层 · dev）」。
    ///
    /// ## 口径
    /// · 敌弹的两个开关都走**真实的失效通道**（`ActivePingEnabled` 是属性、`HasActiveBurn` 是 public 字段
    ///   ⇒ 后者必须显式 `InvalidateSignal()`，`[V7]/[V8]` 的既有教训）；
    /// · 本场景只**布置 + 读数**，不判定（判定归「认知层验收」与「视野对账」）。
    /// </summary>
    public static class SensorAmbushDemo
    {
        private static readonly List<int> spawned = new();
        private static int playerShipId = -1, playerMissileId = -1, enemyShipId = -1;
        private static int enemyMissileNearId = -1, enemyPingDemoId = -1, enemyBurnDemoId = -1;
        private static int observerGroupId = -1;                 // 我方那条装传感器的船（= 观测者）
        private static int enemyJammerId = -1;                   // 敌方干扰机（[E3]；开机后把观测者按住）
        private static bool ownJamOn;                            // 我方干扰机开关（部件级效力，同一条判据通道）
        private static bool jamOn;
        private static readonly List<int> demoDecoys = new();   // [E5] 本演示注入的假接触
        private static int playerFaction = -1, enemyFaction = -1;
        private static bool pingOn;                              // B 的开机开关当前状态
        private static bool burnOn;                              // C 的点火开关当前状态

        public static bool Deployed => spawned.Count > 0;

        // ═══════════════════════════════════════════════════════════════════
        //  布置 / 清场
        // ═══════════════════════════════════════════════════════════════════

        public static void Deploy()
        {
            if (Deployed) { Log.Message("[来袭演示] 已经布置过了 ⇒ 先「清场」再来（或直接用读数/开关）"); return; }
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (sg == null || mgr == null) { Log.Warning("[来袭演示] 缺 SpaceGameComp / 天体管理器"); return; }

            playerFaction = Faction.OfPlayer?.loadID ?? 0;
            enemyFaction = FactionResolver.HostileFactionIdForPlayer();
            if (enemyFaction < 0 || enemyFaction == playerFaction)
            {
                Log.Warning("[来袭演示] 本局没有与玩家敌对的阵营 ⇒ 匿踪判定跑不起来（同阵营不进判据）");
                return;
            }

            var planets = mgr.GetPlanets();
            int bodyId = planets != null && planets.Count > 0 ? planets[0].bodyId : 1;
            double mu = sg.GetBodyGM(bodyId);
            if (!(mu > 0.0)) { Log.Warning("[来袭演示] 根天体 GM 非正"); return; }

            PflTestKit.SnapshotGroups();     // 清场口径：只销毁本次新建的

            double r = SpaceConstants.PlanetRadiusMeters + 500000.0;
            double v = System.Math.Sqrt(mu / r);
            var basePos = new DVector3(r, 0, 0);
            var baseVel = new DVector3(0, 0, v);
            // ⚠ 摆位用**同轨不同相位**（沿轨弧长），不是径向拉开：全都进圆轨 ⇒ **队形稳定**，
            //   距离不随积分漂移（否则几分钟后"该看不见的"会漂进/漂出探测圈，演示就不可信了）。
            //   弧长与弦长在小角度下几乎相等（3,000 km 弧 ⇒ 弦 2,999.7 km），读数照弦长算。
            int deployTick = Find.TickManager.TicksGame;

            // 我方观测者（自带 1000 km 传感器）
            var obs = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, basePos, baseVel, out _, overThrust: false, circularOrbit: true);
            if (obs == null) { Log.Warning("[来袭演示] 我方船创建失败"); return; }
            playerShipId = obs.GroupId;
            observerGroupId = obs.GroupId;
            // 演示用记忆窗口 6 s（观测者的**具名分量**，裁定 ②）：这样"被干扰按住 ⇒ 影子 ⇒ 失联"几秒内看得见，
            // 而不是等默认的 30 s。**不是改判据**，是场景给自己的观测者一个参数。
            obs.BuildingTechBonus = new SensorTech { sensitivityMeters = 0f, memorySeconds = 6f };
            spawned.Add(obs.GroupId);

            // 我方匿踪弹（弧长 200 km）：自己人，应当在自家图上可见
            if (SpawnMissile(sg, bodyId, playerFaction, AtArc(r, v, 200000.0, out var velP), velP, out playerMissileId))
            {
                spawned.Add(playerMissileId);
                Group(playerMissileId)?.EnterCircularOrbit(deployTick, bodyId);
            }

            // 敌船（公开应答机，弧长 400 km）：不匿踪 ⇒ 任何阵营都看得见
            var eship = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, AtArc(r, v, 400000.0, out var velE), velE, out int eshipEid, overThrust: false, circularOrbit: true);
            if (eship != null)
            {
                eship.FactionId = enemyFaction;
                var e = sg.GetEntityById(eshipEid);
                if (e != null) e.FactionId = enemyFaction;
                enemyShipId = eship.GroupId;
                spawned.Add(eship.GroupId);
            }

            // 敌匿踪弹 A（弧长 200 km，略占相位）：应当被真判据探到
            if (SpawnMissile(sg, bodyId, enemyFaction, AtArc(r, v, 240000.0, out var velA), velA, out enemyMissileNearId))
            {
                spawned.Add(enemyMissileNearId);
                Group(enemyMissileNearId)?.EnterCircularOrbit(deployTick, bodyId);
            }

            // 敌匿踪弹 B（弧长 1,500 km）：静默看不见、开机现身
            if (SpawnMissile(sg, bodyId, enemyFaction, AtArc(r, v, 1500000.0, out var velB), velB, out enemyPingDemoId))
            {
                spawned.Add(enemyPingDemoId);
                Group(enemyPingDemoId)?.EnterCircularOrbit(deployTick, bodyId);
            }

            // 敌匿踪弹 C（弧长 3,000 km）：静默看不见、点火现身
            if (SpawnMissile(sg, bodyId, enemyFaction, AtArc(r, v, 3000000.0, out var velC), velC, out enemyBurnDemoId))
            {
                spawned.Add(enemyBurnDemoId);
                Group(enemyBurnDemoId)?.EnterCircularOrbit(deployTick, bodyId);
            }

            // 敌**干扰机**（弧长 300 km，带 `JammerComponentData`）：开机后抬高观测者阈值 ⇒
            // 连近处那枚弹也会被按住（而公开组与我方单位走另一条路，照样可见 —— 这就是 W1 的对照）。
            var jammer = DebugShipSpawner.CreateNoMapShipGroup(sg, bodyId, AtArc(r, v, 300000.0, out var velJ), velJ,
                out int jamEid, overThrust: false, circularOrbit: true);
            if (jammer != null)
            {
                jammer.FactionId = enemyFaction;
                var jent = sg.GetEntityById(jamEid);
                if (jent != null)
                {
                    jent.FactionId = enemyFaction;
                    if (jent.Parts.Count > 0)
                        jent.Parts[0].AddComponent(new PioneerFramework.Components.JammerComponentData { jamStrength = 2f });
                    jent.InvalidateStats();          // ⚠ 部件级失效不上行（[C4]）⇒ 实体级显式
                    jammer.InvalidateSignal();
                }
                enemyJammerId = jammer.GroupId;
                spawned.Add(jammer.GroupId);
            }

            pingOn = false;
            burnOn = false;
            jamOn = false;
            Log.Message($"[来袭演示] 已布置 {spawned.Count} 组（我方阵营 {playerFaction} / 敌方 {enemyFaction}）⇒ **世界视图里应当只看得到：我方船 + 我方弹 + 敌船 + 敌近弹**；那两枚远弹（1.5 Mm / 3 Mm）**看不见**，点「开机/点火」让它们现身。想看全的：勾「显示真实棋子（绕过认知层 · dev）」。");
            PrintRoster();
        }

        public static void Cleanup()
        {
            if (!Deployed) { Log.Message("[来袭演示] 没布置过"); return; }
            int n = PflTestKit.CleanupSince();
            spawned.Clear();
            playerShipId = playerMissileId = enemyShipId = -1;
            enemyMissileNearId = enemyPingDemoId = enemyBurnDemoId = observerGroupId = enemyJammerId = -1;
            pingOn = burnOn = jamOn = ownJamOn = false;
            demoDecoys.Clear();
            Log.Message($"[来袭演示] 已清场：销毁 {n} 组");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  两个开关（W1 的可视化）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>切换敌弹 B 的开机（W1）：开机 ⇒ S 从 ≈0.077 跳到 ≈10 ⇒ 从 1.5 Mm 外现身。</summary>
        public static void TogglePing()
        {
            var g = Group(enemyPingDemoId);
            if (g == null) { Log.Message("[来袭演示] 敌弹 B 不在（先「布置」）"); return; }
            pingOn = !pingOn;
            g.ActivePingEnabled = pingOn;       // 属性 setter 自带失效钩子
            Log.Message($"[来袭演示] 敌弹 B 开机 = {pingOn}｜它的 S = {g.SignalStrength:F3} ⇒ 我方有效距离 ≈ {EffectiveRangeKm(g):F0} km（它距我方 1,500 km）");
            PrintRoster();
        }

        /// <summary>
        /// **敌方投诱饵**（[E5] 假目标）：在我方附近注入一条**假接触**（没有真值对应物）。
        /// ⚠ 欺骗**依赖对方主动探测开机**（DRFM 语义）⇒ 本动作顺手把我方观测者的雷达打开
        /// （否则"对方静默聆听"就骗不到它）。抗欺骗不够时**看不出它是假的**——图上就是多了一枚"敌弹"。
        /// </summary>
        public static void InjectDecoyDemo()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            var obs = Group(observerGroupId);
            if (sg == null || sensor == null || obs == null) { Log.Message("[来袭演示] 先「布置」"); return; }

            obs.ActivePingEnabled = true;      // DRFM：诱饵只有对方开机才骗得到（W1 的另一面）
            var pos = obs.CurrentOrbit.positionSystem.v + new DVector3(0, 0, 300000.0);
            int id = sensor.InjectDecoy(enemyFaction, pos, obs.CurrentOrbit.velocitySystem.v,
                signalStrength: 2f, lifetimeSeconds: 120.0, apparentRadius: 20f);
            demoDecoys.Add(id);
            Log.Message($"[来袭演示] 敌方投出诱饵 #{id}（距我方 300 km，欺骗强度 2.0，生命期 120 s）｜我方观测者抗欺骗 = {obs.DetectTech.eccmResist:F1} ⇒ {(obs.DetectTech.eccmResist > 2f ? "**会打上识破标记（深层提示）**" : "看不出是假的（当成真目标）")}｜它**没有真值对应物**——勾 dev 真值开关也看不到它，只能靠识破标记分辨");
            PrintRoster();
        }

        /// <summary>清掉本演示注入的全部假接触。</summary>
        public static void ClearDecoys()
        {
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sensor == null) return;
            int n = 0;
            for (int i = 0; i < demoDecoys.Count; i++) if (sensor.RemoveDecoy(demoDecoys[i])) n++;
            demoDecoys.Clear();
            Log.Message($"[来袭演示] 已清掉 {n} 条假接触");
            PrintRoster();
        }

        /// <summary>切换**我方**干扰机（[E3]）：开机 ⇒ 抬高**敌方**观测方的阈值（本站那条船带干扰部件）。
        /// 与敌方干扰机走**同一条判据通道**（`Group.JamStrength` + `JammingEnabled`），只是作用方向相反。</summary>
        public static void ToggleStationJammer()
        {
            var g = Group(observerGroupId);
            if (g == null) { Log.Message("[来袭演示] 先「布置」"); return; }
            if (g.JamStrength <= 0f)
            {
                // 演示用：观测者船上是空的（没装干扰部件）⇒ 临时补给一份部件级效力（生产里由建筑/部件给）
                var sg = Current.Game?.GetComponent<SpaceGameComp>();
                foreach (int eid in g.GetEntityIds())
                {
                    var e = sg?.GetEntityById(eid);
                    if (e == null || e.Parts.Count == 0) continue;
                    if (!e.Parts[0].components.Any(c => c is JammerComponentData))
                        e.Parts[0].AddComponent(new JammerComponentData { jamStrength = 2f });
                    e.InvalidateStats();
                }
                g.InvalidateSignal();
            }
            ownJamOn = !ownJamOn;
            g.JammingEnabled = ownJamOn;
            Log.Message($"[来袭演示] **我方**干扰机开机 = {ownJamOn}｜本站 JamStrength = {g.JamStrength:F2}｜我方信号 S = {g.SignalStrength:F3}（开机即暴露）⇒ 敌方观测方的阈值被抬高（效果与敌方干扰我方对称）");
        }

        /// <summary>切换敌方**干扰机**（[E3]）：开机 ⇒ 抬高我方阈值 ⇒ 连 240 km 的近弹也被按住。</summary>
        public static void ToggleJam()
        {
            var g = Group(enemyJammerId);
            if (g == null) { Log.Message("[来袭演示] 干扰机不在（先「布置」）"); return; }
            jamOn = !jamOn;
            g.JammingEnabled = jamOn;
            Log.Message($"[来袭演示] 敌方干扰机开机 = {jamOn}｜它自己的 S = {g.SignalStrength:F3}（干扰必须辐射 ⇒ 开机即更响）｜此时**我方近弹会被按住**：接触不再刷新 ⇒ 先变**影子**（仍可打最后已知位置），记忆窗口（本演示 6 s）过后**失联**、从图上消失。公开组与我方单位不受影响（走的不是探测那条路）。");
            PrintRoster();
        }

        /// <summary>切换敌弹 C 的点火：点火 ⇒ S ≈ 20 ⇒ 从 3 Mm 外现身。</summary>
        public static void ToggleBurn()
        {
            var g = Group(enemyBurnDemoId);
            if (g == null) { Log.Message("[来袭演示] 敌弹 C 不在（先「布置」）"); return; }
            burnOn = !burnOn;
            g.HasActiveBurn = burnOn;           // ⚠ public 字段：生产里只有 StartBurn/EndBurn 挂钩
            g.InvalidateSignal();               // ⇒ 场景里必须显式置脏（本库既有教训）
            Log.Message($"[来袭演示] 敌弹 C 点火 = {burnOn}｜它的 S = {g.SignalStrength:F3} ⇒ 我方有效距离 ≈ {EffectiveRangeKm(g):F0} km（它距我方 3,000 km）");
            PrintRoster();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  花名册（读数：谁可见、为什么）
        // ═══════════════════════════════════════════════════════════════════

        public static void PrintRoster()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sg == null || sensor == null || !Deployed) { Log.Warning("[来袭演示] 无从打印（没布置或缺组件）"); return; }

            var obs = Group(observerGroupId);
            var sb = new System.Text.StringBuilder();
            sb.Append("[来袭演示·花名册] 我方观测者灵 ").Append(obs != null ? $"{(obs.DetectTech.sensitivityMeters / 1000f):F0}" : "?")
              .Append(" km｜dev 真值开关 ").Append(SensorManager.DebugShowGroundTruth ? "开" : "关");
            string[] names = ["我方船（观测者）", "我方匿踪弹", "敌船（公开）", "敌近弹 A", "敌远弹 B（开机演示）", "敌远弹 C（点火演示）", "敌干扰机（E3）"];
            int[] ids = [playerShipId, playerMissileId, enemyShipId, enemyMissileNearId, enemyPingDemoId, enemyBurnDemoId, enemyJammerId];
            for (int i = 0; i < ids.Length; i++)
            {
                var g = Group(ids[i]);
                if (g == null) continue;
                double distKm = obs != null
                    ? (g.CurrentOrbit.positionSystem.v - obs.CurrentOrbit.positionSystem.v).magnitude / 1000.0
                    : -1.0;
                bool canSee = sensor.PlayerCanSee(g);
                var phase = sensor.ContactPhaseOf(playerFaction, g.GroupId);
                string state, why;
                if (!g.IsStealth) { state = canSee ? "可见" : "不可见"; why = "公开应答机（不匿踪：任何阵营可见，与探测无关）"; }
                else if (g.FactionId == playerFaction) { state = "可见"; why = "我方（自有遥测，不是探测结果）"; }
                else if (phase == TrackPhase.Live) { state = "可见"; why = "新鲜：本步判据成立（刚刷新）"; }
                else if (phase == TrackPhase.Shadow) { state = "影子"; why = "断刷新（被干扰/信号掉了）但记忆窗口未满 ⇒ **仍可用**（可打最后已知位置）"; }
                else { state = "不可见"; why = "没探到（或记忆已过期）"; }
                sb.Append("\n  ").Append(names[i]).Append("：G").Append(g.GroupId)
                  .Append("｜距 ").Append(distKm < 0 ? "?" : $"{distKm:F0}").Append(" km")
                  .Append("｜S=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{g.SignalStrength:F3}")
                  .Append("｜**").Append(state).Append("**（").Append(why).Append('）');
            }
            // [E5] 假接触（诱饵）：**不在世界棋子列表里** ⇒ 单列一段（它们只活在认知层）
            if (demoDecoys.Count > 0)
            {
                var buf = visibleScratchRoster;
                buf.Clear();
                sensor.AppendVisible(playerFaction, buf);
                int n = 0;
                for (int i = 0; i < buf.Count; i++)
                    if (buf[i].decoyId >= 0)
                    {
                        n++;
                        sb.Append("\n  【假接触】诱饵 #").Append(buf[i].decoyId)
                          .Append('｜').Append(buf[i].isDecoy ? "**已识破（深层提示）**" : "当真目标显示（未识破）");
                    }
                if (n == 0) sb.Append("\n  【假接触】本步没有被照到（观测者开机才有回波 ⇒ 欺骗依赖对方开机）");
            }
            sb.Append("\n  ⇒ 对照：勾「显示真实棋子（绕过认知层 · dev）」应看到全部 ").Append(spawned.Count).Append(" 组（**诱饵不在其中**——它没有真值对应物）");
            Log.Message(sb.ToString());
        }

        // ═══════════════════════════════════════════════════════════════════

        private static readonly List<Contact> visibleScratchRoster = new();

        private static double EffectiveRangeKm(SpaceEntityGroup target)
        {
            var obs = Group(observerGroupId);
            if (obs == null) return 0.0;
            var tech = obs.DetectTech;
            return DetectionCore.EffectiveDetectRange(target.SignalStrength, in tech, 0.0) / 1000.0;
        }

        /// <summary>同轨、沿轨弧长 <paramref name="arcMeters" /> 处的 (位置, 切向速度) —— 进圆轨后队形稳定。</summary>
        private static DVector3 AtArc(double radius, double speed, double arcMeters, out DVector3 velocity)
        {
            double theta = arcMeters / radius;
            double c = System.Math.Cos(theta), sn = System.Math.Sin(theta);
            velocity = new DVector3(-sn * speed, 0.0, c * speed);
            return new DVector3(c * radius, 0.0, sn * radius);
        }

        private static SpaceEntityGroup? Group(int groupId)
            => groupId < 0 ? null : Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId);

        /// <summary>匿踪来袭体：小尺寸单部件、无推进、不广播坐标（[N3] 的导弹口径）。</summary>
        private static bool SpawnMissile(SpaceGameComp sg, int bodyId, int factionId, DVector3 pos, DVector3 vel, out int groupId)
        {
            groupId = -1;
            int gid = sg.AllocGroupId();
            int eid = sg.AllocEntityId();
            var group = new SpaceEntityGroup(gid, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                IsStealth = true,
                CelestialBodyId = bodyId,
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)vel,
                rotation = DQuaternion.identity,
            };
            sg.RegisterGroup(group);

            var entity = new SpaceEntity(eid, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true,
            };
            entity.GroupId = gid;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.mainAxisShip = new DVector3(0, 0, -1);
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(10.0, 10.0, 30.0),
                hp = 150f,
                isIndestructible = false,
            };
            part.AddComponent(new HullMassComponent { mass = 400f });
            entity.SetParts(new List<SpacePart> { part });
            sg.RegisterEntity(entity);
            group.AddEntity(eid);
            groupId = gid;
            return true;
        }
    }
}
