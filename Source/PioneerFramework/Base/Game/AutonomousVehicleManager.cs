using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    /// <summary>
    /// 自主飞行器（导弹/无人机）管理器（《导弹系统设计》§3/§4）。
    /// 导弹与无人机物理同源（无 Map SpaceEntity + group），只按行为 profile 分层。
    ///
    /// 进度（2026-09-06/07）：登记/身份数据/失效清理/序列化 ✓；Attack 自动段 = 巡航惯性 +
    /// 终末收敛制导撞击 ✓（TickAttack——横向速度匹配 + 接近巡航冲刺，解离/甩锁/aimOffsetShip 后置；
    /// 巡航段的远距交会/变轨由玩家与引导引擎旧功能管辖）。Loiter/Drone profile 后置。
    ///
    /// 职责边界：不做实体/group 创建（归发射链 FireWeapon Missile 分支 / 未来 Drone 工厂）、
    /// 不做物理积分（归 SpaceGameComp）、不做碰撞判定（归 CombatManager）——
    /// 导弹识别 = 按 groupId 反查本管理器（§0.2-K9：SpaceEntityGroup 无身份字段，不加字段）。
    /// </summary>
    public class AutonomousVehicleManager : GameComponent
    {
        /// <summary>任务类型（指挥模型 Mission 制，§9/§0.2-K7：只有 Attack 撞击段自动，其余由玩家/AI 指挥）。
        /// 注意：序列化按枚举值，新值必须追加在末尾（勿插中间破坏旧档）。</summary>
        public enum MissionType
        {
            Attack,      // 自动段入口：终末撞击（横向匹配收敛 + 接近冲刺；追丢自动中止）
            Loiter,      // 部署到待机轨道熄火（执行后置）
            Standby,     // 保持当前轨道待命
            SelfDestruct, // 自毁
            Following    // 发射器释放默认态（2026-09-06）：目标跟随发射器（发射器换目标自动同步），
                         // 但不点火不进入终末——普通航天器，由玩家/面板在合适时机下 Attack
        }

        /// <summary>
        /// 弹群/自主飞行器登记记录（K9）。一个 record = 一个逻辑弹群（巡航段 = 1 领弹 group，
        /// N 为 logicalCount，解离克隆后每子弹各自一条 record——解离任务后置）。
        /// </summary>
        public class VehicleRecord : IExposable
        {
            /// <summary>车辆编号（管理器独立递增序列，贯穿 Missile/Drone 两态）。</summary>
            public int vehicleId = -1;
            /// <summary>领弹 group id（反查键）。</summary>
            public int groupId = -1;
            /// <summary>领弹实体 id。</summary>
            public int entityId = -1;
            /// <summary>同齐射归属 id（§10：同 volley 兄弟互碰排除用，发射侧发号）。</summary>
            public int volleyId = -1;
            /// <summary>发射方 group id（§0.2-K1：不做永久豁免发射方；结算归属/日志用）。</summary>
            public int launcherGroupId = -1;
            /// <summary>弹群逻辑枚数 N（§5.1 单等效体：巡航段整群按 1 枚标准弹跑，N 仅逻辑口径）。</summary>
            public int logicalCount = 1;
            /// <summary>弹体/弹头 Def 名（转化交接生成弹头射弹时解析 MissileDef 参数）。</summary>
            public string missileDefName;
            /// <summary>发射武器 Def 名（弹头射弹 weaponDef 引用 + 面板/日志用）。</summary>
            public string weaponDefName;
            /// <summary>当前 Mission（默认 Attack(目标) 或 Standby，发射链写入；新 Mission 覆盖旧 Mission）。</summary>
            public MissionType mission = MissionType.Standby;
            /// <summary>Attack 目标 group id（Standby/Loiter 时为 -1）。</summary>
            public int targetGroupId = -1;
            /// <summary>目标局部系弹着偏置（米，y=0；零 = 瞄准目标质心）。有 Map 目标精确打击（§10，2026-09-07）：
            /// 发射器点选目标地图一格 → 格偏移随导弹携带；终末制导每步随目标实体旋转换算世界瞄准点，
            /// 撞击/弹头 Map 爆炸落于该格。换目标/改派新目标时清零（旧目标局部系对新目标无意义）。</summary>
            public DVector3 aimOffsetShip;
            /// <summary>巡航日志上次打印 tick（瞬态，不序列化：每车各自 600 tick 限频，避免多车共享窗口互相吞日志）。</summary>
            public int lastCruiseLogTick;
            /// <summary>燃尽已上报（瞬态：一次后不再重复刷屏；导弹无补油路径，无需复位）。</summary>
            public bool fuelOutReported;
            /// <summary>本次 Attack 终末接管时的表面距（瞬态：HasActiveAttack 判定 = 是否已进入终末段；
            /// 追丢基准已改由 <see cref="guidanceState"/> 的最近点承担，2026-09-08）。</summary>
            public double attackStartSurface;
            /// <summary>制导跨步状态（瞬态，不序列化：最近点/再捕获计时；载入存档重置 = 重新观测语义）。</summary>
            public MissileGuidanceState guidanceState;
            /// <summary>燃尽惯性弹清理时刻（瞬态，tick；0 = 未燃尽。K5：Def 寿命后自动清理）。</summary>
            public int despawnAtTick;
            /// <summary>再捕获已上报（瞬态：擦过目标后掉头的那条日志只打一次，恢复接近时复位）。</summary>
            public bool reacquireLogged;
            /// <summary>制导详细日志上次打印 tick（瞬态，不序列化；**每车独立**限频——共享窗口会被
            /// 列表首车吃光（2026-09-08 弹群场景：V1 独占 122 行，其余 7 枚各 1 行，无法对照轨迹）。</summary>
            public int lastGuidanceLogTick;
            /// <summary>多实体目标随机锁定的实体 id（瞬态，不序列化；-1 = 未锁定）：零偏置打合并弹群时
            /// 随机锁定一枚成员、**交战期保持**——每步重随机会让瞄准点跳变；缓存后每步只有一次 O(1)
            /// GetEntityById 查询，无逐实体遍历/距离比较开销（2026-09-08 用户定稿）。锁定实体离群
            /// （被拦截/摧毁，ContainsEntity false）时重新随机。换目标时新 group 不含旧实体 id → 自动重选。</summary>
            public int aimEntityId = -1;
            /// <summary>错峰点火时隙（瞬态，TicksGame；0 = 无限制）：解离时按队列深度给后弹
            /// 排点火时隙（depth/cruise 游戏秒），时隙前只修横向不补接近率——到达间隔由构造保证
            /// ≥ 间距/巡航（防收敛聚堆被兄弟爆炸误杀，2026-09-08 三轮实测 E5 差 0.1s 命中却被
            /// E6 弹头 AoE 杀死）。玩家重新下 Attack 时清零（新命令=立即权威）。</summary>
            public int holdFireUntilTick;
            /// <summary>弹群已解离（瞬态，不序列化，2026-09-08 §5）：终末接管时 1 group N 实体逐个拆成
            /// N 个单实体 group 后置位——防重复拆分（含拆出的单枚记录，单枚不再拆）。</summary>
            public bool volleySplit;

            public void ExposeData()
            {
                Scribe_Values.Look(ref vehicleId, "vehicleId", -1);
                Scribe_Values.Look(ref groupId, "groupId", -1);
                Scribe_Values.Look(ref entityId, "entityId", -1);
                Scribe_Values.Look(ref volleyId, "volleyId", -1);
                Scribe_Values.Look(ref launcherGroupId, "launcherGroupId", -1);
                Scribe_Values.Look(ref logicalCount, "logicalCount", 1);
                Scribe_Values.Look(ref missileDefName, "missileDefName");
                Scribe_Values.Look(ref weaponDefName, "weaponDefName");
                Scribe_Values.Look(ref mission, "mission", MissionType.Standby);
                Scribe_Values.Look(ref targetGroupId, "targetGroupId", -1);
                DVector3.Scribe("aimOffsetShip", ref aimOffsetShip);
            }

            public override string ToString()
            {
                return string.Format("V{0} G{1} E{2} volley={3} fromG{4} N={5} {6}->G{7}",
                    vehicleId, groupId, entityId, volleyId, launcherGroupId, logicalCount,
                    mission, targetGroupId);
            }
        }

        private List<VehicleRecord> vehicles = new List<VehicleRecord>();
        /// <summary>groupId → record 反查索引（K9 导弹识别入口热路径——碰撞检测每物理步对每个球快筛
        /// 拒绝的 pair 反查；线性遍历在齐射多枚时被 pair 数放大，改字典 O(1)。登记/注销/存档加载维护）。</summary>
        private readonly Dictionary<int, VehicleRecord> byGroupId = new Dictionary<int, VehicleRecord>();
        private int nextVehicleId = 1;
        private int nextVolleyId = 1;

        /// <summary>失效记录清理频率（tick；记录只是登记簿，无需物理步级精度）。</summary>
        private const int PruneIntervalTicks = 120;

        private int nextPruneTick;

        // ── 终末制导（Attack 自动段执行，《导弹系统设计》§7；控制律在 MissileGuidanceCore 纯核心）──
        /// <summary>燃尽预检阈值（kg）：主+RCS 总工质低于此值不再点火（惯性弹，K5）。</summary>
        private const double FuelOutKg = 5.0;

        /// <summary>详细制导日志开关（调试场景/测试用；默认关）。</summary>
        public static bool DebugDetailedGuidance;

        /// <summary>已打过"终末接管"首条日志的 group（瞬态不序列化；mission 改派后重打）。</summary>
        private readonly HashSet<int> terminalLogged = new HashSet<int>();

        public AutonomousVehicleManager(Game game) { }

        // 性能第 5 条：OnPhysicsStepCompleted / TickAttack 每物理步都要取，按 Game 缓存
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new GameComponentRef<SpaceGameComp>();
        private SpaceGameComp SpaceGame => spaceGameRef.Get();

        public IReadOnlyList<VehicleRecord> Vehicles => vehicles;

        public int AllocVolleyId() => nextVolleyId++;

        /// <summary>
        /// 发射链注册领弹弹群（FireWeapon Missile 分支调用）。
        /// volleyId 由发射侧经 AllocVolleyId 取号（一次齐射共号）；同齐射的后续记录沿用。
        /// aimOffsetShip：发射时携带的弹着偏置（发射器精确打击点，目标局部米，y=0）；零 = 瞄目标质心。
        /// </summary>
        public VehicleRecord RegisterMissileLeader(int groupId, int entityId, int volleyId,
            int launcherGroupId, int logicalCount, string missileDefName, string weaponDefName,
            MissionType mission, int targetGroupId, DVector3 aimOffsetShip = default)
        {
            var rec = new VehicleRecord
            {
                vehicleId = nextVehicleId++,
                groupId = groupId,
                entityId = entityId,
                volleyId = volleyId,
                launcherGroupId = launcherGroupId,
                logicalCount = logicalCount,
                missileDefName = missileDefName,
                weaponDefName = weaponDefName,
                mission = mission,
                // Attack/Following 携带目标（Following = 跟随发射器当前目标）；Standby/Loiter 无目标
                targetGroupId = mission == MissionType.Attack || mission == MissionType.Following ? targetGroupId : -1,
                aimOffsetShip = aimOffsetShip
            };
            vehicles.Add(rec);
            byGroupId[groupId] = rec;
            Log.Message(string.Format("[AutonomousVehicle] 登记 {0}", rec));
            return rec;
        }

        /// <summary>按 groupId 反查（K9 导弹识别入口；字典 O(1)）。</summary>
        public VehicleRecord GetRecord(int groupId)
        {
            return byGroupId.TryGetValue(groupId, out var rec) ? rec : null;
        }

        /// <summary>
        /// 是否存在处于 Attack **终末段**（已接管，attackStartSurface&gt;0）的在册导弹。时间流速系统把
        /// 它当作与"射弹在飞"同级的战斗 flag：强制 Combat 档（1/60 = 1 游戏秒 ≈ 1 现实秒），玩家才能
        /// 看清终末冲刺与撞击（2026-09-07 用户定稿）。不满足条件的情形：
        /// - 巡航段 Attack（表面距 &gt; homing 边界，attackStartSurface 尚未设）——导弹仍是普通航天器
        ///   在等玩家交会/改派，锁 1/60 会让玩家卡在慢动作（09-07 日志实证：错时机下 Attack 的导弹
        ///   巡航到 8 万 km 外仍挂着 Attack → 全场景锁 Combat）；
        /// - 燃尽惯性弹（fuelOutReported）——任务已结束，不锁。
        /// 中止/命中转化后 mission 变更/注销自然清除。
        /// </summary>
        public bool HasActiveAttack
        {
            get
            {
                for (int i = 0; i < vehicles.Count; i++)
                {
                    var v = vehicles[i];
                    if (v.mission == MissionType.Attack && v.attackStartSurface > 0.0 && !v.fuelOutReported)
                        return true;
                }
                return false;
            }
        }

        /// <summary>注销（弹群销毁/解离母群退役时调用）。</summary>
        public void UnregisterGroup(int groupId)
        {
            for (int i = vehicles.Count - 1; i >= 0; i--)
            {
                if (vehicles[i].groupId == groupId)
                {
                    Log.Message(string.Format("[AutonomousVehicle] 注销 V{0} G{1}", vehicles[i].vehicleId, groupId));
                    vehicles.RemoveAt(i);
                    byGroupId.Remove(groupId);
                    return;
                }
            }
        }

        /// <summary>
        /// 对某发射器释放的全部在册导弹统一下发 Mission（发射器 Gizmo「命令全体导弹」）。
        /// Attack 携带目标；Standby/SelfDestruct 无目标。倒序遍历：SelfDestruct 会在执行中注销记录。
        /// 3D 已销毁的记录跳过（清理兜底）。返回成功下发的枚数。
        /// </summary>
        public int CommandAll(int launcherGroupId, MissionType mission, int targetGroupId = -1)
        {
            var spaceGame = SpaceGame;
            if (spaceGame == null) return 0;
            int n = 0;
            for (int i = vehicles.Count - 1; i >= 0; i--)
            {
                var rec = vehicles[i];
                if (rec.launcherGroupId != launcherGroupId) continue;
                if (spaceGame.GetGroupById(rec.groupId) == null) continue; // 已销毁：失效清理兜底，不下发
                if (IssueCommand(rec.groupId, mission, targetGroupId)) n++;
            }
            Log.Message(string.Format("[导弹指令] 发射器G{0} 命令全体：{1} 成功下发 {2} 枚（目标G{3}）",
                launcherGroupId, mission, n, targetGroupId));
            return n;
        }

        /// <summary>
        /// 统一指令入口（§9 指挥模型：玩家/AI 经此下发 Mission，新 Mission 覆盖旧 Mission；
        /// 星座组网将来在"无中继则指令延迟/失败"就在这一个函数口实现）。
        /// Attack = 终末撞击命令（目标须存在；推进直扑只在可达几何有效，追丢自动中止回 Standby）；
        /// Standby = 中止当前动作回待命；Loiter = 熄火待机意图（轨道部署由玩家机动完成，执行逻辑后置）；
        /// SelfDestruct = 就地引爆自毁。
        /// aimOffset：Attack 弹着偏置（目标局部米，y=0）。null = 自动——目标与当前相同则保留原偏置
        /// （面板重下 Attack 不丢精确点），换了目标则清零（旧目标局部系对新目标无意义）。
        /// </summary>
        public bool IssueCommand(int groupId, MissionType mission, int targetGroupId = -1, DVector3? aimOffset = null)
        {
            var spaceGame = SpaceGame;
            if (spaceGame == null) return false;
            var rec = GetRecord(groupId);
            if (rec == null)
            {
                Log.Warning(string.Format("[导弹指令] G{0} 不在册（无记录）", groupId));
                return false;
            }
            var group = spaceGame.GetGroupById(groupId);
            if (group == null || !group.IsActive)
            {
                Log.Warning(string.Format("[导弹指令] G{0} 3D 对象不存在", groupId));
                return false;
            }

            switch (mission)
            {
                case MissionType.Attack:
                    var target = spaceGame.GetGroupById(targetGroupId);
                    if (target == null || !target.IsActive)
                    {
                        Log.Warning(string.Format("[导弹指令] V{0} Attack 目标 G{1} 不存在", rec.vehicleId, targetGroupId));
                        return false;
                    }
                    if (group.HasActiveBurn) group.EndBurn();
                    int oldTarget = rec.targetGroupId;
                    rec.mission = MissionType.Attack;
                    rec.targetGroupId = targetGroupId;
                    rec.attackStartSurface = 0.0; // 接管日志与 HasActiveAttack 在下一物理步重设
                    rec.guidanceState = default;   // 制导状态清零：最近点基准对新目标/新几何无意义（防跨目标误判几何丢失）
                    rec.holdFireUntilTick = 0;     // 玩家重新下令 = 立即权威（清错峰时隙）
                    // 弹着偏置：显式携带 → 写入；未携带且目标没换 → 保留（不丢精确点）；换了 → 清零
                    if (aimOffset.HasValue)
                        rec.aimOffsetShip = aimOffset.Value;
                    else if (oldTarget != targetGroupId)
                        rec.aimOffsetShip = DVector3.zero;
                    terminalLogged.Remove(rec.groupId);
                    Log.Message(string.Format("[导弹指令] V{0} G{1} Attack→G{2}（终末撞击命令）{3}", rec.vehicleId, rec.groupId, targetGroupId,
                        rec.aimOffsetShip != DVector3.zero ? string.Format(" 弹着偏置=({0:F1},{1:F1})m", rec.aimOffsetShip.x, rec.aimOffsetShip.z) : ""));
                    return true;

                case MissionType.Standby:
                    if (group.HasActiveBurn) group.EndBurn();
                    rec.mission = MissionType.Standby;
                    terminalLogged.Remove(rec.groupId);
                    rec.attackStartSurface = 0.0;
                    rec.guidanceState = default; // 中止后重新观测（下次 Attack 从零建立最近点基准）
                    Log.Message(string.Format("[导弹指令] V{0} G{1} Standby（中止当前动作，保持轨道待命）", rec.vehicleId, rec.groupId));
                    return true;

                case MissionType.Loiter:
                    // K8 首版：部署动作 = 玩家/AI 用机动 UI 把导弹送到待机轨道；导弹侧只熄火静默待机
                    if (group.HasActiveBurn) group.EndBurn();
                    rec.mission = MissionType.Loiter;
                    terminalLogged.Remove(rec.groupId);
                    rec.attackStartSurface = 0.0;
                    Log.Message(string.Format("[导弹指令] V{0} G{1} Loiter（熄火静默待机——轨道部署由玩家机动完成）", rec.vehicleId, rec.groupId));
                    return true;

                case MissionType.SelfDestruct:
                    var cm = Current.Game?.GetComponent<CombatManager>();
                    if (cm != null)
                    {
                        Log.Message(string.Format("[导弹指令] V{0} G{1} SelfDestruct → 就地引爆", rec.vehicleId, rec.groupId));
                        cm.ExplodeMissileAt(group, rec, spaceGame);
                    }
                    else
                    {
                        UnregisterGroup(rec.groupId);
                        spaceGame.DestroyGroup(rec.groupId, "自主载具回收");
                    }
                    return true;
            }
            return false;
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            // 每物理步工作（失效清理 + Mission 执行）改由 SpaceGameComp 在物理推进完成后统一驱动
            // （OnPhysicsStepCompleted）——组件 tick 顺序（全名字母序：本组件在 SpaceGameComp 前）
            // 若自行 %2 tick 会读物理推进前的位置：制导瞄准比碰撞滞后 2s ≈ 5km，
            // 导弹真实航线从不穿过目标盒（2026-09-07 日志实证：同 tick 制导 0.7km vs 碰撞 2.6km）。
        }

        /// <summary>
        /// 每物理步执行（SpaceGameComp 物理推进完成后调用；与 CombatManager 同基准 = 读到本步新位置）。
        /// </summary>
        internal void OnPhysicsStepCompleted()
        {
            var spaceGame = SpaceGame;
            if (spaceGame == null) return;

            // 低频清理：① 3D 对象已消失的失效记录（正常链路由转化/命中结算注销，此处兜底防登记簿泄漏）；
            // ② 弹群实体完整性兜底——AoE 击毁已改为当场脱队（CombatManager.ApplyExplosionDamage，2026-09-09 C），
            //    此处只兜底非爆炸路径的部件全毁（防幽灵碰撞盒占格/占质量）
            if (vehicles.Count > 0 && Find.TickManager.TicksGame >= nextPruneTick)
            {
                nextPruneTick = Find.TickManager.TicksGame + PruneIntervalTicks;
                for (int i = vehicles.Count - 1; i >= 0; i--)
                {
                    var rec = vehicles[i];
                    // ③ 燃尽惯性弹寿命到期（K5「Def 寿命后自动清理」）：从世界移除——无爆炸，
                    //    工质尽即失效，只清理登记簿与碰撞池/网格（2026-09-08 补，此前惰性弹永久滞留）
                    if (rec.despawnAtTick > 0 && Find.TickManager.TicksGame >= rec.despawnAtTick)
                    {
                        var dead = spaceGame.GetGroupById(rec.groupId);
                        int n = dead != null ? dead.GetEntityIds().Count : 0;
                        Log.Message(string.Format("[导弹清理] V{0} G{1} 燃尽惯性弹寿命到期（{2} 枚）→ 从世界移除",
                            rec.vehicleId, rec.groupId, n));
                        terminalLogged.Remove(rec.groupId);
                        byGroupId.Remove(rec.groupId);
                        vehicles.RemoveAt(i);
                        if (dead != null) spaceGame.DestroyGroup(rec.groupId, "自主载具损毁");
                        continue;
                    }
                    var group = spaceGame.GetGroupById(rec.groupId);
                    if (group == null)
                    {
                        Log.Message(string.Format("[AutonomousVehicle] G{0} 3D 对象已消失，清理登记（V{1} {2}->G{3}）",
                            rec.groupId, rec.vehicleId, rec.mission, rec.targetGroupId));
                        terminalLogged.Remove(rec.groupId);
                        byGroupId.Remove(rec.groupId);
                        vehicles.RemoveAt(i);
                        continue;
                    }
                    // 实体完整性：多实体弹群中 part 全毁的实体逐枚拆出（燃料随实体，无记账）
                    bool groupGone = false;
                    foreach (int eid in new List<int>(group.GetEntityIds()))
                    {
                        var e = spaceGame.GetEntityById(eid);
                        if (e == null || e.Parts.Count == 0) continue;
                        bool allDead = true;
                        for (int p = 0; p < e.Parts.Count; p++)
                        {
                            if (!e.Parts[p].isDestroyed) { allDead = false; break; }
                        }
                        if (!allDead) continue;
                        if (RemoveMissileEntity(rec, eid, "部件全毁清理"))
                        {
                            groupGone = true;
                            break;
                        }
                    }
                    if (groupGone)
                    {
                        terminalLogged.Remove(rec.groupId);
                        byGroupId.Remove(rec.groupId);
                        vehicles.RemoveAt(i);
                        spaceGame.DestroyGroup(rec.groupId, "自主载具回收");
                    }
                }
            }

            // Mission 执行：Attack 自动段（终末撞击）；Following = 目标跟随发射器（不点火，轻量检查）
            for (int i = 0; i < vehicles.Count; i++)
            {
                var rec = vehicles[i];
                if (rec.mission == MissionType.Attack)
                    TickAttack(rec, spaceGame);
                else if (rec.mission == MissionType.Following)
                    TickFollowing(rec, spaceGame);
            }
        }

        /// <summary>
        /// 发射器目标同步（2026-09-06）：发射器（定位机）锁定目标变化时调用——该发射器释放的、
        /// 处于 Following 的导弹全部改指新目标（目标选取自动跟随发射器，不立即进终末）。
        /// </summary>
        // [V3] 认知层（S3c）：末制导吃接触 ⇒ 需要每物理步取 SensorManager（按 Game 缓存）
        private readonly GameComponentRef<SensorManager> sensorMgrRef = new GameComponentRef<SensorManager>();

        public void SyncFollowerTargets(int launcherGroupId, int targetGroupId)
        {
            int n = 0;
            for (int i = 0; i < vehicles.Count; i++)
            {
                var rec = vehicles[i];
                if (rec.mission != MissionType.Following) continue;
                if (rec.launcherGroupId != launcherGroupId) continue;
                if (rec.targetGroupId == targetGroupId) continue;
                rec.targetGroupId = targetGroupId;
                rec.aimOffsetShip = DVector3.zero; // 跟随目标更换：旧目标局部系弹着偏置作废（新目标重新指定/发射）
                n++;
            }
            if (n > 0)
                Log.Message(string.Format("[导弹跟随] 发射器G{0} 换目标 → {1} 枚在飞导弹同步改指 G{2}", launcherGroupId, n, targetGroupId));
        }

        /// <summary>
        /// Following 轻量执行：目标失活 → 置空等待发射器重指（导弹本身不点火 = 普通航天器，
        /// 轨道部署/撞击时机由玩家指挥——面板 Attack 或发射器保持目标后玩家下令）。
        /// </summary>
        private void TickFollowing(VehicleRecord rec, SpaceGameComp spaceGame)
        {
            if (rec.targetGroupId < 0) return;
            var target = spaceGame.GetGroupById(rec.targetGroupId);
            // [V3]/S4c：跟随也**吃认知**（与 TickAttack 同口径）——"看不见了"同样算丢失，
            // 否则会出现"图上没有它、弹还跟着它飞"的自相矛盾。
            if (target != null && target.IsActive)
            {
                var sensor = sensorMgrRef.Get();
                var follower = spaceGame.GetGroupById(rec.groupId);
                if (sensor == null || follower == null || sensor.TryGetKnownState(follower.FactionId, target, out _)) return;
                Log.Message(string.Format("[导弹跟随] V{0} G{1} 跟随目标 G{2} 接触丢失（看不见了）→ 等待发射器重指",
                    rec.vehicleId, rec.groupId, rec.targetGroupId));
                rec.targetGroupId = -1;
                return;
            }
            // 跟随目标失活：清目标等待发射器重新指定（记录保持 Following，不擅自转 Standby——
            // 发射器换目标时会再次同步；导弹保持普通航天器状态）
            Log.Message(string.Format("[导弹跟随] V{0} G{1} 跟随目标 G{2} 失活 → 等待发射器重指", rec.vehicleId, rec.groupId, rec.targetGroupId));
            rec.targetGroupId = -1;
        }

        // ════════════════════════════════════════════
        //  Attack 执行：巡航惯性 + 终末火炬撞击（§7 简版）
        // ════════════════════════════════════════════

        /// <summary>
        /// Attack 自动段每物理步执行（2026-09-06 首版 = 终末撞击）：
        /// - 目标失活/消失 → 中止回 Standby + 上报（K6）
        /// - 表面距（K4 = 质心距 − 目标 AABB 半径）&gt; homingEngageDistance → 巡航：不点火，
        ///   惯性/轨道力学飞行——远距交会/变轨归旧功能（玩家 ManeuverPlanner/RendezvousPlanner、
        ///   引导引擎 RegisterApproachGuidance/RegisterRendezvousGuidance）
        /// - ≤ 边界 → 弹群解离（1 group N 实体 → N 个单实体 group）→ 终末收敛制导
        ///   （**纯核心 MissileGuidanceCore**：横向相对速度归零 + 接近率补量双门控（阈值 + 横差修正
        ///   时间约束）+ 几何丢失判定；参数全部 MissileDef 可配；每物理步 EndBurn→StartBurn 重置，
        ///   物理引擎 D15 自动转向-收心-主网点火，RCS 未对准修正/主网尽兜底）
        /// - 燃尽（主+RCS 总料 &lt; 阈值）→ 熄火惯性弹 + 寿命计时（K5，Def 寿命后自动清理）
        /// 撞击 = 通用碰撞检测 → 转化交接（CombatManager），命中即结算，本条记录随销毁注销。
        /// </summary>
        private void TickAttack(VehicleRecord rec, SpaceGameComp spaceGame)
        {
            int now = Find.TickManager.TicksGame;
            var group = spaceGame.GetGroupById(rec.groupId);
            if (group == null || !group.IsActive) return; // 失效清理兜底

            var target = spaceGame.GetGroupById(rec.targetGroupId);
            // ⚠ [V3] S3c / 设计稿 §9：**末制导吃接触** —— 目标解析从"必须解析为一个 group"改为
            //   "可解析为一条**已知状态**"：公开组/我方单位 ⇒ 真值；匿踪组 ⇒ 本阵营的接触（Live/Shadow）。
            //   ⇒ **接触丢了就追丢**（走 K6 那条中止路，处置归玩家/AI）；影子仍可打（打"它大概在哪"）。
            var sensor = sensorMgrRef.Get();
            Contact know = default(Contact);
            bool targetKnown;
            if (target == null || !target.IsActive) targetKnown = false;
            else if (sensor == null) targetKnown = true;                 // 认知层缺位 ⇒ 放行（旧行为）
            else targetKnown = sensor.TryGetKnownState(group.FactionId, target, out know);
            if (!targetKnown)
            {
                // K6：目标失活/消失/接触丢失 → 只中止回 Standby 并上报，处置归玩家/AI（不自毁不改派）
                if (group.HasActiveBurn) group.EndBurn();
                rec.mission = MissionType.Standby;
                terminalLogged.Remove(rec.groupId);
                Log.Message(string.Format("[导弹中止] V{0} G{1} 目标G{2} {3} → 中止撞击，回 Standby 待命",
                    rec.vehicleId, rec.groupId, rec.targetGroupId,
                    target == null || !target.IsActive ? "失活/消失" : "接触丢失（看不见了）"));
                return;
            }
            if (target == null) return;   // ⚠ 只为可空分析：上面的 `target != null` 被折进 `targetKnown`，流分析看不见（运行期不可能触发）

            var missileDef = rec.missileDefName == null
                ? null
                : DefDatabase<MissileDef>.GetNamedSilentFail(rec.missileDefName);
            if (missileDef == null) return; // Def 缺失：发射链已警告，此处防御性静默

            DVector3 pos = GroupPos(group);
            DVector3 vel = GroupVel(group);
            // 目标位置/速度：**有认知层就吃已知状态**（影子期按速度外推 = "假定它没机动"；真机动了就打偏）；
            // 认知层缺位（组件没加载）⇒ 回真值（旧行为，别让缺组件变成"弹全哑了"）。
            DVector3 tPos = sensor != null ? ContactMemory.CoastedPosition(in know, TimeScaleManager.GetPhysicsTickSafe()) : GroupPos(target);
            DVector3 tVel = sensor != null ? know.velocity : GroupVel(target);
            // 弹着偏置（§10 精确打击，2026-09-07）：Attack 携带"目标局部系偏移"（发射器点选格 → 米，
            // y=0）时，瞄准点 = 目标锚点（群中心 + 目标本体实体局部偏移）+ 旋转后的偏置——随目标
            // 运动/姿态保持贴在该格；无偏置 / 目标无实体 → 目标质心（零偏置路径，无行为回归）。
            DVector3 aimPoint = tPos;
            if (rec.aimOffsetShip != DVector3.zero)
            {
                var aimEntity = GetAimEntity(target, spaceGame);
                if (aimEntity != null)
                    aimPoint = tPos + aimEntity.Position + DVector3.Rotate(rec.aimOffsetShip, aimEntity.Rotation);
            }
            else if (CountAliveEntities(target, spaceGame) > 1)
            {
                // 多实体目标（合并弹群）零偏置 → **随机锁定一枚实体**（交战期缓存在记录上）：
                // 质心 = 队列中点的空当，瞄质心 = 瞄空处，导弹会从编队实体间穿过零接触
                // （2026-09-08 合并期被撞场景实测：探针弹两次穿过编队零接触）。随机 = 多枚打弹群的
                // 导弹天然散布到不同成员；缓存后每步只有一次 O(1) 查询，无遍历开销（用户定稿，
                // 否决"每步选最近"的逐实体距离比较）。单实体目标不受影响。
                var aimE = rec.aimEntityId >= 0 ? spaceGame.GetEntityById(rec.aimEntityId) : null;
                if (aimE == null || !target.ContainsEntity(rec.aimEntityId))
                {
                    aimE = null;
                    rec.aimEntityId = -1;
                    int n = target.GetEntityIds().Count;
                    if (n > 0)
                    {
                        int pick = UnityEngine.Random.Range(0, n);
                        foreach (int eid in target.GetEntityIds())
                        {
                            if (pick == 0)
                            {
                                var e = spaceGame.GetEntityById(eid);
                                if (e != null) { aimE = e; rec.aimEntityId = eid; }
                                break;
                            }
                            pick--;
                        }
                    }
                    if (rec.aimEntityId >= 0)
                        Log.Message(string.Format("[导弹瞄准] V{0} G{1} 多实体目标G{2} 随机锁定 E{3}（交战期保持，锁定实体离群时重选）",
                            rec.vehicleId, rec.groupId, target.GroupId, rec.aimEntityId));
                }
                if (aimE != null)
                    aimPoint = tPos + aimE.Position;
            }
            DVector3 toTarget = aimPoint - pos;
            double dist = toTarget.magnitude;
            if (dist < 1e-6) return; // 与目标/弹着点重合（罕见）：物理碰撞同步处理

            double surfaceDist = dist - GroupRadius(target, spaceGame);

            // ── 巡航段（> homing 边界）：不点火。转移/定期日志（每车 600 tick 限频）──
            if (surfaceDist > missileDef.homingEngageDistance)
            {
                if (now - rec.lastCruiseLogTick >= 600)
                {
                    rec.lastCruiseLogTick = now;
                    Log.Message(string.Format(
                        "[导弹巡航] V{0} G{1}→G{2} 表面距={3:F0}km（&gt;接管边界 {4:F0}km）惯性巡航——轨道交会/变轨由指挥（旧功能）管辖，等待进入终末或改派",
                        rec.vehicleId, rec.groupId, rec.targetGroupId,
                        surfaceDist / 1000.0, missileDef.homingEngageDistance / 1000.0));
                }
                return;
            }

            // ── 弹群解离（终末接管即拆，§5 2026-09-08）：合并群 surfaceDist ≤ homing 边界即整群
            // 一次性拆成 N 个单实体 group。每枚自带满罐（燃料随弹走，无分账）；拆后各枚独立制导，
            // 沿航向队列（发射散布格距）自然错峰到达——前弹弹头 AoE 打不到后到兄弟。──
            if (!rec.volleySplit && rec.logicalCount > 1)
            {
                if (mergedInboundLogged.Add(rec.groupId))
                {
                    Log.Message(string.Format(
                        "[弹群] V{0} G{1} Attack 合并态入界（N={2} 组内实体={3} surface={4:F1}km ≤ homing {5:F0}km）→ 解离",
                        rec.vehicleId, rec.groupId, rec.logicalCount, CountAliveEntities(group, spaceGame),
                        surfaceDist / 1000.0, missileDef.homingEngageDistance / 1000.0));
                }
                DissociateVolley(rec, group, spaceGame);

                // 解离后本组原点已移到领弹世界位（局部偏移清零）——**必须重取几何**：
                // 上面缓存的 pos/surfaceDist 是解离前的组原点（队首在队列最前，可差半队长 ~7km）。
                // 2026-09-08 游戏内实证：旧位置算出的首条指令方向 = 出膛轴方向，与弹体姿态恰好对准
                // → 主网立刻满推力点火（该步烧 120kg = 整步 158m/s），接近率瞬间冲到 ~310，
                // 而队首 400m 横偏还没开始修 → 到靶仅剩 2s → 以 0.3km 擦过。
                pos = GroupPos(group);
                vel = GroupVel(group);
                toTarget = aimPoint - pos;
                dist = toTarget.magnitude;
                if (dist < 1e-6) return;
                surfaceDist = dist - GroupRadius(target, spaceGame);
            }

            // ── 终末段接管 ──
            if (!terminalLogged.Contains(rec.groupId))
            {
                terminalLogged.Add(rec.groupId);
                rec.attackStartSurface = surfaceDist; // 追丢中止基准（本次 Attack 接管点）
                // 目标状态诊断（2026-09-07：designator 开火报 20km 而接管报 56km/0.22km 的矛盾——
                // 打印目标解析/数值状态与绝对位置速度，判定目标是否在圆轨解析态）
                string targetState = string.Format(
                    "目标G{0} pos=({1:F0},{2:F0},{3:F0})m vel=({4:F1},{5:F1},{6:F1})m/s 圆轨={7} r={8:F0}km",
                    target.GroupId,
                    tPos.x, tPos.y, tPos.z,
                    tVel.x, tVel.y, tVel.z,
                    target.IsCircularOrbit ? "是" : "否",
                    (target.IsCircularOrbit ? target.circularRadius : 0.0) / 1000.0);
                Log.Message(string.Format(
                    "[导弹制导] V{0} G{1}→G{2} 终末接管：表面距={3:F2}km 质心距={4:F2}km 目标半径={5:F0}m（控制律：横向速度归零 + 接近率补量双门控[阈值{6:F0} 巡航{7:F0} 时间裕度{8:F1}] 几何丢失[最近点+{9:F0}km]）｜ {10}",
                    rec.vehicleId, rec.groupId, rec.targetGroupId,
                    surfaceDist / 1000.0, dist / 1000.0, GroupRadius(target, spaceGame),
                    missileDef.guidanceClosingThreshold, missileDef.terminalSpeed, missileDef.guidanceTimeMargin,
                    missileDef.guidanceLostMarginMeters / 1000.0, targetState));
                if (aimPoint != tPos)
                {
                    Log.Message(string.Format(
                        "[导弹制导] V{0} G{1} 弹着偏置启用：瞄准目标局部({2:F1},0,{3:F1})m → 世界点=({4:F0},{5:F0},{6:F0})m（撞击与弹头 Map 爆炸落于所选格）",
                        rec.vehicleId, rec.groupId, rec.aimOffsetShip.x, rec.aimOffsetShip.z,
                        aimPoint.x, aimPoint.y, aimPoint.z));
                }
            }
            // 清玩家遗留机动节点（巡航段节点由队列引擎执行；终末起自主撞击接管）
            if (group.plannedManeuvers.Count > 0)
            {
                int n = group.plannedManeuvers.Count;
                group.ClearManeuvers();
                Log.Message(string.Format("[导弹接管] V{0} G{1} 清除遗留机动节点 {2} 个 → 自主火炬撞击", rec.vehicleId, rec.groupId, n));
            }

            // 燃尽预检：主+RCS 总工质低于阈值 → 熄火惯性弹（K5；物理侧主网尽会自动转 RCS 兜底直至燃尽），
            // 并开始寿命计时（Def.bodyLifetimeSeconds 后自动清理——此前缺失，惰性弹永久占碰撞池/登记簿）
            var src = spaceGame.GetGroupPropulsionSource(group);
            double fuelKg = src?.TotalPropellantMass ?? 0.0;
            if (fuelKg < FuelOutKg)
            {
                if (group.HasActiveBurn) group.EndBurn();
                if (!rec.fuelOutReported)
                {
                    rec.fuelOutReported = true;
                    rec.despawnAtTick = now + (int)Math.Max(30f, missileDef.bodyLifetimeSeconds);
                    Log.Message(string.Format("[导弹燃尽] V{0} G{1} 工质尽（{2:F0}kg）→ 熄火惯性弹（K5：不判 miss，{3}s 后自动清理）",
                        rec.vehicleId, rec.groupId, fuelKg, (int)missileDef.bodyLifetimeSeconds));
                }
                return;
            }

            // ── 末段收敛制导（纯核心 MissileGuidanceCore，2026-09-08 抽纯）──
            // 控制律 = 横向相对速度归零 + 接近率补量双门控（阈值 + 横差修正时间约束）+
            // 几何丢失判定（最近点基准）→ Abort。全部参数来自 MissileDef（可配），
            // 控制律本体在纯测试里用迷你飞行仿真迭代（**不在此处记用例数**：数字必漂，2026-09-14 实际已与旧注释差 13 条），游戏内只做验收。
            bool allowBoost = now >= rec.holdFireUntilTick; // 错峰时隙未到：只修横向不冲刺
            var step = MissileGuidanceCore.ComputeStep(pos, vel, aimPoint, tVel,
                GroupRadius(target, spaceGame), missileDef.GuidanceParams, rec.guidanceState, allowBoost);
            rec.guidanceState = step.state;

            if (step.action == MissileGuidanceAction.Abort)
            {
                if (group.HasActiveBurn) group.EndBurn();
                rec.mission = MissionType.Standby;
                terminalLogged.Remove(rec.groupId);
                rec.attackStartSurface = 0.0;
                rec.guidanceState = default;
                Log.Message(string.Format(
                    "[导弹中止] V{0} G{1}→G{2} 几何丢失：最近点 {3:F1}km → 当前 {4:F1}km（超出 lostMargin {5:F0}km，掉头再捕获无望）→ 中止回 Standby 待命，等待玩家改派",
                    rec.vehicleId, rec.groupId, rec.targetGroupId,
                    step.state.minSurfaceDist / 1000.0, step.surfaceDist / 1000.0,
                    missileDef.guidanceLostMarginMeters / 1000.0));
                return;
            }

            if (step.action == MissileGuidanceAction.Burn)
            {
                if (group.HasActiveBurn) group.EndBurn(); // 清上一步残余（两拍时延语义，Boarding 同构）
                group.StartBurn(step.dv); // 物理引擎：转向-收心（≤1.5°）-主网点火；每步重置方向
            }
            else if (group.HasActiveBurn)
            {
                group.EndBurn(); // 已达期望速度场：熄火惯性逼近（轨道弯曲/目标机动会重新触发修正）
            }

            // 再捕获事件（一次性日志）：擦过目标 → 掉头（closing 翻负）；恢复接近后复位
            if (step.reacquiring)
            {
                if (!rec.reacquireLogged)
                {
                    rec.reacquireLogged = true;
                    Log.Message(string.Format(
                        "[导弹再捕获] V{0} G{1}→G{2} 擦过目标（最近点 {3:F1}km，当前 {4:F1}km）→ 掉头再捕获（接近率 {5:F0}m/s 反向，横向差 {6:F0}m/s）",
                        rec.vehicleId, rec.groupId, rec.targetGroupId,
                        step.state.minSurfaceDist / 1000.0, step.surfaceDist / 1000.0, step.closing, step.lateral));
                }
            }
            else if (rec.reacquireLogged)
            {
                rec.reacquireLogged = false;
                Log.Message(string.Format("[导弹再捕获] V{0} G{1} 已重新咬住目标（表面距 {2:F1}km，接近率 {3:F0}m/s）",
                    rec.vehicleId, rec.groupId, step.surfaceDist / 1000.0, step.closing));
            }

            // 详细日志（门控，10 tick 限频）：距离/速度/接近率/横向差/点火状态
            // 附双方权威坐标（与碰撞 [碰撞详细]/[导弹复核] 同 tick 对照——2026-09-07 制导-碰撞距离矛盾诊断）
            if (DebugDetailedGuidance && now - rec.lastGuidanceLogTick >= 10)
            {
                rec.lastGuidanceLogTick = now;
                Log.Message(string.Format(
                    "[导弹制导] V{0} G{1}→G{2} 表面距={3:F1}km 速={4:F0}m/s 接近={5:F1}m/s 横差={6:F0}m/s 冲刺={7:F0}m/s 需求上限={8:F0}m/s 燃料={9:F0}kg burn={10}{11} | t={12} 弹pos=({13:F0},{14:F0},{15:F0}) 目pos=({16:F0},{17:F0},{18:F0})",
                    rec.vehicleId, rec.groupId, rec.targetGroupId,
                    step.surfaceDist / 1000.0, vel.magnitude, step.closing, step.lateral, step.boostDemand,
                    step.closingDemand,
                    fuelKg,
                    group.HasActiveBurn ? "on" : "off",
                    step.reacquiring ? " 再捕获" : "",
                    now,
                    pos.x, pos.y, pos.z,
                    tPos.x, tPos.y, tPos.z));
            }
        }

        // ════════════════════════════════════════════
        //  弹群（多实体）生命周期（2026-09-08 §5 思路 A：每枚自带满罐，无任何燃料记账）
        // ════════════════════════════════════════════

        /// <summary>群内存活实体数（弹群实体完整性诊断用）。</summary>
        private static int CountAliveEntities(SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            int n = 0;
            foreach (int eid in group.GetEntityIds())
            {
                if (spaceGame.GetEntityById(eid) != null) n++;
            }
            return n;
        }

        /// <summary>合并态入界探针（瞬态，不序列化）：记录已打过"Attack 合并态入界"首条日志的 group，
        /// 验证解离触发链（2026-09-08 场景排障：首波 volley 未解离即接触目标）。</summary>
        private readonly HashSet<int> mergedInboundLogged = new HashSet<int>();

        /// <summary>
        /// 实体离群唯一出口（阵亡清理/拦截引爆/碰撞转化共用）：
        /// 移除实体出组 + 维护记录（logicalCount = 剩余枚数、entityId 指向群内存活实体）。
        /// **无燃料逻辑**——每枚自带满罐：燃料/质量随实体一起消失，群级推进（聚合器）按存活实体
        /// 自动收缩，不存在油箱移交/流率缩放（思路 A 删除的记账不复存在）。
        /// 组空返回 true——调用方负责注销记录 + DestroyGroup（pair 循环内须延后到循环外）。
        /// </summary>
        public bool RemoveMissileEntity(VehicleRecord rec, int deadEntityId, string reason)
        {
            var spaceGame = SpaceGame;
            if (spaceGame == null || rec == null) return false;
            var group = spaceGame.GetGroupById(rec.groupId);
            if (group == null || !group.ContainsEntity(deadEntityId)) return false;

            group.RemoveEntity(deadEntityId);
            // A5b：离群 = 该实体已死 → **必须从实体注册表注销**。
            // 旧实现只移出组、从不注销：DestroyGroup 遍历的是组内**剩余**成员，被移出的那枚从此
            // 永远留在 `entities`（并随档序列化），且 `GetEntityById(陈旧 id)` 会取回僵尸实体
            // （质量/燃料/推进全错）。2026-09-14 查出的泄漏，实测每轮验收累积。
            var deadEntity = spaceGame.GetEntityById(deadEntityId);
            if (deadEntity != null) spaceGame.UnregisterEntity(deadEntity);
            rec.logicalCount = Math.Max(0, rec.logicalCount - 1);
            if (rec.entityId == deadEntityId)
            {
                rec.entityId = -1;
                foreach (int eid in group.GetEntityIds()) { rec.entityId = eid; break; }
            }
            Log.Message(string.Format("[弹群] G{0} E{1} 离群（{2}）：剩 {3} 枚",
                group.GroupId, deadEntityId, reason, rec.logicalCount));
            return rec.logicalCount <= 0;
        }

        /// <summary>
        /// 弹群解离（§5，2026-09-08）：1 group N 实体 → 领弹留本组、其余逐个拆成独立单实体 group。
        /// **每枚自带满罐/当前余量**——解离纯搬实体与记录，无分账无补装（燃料随弹走）。
        /// 本组原点移到领弹世界位（领弹局部偏移清零）；拆出组原点 = 各实体当前世界位（同速同姿态）。
        /// 克隆记录：volleyId/launcherGroupId 同源、mission/目标/弹着偏置拷贝 → 同 volley 互碰排除
        /// 自动生效、面板/全体命令/跟随同步天然覆盖。拆出组各自从自身表面距起算（&gt;边界先巡航，
        /// 穿越后自动进终末），沿航向队列错峰到达。
        /// 调用约束：TickAttack 内（vehicles 遍历中本方法只追加记录，安全）。
        /// </summary>
        private void DissociateVolley(VehicleRecord rec, SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            var ids = new List<int>(group.GetEntityIds());
            int alive = ids.Count;
            if (alive <= 1)
            {
                rec.logicalCount = alive;
                rec.volleySplit = true;
                return;
            }

            // ── 本组原点移到领弹世界位（后续制导按领弹实际位置）──
            if (group.IsCircularOrbit)
                group.ExitCircularOrbit(); // 圆轨模式 CurrentOrbit 非权威：先退出（用真实位置拆组）
            int leaderId = rec.entityId;
            if (!group.ContainsEntity(leaderId) && ids.Count > 0)
                leaderId = ids[0];
            rec.entityId = leaderId;
            var orbit = group.CurrentOrbit;
            DVector3 oldPos = orbit.positionSystem.v;
            DVector3 oldVel = orbit.velocitySystem.v;
            var leader = spaceGame.GetEntityById(leaderId);
            if (leader != null)
            {
                group.CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)(oldPos + leader.Position),
                    velocitySystem = (SystemVec3)oldVel,
                    rotation = orbit.rotation
                };
                leader.SetPositionVelocity(DVector3.zero, DVector3.zero);
            }
            rec.logicalCount = 1;
            rec.volleySplit = true;

            // ── 逐实体拆出：新组（原点 = 实体世界位，同速同姿态）+ 克隆记录 ──
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            int created = 0;
            foreach (int eid in ids)
            {
                if (eid == leaderId) continue;
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;

                int gid = spaceGame.AllocGroupId();
                var ng = new SpaceEntityGroup(gid, group.FactionId)
                {
                    IsActive = true,
                    IsMovable = true,
                    IsCollidable = true,
                    // ⚠ **派生点清单**：这是全库唯一一处"从既有 group 派生新 group"的地方（其余 11 处都是从零构造）。
                    // 逐字段复制的写法意味着**每新增一个 group 字段都要回来加一行**，否则子群静默回到默认值。
                    // 2026-09-16 步 1 就漏过一次：`IsStealth` 未复制 ⇒ 弹群一解离（进入末制导段）子群全部变"公开坐标"，
                    // 匿踪语义**在最该隐蔽的时刻丢失**。新增 group 字段时请对照此清单（§0.3 纪律）。
                    IsStealth = group.IsStealth,
                    CelestialBodyId = group.CelestialBodyId
                };
                ng.CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)(oldPos + e.Position),
                    velocitySystem = (SystemVec3)(oldVel + e.Velocity),
                    rotation = e.Rotation
                };
                ng.maxAngularRate = group.maxAngularRate;
                spaceGame.RegisterGroup(ng);

                group.RemoveEntity(eid);
                e.GroupId = gid;
                e.SetPositionVelocity(DVector3.zero, DVector3.zero);
                ng.AddEntity(eid); // ← 2026-09-08 修复：此前漏加——拆出组 entityIds 为空壳，
                                   //   推进源/质量/碰撞盒全部查不到（0kg 燃尽、幽灵组）
                if (ng.CelestialBodyId >= 0 && celestialMgr != null)
                    celestialMgr.RegisterEntityToBody(eid, ng.CelestialBodyId);

                var clone = RegisterMissileLeader(gid, eid, rec.volleyId, rec.launcherGroupId, 1,
                    rec.missileDefName, rec.weaponDefName, rec.mission, rec.targetGroupId, rec.aimOffsetShip);
                if (clone != null)
                {
                    clone.volleySplit = true; // 单枚记录不再拆
                    // 错峰点火时隙（2026-09-08 三轮实测：各弹收敛 closing 不同 → 队列间隔在到达前
                    // 坍缩 → 后弹爆炸杀死先到兄弟）。时隙 = 距领弹深度 / 巡航接近率（游戏秒→tick：
                    // 1 tick = PhysicsSpeedMultiplier 游戏秒）。时隙前只修横向不冲刺 → 到达间隔由
                    // 构造保证 ≥ 间距/巡航（≥7.4s @2km/450），爆炸（致死带 1km）够不到后到兄弟。
                    var missileDefClone = DefDatabase<MissileDef>.GetNamedSilentFail(rec.missileDefName);
                    double cruise = missileDefClone != null && missileDefClone.terminalSpeed > 1f
                        ? missileDefClone.terminalSpeed : 450.0;
                    // 深度 = 拆出组世界位（ng.CurrentOrbit.positionSystem）到本组原点的距离——队列共线，即距领弹的队列深度
                    double depth = (ng.CurrentOrbit.positionSystem - group.CurrentOrbit.positionSystem).magnitude;
                    double delayGameSec = depth / cruise;
                    var tsm = Current.Game?.GetComponent<TimeScaleManager>();
                    float mult = tsm != null ? tsm.PhysicsSpeedMultiplier : 1f;
                    clone.holdFireUntilTick = Find.TickManager.TicksGame
                        + (int)Math.Ceiling(delayGameSec / Math.Max(1e-3f, mult));
                }
                created++;
            }
            Log.Message(string.Format(
                "[弹群解离] G{0}（N={1}）→ 领弹留组（组内实体 {2}）+ 拆出 {3} 枚独立导弹（volley={4} 目标G{5} 偏置=({6:F1},0,{7:F1})m）",
                group.GroupId, alive, CountAliveEntities(group, spaceGame), created, rec.volleyId, rec.targetGroupId,
                rec.aimOffsetShip.x, rec.aimOffsetShip.z));
        }

        /// <summary>与 CombatManager.GetGroupPos 同基准：圆轨解析位置 / 非圆轨 CurrentOrbit.position（physicsTick 基准）。</summary>
        private static DVector3 GroupPos(SpaceEntityGroup g)
        {
            if (g.IsCircularOrbit)
                return g.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.positionSystem.v;
        }

        /// <summary>与 CombatManager.GetGroupVelocity 同基准：圆轨解析切向速度 / 非圆轨 CurrentOrbit.velocity。</summary>
        private static DVector3 GroupVel(SpaceEntityGroup g)
        {
            if (g.IsCircularOrbit)
                return g.GetCircularVelocityAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.velocitySystem.v;
        }

        /// <summary>目标瞄准本体实体：首个带 Map 实体（空间站/带图船——弹着偏置只对 Map 目标有意义）；
        /// 无带 Map 实体时取首个实体（防御性兜底）。</summary>
        private static SpaceEntity GetAimEntity(SpaceEntityGroup target, SpaceGameComp spaceGame)
        {
            foreach (int eid in target.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null && e.MapId >= 0) return e;
            }
            foreach (int eid in target.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null) return e;
            }
            return null;
        }

        /// <summary>group 世界包围球半径（m）：实体旋转后 AABB 半对角线最大者（K4 表面距口径）。</summary>
        private static double GroupRadius(SpaceEntityGroup g, SpaceGameComp spaceGame)
        {
            double r = 0.0;
            foreach (int eid in g.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                e.GetAabbSystem(out var mn, out var mx);
                double er = (mx - mn).magnitude * 0.5;
                if (er > r) r = er;
            }
            return r;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextVehicleId, "nextVehicleId", 1);
            Scribe_Values.Look(ref nextVolleyId, "nextVolleyId", 1);
            Scribe_Collections.Look(ref vehicles, "vehicles", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (vehicles == null)
                    vehicles = new List<VehicleRecord>(); // 旧存档无此节点 → Scribe 不建列表，这里补建
                // 注：原实现误写为 vehicles.Clear()（对 null 调用）→ 旧存档读档必 NRE；CS8602 静态分析抓出
                // 反查索引随存档重建
                byGroupId.Clear();
                for (int i = 0; i < vehicles.Count; i++)
                    byGroupId[vehicles[i].groupId] = vehicles[i];
            }
        }
    }
}
