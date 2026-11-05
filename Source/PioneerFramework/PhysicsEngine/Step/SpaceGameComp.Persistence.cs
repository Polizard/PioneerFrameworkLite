// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using MechJebLib.Lambert;

namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        public override void LoadedGame()
        {
            base.LoadedGame();
            SpacecraftMapRegistry.Reconcile();
            RestorePhysicsClockAnchor();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SpacecraftMapRegistry.Reconcile();
            RestorePhysicsClockAnchor();
        }

        /// <summary>
        /// 读档/新局收尾后预热（2026-10-19 尖峰归因）：
        /// 实测第一次碰撞帧 11.2ms 里碰撞本身只有 1.1ms；2.5ms 是引力源表**首次构建**、
        /// 1.3ms 是 apply 块里调试/巡检路径首次 JIT。这类一次性成本必须放在 **FinalizeInit**
        /// 而不是 `ExposeData(PostLoadInit)`：`CelestialBodyManager.ExposeData` 会在 PostLoadInit
        /// 把 `initialized` 置回 false，早于它的预热会被随后的重建吃掉（2026-10-19 13:20 日志实证）。
        /// 失败只告警：预热是优化，不是读档依赖。
        /// </summary>
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            try
            {
                var warmMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (warmMgr != null)
                {
                    double warmTick = TimeScaleManager.GetPhysicsTickSafe();
                    warmMgr.PrefetchSystemSources(1, warmTick);   // 1 = 地球（与调试场景同值）
                    ApplyAnalyticTransfer(0.0, warmTick, warmMgr);
                    ApplyHover(0.0, warmTick, warmMgr);

                    // 最大的一笔：`UpdateAllMovements` 自身（~千行）的首次 JIT 落在第一个物理步的
                    // "阶段之前"（步钟已启动、第一个阶段钟还没开始）⇒ 实测 12–13ms 残差。
                    // 这里用 early-return 预热：方法体在入口处已被完整编译，随后立刻返回，不碰任何状态。
                    warmupPhysicsStep = true;
                    // 形参在标志检查之后才会被碰 ⇒ 传 null! 安全（只为触发方法体 JIT）
                    try { UpdateAllMovements(Current.Game?.GetComponent<TimeScaleManager>()!); }
                    finally { warmupPhysicsStep = false; }

                    // 合成组预热：`ComputeGroupPhysics` 的首次 JIT / 天体场路径只在“有组的第一步”付
                    // （2026-10-19 13:32 日志：碰撞帧的组物理 4,326µs，扣掉引力/源表后 ~3.6ms 是首次路径）。
                    // 临时组不注册进 groups / 细网格、不可碰撞、远场；结果丢弃，不落盘。
                    var warmGroup = new SpaceEntityGroup(-1, 0)
                    {
                        IsActive = true,
                        IsMovable = true,
                        IsCollidable = false,
                        CelestialBodyId = 1,
                        CurrentOrbit = new OrbitState
                        {
                            positionSystem = (SystemVec3)new DVector3(1.0e9, 0.0, 0.0),
                            velocitySystem = (SystemVec3)new DVector3(0.0, 3000.0, 0.0),
                            rotation = DQuaternion.identity
                        }
                    };
                    ComputeGroupPhysics(warmGroup, 0.0, warmTick, warmMgr, new GravityScratch(), 1);

                    // 碰撞纯函数预热（2026-10-19 P0）：实测隔离冷启动 CollectPartContacts 1.8–2.3ms、
                    // TryFindDeepestPartPair 0.5–0.7ms、CollisionSolver 4.4–5.6ms。纯静态、无组/实体/
                    // 地图状态；10 km/s 的爆炸/伤害路径不覆盖（需另案隔离预热）。
                    CombatManager.WarmupCollisionPaths();

                    // 阶梯规划预热（2026-10-21）：游戏内 MakePlan 6.9–9.2ms vs 台架 0.2–0.5ms。
                    // **实测结论（重要）**：net10 上 `DOTNET_TieredCompilation=0` 时全支 0.26–0.95ms，
                    // 开着分层编译时即使 24 次热身仍 2.2ms、**200 次才收敛到 0.27/0.78ms** ⇒
                    // 贵的不是算法、是 **tier0 代码**（每组每 120s 才调一次 ⇒ 永远升不上 tier1）。
                    // 所以：①同步 24 次兜住"读档后马上要用"的第一拍；②后台再跑 200 次把 tier1 催到位
                    // （结果丢弃、不碰状态；`Warmup` 是纯函数，后台线程安全）。曲线只在 DevMode 打。
                    double ladderMu = GetBodyGM(1);
                    double ladderBodyR = GetBodySafeRadius(1);
                    string ladderWarm = RendezvousLadder.Warmup(ladderMu, ladderBodyR, 24);
                    if (ladderWarm.Length > 0 && Prefs.DevMode)
                        Log.Message($"[阶梯·预热] {ladderWarm}");
                    try
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { ladderWarmBg = RendezvousLadder.Warmup(ladderMu, ladderBodyR, 200); }
                            catch { /* 后台预热失败无声：只影响速度 */ }
                        });
                    }
                    catch { /* 线程池不可用时静默降级 */ }
                }
            }
            catch (System.Exception e)
            {
                PhysicsLogWarning($"[Prefetch] 初始化预热失败（不影响读档）：{e.GetType().Name}: {e.Message}");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entities, "entities", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref groups, "groups", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref nextGroupId, "nextGroupId", 0);
            Scribe_Values.Look(ref nextEntityId, "nextEntityId", 0);
            // 载荷注册表（2026-10-26）：id → 载荷设计（深拷）；载荷舱只存 id。
            Scribe_Collections.Look(ref payloadDesigns, "payloadDesigns", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref nextPayloadDesignId, "nextPayloadDesignId", 1);
            if (payloadDesigns == null) payloadDesigns = new Dictionary<int, GroundAssembly.RocketDesignSpec>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                RebuildAutoStagePlans();
                nextGroupId = Math.Max(nextGroupId,MaxLiveId(groups.Keys)+1);
                nextEntityId = Math.Max(nextEntityId,MaxLiveId(entities.Keys)+1);
                planetGrids = new Dictionary<int, SpatialGrid3D<ISpatialEntity>>();
                groupLastCoarsePos = new Dictionary<int, DVector3>();
                hoverActiveCount = 0;   // [A7] 静态计数**按档重数**（静态量跨会话 ⇒ 不重数就会与档差一位）
                foreach (var group in groups.Values)
                {
                    if (group.hoverActive) hoverActiveCount++;
                    var targetGrid = GetGridForGroup(group.GroupId);
                    if (targetGrid != null)
                    {
                        targetGrid.Add(group, group.Position);
                        groupLastCoarsePos[group.GroupId] = group.Position;
                    }
                }

            }
        }
    }
}