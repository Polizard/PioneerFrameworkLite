using System;
using System.Text;
using MechJebLib.Primitives;
using PioneerFramework.Launch;   // GetSurfaceState（天地映射单一事实源；口径的宿主侧权威）
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **OrbitView / F1 一致性自检探针**（2026-09-18 立；2026-09-28 批4 P4/P5 随极轴统一改写）。
    ///
    /// ## ⚠ 先说变化（否则会把这几个读数看错）
    /// 本探针原先验的是"适配层 +z 极轴 ↔ 游戏世界 +Y 极轴**之间那一次换系**写没写错"。
    /// **批4 已把适配层统一到 +Y、与游戏世界同系**（`GameWorldToAdapter`/`AdapterToGameWorld` 退化为恒等，
    /// 批6 S2 起**降为 `OrbitView` 私有**，跨系只经两个口：进 `FromGameStateVectors` / 出 `GameFrameStateAtUT`）
    /// ⇒ **"换系"这个被测量不存在了**：判据①（往返恒等）现在走**两个口**（`ut` 取构造时刻 ⇒ Δt=0 逐位返回），
    /// 是 smoke 兼"口没写错"的守卫；
    /// 判据②③仍然是真的（它比的是"游戏系里独立叉乘的倾角/法向"与"适配层要素反解出来的值"，
    /// 两者走的仍是完全不同的代码路径，对不上就说明要素提取那条路坏了）。
    ///
    /// ## 为什么当初必须有
    /// 换系写错的症状是"每个数看起来都合理，但全是错的"（《P-1适配层决策》§6 R1）——**不会有东西报警**。
    /// 单元测试证明不了游戏侧传进来的确实是 +Y 系，所以只能喂真状态来验。
    ///
    /// ## 判据仍是"公式无关"的
    ///   ① 往返恒等：**进** `FromGameStateVectors` → **出** `GameFrameStateAtUT(构造时刻)`，与原始 (r,v) 逐位比
    ///   ② 倾角：游戏系里 `acos(ĥ·up)`（`up = DVector3.up`、`ĥ = normalize(r×v)`）必须等于 `ov.inclination`
    ///   ③ 轨道法向：`ov.OrbitNormal()` 必须等于游戏系里直接 `cross(r,v)` 的单位化（两系同轴 ⇒ 不经转换）
    ///
    /// 【边界】只读 + 只打日志，不修改任何状态。
    /// </summary>
    public static class OrbitViewProbe
    {
        // 批5 U4（2026-09-28）：本地那个恒等转换助手已删（类型统一后移植面类型就是 `DVector3`）。

        public static void PrintSnapshot()
        {
            const int EARTH_BODY_ID = 1; // 与 DebugShipSpawner 同值

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Log.Warning("[OrbitView/F1] 无 SpaceGameComp（不在太空场景？）");
                return;
            }

            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null)
            {
                Log.Warning("[OrbitView/F1] 无 CelestialBodyManager");
                return;
            }

            double tick = TimeScaleManager.GetPhysicsTickSafe();

            var sb = new StringBuilder();
            sb.Append("[OrbitView/F1 一致性自检] 适配层与游戏世界**同系（+Y）**，批4 P4 起不再有换系；"
                      + "判据 = 往返恒等（P4 后恒真，仅 smoke）+ 倾角一致 + 法向一致（后两者用 DVector3 在游戏系里独立算）");

            int checkedGroups = 0;
            int failures = 0;
            int equatorialIsZeroInc = 0;
            int checks = 0;
            int uncheckedSamples = 0;
            // ⚠⚠ 批6 S3-3d（2026-09-30）：**"S1 部分 oracle" 已退役，判据随被测量一起消失**。
            //   它原本考的是"`positionBody`/`velocityBody` 这两个**滞后副本**是否等于现推的定义式"。
            //   那两个字段现在**就是现算属性**（`SpaceEntityGroup.positionBody` 的文档）⇒ 两边同一个算式
            //   ⇒ 这条等式**恒真**。按本库纪律（§13.4 P5）**不许留一个恒真的假判据**，故整块删除。
            //   **它的替代是机械的**：`tools/check-frame-crossing.ps1`（闸门第 11 步）—— 副本读不回来，
            //   类型 + 棘轮会当场拦住；而"滞后 ≤ 一步"这个量级（月球档 ≈1.8 km，与交会门同阶）**已随副本一起消失**。

            foreach (SpaceEntityGroup g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive) continue;

                CelestialBodyDef def = celestialMgr.GetBodyById(g.CelestialBodyId);
                if (def == null) continue;

                checkedGroups++;

                DVector3 gp = g.positionBody;
                DVector3 gv = g.velocityBody;

                // —— 游戏系里直接算的量（不经过适配层）——
                DVector3 hGame = DVector3.Cross(gp, gv);
                if (hGame.sqrMagnitude <= 0.0)
                {
                    sb.Append("\n  G").Append(g.GroupId).Append(" 跳过：r×v 为零");
                    continue;
                }

                DVector3 hHat = hGame.normalized;
                double incGame = Math.Acos(Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(hHat, DVector3.up))));

                // —— 经过适配层 ——
                OrbitView ov = OrbitView.FromGameStateVectors(gp, gv, def.gravitationalParameter, tick, DVector3.up, def.radius);

                // ① **往返恒等**（批6 S2 起走两个口：进 = 上面的 `FromGameStateVectors`；出 = `GameFrameStateAtUT`。
                //    `ut` 取构造时刻 ⇒ `Shepperd` 的 Δt=0 逐位返回原状态）
                (DVector3 backR, DVector3 backV) = ov.GameFrameStateAtUT(tick);
                double roundTripErr = Math.Max((backR - gp).magnitude / Math.Max(gp.magnitude, 1.0),
                    (backV - gv).magnitude / Math.Max(gv.magnitude, 1.0));

                // ② 倾角一致
                double incDiff = Math.Abs(ov.inclination - incGame);

                // ③ 法向一致：两系同轴（批4 P4）⇒ 不经转换；保留此比较是为了抓"构造过程引入旋转"这类回归
                DVector3 nBack = ov.OrbitNormal();
                double normalErr = (nBack - hHat).magnitude;

                bool ok = roundTripErr < 1e-12 && incDiff < 1e-9 && normalErr < 1e-9;
                if (!ok) failures++;
                if (incGame < 1e-6) equatorialIsZeroInc++;

                // ⚠ 批6 S3-3d（2026-09-30）：原来这里有一整块"R5 / S1 部分 oracle"判据
                //   （`positionBody`/`velocityBody` 这两个**滞后副本** vs 现推的定义式，判"滞后是否超过一步"）。
                //   **那两个字段现在是现算属性** ⇒ 等式两边同一个算式、恒真 ⇒ 按纪律整块删除
                //   （"不许留一个恒真的假判据"，§13.4 P5）。它的替代 = `tools/check-frame-crossing.ps1`
                //   的类型 + 棘轮：副本读不回来。`gp`/`gv` 仍在上面用于构造适配层视图，本身照旧。

                sb.Append("\n  G").Append(g.GroupId)
                  .Append(g.IsCircularOrbit ? " 圆轨模式" : " 自由飞行")
                  .Append(" |r|=").Append((gp.magnitude / 1000.0).ToString("F0")).Append("km")
                  .Append(" e=").Append(ov.eccentricity.ToString("E2"))
                  .Append(" a=").Append((ov.semiMajorAxis / 1000.0).ToString("F0")).Append("km")
                  .Append(" T=").Append(ov.period.ToString("F0")).Append("s")
                  .Append(" inc=").Append((ov.inclination * 180.0 / Math.PI).ToString("F4")).Append("°")
                  .Append(" LAN=").Append((ov.LAN * 180.0 / Math.PI).ToString("F3")).Append("°")
                  .Append(" ArgP=").Append((ov.argumentOfPeriapsis * 180.0 / Math.PI).ToString("F3")).Append("°")
                  .Append(" ν=").Append((ov.trueAnomaly * 180.0 / Math.PI).ToString("F3")).Append("°")
                  .Append("\n      ①往返 ").Append(roundTripErr.ToString("E1"))
                  .Append("  ②倾角差 ").Append((incDiff * 180.0 / Math.PI).ToString("E2")).Append("°")
                  .Append("（游戏系独立算 ").Append((incGame * 180.0 / Math.PI).ToString("F4")).Append("°）")
                  .Append("  ③法向差 ").Append(normalErr.ToString("E1"))
                  .Append(ok ? "   ⇒ PASS" : "   ⇒ **FAIL**");
            }

            sb.Append("\n  合计 ").Append(checkedGroups).Append(" 组；失败 ").Append(failures)
              .Append(" 组；其中游戏系倾角为 0 的 ").Append(equatorialIsZeroInc).Append(" 组（赤道轨道）");

            // ⚠ 批6 S3-3d：**"S1 部分 oracle" 整条已退役**（被测量 = 滞后副本，已删；见上面的注释）。
            //   这里只留下一句说明，免得读日志的人以为它坏了。
            if (checkedGroups == 0)
            {
                uncheckedSamples++;
                sb.Append("\n  ⚠ 本场景**一个活动编队都没有** ⇒ 上面的 F1 判据**没有样本、未验**（0 样本不折算成 PASS）");
            }
            // ═══════════════════════════════════════════════════════════════════
            // ② 地表几何的**口径**自检（P-1 §1.3/§6-R2 登记的那条待办）
            //
            // 要用 `LAN`/`Longitude`/`Stationary` 那三个 Operation，必须先确认宿主的极轴口径
            // 与我们声明的一致（`equatorialAxis = DVector3.up` = +Y，P4 起适配层同系）。
            // 口径错了的症状是"算出一个看起来合理的错角度"——**不会有东西报警**。
            // ═══════════════════════════════════════════════════════════════════
            sb.Append("\n\n── ② 地表几何口径（宿主 GetSurfaceState.EciToLongLat vs 适配层）──");

            var mgrSurf = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgrSurf == null)
            {
                sb.Append("\n  查不到 CelestialBodyManager ⇒ **这条没验**（不是通过）");
                uncheckedSamples++;
            }
            else
            {
                double omega = GetSurfaceState.RotationOmega(mgrSurf, EARTH_BODY_ID);
                double bodyR = GetSurfaceState.BodyRadius(mgrSurf, EARTH_BODY_ID);
                if (!(omega != 0.0) || !(bodyR > 0.0))
                {
                    sb.Append("\n  自转/半径为 0 ⇒ **这条没验**（不是通过）");
                    uncheckedSamples++;
                }
                else
                {
                    // ── 判据 A：**合成点**，完全不经过适配层 ──
                    //   极轴上的点纬度必须是 ±90°、赤道面内必须是 0°。
                    //   这是**宿主自己的约定**，错一个正负号就会红，与我们的换系无关。
                    double latN = GetSurfaceState.EciToLongLat(new DVector3(0, bodyR, 0), omega, tick).latRad * 180.0 / Math.PI;
                    double latS = GetSurfaceState.EciToLongLat(new DVector3(0, -bodyR, 0), omega, tick).latRad * 180.0 / Math.PI;
                    double latX = GetSurfaceState.EciToLongLat(new DVector3(bodyR, 0, 0), omega, tick).latRad * 180.0 / Math.PI;
                    double latZ = GetSurfaceState.EciToLongLat(new DVector3(0, 0, bodyR), omega, tick).latRad * 180.0 / Math.PI;

                    bool poleOk = Math.Abs(latN - 90.0) < 1e-6 && Math.Abs(latS + 90.0) < 1e-6
                                  && Math.Abs(latX) < 1e-6 && Math.Abs(latZ) < 1e-6;
                    checks++;
                    if (!poleOk) failures++;
                    sb.Append("\n  ").Append(poleOk ? "✓" : "✗ **FAIL**")
                      .Append(" A. 合成点判据：极轴上 = ±90°、赤道面内 = 0°｜实得 +Y→")
                      .Append(latN.ToString("F4")).Append("°  -Y→").Append(latS.ToString("F4"))
                      .Append("°  +X→").Append(latX.ToString("F4")).Append("°  +Z→").Append(latZ.ToString("F4")).Append("°");

                    // ── 判据 B：逐组交叉核对（宿主纬度 vs 适配层 asin(r̂·ŷ)） ──
                    //   验的是"我们声明的赤道轴（`DVector3.up` = +Y，P4 起适配层同系）与宿主实际的极轴是不是同一个"。
                    int surfSamples = 0;
                    double worstLatDiff = 0.0;
                    foreach (SpaceEntityGroup gg in spaceGame.GetAllGroups())
                    {
                        if (!gg.IsActive || gg.CelestialBodyId != EARTH_BODY_ID) continue;

                        int tkSurf = (int)Math.Round(tick);
                        DVector3 rGame = gg.GetBodyPosition(tkSurf).v;   // 批6 S3-3c：现算（唯一换算）
                        if (rGame.sqrMagnitude < 1.0) continue;

                        surfSamples++;
                        double latHost = GetSurfaceState.EciToLongLat(rGame, omega, tick).latRad;
                        // 适配层纬度 = 位置本身的 y 分量（两系同轴 ⇒ 无换系；批6 S2 起跨系只经 OrbitView 的进/出两个口）
                        double latAdapter = Math.Asin(Math.Max(-1.0, Math.Min(1.0, rGame.normalized.y)));
                        double dLat = Math.Abs(latHost - latAdapter) * 180.0 / Math.PI;
                        if (dLat > worstLatDiff) worstLatDiff = dLat;
                    }

                    checks++;
                    if (surfSamples == 0) uncheckedSamples++;
                    else if (worstLatDiff > 1e-6) failures++;
                    sb.Append("\n  ").Append(surfSamples == 0 ? "⊘ **UNCHECKED**（0 个样本）" : (worstLatDiff <= 1e-6 ? "✓" : "✗ **FAIL**"))
                      .Append(" B. 逐组交叉核对：宿主纬度 vs 适配层纬度｜样本 ").Append(surfSamples)
                      .Append(" 组，最大差 ").Append(worstLatDiff.ToString("E2")).Append("°");

                    // ── 判据 C：**顺行判据**（钉住"本 mod 里 inclination=180° 才是顺行"这件事） ──
                    //   本 mod 的约定（`GetSurfaceState` 文件头）：自转轴 = +Y、东 = (−z→+x)、p_eci = Ry(−ωt)·p_fix。
                    //   在 p = +x̂ 处 East(∝ SurfaceVelocity) = ω·(0,0,R) = **+ẑ** ⇒ h = p×v ∥ **−Ŷ**
                    //   ⇒ **顺行赤道轨的 `inclination` 是 180°，不是 0°**（`inclination` 是 h 与 +极轴的夹角）。
                    //   ⚠ 这与 KSP/MJ 的直觉**相反**（那边 0° 就是顺行）。⇒ 没有这条，将来谁看到 180° 会以为坏了。
                    //   做法是**合成的**（不依赖场景里恰好有什么）：同一点、同半径，速度分别取"东向"与"西向"。
                    {
                        // 两系同轴（批4 P4）⇒ 值直传；跨系只经 `OrbitView` 的进/出两个口（批6 S2）。
                        V3 poleDir = DVector3.up;
                        V3 rAt = new V3(bodyR, 0, 0);
                        V3 eastDir = new V3(0.0, 0.0, 1.0).normalized; // p=+x̂ 处的东向（见上面推导）= +ẑ
                        double vCirc = Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / bodyR);

                        OrbitView ovPro = OrbitView.FromStateVectors(rAt, eastDir * vCirc, SpaceConstants.PrimaryGravitationalParameter, tick, poleDir, bodyR);
                        OrbitView ovRetro = OrbitView.FromStateVectors(rAt, eastDir * -vCirc, SpaceConstants.PrimaryGravitationalParameter, tick, poleDir, bodyR);

                        bool proOk = Math.Abs(ovPro.inclination - Math.PI) < 1e-9
                                     && Math.Abs(ovRetro.inclination) < 1e-9;
                        checks++;
                        if (!proOk) failures++;
                        sb.Append("\n  ").Append(proOk ? "✓" : "✗ **FAIL**")
                          .Append(" C. 顺行判据（合成）：东向圆轨的 inclination 必须是 **180°**、西向是 0°｜实得 ")
                          .Append((ovPro.inclination * 180.0 / Math.PI).ToString("F4")).Append("° / ")
                          .Append((ovRetro.inclination * 180.0 / Math.PI).ToString("F4")).Append("°")
                          .Append("\n      ⇒ **本 mod 里 inclination=180° = 顺行**（与 KSP/MJ 的 0° 直觉相反）。")
                          .Append("任何面向玩家的倾角参数都要做 `180° − i` 的意识换算，否则 `OperationInclination` 会给出近逆行的轨道。");
                    }

                    sb.Append("\n  判读：A 红 ⇒ 宿主的极轴不是我们以为的那个（**先别用 LAN/Longitude/Stationary**）；")
                      .Append("A 绿 B 红 ⇒ 适配层的极轴口径与宿主不一致（P4 起两系应完全相同 ⇒ 这是硬矛盾，要查 `OrbitAdapter` 传的 `equatorialAxis`）。")
                      .Append("\n  （经度**不在这里断言**：它要 EciToFixed 的自转相位，属宿主自己的约定；本步只把**极轴**钉死。）");
                }
            }


            // ⚠⚠ **0 样本不许报 PASS**（2026-09-18 实测踩到）：
            // 第一版写的是 `failures == 0 ⇒ PASS`，而"场景里一个活动编队都没有"时
            // `checkedGroups == 0` 会让它**平凡成立** ⇒ 打出 `PASS` 却什么都没验。
            // 这正是本项目在 `tools/test-all.ps1` 里立过的 D4 规则
            // ——"跑不起来的步骤不许报 PASS，那是静默假绿"，我却在自己写的探针里犯了。
            if (uncheckedSamples > 0)
                sb.Append("\n  注意：有 ").Append(uncheckedSamples).Append(" 条判据**没有样本、未验**（不是通过）。");

            if (checkedGroups == 0)
                sb.Append("\n  结论：**UNCHECKED** —— 场景里没有活动编队，这条探针**什么都没验**。"
                          + "\n  （`0 组 / 0 失败` 不是 PASS。请先铺一个：调试页签 `45 放置 · 无Map编队` → "
                          + "「放置：近地轨道圆轨（地表 500km，顺行）」，或 `10 场景` → 「部署空间站」，再点一次。）");
            else if (failures == 0)
                sb.Append("\n  结论：F1 换系 PASS —— 适配层与游戏世界在倾角/法向上逐点一致。");
            else
                sb.Append("\n  结论：**F1 换系 FAIL** —— 不要往下接，先查 `OrbitView` 的进/出两个口"
                          + "（`FromGameStateVectors` / `GameFrameStateAtUT`）。");

            Log.Message(sb.ToString());
        }
    }
}
