using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;
using PioneerFramework.Launch;   // GetSurfaceState（天体→PlanetLayer）
using RimWorld.Planet;            // PlanetLayer（自转自检的参数类型）
using RimWorld;
using PioneerFramework.Combat;
using PioneerFramework.ShipSkin;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView : MainTabWindow
    {
        /// <summary>每帧更新方向光，使其从恒星方向照射到行星</summary>
        /// <summary>太阳方向上一条读数的时间戳（限频日志用）。</summary>
        private double lastSunLogTick = double.MinValue;

        /// <summary>蒙皮在"无 Map 编队"路径上的首次命中读数（只打一次）。</summary>
        private static bool loggedPartSkin;

        /// <summary>把当前太阳口径写进某材质（`renderMat` 本体与它的克隆们共用；2026-10-14）。</summary>
        private static void ApplySunUniforms(Material m, Vector3 toSun, Color sunColor)
        {
            m.SetVector("_SunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            m.SetVector("_SunColor", new Vector4(sunColor.r, sunColor.g, sunColor.b, 1f));
        }

        private void UpdateLighting(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            if (sunLight == null) return;
            var star = mgr.GetStar();
            if (star == null) return;
            DVector3 starPosition = mgr.GetBodyPositionAbs(star.bodyId, tick);
            DVector3 cameraPosition = ViewToAbs(GetCameraViewPos(), origin);
            Vector3 toSun = PFViewCelestialLighting.Direction(starPosition, cameraPosition, viewFrameRot);
            // 方向光的 `forward` = 光**传播**方向 = 太阳→天体 = −toSun
            // 2026-10-13（P2-a）：1.7 的 UnityStubs 里 `Transform.forward` 只读（无 set_forward）
            // ⇒ 改用 rotation=LookRotation（与"设 forward"同义；上向量取 Vector3.up 即原默认）。
            sunLight.transform.rotation = Quaternion.LookRotation(-toSun, Vector3.up);

            // 2026-10-13（P2-c）：同一个太阳方向喂给 PFLit（视图系单位向量，与网格法线同系）；
            // 1.6 的 Standard 忽略未知属性 ⇒ 两版本共用这一行。
            // 2026-10-14（亮度）：1.6 的 Standard 吃方向光的 intensity（`sunLight.intensity = 5f`，见 EnsureResources）；
            // PFLit 不走引擎光照 ⇒ 把"颜色 × 强度"显式折进 `_SunColor`（恢复 1.6 的亮面口径）。
            if (renderMat != null)
            {
                // ⚠ 2026-10-14：不再乘 `intensity`——`intensity = 5f` 是 1.6 Standard 的旧口径；
                //   PFLit 已改为**照抄原版世界地表**的昼夜色模型（白天 = 满亮度）⇒ `_SunColor` 只传灯色。
                Color sc = sunLight.color;
                ApplySunUniforms(renderMat, toSun, sc);

                // 天体材质在各自绘制入口按真实中心更新，避免月球复用地球的光照方向。
                // 2026-10-19 蒙皮：独立材质同样收不到 renderMat 的每帧刷新 —— 同一个坑，同一个修法
                // （另外把相机位置一起喂进去，rim 要用；全在视图系）。
                ShipSkinAssets.ApplyLighting(toSun, sc, GetCameraViewPos());
            }

            if (tick - lastSunLogTick > 300.0)
            {
                lastSunLogTick = tick;
                Log.Message($"[PFView·光照] 相机到恒星的视图系方向 ({toSun.x:F2},{toSun.y:F2},{toSun.z:F2})｜参考系={(viewFrameIsFixed ? "固连" : "惯性")}");
            }
        }

        // ════════════════════════════════════════════════════
        //  场景渲染
        // ════════════════════════════════════════════════════

        private int lastSceneRenderFrame = int.MinValue;
        // 点击发生在下次重绘前，拾取使用当前画面实际提交的位置与缩放。
        private readonly List<(int BodyId, Vector3 Center, float Radius)> presentedBodySpheres = new();

        private void RenderScene(CelestialBodyManager mgr)
        {
            if (renderCamera == null || renderTexture == null) return;
            // 1.7 先派发输入，再推进模拟，最后派发布局/重绘；DrawMesh 队列会在帧内累计。
            // 输入期绘制会把旧编队原点的场景留在队列中，与重绘期的新场景混在一起。
            // Camera.Render 按提交顺序绘制：后一次星空可能落在旧行星之上，产生局部漏星。
            if (Event.current == null || Event.current.type != EventType.Repaint
                || lastSceneRenderFrame == RealTime.frameCount) return;
            lastSceneRenderFrame = RealTime.frameCount;

            // 实时逐格表面：与绘制同帧推进（4 ms 预算），构建期不阻塞输入。
            PumpTileSurfaceBuilds();

            // ⚠ 2026-10-11（"别的航天器整体位置在抖"的治本）：这里用**帧级连续时钟**（不是阶梯的物理 tick）。
            //   物理 tick 由整数 `TicksGame` 推出 ⇒ 渲染帧率高于 tick 率时，位置一格一格跳；
            //   两台站不同轨道时相对速度可达 km/s（每 tick 相对位移上千米）⇒ 肉眼就是"抖"。
            //   连续时钟 + 解析圆轨（本就支持子 tick）/ 积分轨按速度外推 ⇒ 位置连续。
            double tick = RenderTick();
            cachedRenderTick = (int)Math.Round(tick); // int 缓存仅作 OnPostRender 轨道线采样/键，天体位置一律用 double tick
            DVector3 origin = GetRenderOrigin(tick);

            UpdateViewFrame(mgr, tick, origin);   // 参考系（惯性/固连）——**必须在一切取位/取向之前**
            // 相机防穿入/近裁与天体绘制共享这一份时钟、原点和参考系。
            UpdateCamera(mgr, tick, origin);
            UpdateLighting(mgr, tick, origin);
            UpdateAtmosphereSky(mgr, tick, origin);   // 大气视觉：天空盒按相机高度变蓝（并给雾层/壳准备读数）
            DrawStarfield();                          // 倒数第一层：星空盒（复用世界视图同一份星表；大气里 1−天空α 淡出）
            DrawAllObjects(origin, mgr, tick);
            // S3-b：先渲染星体半分辨率离屏（StarLayer），再渲染主 RT（composite 采样星体 RT）。
            // 两次 Camera.Render 的契约由 PFViewOutlineIntegrationTests 显式看守（用户 2026-11-20 批准）。
            RenderStellarOffscreen();

            renderCamera.Render();
            RecordStellarPerfFrame();   // P0 探针：仅调试窗打开时记录（关窗零开销）
        }

        /// <summary>帧级连续渲染时钟（`TimeScaleManager.GetRenderTick()` 的静态便捷口；无 Game ⇒ 退回物理 tick）。</summary>
        private static double RenderTick()
        {
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            return tsm != null ? tsm.GetRenderTick() : TimeScaleManager.GetPhysicsTickSafe();
        }

        private void DrawAllObjects(DVector3 origin, CelestialBodyManager mgr, double tick)
        {
            stellarHaloDraws.Clear();
            BeginImpactVisibility();
            BeginEnginePlumeFrame(mgr);   // 2026-11-08：本帧引擎喷焰采集（只收集，不提交）
            DrawCelestialBodies(mgr, tick, origin);
            DrawSystemGroups(mgr, tick, origin);
            DrawMegastructures(mgr, origin);
            DrawProjectiles(origin, tick);
            DrawStellarHalos();
            DrawEnginePlumes(tick);       // 加性 / queue 3400：一次批提交，必须在 Render() 之前
        }

        /// <summary>旧消费者兼容分支的显示集合；实例框架直接使用当前呈现批次，不经过此缓存。</summary>
        private HashSet<int> GetLocalSystemBodyIds(CelestialBodyManager mgr)
        {
            // 2026-10-18 用户报「选中编队时不会显示出其他星体」：
            // 原来只画「恒星 + 焦点所在根行星 + 其卫星」，聚焦到别的天体（恒星/深空/卫星）时其余天体会被过滤掉。
            // 兼容分支保留全画行为，实例框架的动态目录由呈现批次处理。
            int shipBodyId = ResolveFocusBodyId();
            if (shipBodyId < 0) shipBodyId = currentSystemBodyId;
            if (localSystemBodyIdsCacheKey == shipBodyId) return localSystemBodyIdsCache;
            localSystemBodyIdsCacheKey = shipBodyId;

            var ids = localSystemBodyIdsCache;
            ids.Clear();
            foreach (var body in mgr.GetAllBodies())
                ids.Add(body.bodyId);
            return ids;
        }

        /// <summary>第 12 条：当前星系 + 其卫星的 id 集（记忆化，免每帧重建；调用方仅 Contains/遍历）。</summary>
        private HashSet<int> GetCurrentSystemBodyIds(CelestialBodyManager mgr)
        {
            if (currentSystemBodyIdsCacheKey == currentSystemBodyId) return currentSystemBodyIdsCache;
            currentSystemBodyIdsCacheKey = currentSystemBodyId;

            var ids = currentSystemBodyIdsCache;
            ids.Clear();
            ids.Add(currentSystemBodyId);
            var bodyDef = mgr.GetBodyById(currentSystemBodyId);
            if (bodyDef != null)
                foreach (var m in bodyDef.moonBodies)
                    ids.Add(m.bodyId);
            return ids;
        }


        private void DrawCelestialBodies(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            DrawCelestialBodiesCore(mgr, tick, origin);
        }

        private void DrawCelestialBodiesCore(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            presentedBodySpheres.Clear();
            var renderBodies = GetCelestialRenderBodies(mgr, tick);
            var relevantIds = celestialRenderRuntime == null ? GetLocalSystemBodyIds(mgr) : null;
            Vector3 camPos = GetCameraViewPos();
            Color sunColor = sunLight != null ? sunLight.color : Color.white;

            foreach (var body in renderBodies)
            {
                if (relevantIds != null && !relevantIds.Contains(body.bodyId)) continue;

                bool isStar = body.bodyType == CelestialBodyType.Star;
                DVector3 wp = GetCelestialRenderPosition(mgr, body.bodyId, tick);
                Vector3 vp = AbsToView(wp, origin);
                Vector3 toSun = PFViewCelestialLighting.Direction(GetCelestialRenderLight(mgr, body.bodyId, tick), wp, viewFrameRot);

                // 所有天体共享星历位置、距离缩放、球网格与深度遮挡。
                float bodyR = (float)(body.radius * MetersToView);

                Color c = CelestialBodyManager.GetBodyColor(body);
                var configuredColor = GetCelestialRenderVisual(body.bodyId, Celestial.CelestialVisualKind.Surface)?.Profile;
                if (configuredColor != null) c = new Color((float)configuredColor.Red, (float)configuredColor.Green, (float)configuredColor.Blue, 1f);

                // ⚠ **天体要按自转相位转**（2026-10-10 用户实测："世界视图里 Group 在陆地上方，PFView 一侧却不是"）：
                //    贴图内容是**天体固连系**的地形，而飞船位置是 **ECI/绝对系**的 ⇒ 画行星必须把贴图转到 ECI 才对齐。
                //    原来这里是 `Quaternion.identity`（"天体不自转"）—— 纯色球时代无所谓，有贴图之后就是错的
                //    （差一个 ωt，地球 6°/分钟）。相位出自 `PFViewBodyRotation`（SurfaceFrame.FixedToEci 的列反推，不写符号）。
                // ⚠ 左乘 `viewFrameRot`：固连档下"本船所在天体"变成恒等（地面静止），其余天体保留相对自转 ✓
                // ⚠⚠ 自检**必须用未换系的那一份**（`bodyRotEci`）——自检比的是"我的自转 vs 权威 tile→ECI"，
                //    在 ECI 系里比；喂换系后的值会在固连档下报几千 km 的**假红**（2026-10-12 修，别改回去）。
                Quaternion bodyRotEci = GetCelestialRenderRotation(body.bodyId);
                Quaternion selfRot = viewFrameRot * bodyRotEci;

                // 相机到天体的真实方向与距离
                Vector3 dirFromCam = (vp - camPos).normalized;
                float camDistToBody = Vector3.Distance(vp, camPos);

                Vector3 renderPos;
                float renderR;

                if (camDistToBody < FarClip * 0.5f)
                {
                    // 在裁剪面内：直接渲染真实位置
                    renderPos = vp;
                    renderR = bodyR;
                }
                else
                {
                    // 在裁剪面外：沿相机方向挪到 FarClip 内侧，等比缩放保持角大小
                    float placeDist = FarClip * 0.5f;
                    renderPos = camPos + dirFromCam * placeDist;
                    renderR = bodyR * (placeDist / camDistToBody);
                }

                // 2026-10-18 用户报「右键转动视角时行星闪烁/突然透明」：
                // 天体中心点可能已在视锥外，但巨大球面仍占满视野（高轨/近表都常见）⇒ 中心点剔除会误杀。
                // 遍历当前呈现集合并交给 GPU 裁剪，避免焦点变化导致已发布天体被过滤。

                // LOD 选择：根据天体在屏幕上的角大小决定网格精度
                float camDistToRender = Vector3.Distance(renderPos, camPos);
                float angularSize = renderR / Mathf.Max(camDistToRender, 0.01f);
                Mesh lodMesh = angularSize > 0.15f ? sphereMeshLOD0 :
                               angularSize > 0.05f ? sphereMeshLOD1 :
                                                     sphereMeshLOD2;

                // 基底球（底）：**无数据区 / 无补丁**的那部分 —— 背景 biome 贴图平铺（原版对未生成区域的同一口径）。
                // ⚠ `drawOutline: false` 是必需的：回退描边画的是**1.05 倍大的纯黑球**，
                // 而地表分格只外扩 0.6% ⇒ 黑壳比两者都靠外，会把贴图球和分格**一起挡住**
                // （2026-10-10 用户："看不见 tile" —— 实际看到的就是那颗黑壳）。
                // 描边本来就是"toon shader 缺席时的替身"，贴图球这条路上不该有它。
                var bodyLayer = GetCelestialRenderLayer(mgr, body.bodyId);

                // 先看有没有烘焙补丁 —— 它决定**基底球要不要下沉**（见下方注释）。
                bool drawSurface = celestialRenderRuntime == null || GetCelestialRenderVisual(body.bodyId, Celestial.CelestialVisualKind.Surface) != null;
                Mesh? patchMesh = null;
                Material? patchMat = null;
                bool hasPatch = drawSurface && TryGetSurfacePatch(body, bodyLayer, out patchMesh, out patchMat);

                // ⚠ 两层的**高度分工**（2026-10-11 用户实测"低飞朝向地表时烘焙渲染消失"之后定死）：
                //   · **补丁**只离地一点点（`PatchEpsilon`，地球 1 km）⇒ 低飞的相机永远不会钻到它下面；
                //   · **基底球**在有补丁时**下沉** `BaseSinkFactor`（地球 40 km）⇒ 深度余量由下沉买（两层间距 ≫ δz），
                //     而不是把补丁抬高（上一批抬到 30 km，低飞相机于是跑到了补丁底下、朝下只看到基底球 = 那次的病根）。
                // 没有烘焙补丁时，实时逐格表面顶上：基底球同样要下沉。
                // 深度余量只能靠"基底球下沉"买（抬高表面会把低飞相机关到面下，2026-10-11 的事故）。
                bool hasTileSurface = !hasPatch && HasPlanetTileSurface(body.bodyId, bodyLayer,
                    renderPos, renderR, camDistToRender);
                // 几何起伏可下探到 0.97R ⇒ 基底球必须沉得比"补丁时代"的 0.8% 更深，否则低洼顶点会从基底球里穿出来。
                float baseSink = hasTileSurface && TileSurfaceReliefActive(body.bodyId) ? 0.09f : PFViewSurfaceBake.BaseSinkFactor;
                float baseRenderR = (hasPatch || hasTileSurface) ? renderR * (1f - baseSink) : renderR;
                if (isStar ? PFViewStellarProbe.SurfacePass : drawSurface)
                    presentedBodySpheres.Add((body.bodyId, renderPos, Mathf.Max(renderR, MinBodyVisRadius)));
                if (drawSurface || isStar)
                {
                var baseMat = GetBaseSphereMaterial(body, bodyLayer);
                if (baseMat != null)
                {
                    ApplySunUniforms(baseMat, toSun, sunColor);
                    float visualRadius = isStar
                        ? PrepareStellarVisual(body, baseMat, renderPos, Mathf.Max(baseRenderR, MinBodyVisRadius), selfRot, lodMesh, tick) : baseRenderR;
                    // P0 性能探针（2026-10-28）：surface pass 可单独关掉做 A/B；halo 的提交在
                    // PrepareStellarVisual 内部由 `PFViewStellarProbe.HaloPass` 控制，两者独立 ⇒ 能分辨
                    // "谁贵"。默认全开 = 现状。
                    // ⚠⚠ 2026-10-11 修：这里的颜色参数**会被写进 MaterialPropertyBlock 覆盖材料的 `_Color`** ——
                    //   原来传 `Color.white` ⇒ 材料上的 `SurfaceReflectance`(0.20) 被抹掉 ⇒ 基底球一直**比补丁亮 5 倍**
                    //   （"反射率不一致"从第一天起就是这个原因；没有贴图时更直接变成"纯白"）。
                    //   ⇒ 传**与材料同值**的灰色，覆盖就等于没覆盖 ✓（补丁那条路没有 PropertyBlock，材料值直接生效）。
                    // S3-b：星体 surface 已由 PrepareStellarVisual 提交到 StarLayer + composite 到主 RT，
                    // 这里不能再画一遍（会重复叠加/覆盖离屏结果）。
                    if (!isStar || (!StellarOffscreenActive && PFViewStellarProbe.SurfacePass))
                        DrawCelestialSphere(renderPos, visualRadius, isStar ? Color.black : SurfaceTint, selfRot, lodMesh, baseMat, false, allowToon: false);
                }
                else
                    DrawCelestialSphere(renderPos, baseRenderR, c, selfRot, lodMesh, renderMat, true);
                }

                // 实时逐格表面：只在没有烘焙补丁时画（有补丁时补丁带河/路/山体细节，更细）。
                if (hasTileSurface)
                    DrawPlanetTileSurface(body.bodyId, renderPos, renderR, selfRot, toSun, sunColor);
                if (bodyLayer != null)
                    DrawTilePickHighlight(body, bodyLayer, renderPos, renderR, selfRot);

                if (hasPatch && patchMat != null)
                {
                    ApplySunUniforms(patchMat, toSun, sunColor);
                    Matrix4x4 patchMtx = Matrix4x4.TRS(renderPos, selfRot, Vector3.one * renderR * 2f);
                    Graphics.DrawMesh(patchMesh, patchMtx, patchMat, SceneLayer);
                }
                else if (body.hasSurface && noPatchLogged.Add(body.bodyId))
                {
                    // 没补丁 = 这颗天体的"地表"就是**基底球**（背景 biome 平铺），不是烘焙地形 —— 说明白，别让它像"渲染坏了"
                    Log.Message($"[PFView·地表] body={body.defName} **没有烘焙补丁** ⇒ 现在画的这一层是基底球（背景 biome 平铺），不是烘焙地形；去调试页签烤一次即得");
                }

                // 自转/换系自检（限频）：拿"船下地块"比"我画的方向"与 mod 的权威 tile→ECI
                // —— 这正是用户报的那条症状的判据（差 > 1 km 就是自转/换系错了）。
                VerifySurfaceAlignment(body, bodyLayer, bodyRotEci, tick);

                // 定居点层（D-2，2026-10-12）：**实时画、不烘**（动态数据不进静态图）。
                // 挂在这里 = 贴在同一套 TRS 上（renderPos/selfRot/renderR 都在作用域内），且在**大气壳之前**
                // ⇒ 壳会自然罩在定居点上面（与"贴在地表"的观感一致）。
                DrawSettlementOverlay(body, bodyLayer, renderPos, renderR, selfRot, toSun);

                // 云层与地表共用姿态/等比挪位；淡出用真实相机高度，避免远景缩放影响显隐。
                DrawCloudLayer(body, renderPos, renderR, selfRot, tick,
                    camDistToBody / (double)MetersToView - body.radius, toSun);

                // 大气壳（2026-10-10 用户口径）：半径 = R + **大气厚度的一半**（XML 定义），半透明。
                // 日照走**几何**（只画朝太阳那半边 ⇒ 夜侧没有壳）；双面网格 ⇒ 球缘那圈自然叠两层 = 大气辉光贴边。
                DrawAtmosphereShell(body, renderPos, renderR, toSun);

                if (isStar && DebugShowSunMarker && renderMat != null)
                {
                    matBlock.SetColor("_Color", new Color(1f, 1f, 0f));
                    Graphics.DrawMesh(cubeMesh, Matrix4x4.TRS(renderPos, Quaternion.identity,
                        Vector3.one * renderR * 2.2f), renderMat, SceneLayer, null, 0, matBlock);
                }

                // 2026-10-10：原"近档：可见分格补丁"已随用户裁定（不论远近均用烘焙贴图、取消档位）退役，
                // 归档在 `Source/已弃置/PFViewTiles-近档分格-20261010.cs`。天体表面现在统一由上面两层承担。
            }
        }

        private readonly System.Collections.Generic.Dictionary<int, long> lastAlignCheckTick =
            new();

        /// <summary>"这颗天体没有烘焙补丁"只说一次（免得每帧刷屏）。</summary>
        private readonly System.Collections.Generic.HashSet<int> noPatchLogged =
            new();

        /// <summary>
        /// **自转/换系自检**（限频：每天体每 60 物理秒一次）：拿某一个地块比两边 ——
        /// ① 我画的 = `selfRot ×（层里的格心方向**归一化后**）× R`（`PFViewBodyRotation.SurfacePointEci`）；
        /// ② mod 的权威 = `GetSurfaceState.TileToEci`。
        /// ⇒ 差应 ≈ 0；**> 1 km 就是自转相位/换系错了**（正是用户 2026-10-10 报的「PFView 贴图与世界视图角度不一致」的判据）。
        /// 地块取"生成区中间的格子"：本条判据与"取哪一格"无关（两边都是同一个旋转关系），取它能顺便在人眼上对得上。
        /// </summary>
        private void VerifySurfaceAlignment(CelestialBodyInfo body, PlanetLayer? layer, Quaternion selfRot, double tick)
        {
            if (layer == null || layer.TilesCount <= 0) return;
            long now = (long)tick;
            if (lastAlignCheckTick.TryGetValue(body.bodyId, out long last) && now - last < 60) return;
            lastAlignCheckTick[body.bodyId] = now;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            double omega = GetSurfaceState.RotationOmega(mgr, body.bodyId);
            int tile = layer.TilesCount / 2;
            Vector3 dirFixedRaw = layer.GetTileCenter(tile);

            // ⚠ 2026-10-11 修（**仪器自误**）：`GetTileCenter` 返回的是**"layer 半径"长的向量**（1 单位 = 50 km
            //   ⇒ 地球层 ≈100、月球层 ≈30），原式 `(selfRot * dirFixed) * body.radius` 漏了归一化 ⇒
            //   `|mine|` 大了 layer 半径倍 ⇒ 假红「差 494,998 km / 43,454 km」（与 (100−1)×5000 km、(30−1)×1500 km 逐位对上）。
            //   归一化收在 `PFViewBodyRotation.SurfacePointEci`（与权威侧 `TileToFixedPosition` 同口径），判据见该类注释。
            DVector3 mine = PFViewBodyRotation.SurfacePointEci(selfRot, dirFixedRaw, body.radius);   // 我画的（米、绝对系）
            DVector3 auth = mgr?.TryGetCelestialRuntime(out _) == true
                ? GetSurfaceState.TileToEci(tile, mgr, body.bodyId, tick)
                : GetSurfaceState.TileToEci(layer, tile, body.radius, omega, tick);
            double errKm = (mine - auth).magnitude / 1000.0;

            // 自转相位做成**可读数字**（用户 2026-10-10："没有自转起来" ⇒ 光靠眼睛判不了 21.6°/分钟 的东西）：
            double angleDeg = omega * tick * Mathf.Rad2Deg % 360.0;
            double degPerMinute = omega * Mathf.Rad2Deg * 60.0;   // tick ≈ 物理秒；游戏内 60 tick/秒 ⇒ 实时约 ×60

            // 船下地块（**权威口径**给的地块 + 经纬度）：跟世界视图那边可以交叉核对的一把手柄
            string shipInfo = "";
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var grp = selectedGroupId >= 0 && spaceGame != null ? spaceGame.GetGroupById(selectedGroupId) : null;
            if (grp != null && grp.CelestialBodyId == body.bodyId && mgr != null)
            {
                DVector3 shipEci = GetGroupPosAbs(grp, tick) - mgr.GetBodyPositionAbs(body.bodyId, tick);
                int t = mgr.TryGetCelestialRuntime(out _) ? GetSurfaceState.EciToTile(shipEci, mgr, body.bodyId, tick)
                    : GetSurfaceState.EciToTile(shipEci, layer, omega, tick);
                if (t >= 0)
                {
                    Vector2 ll = layer.LongLatOf(t);
                    shipInfo = $"｜船下地块={t}（φ{ll.y:F1}° λ{ll.x:F1}°）";
                }
            }

            Log.Message($"[PFView·自转] body={body.defName} 相位 {angleDeg:F1}°（{degPerMinute:F2}°/分钟·物理秒）{shipInfo}｜网格×自转 对 权威(tile→ECI) 差 {errKm:F2} km{(errKm > 1.0 ? "  ⚠ > 1 km ⇒ 自转/换系错了" : "  ✓")}");
        }

        /// <summary>天体球体渲染（三渲二：Toon Shader 内置轮廓线 + 二分阴影，不可用时回退）</summary>
        private void DrawCelestialSphere(Vector3 pos, float radius, Color c, Quaternion selfRot, Mesh mesh, Material mat,
            bool drawOutline, bool allowToon = true)
        {
            float r = Mathf.Max(radius, MinBodyVisRadius);

            if (allowToon && ToonMat != null && drawOutline)
            {
                // Toon Shader：轮廓线由 Shader 的 OUTLINE Pass 自动处理，仅需一次 DrawMesh
                Matrix4x4 mtx = Matrix4x4.TRS(pos, selfRot, Vector3.one * r * 2f);
                matBlock.SetColor("_Color", c);
                matBlock.SetFloat("_OutlineWidth", 0.035f); // V-1.3：描边加粗（"卡通一点"）
                Graphics.DrawMesh(mesh, mtx, ToonMat, SceneLayer, null, 0, matBlock);
                return;
            }

            // 回退：双层绘制模拟轮廓线
            if (drawOutline)
            {
                Matrix4x4 outlineMtx = Matrix4x4.TRS(pos, selfRot, Vector3.one * r * 2f * 1.05f);
                matBlock.SetColor("_Color", Color.black);
                Graphics.DrawMesh(mesh, outlineMtx, renderMat, SceneLayer, null, 0, matBlock);
            }

            Matrix4x4 mtx2 = Matrix4x4.TRS(pos, selfRot, Vector3.one * r * 2f);
            matBlock.SetColor("_Color", c);
            Graphics.DrawMesh(mesh, mtx2, mat, SceneLayer, null, 0, matBlock);
        }

        private void DrawSystemGroups(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            EnsureBuildingVisualSession();
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            var sysIds = GetCurrentSystemBodyIds(mgr); // 第 12 条：记忆化复用（此前每帧新建 HashSet + 填卫星）
            var sensor = Current.Game?.GetComponent<SensorManager>();   // [V3] 列表准入（本方法低频，直接取组件）

            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive) continue;
                if (!spaceGame.GroupIsVisibleAt(g, tick)) continue;
                if (!sysIds.Contains(g.CelestialBodyId)) continue;
                if (!CanInspectEntity(g)) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;   // [V3]/S4c：列表只列看得见的
                impactVisibleGroups.Add(g.GroupId);

                DVector3 gPosAbs = GetGroupPosAbs(g, tick);
                Vector3 vp = AbsToView(gPosAbs, origin);
                float dist = vp.magnitude;

                // [B4 排查读数] 盯第一"别的"组：帧间位移统计（限频 300 帧一行）
                if (dbgWatchedGroupId < 0 || dbgWatchedGroupId == g.GroupId)
                    FeedJitterProbe(g, vp, tick, dist);

                if (dist < PartRenderDist)
                {
                    DrawGroupParts(g, gPosAbs, origin, spaceGame);
                    continue;
                }

                // Billboard 面片：始终朝向相机，尺寸随距离增大保持屏幕可见
                float s = Mathf.Clamp(dist * 0.005f, 10f, 500f);
                if (!SphereInFrustum(vp, s)) continue;

                Quaternion rot = Quaternion.LookRotation(renderCamera.transform.forward, renderCamera.transform.up);
                Matrix4x4 mtx = Matrix4x4.TRS(vp, rot, Vector3.one * s);
                matBlock.SetColor("_Color", new Color(0.3f, 0.9f, 1.0f, 0.9f));
                Graphics.DrawMesh(quadMesh, mtx, renderMat, SceneLayer, null, 0, matBlock);

                // 2026-10-30（用户令"直接删"）：远距标记不再配"黄色主推方向棒"。
                // 主推轴读数仍在姿态球（T 记号）与左栏文字里 —— 那根棒只剩重复信息 + 穿过船体的观感问题。
            }

        }


        // ═══════════════════════════════════════════════════════════════════
        //  [B4 排查读数] "别的航天器整体位置在抖"（2026-10-11，用户实测报回）
        //  量的是**帧间位移**：安静的光滑运动 ⇒ 各帧位移均匀（最大/平均 ≈1）；
        //  时间轴阶梯 ⇒ 大量**停顿帧**（位移=0，同一 tick 重复渲染）+ 偶发大跳（最大/平均 ≫1）。
        //  限频：每 300 帧一行（约 2~5 秒），盯**第一个非选中组**。
        // ═══════════════════════════════════════════════════════════════════
        private int dbgWatchedGroupId = -1;
        private int dbgFrames;
        private double dbgLastTick;
        private Vector3 dbgLastVp;
        private double dbgSumStep, dbgMaxStep, dbgTickAdvance;
        private int dbgStallFrames;

        private void FeedJitterProbe(SpaceEntityGroup g, Vector3 vp, double tick, float distUnits)
        {
            if (dbgFrames > 0)
            {
                double step = (vp - dbgLastVp).magnitude;
                dbgSumStep += step;
                if (step > dbgMaxStep) dbgMaxStep = step;
                dbgTickAdvance += tick - dbgLastTick;
                if (step <= 1e-6) dbgStallFrames++;
            }
            dbgWatchedGroupId = g.GroupId;
            dbgLastVp = vp;
            dbgLastTick = tick;
            dbgFrames++;
            if (dbgFrames < 300) return;

            double mean = dbgSumStep / (dbgFrames - 1);
            Log.Message($"[PFView·抖动] G{g.GroupId} 圆轨={(g.IsCircularOrbit ? "是" : "否")} 距离 {distUnits * 0.01f:F0} km｜帧 {dbgFrames}｜tick 推进 {dbgTickAdvance:F2}（{dbgTickAdvance / (dbgFrames - 1):F3}/帧）｜停顿帧 {dbgStallFrames}｜帧间位移 平均 {mean:F3} 最大 {dbgMaxStep:F3} 单位（比 {(mean > 1e-9 ? dbgMaxStep / mean : 0.0):F1}×）");
            dbgFrames = 0; dbgSumStep = 0.0; dbgMaxStep = 0.0; dbgTickAdvance = 0.0; dbgStallFrames = 0;
        }

        private void DrawGroupParts(SpaceEntityGroup group, DVector3 groupPosAbs, DVector3 origin, SpaceGameComp spaceGame)
        {
            // 姿态 = 编队真实姿态（CurrentOrbit.rotation）。
            // 旧实现用**速度方向**做 LookRotation（船头永远朝轨道切线）：那是轨道运动的切线，不是船的
            // 朝向 —— 船转到任何姿态画面都一样，玩家看到的朝向与物理（主推对准 / 碰撞盒 / 命中几何，
            // 三者都消费同一个 rotation）毫无关系。改用真姿态后，船怎么转、画面就怎么转。
            Quaternion gRot = viewFrameRot * group.CurrentOrbit.rotation.ToQuaternion();   // 姿态同过参考系（固连档下"相对地面"的姿态）
            float scale = DebugScaleParts ? 200f : 1f;
            // 2026-10-19 蒙皮：阵营 tint 每帧算一次（同组所有实体共用）；蒙皮不可用 ⇒ null = 旧常量色。
            Color? skinTint = ShipSkinSettings.Enabled && ShipSkinAssets.Available
                ? ShipSkinPalette.PartTint(group.FactionId)
                : (Color?)null;

            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity == null) continue;

                DVector3 eAbs = groupPosAbs + entity.Position;
                Vector3 eVp = AbsToView(eAbs, origin);

                float visualRadius = Mathf.Max((float)entity.AabbHalfSize.magnitude, MeasureBuildingVisualRadius(entity));
                if (!SphereInFrustum(eVp, Mathf.Max(10f, visualRadius * MetersToView) * scale)) continue;

                // 有 Map 的实体（空间站/舱内图火箭）：画"地图挤出来的浮雕"（2026-10-02）；
                // 无图 / 图已封存 / 房间数据未追平 / 全图无建造痕迹 ⇒ 落回下面的 part 盒体。
                bool relief = TryDrawMapRelief(entity, eVp, gRot, scale);
                bool rocketHull = false;
                HashSet<int>? hullCovered = null;

                // 单 part 化：无设计数据分支，统一按运行时 Parts 绘制盒体（§5.4 C4）
                // 2026-10-30：有壳的火箭先画壳，只把"露出来的喷管"作为零件盒补画（其余件被壳包住）。
                if (!relief && entity.Parts.Count > 0)
                {
                    if (RocketHullMesh.TryGet(entity, out var hull))
                    {
                        rocketHull = true;
                        hullCovered = hull.coveredPartIds;
                        DrawRuntimeHull(hull, eVp, gRot, scale, skinTint);
                        DrawRuntimeParts(entity, eVp, gRot, scale, skinTint, hull.coveredPartIds);
                    }
                    else
                    {
                        DrawRuntimeParts(entity, eVp, gRot, scale, skinTint);
                    }
                }
                else if (relief && !reliefWholeEntity && entity.Parts.Count > 0)
                    DrawRuntimeParts(entity, eVp, gRot, scale, skinTint, reliefDrawnPartIds);

                DrawBuildingVisuals(entity, group, eVp, gRot, scale, rocketHull);

                // 2026-11-08：引擎喷焰采集。零件引擎（外壳/零件船、舱盒级地图的引擎件）+ 地图推进器建筑
                // （旧式/遭遇舰）都收；实体级浮雕船没有可见零件，只走地图推进器路径。
                bool partsVisible = !relief || !reliefWholeEntity;
                CollectEnginePlumes(entity, group, eVp, gRot, scale, hullCovered, partsVisible);
                if (!partsVisible) PFViewEnginePlumeProbe.LastWholeReliefEntities++;

                sceneMapContexts.Clear();
                drawnReliefSurfaces.Clear();
            }

            // 2026-10-30（用户令"直接删"）：这里原先给**无图编队**画"黄色主推方向棒"。
            // 删掉的理由不是缺信息（姿态球 T 记号 + 左栏"主推轴（T 记号）"文字仍在），
            // 而是那根棒会**穿过船体**（近距下限 30 m）、且对"船体本身已经是朝向读数"的模型是重复信息。
            // 详情与判据：`DesignNotes/交接-20261030-问题登记.md` §3。
        }

        /// <summary>
        /// PFView 视角适配（D-1，2026-10-11）：把窗口自己的状态解析成 `ShipModelRenderer` 的**显式入参**
        /// （材质 = Toon 优先 / 网格 / 层 / 米→视图换算 / 共享 matBlock）。矩阵与画法的口径全在绘制段那边
        /// —— 本函数不表达任何"怎么画"，只表达"PFView 用哪套资源画"。
        /// </summary>
        private static void DrawRuntimeParts(SpaceEntity entity, Vector3 eVp, Quaternion gRot, float scale,
            Color? skinTint, HashSet<int>? skipPartIds = null)
        {
            // 2026-10-19 蒙皮：可用且开 = PF_ShipSkin + 阵营 tint；否则逐字回到旧路径（Toon 优先/单色）。
            // 蒙皮用**硬法线**立方体（三平面投影/几何边亮棱需要面法线恒定）；旧路径仍用平滑法线 cubeMesh。
            Material? skin = ShipSkinSettings.Enabled ? ShipSkinAssets.Material : null;
            Material mat = skin ?? (ToonMat ?? renderMat);
            Mesh mesh = skin != null ? cubeMeshHard : cubeMesh;
            if (skin != null && !loggedPartSkin)
            {
                loggedPartSkin = true;
                Log.Message("[PFView·蒙皮] 首次用于无 Map 编队：贴图=" + (ShipSkinAssets.TextureLoaded ? "OK" : "缺失→白图")
                    + "｜" + ShipSkinAssets.StatusText);
            }
            Color? tint = skin == null ? null : skinTint;
            ShipModelRenderer.DrawParts(entity, eVp, gRot, scale, mesh, mat, matBlock,
                skin == null && ToonMat != null, MetersToView, SceneLayer, tint, skipPartIds);
        }

        /// <summary>
        /// **画火箭外壳**（2026-10-30）：材质/网格选择与 `DrawRuntimeParts` 同口径（蒙皮优先），
        /// 只是把"三批颜色 + 米制网格"交给 `ShipModelRenderer.DrawHull`。
        /// </summary>
        private static void DrawRuntimeHull(RocketHullGeometry hull, Vector3 eVp, Quaternion gRot, float scale,
            Color? skinTint)
        {
            Material? skin = ShipSkinSettings.Enabled ? ShipSkinAssets.Material : null;
            Material mat = skin ?? (ToonMat ?? renderMat);
            Color hullColor = skinTint ?? ShipModelRenderer.PartColor;
            ShipModelRenderer.DrawHull(hull, eVp, gRot, scale, mat, matBlock, MetersToView, SceneLayer,
                skin == null && ToonMat != null, hullColor);
        }

        private void DrawProjectileExplosions(CombatManager cm, DVector3 origin, double tick)
        {
            // 爆炸视觉：扩散闪光球（半径随时间增长、透明度淡出）
            foreach (var exp in cm.ActiveExplosions)
            {
                if (exp.HasImpactEvent) continue;
                float t = 1f - (float)exp.remainingTicks / exp.totalTicks; // 0→1
                DVector3 bodyOff = GetBodyPosAbs(exp.systemBodyId, tick);
                Vector3 vp = AbsToView(exp.position + bodyOff, origin);
                float radius = (float)(exp.maxRadius * MetersToView) * (0.2f + 0.8f * t);
                if (radius < 0.01f) continue;
                float alpha = 1f - t;
                Matrix4x4 mtx = Matrix4x4.TRS(vp, Quaternion.identity, Vector3.one * radius);
                matBlock.SetColor("_Color", new Color(1f, 0.45f, 0.1f, alpha));
                Graphics.DrawMesh(sphereMeshLOD1, mtx, renderMat, SceneLayer, null, 0, matBlock);
            }
        }

        // 轨道线已删（2026-10-14，用户："轨道线本来就是不需要存在的东西吧？"——判过不接，见 1.7 计划台账 #14）。
        // 历史：1.6 = GL 立即模式 + RenderPipelineManager 后置钩子；1.7 试过 mesh ribbon（平面环 + 双面）。
        // 若要恢复：git 历史里有两版实现（GL 版与平面 mesh 版）。
    }
}
