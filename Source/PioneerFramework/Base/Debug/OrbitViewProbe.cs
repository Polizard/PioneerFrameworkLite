using System;
using System.Text;
using MechJebLib.Primitives;
using PioneerFramework.Launch;   // GetSurfaceState（天地映射单一事实源；口径的宿主侧权威）
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **OrbitView / F1 换系自检探针**（2026-09-18，适配层接游戏时的验收件）。
    ///
    /// ## 它为什么必须存在
    /// 适配层参考系是 **+z 极轴**（跟上游 `Astro` 走），而**游戏世界的极轴是 +Y**（`DVector3.up`、
    /// `EnterCircularOrbit` 的退化法向、`OrbitMath` 的默认参考法向）。
    /// 两者之间**只允许一次换系**，就是 `OrbitView.GameWorldToAdapter`（`x→x, y→z, z→-y`）。
    ///
    /// **换系写反/写错的症状是"每个数看起来都合理，但全是错的"** —— 这正是《P-1适配层决策》§6 R1
    /// 描述的那一类：**不会有任何东西报警**。单元测试证明不了游戏侧传进来的确实是 +Y 系，
    /// 所以只能喂真状态来验。
    ///
    /// ## 判据是"公式无关"的
    /// 探针**不**用适配层的任何公式去验适配层。它用 `DVector3` **直接**在游戏系里算三个量，
    /// 再要求适配层给出同一个答案：
    ///   ① **往返恒等**：`AdapterToGameWorld(ov.r) == g.localPosition`（逐位）
    ///   ② **倾角**：游戏系里 `acos(ĥ·up)`（`up = DVector3.up`、`ĥ = normalize(r×v)`）
    ///      必须等于 `ov.inclination`
    ///   ③ **轨道法向**：把 `ov.OrbitNormal()` 换回游戏系，必须等于游戏系里直接 `cross(r,v)` 的单位化
    /// ②与③走的是**完全不同的代码路径**（叉乘 + 反余弦 vs 要素反解）⇒ 它们对上了，
    /// 换系就是对的；对不上，就是换系错了。
    ///
    /// 【边界】只读 + 只打日志，不修改任何状态。
    /// </summary>
    public static class OrbitViewProbe
    {
        private static DVector3 ToD(V3 v) => new DVector3(v.x, v.y, v.z);

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
            sb.Append("[OrbitView/F1 换系自检] 适配层 +z 极轴 ↔ 游戏世界 +Y 极轴；"
                      + "判据 = 往返恒等 + 倾角一致 + 法向一致（这三者都用 DVector3 在游戏系里独立算）");

            int checkedGroups = 0;
            int failures = 0;
            int equatorialIsZeroInc = 0;
            int checks = 0;
            int uncheckedSamples = 0;
            double worstLocalPosDiff = 0.0; // R5 实测：localPosition 与 CurrentOrbit 派生值的一致性

            foreach (SpaceEntityGroup g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive) continue;

                CelestialBodyDef def = celestialMgr.GetBodyById(g.CelestialBodyId);
                if (def == null) continue;

                checkedGroups++;

                DVector3 gp = g.localPosition;
                DVector3 gv = g.localVelocity;

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

                // ① 往返恒等
                DVector3 backR = ToD(OrbitView.AdapterToGameWorld(ov.r));
                DVector3 backV = ToD(OrbitView.AdapterToGameWorld(ov.v));
                double roundTripErr = Math.Max((backR - gp).magnitude / Math.Max(gp.magnitude, 1.0),
                    (backV - gv).magnitude / Math.Max(gv.magnitude, 1.0));

                // ② 倾角一致
                double incDiff = Math.Abs(ov.inclination - incGame);

                // ③ 法向一致（把适配层法向换回游戏系，与直接叉乘比）
                DVector3 nBack = ToD(OrbitView.AdapterToGameWorld(ov.OrbitNormal()));
                double normalErr = (nBack - hHat).magnitude;

                bool ok = roundTripErr < 1e-12 && incDiff < 1e-9 && normalErr < 1e-9;
                if (!ok) failures++;
                if (incGame < 1e-6) equatorialIsZeroInc++;

                // ⚠ **R5 的实测**（不是换系判据，所以**不计进** `failures`）：
                // `OrbitView` 是从 `g.localPosition` 构造的，而 P-1 §6 R5 要求"构造点只从 `CurrentOrbit` 取真值"。
                // `localPosition` 按称是 `CurrentOrbit.position − 中心天体偏移`（两者都在天体参考系），
                // 但它只在少数几个点被写入 ⇒ **它们会不会不一致，只有测了才知道**。
                // 若这一项明显非零 ⇒ `localPosition` 是滞后的派生副本 ⇒ 构造点要改从 `CurrentOrbit` 推。
                DVector3 fromCurrent = g.CurrentOrbit.position
                                       - g.GetCentralBodyOffset((int)Math.Round(tick));
                double localPosDiff = (fromCurrent - gp).magnitude / Math.Max(gp.magnitude, 1.0);
                if (localPosDiff > worstLocalPosDiff) worstLocalPosDiff = localPosDiff;

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
                  .Append(ok ? "   ⇒ PASS" : "   ⇒ **FAIL**")
                  .Append("  R5差 ").Append(localPosDiff.ToString("E1"));
            }

            sb.Append("\n  合计 ").Append(checkedGroups).Append(" 组；失败 ").Append(failures)
              .Append(" 组；其中游戏系倾角为 0 的 ").Append(equatorialIsZeroInc).Append(" 组（赤道轨道）");

            sb.Append("\n  R5 一致性：`localPosition` vs `CurrentOrbit.position − 中心天体偏移` 的最大相对差 = ")
              .Append(worstLocalPosDiff.ToString("E2"))
              .Append(worstLocalPosDiff < 1e-12
                  ? "（一致 ⇒ 从 localPosition 构造合规）"
                  : "（**不一致** ⇒ 构造点应改从 CurrentOrbit 推，见 P-1 §6 R5）");
            // ═══════════════════════════════════════════════════════════════════
            // ② 地表几何的**口径**自检（P-1 §1.3/§6-R2 登记的那条待办）
            //
            // 要用 `LAN`/`Longitude`/`Stationary` 那三个 Operation，必须先确认宿主的极轴口径
            // 与我们声明的一致（`equatorialAxis = DVector3.up` ⇒ 适配层 +z）。
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

                    // ── 判据 B：逐组交叉核对（宿主纬度 vs 适配层 asin(r̂·ẑ)） ──
                    //   验的是"我们声明的赤道轴（DVector3.up ⇒ 适配层 +z）与宿主实际的极轴是不是同一个"。
                    int surfSamples = 0;
                    double worstLatDiff = 0.0;
                    foreach (SpaceEntityGroup gg in spaceGame.GetAllGroups())
                    {
                        if (!gg.IsActive || gg.CelestialBodyId != EARTH_BODY_ID) continue;

                        int tkSurf = (int)Math.Round(tick);
                        DVector3 rGame = gg.CurrentOrbit.position - gg.GetCentralBodyOffset(tkSurf);
                        if (rGame.sqrMagnitude < 1.0) continue;

                        surfSamples++;
                        double latHost = GetSurfaceState.EciToLongLat(rGame, omega, tick).latRad;
                        V3 rAdapter = OrbitView.GameWorldToAdapter(new V3(rGame.x, rGame.y, rGame.z));
                        double latAdapter = Math.Asin(Math.Max(-1.0, Math.Min(1.0, rAdapter.normalized.z)));
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
                        V3 adapterPole = OrbitView.GameWorldToAdapter(new V3(DVector3.up.x, DVector3.up.y, DVector3.up.z));
                        V3 rAt = OrbitView.GameWorldToAdapter(new V3(bodyR, 0, 0));
                        V3 eastDir = OrbitView.GameWorldToAdapter(
                            new V3(-0.0, 0.0, 1.0)).normalized; // p=+x̂ 处的东向（见上面推导）= +ẑ
                        double vCirc = Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / bodyR);

                        OrbitView ovPro = OrbitView.FromStateVectors(rAt, eastDir * vCirc, SpaceConstants.PrimaryGravitationalParameter, tick, adapterPole, bodyR);
                        OrbitView ovRetro = OrbitView.FromStateVectors(rAt, eastDir * -vCirc, SpaceConstants.PrimaryGravitationalParameter, tick, adapterPole, bodyR);

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
                      .Append("A 绿 B 红 ⇒ `equatorialAxis` 的来源（`DVector3.up`）与宿主不一致，要改 `OrbitAdapter`。")
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
                sb.Append("\n  结论：**F1 换系 FAIL** —— 不要往下接，先查 GameWorldToAdapter。");

            Log.Message(sb.ToString());
        }
    }
}
