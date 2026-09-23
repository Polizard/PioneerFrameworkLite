using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 星座组网游戏内自检（Debug 菜单「PFL 测试」页签 → 「星座自检（API 全链）」）：
    /// 组件编排（读 groups / 锚点创建 / 多归属 / 收尸 / 释放）无法脱离 RimWorld 运行，
    /// 纯测试只覆盖 <see cref="ConstellationCore"/>；这里把整条 API 链在真游戏里跑一遍并打印结论块。
    ///
    /// **不依赖场景**：圆轨 / 非圆轨两类成员样本由本类**自造探针 group**（无实体、非碰撞、非移动），
    /// 唯一的外部依赖是"世界里存在一个存活据点"（锚点一段 / 正常存档必有）。
    /// 全程只动探针与合成锚点，**不碰任何真飞船**；结束后清理自检星座 → 锚点随之释放，存档回到原状。
    /// </summary>
    public static class ConstellationSelfTest
    {
        private const string TestNameA = "自检星座A";
        private const string TestNameB = "自检星座B";
        private const int EARTH_BODY_ID = 1;

        private static int passed;
        private static int failed;

        /// <summary>最近一次的通过 / 失败项数（供统一入口收进结论）。</summary>
        public static int LastPassed => passed;
        public static int LastFailed => failed;
        private static readonly StringBuilder log = new StringBuilder();

        public static void Run()
        {
            var comp = Current.Game?.GetComponent<ConstellationGameComp>();
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (comp == null || sg == null)
            {
                Log.Error("[星座自检] 组件缺失（ConstellationGameComp / SpaceGameComp 未注册）");
                return;
            }

            passed = 0;
            failed = 0;
            log.Length = 0;

            log.AppendLine("[星座自检] 开始：API 全链（CRUD / 准入 / 圆轨成员 / 锚点 / 多归属 / 收尸 / 释放）");

            Cleanup(comp); // 上次自检残留（中途异常退出时）先清干净，保证可重复跑

            // ═══ ① CRUD ═══
            int a = comp.CreateConstellation(TestNameA);
            int b = comp.CreateConstellation(TestNameB);
            Check(a > 0 && b > 0 && a != b, string.Format("创建星座 C{0}/C{1}", a, b));
            Check(comp.GetConstellation(a) != null && comp.FindByName(TestNameA)?.ConstellationId == a, "按 id / 按名查找");
            Check(comp.RenameConstellation(a, TestNameA + "（改名）") && comp.FindByName(TestNameA + "（改名）") != null, "重命名");
            comp.RenameConstellation(a, TestNameA);
            Check(comp.RenameConstellation(a, "") == false, "拒绝：空名重命名");

            // ═══ ② 准入拒绝（id 不存在 / 资格不符）═══
            Check(comp.TryAddMember(999999, 1) == ConstellationAddResult.ConstellationNotFound,
                "拒绝：星座 id 不存在 → ConstellationNotFound");
            Check(comp.TryAddMember(a, 999999) == ConstellationAddResult.GroupMissing,
                "拒绝：groupId 不存在 → GroupMissing");

            int freeId = CreateProbeGroup(sg, circular: false);
            if (freeId > 0)
            {
                Check(comp.TryAddMember(a, freeId) == ConstellationAddResult.NotAdmissible,
                    string.Format("拒绝：非圆轨非锚点 G{0}（深空自由飞行体语义）→ NotAdmissible", freeId));
                Check(sg.GetGroupById(freeId).GetEntityIds().Count == 0, "探针成立：无实体 group 也可入 API 判定");
                sg.DestroyGroup(freeId);
            }
            else
            {
                log.AppendLine("  SKIP  非圆轨探针：地球天体缺失（无法造真圆轨半径样本）");
            }

            // ═══ ③ 圆轨成员：入编 / 同星座去重 / 多归属 / 实时查询 / 收尸 ═══
            int orbitId = CreateProbeGroup(sg, circular: true);
            if (orbitId > 0)
            {
                Check(comp.AddMember(a, orbitId), string.Format("圆轨入编 G{0}", orbitId));
                Check(comp.TryAddMember(a, orbitId) == ConstellationAddResult.AlreadyMember,
                    "拒绝：同星座内重复 → AlreadyMember");
                Check(comp.AddMember(b, orbitId), string.Format("多归属：同一圆轨组同时在 C{0} 与 C{1}", a, b));
                Check(comp.GetConstellationIdsOfGroup(orbitId).Count == 2, "反查索引：该组归属 2 个星座");

                var state = comp.GetMembersWithState(a).ToList();
                Check(state.Count == comp.GetMembers(a).Count && state.All(p => p.group != null),
                    string.Format("实时查询：C{0} 返回 {1} 条成员状态", a, state.Count));

                sg.DestroyGroup(orbitId);
                Check(sg.GetGroupById(orbitId) == null, string.Format("收尸：圆轨成员 G{0} 已销毁", orbitId));
                Check(comp.GetConstellationIdsOfGroup(orbitId).Count == 0, "收尸：反查索引已清空");
                Check(!comp.GetMembers(a).Any(m => m.groupId == orbitId) && !comp.GetMembers(b).Any(m => m.groupId == orbitId),
                    "收尸：C" + a + " 与 C" + b + " 的成员表均已摘除");
            }
            else
            {
                log.AppendLine("  SKIP  圆轨探针：地球天体缺失，本段全部跳过");
            }

            // ═══ ④ 锚点：懒创建 / 防重复 / 位置上表面 / 不入空间索引 / 多归属 ═══
            var settlement = Find.WorldObjects.Settlements.FirstOrDefault(s => s != null && !s.Destroyed && s.Faction == Faction.OfPlayer)
                             ?? Find.WorldObjects.Settlements.FirstOrDefault(s => s != null && !s.Destroyed);
            int anchorId = 0;
            if (settlement != null)
            {
                Check(comp.AddSettlementMember(a, settlement.ID), string.Format("据点「{0}」(wo{1}) 入编 C{2}", settlement.Label, settlement.ID, a));
                anchorId = comp.GetMembers(a).FirstOrDefault(m => sg.GetGroupById(m.groupId)?.IsSurfaceAnchor == true).groupId;
                var anchor = sg.GetGroupById(anchorId);
                Check(anchor != null && anchor.IsSurfaceAnchor, string.Format("锚点 group G{0} 已创建", anchorId));
                Check(anchor != null && !anchor.IsActive && !anchor.IsMovable && !anchor.IsCollidable,
                    "锚点约束：IsActive/IsMovable/IsCollidable 全 false");
                Check(anchor != null && anchor.anchorWorldObjectId == settlement.ID, "锚点回指据点 ID");

                Check(comp.EnsureAnchorGroup(settlement) == anchorId, "防重复创建：再次 EnsureAnchorGroup 返回同一 group");
                Check(comp.AddSettlementMember(b, settlement.ID), "多归属：同一据点入 C" + b);
                Check(comp.GetConstellationIdsOfGroup(anchorId).Count == 2, "反查索引：锚点归属 2 个星座");

                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                var body = anchor != null ? celestialMgr?.GetBodyById(anchor.CelestialBodyId) : null;
                if (anchor != null && body != null)
                {
                    // **独立口径**校验：反推用 SpaceEntityGroup.GetCentralBodyOffset（碰撞检查 / 圆轨解析 /
                    // N 体积分器共用的群组系规则），不是重算一遍 AnchorWorldPosition 的输入。
                    // （坐标系约定的唯一出处：CelestialBodyManager.GetBodyWorldPosition 的文档块）
                    // 初版检查与实现同错（两边都用"递归到恒星"的 GetBodyWorldPosition）→ 12 万 km 的
                    // 帧错误被判成 PASS；换成独立口径后这类错误必 FAIL。
                    DVector3 frameOffset = anchor.GetCentralBodyOffset(TimeScaleManager.GetPhysicsTickSafe());
                    double distMeters = (anchor.CurrentOrbit.position - frameOffset).magnitude;
                    Check(System.Math.Abs(distMeters - body.radius) < 1.0,
                        string.Format("锚点在群组坐标系：距中心天体 {0:F0} km == R_body（{1:F0} km）",
                            distMeters / 1000.0, body.radius / 1000.0));

                    // 根行星（地球）在群组系就是原点 → |pos| 必须等于 R_body，与天体绝对位置无关的硬校验
                    if (celestialMgr != null && celestialMgr.GetRootPlanetId(anchor.CelestialBodyId) == anchor.CelestialBodyId)
                    {
                        Check(System.Math.Abs(anchor.CurrentOrbit.position.magnitude - body.radius) < 1.0,
                            string.Format("根行星锚点在系原点：|pos|={0:F0} km == R_body（未串入天体公转偏移）",
                                anchor.CurrentOrbit.position.magnitude / 1000.0));
                    }
                }
                else
                {
                    log.AppendLine("  SKIP  锚点位置校验：天体数据缺失");
                }

                if (anchor != null)
                {
                    var nearby = sg.GetGroupsInRange(anchor.CurrentOrbit.position, 1_000_000.0, -1, g => g.GroupId == anchorId);
                    Check(!nearby.Any(), "空间索引隔离：粗网格范围查询取不到锚点");
                }

                // ═══ ⑤ 锚点收尸：DestroyGroup → 全星座摘除 ═══
                sg.DestroyGroup(anchorId);
                Check(sg.GetGroupById(anchorId) == null, string.Format("收尸：锚点 G{0} 已销毁", anchorId));
                Check(comp.GetConstellationIdsOfGroup(anchorId).Count == 0, "收尸：锚点反查索引已清空");
                Check(!comp.GetMembers(a).Any(m => m.groupId == anchorId) && !comp.GetMembers(b).Any(m => m.groupId == anchorId),
                    "收尸：两星座的锚点成员均已摘除");

                // ═══ ⑥ 释放：删星座 → 无引用锚点走收尸链路 ═══
                int anchor2 = comp.EnsureAnchorGroup(settlement);
                comp.AddSettlementMember(a, settlement.ID);
                Check(comp.GetConstellationIdsOfGroup(anchor2).Count == 1, "重建锚点并入编（为释放测试）");
                comp.DeleteConstellation(a);
                Check(sg.GetGroupById(anchor2) == null, string.Format("释放：删星座后无引用锚点 G{0} 已回收", anchor2));
                Check(comp.GetConstellation(a) == null, "删除：C" + a + " 已不存在");
            }
            else
            {
                log.AppendLine("  SKIP  锚点一段：世界里没有存活据点");
            }

            Cleanup(comp);

            log.AppendLine(string.Format("[星座自检] 结束：{0} 通过 / {1} 失败", passed, failed));
            log.AppendLine(string.Format("[星座自检] 结论：{0}", failed == 0 ? "全部通过" : "存在失败项，见上方 FAIL"));
            Log.Message(log.ToString());
            if (failed == 0)
                Messages.Message("星座自检：全部通过（详见日志）", MessageTypeDefOf.SilentInput, false);
            else
                Messages.Message(string.Format("星座自检：{0} 项失败（详见日志）", failed), MessageTypeDefOf.NegativeEvent, false);
        }

        /// <summary>
        /// 造一枚探针 group（无实体 / 非碰撞 / 非移动——世界可见性与物理风险都归零）：
        ///   circular=true  → 500km 圆轨（`EnterCircularOrbit`，与「部署空间站」同高度口径）
        ///   circular=false → 同样是活跃 group 但不在圆轨上（深空自由飞行体语义，用于验证准入拒绝）
        /// 返回 groupId；地球天体缺失时返回 -1（调用方记 SKIP）。
        /// </summary>
        private static int CreateProbeGroup(SpaceGameComp sg, bool circular)
        {
            var earth = Current.Game?.GetComponent<CelestialBodyManager>()?.GetBodyById(EARTH_BODY_ID);
            if (earth == null) return -1;

            int groupId = sg.AllocGroupId();
            var group = new SpaceEntityGroup(groupId, Faction.OfPlayer?.loadID ?? 0)
            {
                IsActive = true,     // 活跃：证明"活跃"本身不构成准入资格
                IsMovable = false,   // 不参与物理积分（探针全程不移动）
                IsCollidable = false,
                CelestialBodyId = circular ? EARTH_BODY_ID : -1
            };
            double radius = earth.radius + 500_000.0;
            group.CurrentOrbit = new OrbitState
            {
                position = new DVector3(radius, 0.0, 0.0),
                velocity = DVector3.zero,
                rotation = Quaternion.identity
            };
            sg.RegisterGroup(group);
            if (circular)
                group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
            return groupId;
        }

        /// <summary>删除自检星座并释放其锚点（可重复跑；不触碰其它星座）。</summary>
        private static void Cleanup(ConstellationGameComp comp)
        {
            var doomed = new List<int>();
            foreach (var c in comp.AllConstellations)
                if (c.Name == TestNameA || c.Name == TestNameB || c.Name == TestNameA + "（改名）")
                    doomed.Add(c.ConstellationId);
            for (int i = 0; i < doomed.Count; i++)
                comp.DeleteConstellation(doomed[i]);
        }

        private static void Check(bool ok, string label)
        {
            if (ok) passed++;
            else failed++;
            log.AppendLine(string.Format("  {0}  {1}", ok ? "PASS" : "FAIL", label));
        }
    }
}
