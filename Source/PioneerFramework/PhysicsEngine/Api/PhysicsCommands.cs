// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using PioneerFramework.Landing;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **物理引擎的写入口**（2026-10-31，类重组批 2）。
    ///
    /// 壳外代码要改变物理状态，只走这里——每个方法都是“玩法/表现的一次意图”，
    /// 物理语义（质量怎么流、速度怎么变）仍然在引擎里。
    /// 所有方法都返回 bool：组不存在 / 当前世界没有引擎 ⇒ false，不抛异常。
    /// </summary>
    public static class PhysicsCommands
    {
        /// <summary>选燃烧网络（`defName == null` 表示恢复玩家网络选择；`rcsOnly` 只烧 RCS）。</summary>
        public static bool SetBurnNetwork(int groupId, string? defName, bool rcsOnly)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            group.ApplyNetworkSelection(defName, rcsOnly);
            return true;
        }

        /// <summary>进入解析档圆轨（`realTick` = 物理 tick 的取整口径，与既有调用点一致）。</summary>
        public static bool EnterCircularOrbit(int groupId, int realTick, int centralBodyId)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            group.EnterCircularOrbit(realTick, centralBodyId);
            return true;
        }

        /// <summary>退出解析档圆轨，回到数值积分档。</summary>
        public static bool ExitCircularOrbit(int groupId)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            group.ExitCircularOrbit();
            return true;
        }

        /// <summary>切换参考系（SOI 换系）；组不存在/无引擎返回 false。</summary>
        public static bool SwitchFrame(int groupId, int targetBodyId)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out _)) return false;
            var comp = Current.Game?.GetComponent<SpaceGameComp>();
            if (comp == null) return false;
            comp.SwitchGroupFrame(groupId, targetBodyId);
            return true;
        }

        /// <summary>排队一个待处理冲量（系统系，N·s）。</summary>
        public static bool AddPendingImpulse(int groupId, DVector3 impulseSystem)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            group.pendingImpulse += impulseSystem;
            return true;
        }

        // ── 批 5e（2026-11-01）：PFView 取数改 id 前的命令面 ──
        // 只做“按 id 解析 → 调既有宿主方法”的包装：不新增物理语义，算式/时间基/状态提交全在宿主。

        /// <summary>
        /// **立即抛级**（批 5e）：抛掉最高级号，成功时 `newGroupId` = 新组的 id，失败时 `-1`。
        /// 失败不抛异常；`reason` 是宿主的拒绝原因串（`single-stage` / `multi-entity` / `would-orphan` …），
        /// 组不存在给 `group-not-found`，世界没有引擎给 `no-engine`。
        /// </summary>
        public static bool SeparateStage(int groupId, out int newGroupId, out string reason)
        {
            newGroupId = -1;
            reason = "";
            if (!PhysicsApi.TryGetHost(out var host)) { reason = "no-engine"; return false; }
            var group = host.GetGroupById(groupId);
            if (group == null) { reason = "group-not-found"; return false; }

            var separated = host.TrySeparateStage(group, out reason);
            if (separated == null) return false;
            newGroupId = separated.GroupId;
            return true;
        }

        /// <summary>
        /// **立即抛级（多块，2026-11-03）**：一次事件可能抛出多个组（科罗廖夫十字 = 4 个助推器组）。
        /// 成功时 `newGroupIds` = 各个新组 id（blockId 升序）；失败空数组 + `reason`。
        /// </summary>
        public static bool SeparateStageMulti(int groupId, out int[] newGroupIds, out string reason)
        {
            newGroupIds = Array.Empty<int>();
            reason = "";
            if (!PhysicsApi.TryGetHost(out var host)) { reason = "no-engine"; return false; }
            var group = host.GetGroupById(groupId);
            if (group == null) { reason = "group-not-found"; return false; }

            var separated = host.TrySeparateStageMulti(group, out reason);
            if (separated.Count == 0) return false;
            newGroupIds = new int[separated.Count];
            for (int i = 0; i < separated.Count; i++) newGroupIds[i] = separated[i].GroupId;
            return true;
        }

        /// <summary>
        /// **释放载荷**（批 5e）：`partId` = 载荷舱部件的 id（在 `groupId` 的实体上定位），
        /// 宿主按注册的载荷设计在父组位置生成新组。成功时 `newGroupId` = 新组 id，失败 `-1`。
        /// 定位失败给 `bay-part-not-found`；其余失败原因透传宿主（`no-payload-design-id` 等）。
        /// </summary>
        public static bool DeployPayload(int groupId, int partId, out int newGroupId, out string reason)
        {
            newGroupId = -1;
            reason = "";
            if (!PhysicsApi.TryGetHost(out var host)) { reason = "no-engine"; return false; }
            var group = host.GetGroupById(groupId);
            if (group == null) { reason = "group-not-found"; return false; }

            SpacePart? bayPart = null;
            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity == null || entity.GroupId != groupId) continue;
                var part = entity.GetPartById(partId);
                if (part == null) continue;
                bayPart = part;
                break;
            }
            if (bayPart == null) { reason = "bay-part-not-found"; return false; }

            var deployed = host.TryDeployPayload(group, bayPart, out reason);
            if (deployed == null) return false;
            newGroupId = deployed.GroupId;
            return true;
        }

        /// <summary>**自动抛级开关**（批 5e）：落组态 + 建/撤计划，语义全在宿主 `SetAutoStage`。</summary>
        public static bool SetAutoStage(int groupId, bool enabled)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            host.SetAutoStage(group, enabled);
            return true;
        }

        /// <summary>
        /// **进入舱室内部图**（批 5e）：按 `partId` 在 `groupId` 的实体上定位舱室件，
        /// 懒生成口袋地图并进入。不是舱室件 / 不是本组件 / 生成失败 ⇒ false（宿主的日志照旧）。
        /// </summary>
        public static bool EnterRoomMap(int groupId, int partId)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = host.GetEntityById(entityId);
                if (entity == null || entity.GroupId != groupId) continue;
                var part = entity.GetPartById(partId);
                if (part == null) continue;
                var map = SpaceMapGenerator.GenerateRoomMap(entity, part);
                if (map == null) return false;
                SpaceMapGenerator.EnterPocketMap(map);
                return true;
            }
            return false;
        }

        // ── 批 5e §4.3b：机动队列 / 规划器 / 交会窗口 ──

        /// <summary>**紧急终止当前机动**（批 5e §4.3b）：语义全在宿主 `EmergencyStopBurn`。</summary>
        public static bool EmergencyStopBurn(int groupId)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            host.EmergencyStopBurn(group);
            return true;
        }

        /// <summary>**取消此节点及以后**的机动（批 5e §4.3b）：语义全在宿主 `CancelGroupManeuvers`。</summary>
        public static bool CancelManeuversFrom(int groupId, int index)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            host.CancelGroupManeuvers(group, index);
            return true;
        }

        /// <summary>**清除全部机动**（批 5e §4.3b）。</summary>
        public static bool ClearManeuvers(int groupId)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            group.ClearManeuvers();
            return true;
        }

        /// <summary>**断开交会/接近引导**（批 5e §4.3b）。</summary>
        public static bool ClearRendezvousGuidance(int groupId)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            group.ClearRendezvousGuidance();
            return true;
        }

        /// <summary>
        /// **接合交会引导**（批 5e §4.3b）：与旧窗口同一条路——登记（`prograde`、全网络）→ 接合。
        /// 目标不存在 ⇒ false（不登记半条指引）。
        /// </summary>
        public static bool EngageRendezvous(int selfGroupId, int targetGroupId, int tick)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var self = host.GetGroupById(selfGroupId);
            if (self == null || host.GetGroupById(targetGroupId) == null) return false;
            self.RegisterRendezvousGuidance(targetGroupId, tick, prograde: true, useNetworks: null);
            self.ArmRendezvousGuidance();
            return true;
        }

        /// <summary>
        /// **把规划器算出的计划送进机动队列**（批 5e §4.3b）：逐条调用 `OrbitAdapter.TryQueuePlan`，
        /// 失败时整条不加（语义与旧窗口相同），`reason` / `summary` 透传。
        /// </summary>
        public static bool QueuePlan(int groupId, OrbitView from, IReadOnlyList<PlanNode> nodes,
            PlanIntent? intent, out string reason, out string summary)
        {
            reason = "";
            summary = "";
            if (!PhysicsApi.TryGetHost(out var host)) { reason = "no-engine"; return false; }
            var group = host.GetGroupById(groupId);
            if (group == null) { reason = "group-not-found"; return false; }
            var list = new List<PlanNode>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++) list.Add(nodes[i]);
            return OrbitAdapter.TryQueuePlan(group, from, list, out reason, out summary, intent);
        }

        /// <summary>**当前选网**（批 5e §4.3b）：`defName` 空/null = 全启用；Def 找不到 ⇒ false（不改状态）。</summary>
        public static bool SetCurrentNetwork(int groupId, string? defName)
        {
            if (!PhysicsApi.TryGetGroup(groupId, out var group)) return false;
            if (string.IsNullOrEmpty(defName))
            {
                group.currentNetwork = null;
                return true;
            }
            var def = DefDatabase<PropellantDef>.GetNamedSilentFail(defName);
            if (def == null) return false;
            group.currentNetwork = def;
            return true;
        }

        // ── 批 5e §4.3b：着陆窗口 ──

        /// <summary>
        /// **着陆前的地图准备**（批 5e §4.3b）：包 `LandingSourceMapService.BeginPrepare`；
        /// 回调把 `LandingReport.Text` 摊成字符串（表现层不引 Landing 类型）。
        /// </summary>
        public static bool BeginLandingPrepare(int groupId, Action? onReady, Action<string>? onProgress)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            LandingSourceMapService.BeginPrepare(host, group,
                () => onReady?.Invoke(),
                report => onProgress?.Invoke(report.Text));
            return true;
        }

        /// <summary>**选点着陆**（批 5e §4.3b）：语义全在宿主 `TryLandAtTile`。</summary>
        public static bool LandAtTile(int groupId, int targetBodyId, PlanetTile target, out string? reason)
        {
            reason = null;
            if (!PhysicsApi.TryGetHost(out var host)) { reason = "no-engine"; return false; }
            var group = host.GetGroupById(groupId);
            if (group == null) { reason = "group-not-found"; return false; }
            return host.TryLandAtTile(group, targetBodyId, target, out reason);
        }

        // ── 批 5e §4.3b：搬运窗口 ──

        /// <summary>**悬浮开关**（批 5e §4.3b）：`analytic` 只在开启时生效（与宿主重载一致）。</summary>
        public static bool SetHover(int groupId, bool enabled, bool analytic = false)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            return host.SetHover(group, enabled, analytic);
        }

        /// <summary>**在物理档/解析档之间切换**（批 5e §4.3b）。</summary>
        public static bool SwitchHoverTier(int groupId, bool analytic)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            return host.SwitchHoverTier(group, analytic);
        }

        /// <summary>**停止解析搬运**（批 5e §4.3b）：语义全在宿主。</summary>
        public static bool StopAnalyticTransfer(int groupId, string reason)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            return host.StopAnalyticTransfer(group, reason);
        }

        /// <summary>**开始解析搬运**（批 5e §4.3b）：语义全在宿主 `StartAnalyticTransfer`。</summary>
        public static bool StartAnalyticTransfer(int groupId, DVector3 targetPos, DVector3 targetVel,
            double dvTotal, double physicsTick)
        {
            if (!PhysicsApi.TryGetHost(out var host)) return false;
            var group = host.GetGroupById(groupId);
            if (group == null) return false;
            return host.StartAnalyticTransfer(group, targetPos, targetVel, dvTotal, physicsTick);
        }
    }
}
