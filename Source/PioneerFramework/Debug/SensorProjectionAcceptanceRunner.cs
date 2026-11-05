using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **[V7] 建筑级投影链的一键验收（2026-10-01）**
    ///
    /// 自跑一个空间站（`DebugShipSpawner.SpawnTestStation` 会顺带在站内放一座传感器阵列建筑），
    /// 然后依次验三件 —— 全自动，最后打一份结论块：
    ///   ① **投影非零**：`Group.BuildingTechBonus` = 建筑 Def 灵敏度 × HP 比例（=1）；
    ///      编队 `DetectTech` = **部件级求和**（从实体聚合推）+ **建筑级投影**（两段）；
    ///   ② **贡献随毁损而掉**：打到 50% HP ⇒ 建筑级减半；`Destroy()` ⇒ 归零、编队只剩部件级；
    ///   ③ **打印"存档前读数"两行** ⇒ 供**手动**存档往返对比（读档后再看那两行应逐行一致 —— 即 S11 那条判据）。
    ///
    /// 空闲时第一行返回（零开销），与其余验收 runner 同范式（`Tick` 由**物理步**驱动）。
    /// ⚠ 本 runner 的一切破坏都发生在**它自己刚部署的那座站**上；不碰玩家资产。
    /// </summary>
    public static class SensorProjectionAcceptanceRunner
    {
        /// <summary>是否在跑（[EW 总控] 借此等待本段完成）。</summary>
        public static bool Running => stage != Stage.Idle;

        private enum Stage { Idle, AwaitSettle, AwaitDamaged, AwaitDestroyed, Verdict }

        private static Stage stage = Stage.Idle;
        private static int stepCounter;
        private static int stageStep;
        private static int stationGroupId = -1;
        private static float partRange, partEcm, partEccm;   // 部件级求和（从事实推，不从"应该有"推）
        private static float defRange;                      // 建筑 Def 的灵敏度（从 comp 的 Props 读）
        private static float defJam;                        // [E3] 干扰机 Def 的干扰效力（从 comp 的 Props 读）

        /// <summary>调试页签入口：部署测试站并开始验收。</summary>
        public static void Run()
        {
            stage = Stage.AwaitSettle;
            stepCounter = 0;
            stageStep = 0;
            stationGroupId = -1;
            PflTestKit.Begin("[V7] 建筑级投影链（站 + 传感器阵列建筑：投影 / 毁损 / 往返）", "00 验收");
            PflTestKit.Note("等站部署 + 建筑出现；随后会自动打伤 → 摧毁 → 打结论。");
            DebugShipSpawner.SpawnTestStation();
            Log.Message("[V7·验收] 已部署测试站，等待站内传感器阵列建筑就位…");
        }

        private static SpaceEntityGroup? FindStationGroup(SpaceGameComp sg)
        {
            SpaceEntityGroup? best = null;
            // ⚠ 别用 `GetGroupsInRange(…, double.MaxValue)`——它走**网格查询**（`planetGrids.GetNearby`），
            //   超大半径会被网格吃掉（第一版就这么卡住的：永远找不到站）。调试 runner 直接遍历全部组即可。
            foreach (var g in sg.GetAllGroups())
            {
                if (g.GetLinkedMapComp() == null) continue;          // 有地图（=口袋地图）的才可能是站
                if (best == null || g.GroupId > best.GroupId) best = g;   // 取最新那条
            }
            return best;
        }

        private static Thing? FindBuilding(SpaceMapComp mc)
        {
            foreach (var t in mc.map.listerThings.AllThings)
                if (t.TryGetComp<CompSensorArray>() != null) return t;
            return null;
        }

        /// <summary>[E3] 站内那座干扰机建筑（按 comp 找，不按 defName）。</summary>
        private static Thing? FindJammer(SpaceMapComp? mc)
        {
            if (mc == null) return null;
            foreach (var t in mc.map.listerThings.AllThings)
                if (t.TryGetComp<CompJammer>() != null) return t;
            return null;
        }

        /// <summary>部件级求和（与 `FrameworkSelfTest` 同口径）：各实体 `AggregatedStats` 相加。</summary>
        /// <summary>取本组第一个实体（姿态判据用；取不到返回 null）。</summary>
        private static SpaceEntity? FirstEntityOf(SpaceGameComp sg, SpaceEntityGroup g)
        {
            foreach (int eid in g.GetEntityIds())
            {
                var ent = sg.GetEntityById(eid);
                if (ent != null) return ent;
            }
            return null;
        }

        private static void PartSum(SpaceGameComp sg, SpaceEntityGroup g, out float r, out float e, out float ec)
        {
            r = 0f; e = 0f; ec = 0f;
            foreach (int eid in g.GetEntityIds())
            {
                var ent = sg.GetEntityById(eid);
                if (ent == null) continue;
                var st = ent.AggregatedStats;
                r += st.sensorRange; e += st.ecmResist; ec += st.eccmResist;
            }
        }

        /// <summary>物理步驱动（空闲时零开销）。</summary>
        public static void Tick()
        {
            if (stage == Stage.Idle) return;
            stepCounter++;
            stageStep++;

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) { Finish("不在太空场景（无 SpaceGameComp）"); return; }

            switch (stage)
            {
                case Stage.AwaitSettle:
                {
                    var g = FindStationGroup(sg);
                    if (g == null || stageStep < 2) return;                 // 等站有地图 + 至少两拍
                    var mc = g.GetLinkedMapComp();
                    var b = mc != null ? FindBuilding(mc) : null;
                    if (b == null) { if (stageStep > 600) Finish("站里没找到传感器阵列建筑（摆放失败？）"); return; }

                    stationGroupId = g.GroupId;
                    PartSum(sg, g, out partRange, out partEcm, out partEccm);
                    defRange = b.TryGetComp<CompSensorArray>()!.Props.passiveRangeMeters;

                    var jammerB = FindJammer(mc);
                    defJam = jammerB?.TryGetComp<CompJammer>()?.Props.jamStrength ?? 0f;

                    var bonus = g.BuildingTechBonus;
                    float tot = g.DetectTech.sensitivityMeters;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        System.Math.Abs(bonus.sensitivityMeters - defRange) <= System.Math.Max(1f, defRange * 1e-4f),
                        "①建筑级投影 = 建筑 Def 灵敏度（HP 满）",
                        $"投影 {bonus.sensitivityMeters:F0} m vs Def {defRange:F0} m");
                    PflTestKit.Check(PflTestKit.Kind.Invariant,
                        System.Math.Abs(tot - (partRange + bonus.sensitivityMeters)) <= System.Math.Max(1f, partRange * 1e-4f),
                        "①编队 DetectTech = 部件级求和 + 建筑级投影（两段）",
                        $"编队 {tot:F0} m｜部件 {partRange:F0} + 建筑 {bonus.sensitivityMeters:F0}｜（实体聚合 抗压{partEcm:F2}）");

                    // [E3] 干扰机建筑（同一座站里第三座建筑）：建筑级投影 + 开关默认关
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        System.Math.Abs(g.BuildingJamBonus - defJam) <= System.Math.Max(1e-3f, defJam * 1e-4f),
                        "①干扰机建筑已投影（群级建筑级 = 建筑 Def 效力，HP 满）",
                        $"投影 {g.BuildingJamBonus:F2} vs Def {defJam:F2}｜群级 JamStrength {g.JamStrength:F2}（部件级 0）");
                    PflTestKit.Check(PflTestKit.Kind.Invariant, !g.JammingEnabled,
                        "①干扰开关默认**关**（建筑只给能力；开机是群级开关，且开机即暴露）",
                        $"JammingEnabled = {g.JammingEnabled}");

                    // [V7]/[E2] 主动雷达能力（同一座站里另有第二座建筑：`PF_Building_ActiveRadar`）
                    var rc = g.RadarCap;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        rc.activeGainBonus > 0f && rc.activeRadiation > 0f && rc.beamRangeMeters > 0f,
                        "①主动雷达能力已投影（增益 / 辐射 / 波束几何非零）",
                        $"增益 +{rc.activeGainBonus:F2}｜辐射 +{rc.activeRadiation:F2}｜半锥角 {rc.beamHalfAngleDeg:F1}°｜距离 {rc.beamRangeMeters / 1000f:F0} km");

                    // W1（枢纽）：开机 ⇒ 信号升高，且**升幅 = 基准 + 设备辐射增量**（"开机即更响，响多少由设备定"）
                    bool pingSaved = g.ActivePingEnabled;
                    g.ActivePingEnabled = false;
                    double sOff = g.SignalStrength;
                    g.ActivePingEnabled = true;
                    double sOn = g.SignalStrength;
                    g.ActivePingEnabled = pingSaved;                     // 自造状态必须还回去（与自检同纪律）
                    double rise = sOn - sOff;
                    double expectRise = SignalModel.ActivePingMultiplier + rc.activeRadiation;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        System.Math.Abs(rise - expectRise) <= System.Math.Max(1e-6, expectRise * 1e-4),
                        "①W1：开机信号升幅 = 基准 + 设备辐射增量",
                        $"升幅 {rise:F3} vs 期望 {expectRise:F3}（基准 {SignalModel.ActivePingMultiplier:F1} + 设备 {rc.activeRadiation:F2}）");

                    // [V3] 记忆时长 T（2026-10-02 裁定 ②）：建筑 Def 值必须经"建筑级 → 群级 SensorTech"到达
                    float defMem = b.TryGetComp<CompSensorArray>()!.Props.memorySeconds;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        System.Math.Abs(g.DetectTech.memorySeconds - defMem) <= System.Math.Max(0.5, defMem * 1e-4),
                        "①记忆时长 T 已投影（建筑 Def → 建筑级 → 群级 SensorTech）",
                        $"群级 T {g.DetectTech.memorySeconds:F1} s vs Def {defMem:F1} s｜默认兜底 {DetectionCore.DefaultMemorySeconds:F1} s");

                    // ①RCS 尺寸口径姿态无关（2026-10-02 裁定 ①）：**两侧都强制重算**后转 45° ⇒ S 必须逐位不变。
                    //   旧口径取世界 AABB 半对角线（随姿态变），而信号缓存**没有姿态失效钩子** ⇒ 旧写法在这里会变值。
                    var rotEntity = FirstEntityOf(sg, g);
                    if (rotEntity == null)
                    {
                        PflTestKit.CheckUnchecked("①RCS 姿态口径", "取不到站的实体 ⇒ 本条验不了（不折算 PASS）");
                    }
                    else
                    {
                        var rotSaved = rotEntity.Rotation;
                        g.InvalidateSignal();                    // 基准：强制重算
                        double s0 = g.SignalStrength;
                        rotEntity.SetRotation(Quaternion.Euler(45f, 0f, 0f));
                        g.InvalidateSignal();                    // 再强制重算 ⇒ 判据量的是**算式**，不是缓存巧合
                        double s1 = g.SignalStrength;
                        rotEntity.SetRotation(rotSaved);
                        g.InvalidateSignal();                    // 自造状态还回去
                        PflTestKit.Check(PflTestKit.Kind.Invariant, s0 == s1,
                            "①RCS 尺寸口径姿态无关：转 45° 后 S 逐位不变",
                            $"转前 {s0:R} / 转后 {s1:R}");
                    }

                    // [V7] 波束指向三模式（2026-10-01 用户裁定"三者我都要"）：扫掠确定性 / 推进 · 手选 · 跟踪退化 · 波束判据
                    var aimSaved = g.RadarAimMode;
                    var dirSaved = g.RadarManualDir;
                    int trkSaved = g.RadarTrackGroupId;
                    double tNow = TimeScaleManager.GetPhysicsTickSafe();

                    g.RadarAimMode = RadarAim.Scan;
                    var b1 = g.RadarBoresight(tNow);
                    var b2 = g.RadarBoresight(tNow);
                    bool scanPure = b1.x == b2.x && b1.y == b2.y && b1.z == b2.z;      // 同 tick 两次 ⇒ 逐位相同
                    var b3 = g.RadarBoresight(tNow + 2.0);
                    double deltaDeg = System.Math.Acos(System.Math.Max(-1.0, System.Math.Min(1.0, DVector3.Dot(b1, b3))))
                                      * 180.0 / System.Math.PI;
                    PflTestKit.Check(PflTestKit.Kind.Invariant, scanPure,
                        "①扫掠相位是 physicsTick 的纯函数（同 tick 两次调用逐位相同 ⇒ 确定性、不用落盘）");
                    PflTestKit.Check(PflTestKit.Kind.Value, deltaDeg > 5.0 && deltaDeg < 90.0,
                        "①扫掠在推进（2 s 应转过 ≈ 6°/s × 2 = 12°）",
                        $"实测 {deltaDeg:F1}°");

                    g.RadarAimMode = RadarAim.Manual;
                    g.RadarManualDir = new DVector3(0, 1, 0);
                    var bm = g.RadarBoresight(tNow);
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        System.Math.Abs(bm.x) < 1e-9 && System.Math.Abs(bm.y - 1.0) < 1e-9 && System.Math.Abs(bm.z) < 1e-9,
                        "①手选方位生效（指向 == 设的那个方向）",
                        $"指向 ({bm.x:F3},{bm.y:F3},{bm.z:F3})｜设 (0,1,0)");

                    g.RadarAimMode = RadarAim.Track;
                    g.RadarTrackGroupId = g.GroupId + 4242;                            // 不存在的组 ⇒ 应退化为扫掠
                    var bt = g.RadarBoresight(tNow);
                    bool degraded = DVector3.Dot(bt, b1) > 0.9999;
                    PflTestKit.Check(PflTestKit.Kind.Invariant, degraded,
                        "①跟踪拿不到目标 ⇒ 退化为扫掠（**不返回零向量**）",
                        $"指向 ({bt.x:F3},{bt.y:F3},{bt.z:F3}) vs 扫掠 ({b1.x:F3},{b1.y:F3},{b1.z:F3})");
                    g.RadarAimMode = aimSaved;
                    g.RadarManualDir = dirSaved;
                    g.RadarTrackGroupId = trkSaved;                                     // 自造状态必须还回去

                    // 波束判据（纯几何谓词）：锥内且距离内 = true｜超距 = false｜反方向 = false
                    g.RadarAimMode = RadarAim.Manual;
                    var bdir = g.RadarBoresight(tNow);
                    DVector3 selfPos = g.CurrentOrbit.positionSystem.v;
                    double beamRange = rc.beamRangeMeters;
                    bool inBeam = g.RadarBeamContains(selfPos + bdir * (0.5 * beamRange), tNow, out _);
                    bool tooFar = g.RadarBeamContains(selfPos + bdir * (1.5 * beamRange), tNow, out _);
                    bool behind = g.RadarBeamContains(selfPos - bdir * (0.5 * beamRange), tNow, out _);
                    g.RadarAimMode = aimSaved;
                    PflTestKit.Check(PflTestKit.Kind.Invariant, inBeam && !tooFar && !behind,
                        "①波束判据：锥内且距离内 = true｜超距 = false｜反方向 = false",
                        $"内 {inBeam}｜超距 {tooFar}｜反方向 {behind}（range {beamRange / 1000f:F0} km，半锥角 {rc.beamHalfAngleDeg:F0}°）");

                    // 存档前读数（供手动往返对比）
                    string saveBefore = $"[V7·验收·存档前] 编队灵 {tot:F0} m｜建筑投影 {bonus.sensitivityMeters:F0} m｜" +
                                 $"抗压 {g.DetectTech.ecmResist:F2}｜抗骗 {g.DetectTech.eccmResist:F2}｜" +
                                 $"雷达增益 +{rc.activeGainBonus:F2}｜雷达辐射 +{rc.activeRadiation:F2}｜干扰 {g.BuildingJamBonus:F2}｜站 G{g.GroupId}";
                    Log.Message(saveBefore);

                    // 打伤到 50% HP（传感器阵列与干扰机**都打**）
                    b.HitPoints = System.Math.Max(1, b.MaxHitPoints / 2);
                    if (jammerB != null) jammerB.HitPoints = System.Math.Max(1, jammerB.MaxHitPoints / 2);
                    Log.Message($"[V7·验收] 已把建筑打到 {b.HitPoints}/{b.MaxHitPoints} HP，等投影减半…");
                    stage = Stage.AwaitDamaged;
                    stageStep = 0;
                    return;
                }

                case Stage.AwaitDamaged:
                {
                    if (stageStep < 2) return;
                    var g = sg.GetGroupById(stationGroupId);
                    if (g == null) { Finish("站不见了"); return; }
                    var mc = g.GetLinkedMapComp();
                    var b = mc != null ? FindBuilding(mc) : null;
                    if (b == null) { Finish("建筑不见了"); return; }

                    var bonus = g.BuildingTechBonus;
                    float expect = defRange * (float)b.HitPoints / b.MaxHitPoints;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        System.Math.Abs(bonus.sensitivityMeters - expect) <= System.Math.Max(1f, expect * 1e-3f),
                        "②打到半血 ⇒ 建筑级投影按 HP 比例减半",
                        $"投影 {bonus.sensitivityMeters:F0} m vs 期望 {expect:F0} m（{b.HitPoints}/{b.MaxHitPoints} HP）");

                    // [E3] 干扰机的减半断言（与传感器阵列同口径：按 HP 比例）
                    var jamB = FindJammer(mc);
                    if (jamB != null)
                    {
                        float expectJ = defJam * (float)jamB.HitPoints / jamB.MaxHitPoints;
                        PflTestKit.Check(PflTestKit.Kind.Value,
                            System.Math.Abs(g.BuildingJamBonus - expectJ) <= System.Math.Max(1e-3f, expectJ * 1e-3f),
                            "②干扰机打到半血 ⇒ 建筑级干扰效力减半",
                            $"投影 {g.BuildingJamBonus:F2} vs 期望 {expectJ:F2}（{jamB.HitPoints}/{jamB.MaxHitPoints} HP）");
                    }

                    b.Destroy(DestroyMode.Vanish);
                    if (jamB != null) jamB.Destroy(DestroyMode.Vanish);
                    Log.Message("[V7·验收] 建筑已摧毁，等投影归零…");
                    stage = Stage.AwaitDestroyed;
                    stageStep = 0;
                    return;
                }

                case Stage.AwaitDestroyed:
                {
                    if (stageStep < 2) return;
                    var g = sg.GetGroupById(stationGroupId);
                    if (g == null) { Finish("站不见了"); return; }

                    var bonus = g.BuildingTechBonus;
                    float tot = g.DetectTech.sensitivityMeters;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        bonus.sensitivityMeters <= 1e-3f,
                        "③建筑摧毁 ⇒ 建筑级投影归零",
                        $"投影 {bonus.sensitivityMeters:F0} m");
                    PflTestKit.Check(PflTestKit.Kind.Invariant,
                        System.Math.Abs(tot - partRange) <= System.Math.Max(1f, partRange * 1e-4f),
                        "③编队 DetectTech 回到只剩部件级",
                        $"编队 {tot:F0} m vs 部件 {partRange:F0} m");

                    // [E3] 干扰机摧毁 ⇒ 建筑级干扰效力归零
                    PflTestKit.Check(PflTestKit.Kind.Value, g.BuildingJamBonus <= 1e-3f,
                        "③干扰机摧毁 ⇒ 建筑级干扰效力归零",
                        $"投影 {g.BuildingJamBonus:F2}｜群级 JamStrength {g.JamStrength:F2}");

                    Log.Message($"[V7·验收·摧毁后] 编队灵 {tot:F0} m｜建筑投影 {bonus.sensitivityMeters:F0} m｜干扰投影 {g.BuildingJamBonus:F2}（对照：存档前那行应更大）");
                    Finish(null);
                    return;
                }
            }
        }

        private static void Finish(string? failReason)
        {
            if (failReason != null)
                PflTestKit.Check(PflTestKit.Kind.Invariant, false, "验收流程", failReason);
            PflTestKit.CheckUnchecked("③存档往返（手动）", "点「传感器读数」记下两行 → 存档 → 读档 → 再点一次 ⇒ 应逐行一致（S11 那条判据）");
            PflTestKit.End();
            Log.Message("[V7·验收] 手动往返步骤：①「传感器读数」两行 → ②存档 → ③读档 → ④「传感器读数」再点一次 ⇒ 对比。");
            stage = Stage.Idle;
        }
    }
}
