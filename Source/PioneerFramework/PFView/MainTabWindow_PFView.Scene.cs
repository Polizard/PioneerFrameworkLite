using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;
using RimWorld;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView : MainTabWindow
    {
        /// <summary>每帧更新方向光，使其从恒星方向照射到行星</summary>
        private void UpdateLighting(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            if (sunLight == null) return;

            var star = mgr.GetStar();
            if (star == null) return;

            // 找到飞船所在的行星（不是飞船本身的位置！）
            int shipBodyId = selectedGroupId >= 0 ? GetSelectedGroupBodyId() : currentSystemBodyId;
            // ⚠ 2026-09-26（D8）：同上，收敛到 `GetRootPlanetId`（不再手搓"父体即行星"）。
            int shipRootPlanetId = mgr.GetRootPlanetId(shipBodyId);
            CelestialBodyDef planetBody = shipRootPlanetId >= 0 ? mgr.GetBodyById(shipRootPlanetId) : null;
            if (planetBody == null) return;

            // 行星世界坐标 = 从恒星指向行星的矢量（恒星在(0,0,0)）
            DVector3 planetPosAbs = mgr.GetBodyPositionAbs(planetBody.bodyId, tick);

            // 方向光从恒星照向行星：forward = 恒星→行星方向
            Vector3 lightDir = new Vector3(
                (float)planetPosAbs.x,
                (float)planetPosAbs.y,
                (float)planetPosAbs.z).normalized;

            if (lightDir.sqrMagnitude > 0.001f)
                sunLight.transform.forward = lightDir;
        }

        // ════════════════════════════════════════════════════
        //  场景渲染
        // ════════════════════════════════════════════════════

        private void RenderScene(CelestialBodyManager mgr)
        {
            if (renderCamera == null || renderTexture == null) return;

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            cachedRenderTick = (int)Math.Round(tick); // int 缓存仅作 OnPostRender 轨道线采样/键，天体位置一律用 double tick
            DVector3 origin = GetRenderOrigin(tick);

            UpdateLighting(mgr, tick, origin);
            DrawAllObjects(origin, mgr, tick);

            renderCamera.Render();
        }

        private void DrawAllObjects(DVector3 origin, CelestialBodyManager mgr, double tick)
        {
            DrawCelestialBodies(mgr, tick, origin);
            DrawSystemGroups(mgr, tick, origin);
            DrawProjectiles(origin);
        }

        /// <summary>计算本行星系应渲染的天体 ID 集合（恒星 + 飞船所在行星 + 其卫星）。
        /// 第 12 条：按 shipBodyId 记忆化（每帧调用；天体图开局后不可变 → 键相同即内容相同）。</summary>
        private HashSet<int> GetLocalSystemBodyIds(CelestialBodyManager mgr)
        {
            // 找到飞船所在天体
            int shipBodyId = selectedGroupId >= 0 ? GetSelectedGroupBodyId() : currentSystemBodyId;
            if (localSystemBodyIdsCacheKey == shipBodyId) return localSystemBodyIdsCache;
            localSystemBodyIdsCacheKey = shipBodyId;

            var ids = localSystemBodyIdsCache;
            ids.Clear();
            var shipBody = mgr.GetBodyById(shipBodyId);

            // 找到顶层恒星
            var star = mgr.GetStar();
            if (star != null) ids.Add(star.bodyId);

            if (shipBody != null)
            {
                // ⚠ 2026-09-26（D8）：两个分支其实是同一件事——"显示**根行星** + 它的全部卫星"。
                //   原先写成"行星→自身 / 卫星→父体"两路手搓 ⇒ 收敛到 `GetRootPlanetId` 一条。
                int rootPlanetId = mgr.GetRootPlanetId(shipBody.bodyId);
                var rootPlanet = rootPlanetId >= 0 ? mgr.GetBodyById(rootPlanetId) : null;
                if (rootPlanet != null)
                {
                    ids.Add(rootPlanetId);
                    foreach (var moon in rootPlanet.moonBodies)
                        ids.Add(moon.bodyId);
                }
            }

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

        private int GetSelectedGroupBodyId()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return currentSystemBodyId;
            var g = spaceGame.GetGroupById(selectedGroupId);
            return g != null ? g.CelestialBodyId : currentSystemBodyId;
        }


        private void DrawCelestialBodies(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            var relevantIds = GetLocalSystemBodyIds(mgr);
            Vector3 camPos = GetCameraViewPos();

            foreach (var body in mgr.GetAllBodies())
            {
                if (!relevantIds.Contains(body.bodyId)) continue;

                bool isStar = body.bodyType == CelestialBodyType.Star;
                DVector3 wp = mgr.GetBodyPositionAbs(body.bodyId, tick);
                Vector3 vp = AbsToView(wp, origin);

                // ═══ 恒星：等效替代，方向对即可 ═══
                if (isStar)
                {
                    // 恒星真实距离（1 AU 量级）远超远裁剪面，故画"等效替代"：只保留方向。
                    // 距离随相机距离等比放大 → 角大小恒定，缩放到全星系尺度时太阳不会退成一个点；
                    // 方向取**相机→恒星**（旧版取锚点→恒星，相机离锚点远时方向本身就有视差偏差）。
                    Vector3 sunDir = (vp - camPos);
                    if (sunDir.sqrMagnitude < 1e-6f) sunDir = -camPos;
                    if (sunDir.sqrMagnitude < 1e-6f) sunDir = Vector3.forward;
                    float placeDist = Mathf.Clamp(Mathf.Max(camDistance, SunMinPlaceDist), SunMinPlaceDist, FarClip * 0.25f);
                    float sunSize = placeDist * SunAngularScale;
                    Vector3 sunRenderPos = camPos + sunDir.normalized * placeDist;

                    if (SunBillboardMat != null)
                    {
                        // 发光球体（大，保证可见）
                        Matrix4x4 sphereMtx = Matrix4x4.TRS(sunRenderPos, Quaternion.identity,
                            Vector3.one * sunSize * 2f);
                        Graphics.DrawMesh(sphereMeshLOD2, sphereMtx, SunBillboardMat, SceneLayer);

                        // billboard 叠加（朝向相机，RW 风格）
                        Vector3 toCam = (camPos - sunRenderPos).normalized;
                        if (toCam.sqrMagnitude < 0.0001f) toCam = Vector3.forward;
                        Quaternion sunRot = Quaternion.LookRotation(toCam, Vector3.up);
                        Matrix4x4 sunMtx = Matrix4x4.TRS(sunRenderPos, sunRot, Vector3.one * sunSize);
                        Graphics.DrawMesh(quadMesh, sunMtx, SunBillboardMat, SceneLayer);
                    }

                    // ── 调试：在太阳位置放黄色方块（默认关闭；面板上"太阳标记"勾选）──
                    if (debugShowSunMarker && renderMat != null)
                    {
                        matBlock.SetColor("_Color", new Color(1f, 1f, 0f));
                        Matrix4x4 dbgMtx = Matrix4x4.TRS(sunRenderPos, Quaternion.identity,
                            Vector3.one * sunSize * 2.5f);
                        Graphics.DrawMesh(cubeMesh, dbgMtx, renderMat, SceneLayer, null, 0, matBlock);
                    }
                    continue;
                }

                // ═══ 行星/卫星：实际位置渲染 ═══
                float bodyR = (float)(body.radius * MetersToView);

                Color c = body.bodyType == CelestialBodyType.Planet ?
                    new Color(0.2f, 0.5f, 0.8f) : new Color(0.7f, 0.7f, 0.7f);

                // 天体不自转，仅依赖方向光从恒星方向照射呈现明暗面
                Quaternion selfRot = Quaternion.identity;

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

                float cullR = Mathf.Max(renderR, MinBodyVisRadius);
                if (!SphereInFrustum(renderPos, cullR))
                    continue;

                // LOD 选择：根据天体在屏幕上的角大小决定网格精度
                float camDistToRender = Vector3.Distance(renderPos, camPos);
                float angularSize = renderR / Mathf.Max(camDistToRender, 0.01f);
                Mesh lodMesh = angularSize > 0.15f ? sphereMeshLOD0 :
                               angularSize > 0.05f ? sphereMeshLOD1 :
                                                     sphereMeshLOD2;

                DrawCelestialSphere(renderPos, renderR, c, selfRot, lodMesh, renderMat, true);
            }
        }

        /// <summary>天体球体渲染（三渲二：Toon Shader 内置轮廓线 + 二分阴影，不可用时回退）</summary>
        private void DrawCelestialSphere(Vector3 pos, float radius, Color c, Quaternion selfRot, Mesh mesh, Material mat, bool drawOutline)
        {
            float r = Mathf.Max(radius, MinBodyVisRadius);

            if (ToonMat != null && drawOutline)
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
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            var sysIds = GetCurrentSystemBodyIds(mgr); // 第 12 条：记忆化复用（此前每帧新建 HashSet + 填卫星）
            var sensor = Current.Game?.GetComponent<SensorManager>();   // [V3] 列表准入（本方法低频，直接取组件）

            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive) continue;
                if (!sysIds.Contains(g.CelestialBodyId)) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;   // [V3]/S4c：列表只列看得见的

                DVector3 gPosAbs = GetGroupPosAbs(g, tick);
                Vector3 vp = AbsToView(gPosAbs, origin);
                float dist = vp.magnitude;

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

                // 远距离也要能看出朝向：指示棒长度随标记尺寸同比（= 屏幕尺寸恒定）
                DrawHeadingBar(g, spaceGame, vp, s * 1.5f);
            }

        }

        private void DrawGroupParts(SpaceEntityGroup group, DVector3 groupPosAbs, DVector3 origin, SpaceGameComp spaceGame)
        {
            // 姿态 = 编队真实姿态（CurrentOrbit.rotation）。
            // 旧实现用**速度方向**做 LookRotation（船头永远朝轨道切线）：那是轨道运动的切线，不是船的
            // 朝向 —— 船转到任何姿态画面都一样，玩家看到的朝向与物理（主推对准 / 碰撞盒 / 命中几何，
            // 三者都消费同一个 rotation）毫无关系。改用真姿态后，船怎么转、画面就怎么转。
            Quaternion gRot = group.CurrentOrbit.rotation;
            float scale = debugScaleParts ? 200f : 1f;

            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity == null) continue;

                DVector3 eAbs = groupPosAbs + entity.Position;
                Vector3 eVp = AbsToView(eAbs, origin);

                if (!SphereInFrustum(eVp, 10f * scale)) continue;

                // 有 Map 的实体（空间站/舱内图火箭）：画"地图挤出来的浮雕"（2026-10-02）；
                // 无图 / 图已封存 / 房间数据未追平 / 全图无建造痕迹 ⇒ 落回下面的 part 盒体。
                if (entity.MapId >= 0 && TryDrawMapRelief(entity, eVp, gRot, scale))
                    continue;

                // 单 part 化：无设计数据分支，统一按运行时 Parts 绘制盒体（§5.4 C4）
                if (entity.Parts.Count > 0)
                    DrawRuntimeParts(entity, eVp, gRot, scale);
            }

            // 朝向指示棒长：以本组包围盒对角线的 0.7 倍为准（"放大部件"调试模式下同比放大），
            // 下限 3 view 单位 = 30m —— 棒子要能从船体里伸出来才看得见朝向
            float hull = 0f;
            foreach (int eid in group.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null) hull = Mathf.Max(hull, (float)e.BoundsSize.magnitude);
            }
            DrawHeadingBar(group, spaceGame, AbsToView(groupPosAbs, origin),
                Mathf.Max(hull * MetersToView * 0.7f, 3f) * scale);
        }

        /// <summary>
        /// 朝向指示棒：从编队中心沿**主推轴**画一根细棒。
        /// 主推轴 = 点火会把本编队推向哪边，取自 `SpaceGameComp.GetMainThrustAxisSystem`（与 burn / SAS
        /// 自动对准 / 主推方向同一出处），所以画出来的方向就是物理会执行的方向。
        /// 无引擎（无推力）/ 全向类（无主推轴）时不画 —— 不摆一根物理上不存在的箭头。
        /// </summary>
        private static void DrawHeadingBar(SpaceEntityGroup group, SpaceGameComp spaceGame, Vector3 centerVp, float barLen)
        {
            if (spaceGame == null || barLen <= 0.01f) return;

            DVector3 axisSystem = spaceGame.GetMainThrustAxisSystem(group);
            if (axisSystem.sqrMagnitude < 1e-12) return;

            // AbsToView 只有平移 + 正等比缩放 ⇒ 方向在视图系与世界系同向，直接当视图方向用
            Vector3 dir = (Vector3)axisSystem.normalized;
            if (dir.sqrMagnitude < 0.5f) return;

            float width = Mathf.Max(barLen * 0.08f, 0.05f);
            Vector3 mid = centerVp + dir * (barLen * 0.5f);
            Quaternion rot = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.forward);
            Matrix4x4 mtx = Matrix4x4.TRS(mid, rot, new Vector3(width, width, barLen));
            matBlock.SetColor("_Color", new Color(1f, 0.75f, 0.15f, 1f));
            Graphics.DrawMesh(cubeMesh, mtx, renderMat, SceneLayer, null, 0, matBlock);
        }

        private static void DrawRuntimeParts(SpaceEntity entity, Vector3 eVp, Quaternion gRot, float scale = 1f)
        {
            Material partMat = ToonMat ?? renderMat;
            if (ToonMat != null)
                matBlock.SetFloat("_OutlineWidth", 0.02f); // V-1.3：部件也画描边（旧值 0 是"shader 尚未加载"时代的死判断）

            foreach (var part in entity.Parts)
            {
                if (part.isDestroyed) continue;

                var mesh = cubeMesh;

                Vector3 pv = (Vector3)part.positionShip * MetersToView * scale;
                Vector3 sv = (Vector3)part.boundsSizeShip * MetersToView * scale;
                if (sv.x < 0.1f) sv.x = 0.1f;
                if (sv.y < 0.1f) sv.y = 0.1f;
                if (sv.z < 0.1f) sv.z = 0.1f;

                Vector3 wp = eVp + gRot * pv;
                Matrix4x4 mtx = Matrix4x4.TRS(wp, gRot, sv);
                var col = new Color(0.3f, 0.6f, 1.0f);
                matBlock.SetColor("_Color", col);
                Graphics.DrawMesh(mesh, mtx, partMat, SceneLayer, null, 0, matBlock);
            }
        }

        private void DrawProjectiles(DVector3 origin)
        {
            var cm = Current.Game?.GetComponent<CombatManager>();
            if (cm == null) return;

            // 渲染插值必须用 physicsTick 基准（射弹 spawnPhysicsTick 同基准），
            // 不能用 TicksGame：physicsTick 是受时间档缩放的游戏世界秒且可为负，
            // 与 TicksGame 直接相减单位错位 → 外推恒为 0 → 渲染只显示离散物理位置 → 折线。
            // 天体偏移同样用 double physicsTick：int 化在 1/60x 下每 60 real tick 才跳 1 秒，
            // 天体位置离散跳变 → 射弹/场景"集体闪现"。全部用 double 后完全连续。
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            Vector3 camView = GetCameraViewPos();   // 逐弹重复求一次相机位（含三角函数）没有意义

            foreach (var proj in cm.ActiveProjectiles)
            {
                // 渲染位置解析化：无重力射弹位置 = spawnPos + velocity×(physicsTick - spawnPhysicsTick)，
                // 是 physicsTick 的连续函数——任何渲染帧率下都平滑（不依赖每 2 tick 的离散步进点，
                // 低帧率时渲染点与物理点重合会离散跳动呈折线）。重力射弹回退为物理点+外推插值。
                double physicsNowT = tick;
                DVector3 renderPos = proj.affectedByGravity
                    ? (proj.position + proj.velocity * Math.Max(0, physicsNowT - proj.prevPhysicsTick)).v
                    : (proj.spawnPosition + proj.velocity * (physicsNowT - proj.spawnPhysicsTick)).v;

                // 射弹位置是行星系相对坐标，渲染需转绝对（加所属行星世界偏移）
                DVector3 bodyOff = GetBodyPosAbs(proj.systemBodyId, tick);
                Vector3 vp = AbsToView(renderPos + bodyOff, origin);
                float dist = Vector3.Distance(vp, camView);

                // 按相机距离自适应放大：射弹在行星系尺度下极小，固定尺寸不可见
                float headSize = Mathf.Max(4f, dist * 0.008f);
                // 尾迹必须远细于弹头，否则立方体端面在视觉上像第二个球
                float trailWidth = Mathf.Max(1f, headSize * 0.15f);

                // 弹头
                Matrix4x4 mtx = Matrix4x4.TRS(vp, Quaternion.identity, Vector3.one * headSize);
                matBlock.SetColor("_Color", new Color(1f, 0.9f, 0.2f));
                Graphics.DrawMesh(sphereMeshLOD1, mtx, renderMat, SceneLayer, null, 0, matBlock);

#if DEBUG
                // 连续检测：相邻帧弹头渲染位置跳变超 1.5×物理步进 → 折线/闪现（限频提示）
                if (debugLastProjRender.TryGetValue(proj.id, out var lastRender))
                {
                    double jump = (renderPos - lastRender).magnitude;
                    double expectedStep = proj.velocity.magnitude * (2.0 / 60.0);
                    if (jump > expectedStep * 1.5 + 1.0 && Find.TickManager.TicksGame - debugJumpLogTick >= 60)
                    {
                        debugJumpLogTick = Find.TickManager.TicksGame;
                        Log.Message(string.Format("[射弹渲染检测] proj={0} 帧间跳变 {1:F0}m（预期步进 {2:F0}m）→ 疑似折线/闪现",
                            proj.id, jump, expectedStep));
                    }
                }
                debugLastProjRender[proj.id] = renderPos;
                debugLiveProjectileIds.Add(proj.id);
#endif

                // 尾迹：沿 velocity 反方向固定延伸 1 个物理步（长度恒定、端点连续）。
                // 原实现连接 proj.prevPosition（物理上一步，每 2 tick 跳 167m），尾迹长度
                // 每物理步从满长缩到 0 再跳回 → 伸缩抖动，视觉上呈"折线"。弹头本就平滑。
                DVector3 trailEndAbs = renderPos - proj.velocity.v * (2.0 / 60.0);   // 批6 S3-3c：显式跨界
                Vector3 tv = AbsToView(trailEndAbs + bodyOff, origin);
                float len = Vector3.Distance(vp, tv);
                if (len > 0.01f)
                {
                    Vector3 mid = (vp + tv) * 0.5f;
                    Quaternion rot = Quaternion.LookRotation((vp - tv).normalized, Vector3.up);
                    Matrix4x4 lmtx = Matrix4x4.TRS(mid, rot, new Vector3(trailWidth, trailWidth, len));
                    matBlock.SetColor("_Color", new Color(1f, 0.8f, 0.2f, 0.55f));
                    Graphics.DrawMesh(cubeMesh, lmtx, renderMat, SceneLayer, null, 0, matBlock);
                }
            }

            foreach (var beam in cm.ActiveLaserBeams)
            {
                DVector3 bodyOff = GetBodyPosAbs(beam.systemBodyId, tick);
                Vector3 sv = AbsToView(beam.startPos + bodyOff, origin);
                Vector3 ev = AbsToView(beam.endPos + bodyOff, origin);
                Vector3 mid = (sv + ev) * 0.5f;
                float len = Vector3.Distance(sv, ev);
                if (len < 0.01f) continue;
                Quaternion rot = Quaternion.LookRotation((ev - sv).normalized, Vector3.up);
                Matrix4x4 mtx = Matrix4x4.TRS(mid, rot, new Vector3(0.15f, 0.15f, len));
                matBlock.SetColor("_Color", new Color(1f, 0.2f, 0.2f, 0.85f));
                Graphics.DrawMesh(cubeMesh, mtx, renderMat, SceneLayer, null, 0, matBlock);
            }

            // 爆炸视觉：扩散闪光球（半径随时间增长、透明度淡出）
            foreach (var exp in cm.ActiveExplosions)
            {
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

#if DEBUG
            // 清理已消失射弹的检测残留（否则一次会话内字典无界增长：每个弹 id 一条，永不回收）
            if (debugLastProjRender.Count > debugLiveProjectileIds.Count)
            {
                debugDeadProjIds.Clear();
                foreach (var kv in debugLastProjRender)
                    if (!debugLiveProjectileIds.Contains(kv.Key)) debugDeadProjIds.Add(kv.Key);
                for (int i = 0; i < debugDeadProjIds.Count; i++) debugLastProjRender.Remove(debugDeadProjIds[i]);
            }
            debugLiveProjectileIds.Clear();
#endif
        }

        private void OnPostRender(ScriptableRenderContext context, Camera cam)
        {
            if (cam != renderCamera) return;
            DrawOrbitLinesGL();
        }

        private void DrawOrbitLinesGL()
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return;

            int tick = cachedRenderTick;
            DVector3 origin = GetRenderOrigin();
            var bodyDef = mgr.GetBodyById(currentSystemBodyId);
            if (bodyDef == null) return;

            GL.PushMatrix();
            renderMat.SetPass(0);
            GL.Begin(GL.LINES);
            GL.Color(new Color(0f, 0.5f, 0.3f, 0.4f));

            var toDraw = new List<CelestialBodyDef>();
            if (bodyDef.HasOrbit) toDraw.Add(bodyDef);
            foreach (var m in bodyDef.moonBodies) toDraw.Add(m);

            foreach (var body in toDraw)
            {
                var pts = mgr.GetOrbitPathUnity(body.bodyId, tick, 72);
                if (pts == null || pts.Count < 2) continue;

                for (int i = 0; i < pts.Count; i++)
                {
                    int j = (i + 1) % pts.Count;
                    Vector3 a = AbsToView(new DVector3(
                        pts[i].x * SpaceConstants.MetersPerUnit,
                        pts[i].y * SpaceConstants.MetersPerUnit,
                        pts[i].z * SpaceConstants.MetersPerUnit), origin);
                    Vector3 b = AbsToView(new DVector3(
                        pts[j].x * SpaceConstants.MetersPerUnit,
                        pts[j].y * SpaceConstants.MetersPerUnit,
                        pts[j].z * SpaceConstants.MetersPerUnit), origin);
                    GL.Vertex(a); GL.Vertex(b);
                }
            }

            GL.End();
            GL.PopMatrix();
        }
    }
}
