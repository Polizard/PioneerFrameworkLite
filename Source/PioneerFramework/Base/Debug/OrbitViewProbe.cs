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
    /// **批4 已把适配层统一到 +Y、与游戏世界同系**（`GameWorldToAdapter` 退化为恒等）
    /// ⇒ **"换系"这个被测量不存在了**：判据①（往返恒等）现在**恒真**，保留它只是 smoke；
    /// 判据②③仍然是真的（它比的是"游戏系里独立叉乘的倾角/法向"与"适配层要素反解出来的值"，
    /// 两者走的仍是完全不同的代码路径，对不上就说明要素提取那条路坏了）。
    ///
    /// ## 为什么当初必须有
    /// 换系写错的症状是"每个数看起来都合理，但全是错的"（《P-1适配层决策》§6 R1）——**不会有东西报警**。
    /// 单元测试证明不了游戏侧传进来的确实是 +Y 系，所以只能喂真状态来验。
    ///
    /// ## 判据仍是"公式无关"的
    ///   ① 往返恒等：`AdapterToGameWorld(ov.r) == g.positionBody`（逐位；**P4 后恒真**）
    ///   ② 倾角：游戏系里 `acos(ĥ·up)`（`up = DVector3.up`、`ĥ = normalize(r×v)`）必须等于 `ov.inclination`
    ///   ③ 轨道法向：把 `ov.OrbitNormal()` 换回游戏系，必须等于游戏系里直接 `cross(r,v)` 的单位化
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
            // ⚠ **S1 部分 oracle 用独立计数器**（2026-09-29 实测教训）：把它计进共享的 `failures` 会让收尾那句
            //   "结论：**F1 换系 FAIL** —— 先查 GameWorldToAdapter" **归错因** ✗（月球组实测：F1 三条全 PASS，
            //   但 oracle 红 ⇒ 那句话把"滞后副本"说成了"换系坏了"）。**判据要判，归因要分。**
            int s1OracleChecks = 0, s1OracleFailures = 0;
            double worstPosBodyDiff = 0.0; // S1 部分 oracle：positionBody vs CurrentOrbit 派生值
            double worstVelBodyDiff = 0.0; // S1 部分 oracle：velocityBody vs CurrentOrbit 派生值
            // ⚠ 判据**不是**死等号（旧版写 `1e-12`，那是错的：见下面每组的量纲化上界）。

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

                // ① 往返恒等
                DVector3 backR = OrbitView.AdapterToGameWorld(ov.r);
                DVector3 backV = OrbitView.AdapterToGameWorld(ov.v);
                double roundTripErr = Math.Max((backR - gp).magnitude / Math.Max(gp.magnitude, 1.0),
                    (backV - gv).magnitude / Math.Max(gv.magnitude, 1.0));

                // ② 倾角一致
                double incDiff = Math.Abs(ov.inclination - incGame);

                // ③ 法向一致（把适配层法向换回游戏系，与直接叉乘比）
                DVector3 nBack = OrbitView.AdapterToGameWorld(ov.OrbitNormal());
                double normalErr = (nBack - hHat).magnitude;

                bool ok = roundTripErr < 1e-12 && incDiff < 1e-9 && normalErr < 1e-9;
                if (!ok) failures++;
                if (incGame < 1e-6) equatorialIsZeroInc++;

                // ⚠ **R5 的实测**（不是换系判据，所以**不计进** `failures`）：
                // `OrbitView` 是从 `g.positionBody` 构造的，而 P-1 §6 R5 要求"构造点只从 `CurrentOrbit` 取真值"。
                // `positionBody` 按称是 `CurrentOrbit.position − 中心天体偏移`（两者都在天体参考系），
                // 但它只在少数几个点被写入 ⇒ **它们会不会不一致，只有测了才知道**。
                // 若这一项明显非零 ⇒ `positionBody` 是滞后的派生副本 ⇒ 构造点要改从 `CurrentOrbit` 推。
                DVector3 fromCurrent = g.CurrentOrbit.position
                                       - g.GetCentralBodyOffset((int)Math.Round(tick));
                double localPosDiff = (fromCurrent - gp).magnitude / Math.Max(gp.magnitude, 1.0);
                if (localPosDiff > worstPosBodyDiff) worstPosBodyDiff = localPosDiff;

                // ⭐ **S1 的"部分 oracle"（2026-09-29）——把"名字声称的系"交给等式去证**：
                //   这两个字段的**写入点就是定义式**（`SpaceGameComp:2664`：
                //   `group.positionBody = group.CurrentOrbit.position − result.SyncBodyPos`，同 tick；
                //   `velocityBody` 同理）⇒ 在步边界上它们必须与"现推的定义式"**一致**。
                //   · 一致 ⇒ 名字（天体局部系）与实现**互为佐证** ✓（这是"改的名字对不对"唯一的机器证据）
                //   · 不一致 ⇒ 是滞后副本 ⇒ 构造点该改从 `CurrentOrbit` 推（P-1 §6 R5）
                //   ⚠ 此前这一项**只打读数、不计进 failures** ⇒ 判据形同不存在；现在**计入**。
                DVector3 velFromCurrent = g.CurrentOrbit.velocity
                                          - g.GetCentralBodyVelocity((int)Math.Round(tick));
                double localVelDiff = (velFromCurrent - gv).magnitude / Math.Max(gv.magnitude, 1.0);
                if (localVelDiff > worstVelBodyDiff) worstVelBodyDiff = localVelDiff;

                // ⚠ **判据从"死等号"改成"量纲化上界"**（2026-09-29 实测定案）：`CurrentOrbit` 是**连续**读出来的
                //   （圆轨组的解析式按**当前** tick 求值），而 `positionBody/velocityBody` **只在物理步末尾写**
                //   （`SpaceGameComp:2664`）⇒ 步与步之间两者**必然**差"最多一步的漂移"。
                //   月球组实测：位置差 1.8 km ÷ 月球轨道速度 1581 m/s ≈ 1.1 s、速度差 0.031 m/s ÷ 月球引力 0.0313 m/s² ≈ 1.0 s
                //   ⇒ **两个数都正好是一步**（Normal 档 dt = 1 s）⇒ 这是**步量化的后果**，不是缺陷。
                //   ⇒ 判据 = **滞后是否超过一步**：超过 ⇒ 真滞后（构造点该改从 CurrentOrbit 推，P-1 §6 R5）。
                // dt 的**唯一权威**是 `TimeScaleManager` 的实例属性（Combat 1/60 · Normal 1 · Fast 8）；
                // 取不到组件时回落到 Normal 档 1.0 并在读数里注明。
                var tsm = Current.Game?.GetComponent<TimeScaleManager>();
                double dtStep = Math.Max(1e-6, tsm != null ? tsm.CalendarSpeedMultiplier : 1.0);
                double vOffMag = g.GetCentralBodyVelocity((int)Math.Round(tick)).magnitude;
                double boundPos = (gv.magnitude + vOffMag) * dtStep;   // 一步内两者位置差最多能变化这么多
                double aLocal = def.gravitationalParameter / Math.Max(gp.sqrMagnitude, 1.0);
                double boundVel = aLocal * dtStep;                     // 速度差同理（中心天体速度变化更小，忽略并注明）
                double absPosDiff = (fromCurrent - gp).magnitude;
                double absVelDiff = (velFromCurrent - gv).magnitude;
                double impliedSecPos = gv.magnitude > 1e-9 ? absPosDiff / gv.magnitude : 0.0;
                s1OracleChecks++;
                if (absPosDiff > boundPos || absVelDiff > boundVel) s1OracleFailures++;

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

            sb.Append("\n  **S1 部分 oracle（独立计数）**：`positionBody`/`velocityBody` vs "
                      + "`CurrentOrbit.… − 中心天体偏移/速度` 的最大**相对**差 = ")
              .Append(worstPosBodyDiff.ToString("E2")).Append(" / ").Append(worstVelBodyDiff.ToString("E2"))
              .Append("\n    判据 = **绝对差是否超过「一步」的最大可能变化**（位置 `(|v船|+|v天体|)·dt`、速度 `a_本地·dt`）。")
              .Append("\n    ⇒ 本项判定：**")
              .Append(s1OracleChecks == 0 ? "UNCHECKED（0 样本）"
                      : (s1OracleFailures == 0 ? "PASS（滞后 ≤ 一步，属步量化副本的约定后果）" : "**FAIL（滞后 > 一步）**"))
              .Append("**（样本 ").Append(s1OracleChecks).Append(" 组 / 超界 ").Append(s1OracleFailures).Append(" 组）")
              .Append("\n    ⚠ **本项与「换系」无关**：F1 三条考适配层，本项考「派生副本相对权威量的时延」"
                      + " ⇒ 两者可以同时一个 PASS 一个 FAIL（月球组实测正是如此）；")
              .Append("\n      实测形态（月球组）：相对差 5.4E-004 / 2.5E-005 ⇒ 位置 ≈1.8 km、速度 ≈0.031 m/s，"
                      + "**正好各是一步的漂移**（÷v ≈1.1 s、÷a ≈1.0 s，Normal 档 dt=1 s）⇒ 判 PASS、但**量级与交会门同阶**（1 km）值得记着。");
            if (checkedGroups == 0)
            {
                uncheckedSamples++;
                sb.Append("\n  ⚠ 本场景**一个活动编队都没有** ⇒ 上面这条判据**没有样本、未验**（0 样本不折算成 PASS）");
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
                        DVector3 rGame = gg.CurrentOrbit.position - gg.GetCentralBodyOffset(tkSurf);
                        if (rGame.sqrMagnitude < 1.0) continue;

                        surfSamples++;
                        double latHost = GetSurfaceState.EciToLongLat(rGame, omega, tick).latRad;
                        V3 rAdapter = OrbitView.GameWorldToAdapter(new V3(rGame.x, rGame.y, rGame.z));
                        double latAdapter = Math.Asin(Math.Max(-1.0, Math.Min(1.0, rAdapter.normalized.y)));
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
                        // 这几处调 `GameWorldToAdapter`（批4 P4 起恒等）只为保持"唯一咽喉"的形状；值就是游戏系本身。
                        V3 poleDir = OrbitView.GameWorldToAdapter(new V3(DVector3.up.x, DVector3.up.y, DVector3.up.z));
                        V3 rAt = OrbitView.GameWorldToAdapter(new V3(bodyR, 0, 0));
                        V3 eastDir = OrbitView.GameWorldToAdapter(
                            new V3(-0.0, 0.0, 1.0)).normalized; // p=+x̂ 处的东向（见上面推导）= +ẑ
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
                sb.Append("\n  结论：**F1 换系 FAIL** —— 不要往下接，先查 GameWorldToAdapter。");

            Log.Message(sb.ToString());
        }
    }
}
