using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 的**行星地表格拾取 + 选中高亮**（2026-11-08，A 方案第一批）。
    /// 参考「天体运行」的 CelestialRegionRenderer.Pick/高亮，但数据源用我们的 PlanetLayer 格心。
    ///
    /// 交互：Ctrl + 左键点行星（焦点为天体时）= 检视该格；本文件只做"算出是哪一格 + 画高亮"，
    /// 面板显示由 SurfaceInspect partial 负责（Lead 接线）。
    ///
    /// 实现要点：
    /// - 拾取 = 相机射线与天体球求交 -> 命中方向旋回天体固连系 -> 与格心方向最大点积（格心来自逐格表面的缓存）；
    /// - 高亮 = 该格多边形在半径上外扩一点点画一层半透明青色（不做深度写入，避免盖住后续地表细节）；
    /// - 网格/材质必须显式销毁（挂在既有 DestroySurfaceResources 清理口上）。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        private int tilePickBodyId = -1;
        private int tilePickTile = -1;
        private Mesh? tilePickMesh;
        private Material? tilePickMaterial;
        private int tilePickMeshBody = -1;
        private int tilePickMeshTile = -1;

        internal int InspectedTileBodyId => tilePickBodyId;
        internal int InspectedTileId => tilePickTile;
        internal bool HasTilePick => tilePickBodyId >= 0 && tilePickTile >= 0;

        /// <summary>清空地块选择（切换天体/清空选择时调用）。</summary>
        internal void ClearTilePick()
        {
            tilePickBodyId = -1;
            tilePickTile = -1;
            DestroyTilePickVisual();
        }

        /// <summary>
        /// 把屏幕坐标拾取成 (bodyId, tileId)。无命中返回 false。
        /// bodyId 必须是当前呈现集里的天体（presentedBodySpheres 同帧数据）。
        /// </summary>
        private bool TryPickPlanetTile(Vector2 mouseInView, Vector2 viewSize, out int bodyId, out int tileId)
        {
            bodyId = -1;
            tileId = -1;
            if (renderCamera == null) return false;
            int pickedBody = PickViewportBody(mouseInView, viewSize);
            if (pickedBody < 0) return false;

            Vector3 sphereCenter = Vector3.zero;
            float sphereRadius = 0f;
            for (int i = 0; i < presentedBodySpheres.Count; i++)
            {
                if (presentedBodySpheres[i].BodyId != pickedBody) continue;
                sphereCenter = presentedBodySpheres[i].Center;
                sphereRadius = presentedBodySpheres[i].Radius;
                break;
            }
            if (sphereRadius <= 0f) return false;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var body = mgr?.GetBodyById(pickedBody);
            if (body == null) return false;
            var layer = GetCelestialRenderLayer(mgr!, pickedBody);
            if (layer == null) return false;
            var build = EnsureTileSurfaceBuild(pickedBody, layer, layer.TilesCount);
            if (build == null || build.Failed || build.centers == null) return false;

            float tanHalfFov = Mathf.Tan(renderCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float x = (2f * mouseInView.x / viewSize.x - 1f) * tanHalfFov * renderCamera.aspect;
            float y = (1f - 2f * mouseInView.y / viewSize.y) * tanHalfFov;
            var cameraTransform = renderCamera.transform;
            Vector3 origin = cameraTransform.position;
            Vector3 direction = cameraTransform.rotation * new Vector3(x, y, 1f);
            direction.Normalize();

            Vector3 toCenter = sphereCenter - origin;
            float b = Vector3.Dot(toCenter, direction);
            float c = Vector3.Dot(toCenter, toCenter) - sphereRadius * sphereRadius;
            float discriminant = b * b - c;
            if (discriminant < 0f) return false;
            float root = Mathf.Sqrt(discriminant);
            float t = b - root;
            if (t < 0f) t = b + root;
            if (t < 0f) return false;

            Vector3 hit = origin + direction * t;
            Vector3 dirView = (hit - sphereCenter).normalized;
            Quaternion selfRot = viewFrameRot * GetCelestialRenderRotation(pickedBody);
            Vector3 dirBody = Quaternion.Inverse(selfRot) * dirView;

            // 桶索引（纯核 SphereTileIndexCore）首次点击时建一次：~12 万格线性扫太浪费。
            var index = build.pickIndex;
            if (index == null)
            {
                var centers = build.centers;
                var snCenters = new System.Numerics.Vector3[centers.Length];
                for (int i = 0; i < centers.Length; i++)
                {
                    var ct = centers[i];
                    snCenters[i] = new System.Numerics.Vector3(ct.x, ct.y, ct.z);
                }
                index = new Celestial.SphereTileIndexCore(snCenters);
                build.pickIndex = index;
            }
            if (!index.TryPick(new System.Numerics.Vector3(dirBody.x, dirBody.y, dirBody.z), out int bestTile)) return false;
            if (bestTile < 0) return false;
            bodyId = pickedBody;
            tileId = bestTile;
            return true;
        }

        /// <summary>Ctrl+左键入口（由 HandleViewportClick 接线）。返回 true = 已消费。</summary>
        private bool TryInspectPlanetTile(Vector2 mouseInView, Vector2 viewSize)
        {
            if (!TryPickPlanetTile(mouseInView, viewSize, out int bodyId, out int tileId)) return false;
            SetTilePick(bodyId, tileId);
            SetInspectedSurfaceTile(bodyId, tileId);
            return true;
        }

        private void SetTilePick(int bodyId, int tileId)
        {
            tilePickBodyId = bodyId;
            tilePickTile = tileId;
            DestroyTilePickVisual();
        }

        /// <summary>画选中格高亮（在逐格表面之后调，半透明青覆盖）。</summary>
        private void DrawTilePickHighlight(CelestialBodyInfo body, PlanetLayer layer, Vector3 renderPos, float renderR, Quaternion selfRot)
        {
            if (!HasTilePick || tilePickBodyId != body.bodyId) return;
            if (tilePickMesh == null || tilePickMeshBody != tilePickBodyId || tilePickMeshTile != tilePickTile)
            {
                DestroyTilePickVisual();
                tilePickMesh = BuildTilePickMesh(layer, tilePickTile, out tilePickMeshBody, out tilePickMeshTile);
                if (tilePickMesh == null) return;
                var shader = ShaderDatabase.Transparent;
                if (shader == null) return;
                tilePickMaterial = new Material(shader) { color = new Color(0.2f, 0.85f, 1f, 0.35f) };
                tilePickMaterial.renderQueue = 3000;
            }
            if (tilePickMesh == null || tilePickMaterial == null) return;
            var mtx = Matrix4x4.TRS(renderPos, selfRot, Vector3.one * renderR * 2f);
            Graphics.DrawMesh(tilePickMesh, mtx, tilePickMaterial, SceneLayer);
        }

        private Mesh? BuildTilePickMesh(PlanetLayer layer, int tileId, out int bodyHint, out int tileHint)
        {
            bodyHint = tilePickBodyId;
            tileHint = tileId;
            if (tileId < 0 || tileId >= layer.TilesCount) return null;
            var verts = layer.UnsafeVerts;
            var offsets = layer.UnsafeTileIDToVerts_offsets;
            if (verts == null || offsets == null || tileId >= offsets.Length) return null;
            int start = offsets[tileId];
            int end = tileId + 1 < offsets.Length ? offsets[tileId + 1] : verts.Length;
            int polyVerts = end - start;
            if (polyVerts < 3) return null;

            Vector3 center = layer.GetTileCenter(tileId);
            Vector3 centerDir = center.sqrMagnitude > 1e-9f ? center.normalized : Vector3.up;
            var meshVerts = new Vector3[polyVerts];
            var meshNormals = new Vector3[polyVerts];
            const float lift = 1.0004f;
            for (int k = 0; k < polyVerts; k++)
            {
                Vector3 v = verts[start + k];
                Vector3 dir = v.sqrMagnitude > 1e-9f ? v.normalized : centerDir;
                meshVerts[k] = dir * 0.5f * lift;
                meshNormals[k] = dir;
            }
            Vector3 p0 = meshVerts[0];
            Vector3 p1 = meshVerts[1];
            Vector3 p2 = meshVerts[2];
            bool flip = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), centerDir) < 0f;
            var tris = new List<int>((polyVerts - 2) * 6);
            for (int k = 1; k + 1 < polyVerts; k++)
            {
                // 双面：正反两绕序都发（同色 + 半透明，不赌绕序）。
                if (!flip) { tris.Add(0); tris.Add(k); tris.Add(k + 1); tris.Add(0); tris.Add(k + 1); tris.Add(k); }
                else { tris.Add(0); tris.Add(k + 1); tris.Add(k); tris.Add(0); tris.Add(k); tris.Add(k + 1); }
            }
            var mesh = new Mesh { name = "PF_TilePick" };
            mesh.SetVertices(meshVerts);
            mesh.SetNormals(meshNormals);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private void DestroyTilePickVisual()
        {
            if (tilePickMesh != null) UnityEngine.Object.Destroy(tilePickMesh);
            tilePickMesh = null;
            tilePickMeshBody = -1;
            tilePickMeshTile = -1;
            if (tilePickMaterial != null) UnityEngine.Object.Destroy(tilePickMaterial);
            tilePickMaterial = null;
        }
    }
}