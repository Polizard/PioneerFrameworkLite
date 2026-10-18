using System.Collections;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环外装饰性构件绘制层（2026-10-17 电影感视觉升级，task-8；task-9 按用户反馈扩展）。
    ///
    /// 内容（几何全部来自纯核 `RingOrnamentGeometry`，本层只负责提交）：
    /// - **金属**（`RingOrnamentMaterial.Metal`）：外挂塔/桅杆、横臂、斜撑、散热鳍/检修臂、
    ///   **垂直环平面的信号柱**（基座/桅杆/横臂/梯级/单斜撑/天线盘）、外挂货舱/检修舱段、天线阵面板 ——
    ///   闭合实体复用环壳材质与受光；UV 按环壳贴图长宽比和世界尺度转换。
    /// - **光斑**（`RingOrnamentMaterial.Glow`）：塔顶红灯、自由漂浮信标 —— 加性 `PF_RingGlow`
    ///   mode 2 sprite，颜色/闪烁相位在顶点色里（rgb=光色，a=相位）；用户反馈"荧光更强"后强度与尺寸同步上调。
    /// - **末端荧光**（`RingOrnamentMaterial.TerminalGlow`）：信号柱及天线面板顶部的青色呼吸灯，mode 3。
    ///
    /// 纪律：
    /// - `VisibleWhenLayerNotSelected => false`：只在环层视图画（与 RingTerrain/RingShell 一致）。
    /// - 不生成 collider、不写 triangle→tile 映射 ⇒ 装饰不参与地块拾取（拾取仍只认居住面）。
    /// - 旋转**不在这里做**：本层是 `WorldDrawLayer` 且 `planetLayer is RingSurfaceLayer`，
    ///   现有 `RingWorldMotion.DrawRotation` 的 Rotation 补丁会统一给网格套 `RotationAt(tick)`。
    /// - `Render()` 开头调用 `RingWorldVisualState.UpdatePerFrame()`（幂等），保证受光/光斑 uniform 每帧最新。
    /// - 材质缺失（shader 未注册）⇒ 相应类别跳过；两类都缺时本层自动变空层，不抛异常。
    /// </summary>
    public sealed class WorldDrawLayer_RingOrnaments : WorldDrawLayer
    {
        public override bool VisibleWhenLayerNotSelected => false;

        public override void Render()
        {
            RingWorldVisualState.UpdatePerFrame();
            base.Render();
        }

        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate()) yield return item;
            if (planetLayer is not RingSurfaceLayer ring) yield break;

            Material? metal = RingOrnamentMaterials.Metal;
            Material? sprite = RingOrnamentMaterials.Sprite;
            Material? terminal = RingOrnamentMaterials.TerminalGlow;
            Material? core = RingOrnamentMaterials.TerminalCore;
            if (metal == null && sprite == null && terminal == null) yield break;

            int count = 0;
            int coreFaces = 0, haloFaces = 0;
            foreach (RingOrnamentQuad quad in RingOrnamentGeometry.Build(ring.Geometry))
            {
                Material? material = quad.Material switch
                {
                    RingOrnamentMaterial.Metal => metal,
                    RingOrnamentMaterial.Glow => sprite,
                    RingOrnamentMaterial.TerminalGlow => terminal,
                    RingOrnamentMaterial.TerminalCore => core ?? metal,
                    _ => null,
                };
                if (quad.Material == RingOrnamentMaterial.TerminalCore) coreFaces++;
                if (quad.Material == RingOrnamentMaterial.TerminalGlow) haloFaces++;
                if (material != null)
                {
                    LayerSubMesh mesh = GetSubMesh(material);
                    int start = mesh.verts.Count;
                    AddVertex(mesh, ring.Geometry, quad, quad.A, quad.U0, quad.V0);
                    AddVertex(mesh, ring.Geometry, quad, quad.B, quad.U0, quad.V1);
                    AddVertex(mesh, ring.Geometry, quad, quad.C, quad.U1, quad.V1);
                    AddVertex(mesh, ring.Geometry, quad, quad.D, quad.U1, quad.V0);
                    mesh.tris.Add(start); mesh.tris.Add(start + 1); mesh.tris.Add(start + 2);
                    mesh.tris.Add(start); mesh.tris.Add(start + 2); mesh.tris.Add(start + 3);
                }
                if (++count % 256 == 0) yield return null;
            }

            // 只提交网格：本层没有碰撞、没有拾取映射（装饰件不可点选）。
            FinalizeMesh(MeshParts.All);
            Log.Message($"[PFL RingWorld] Ornament terminals: coreFaces={coreFaces}, haloFaces={haloFaces}, " +
                $"coreShader={core?.shader.name ?? "missing"}, haloShader={terminal?.shader.name ?? "missing"}, quads={count}.");
        }

        private static void AddVertex(LayerSubMesh mesh, RingGeometry geometry, RingOrnamentQuad quad,
            DVector3 position, double u, double v)
        {
            bool metal = quad.Material == RingOrnamentMaterial.Metal;
            if (metal) (u, v) = RingOrnamentGeometry.HullUV(geometry, quad, u, v);
            mesh.verts.Add((Vector3)position);
            mesh.normals.Add((Vector3)quad.Normal);
            mesh.uvs.Add(new Vector3((float)u, (float)v, 0f));
            mesh.colors.Add(new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt((metal ? .85f + .15f * (float)quad.Red : (float)quad.Red) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((metal ? .85f + .15f * (float)quad.Green : (float)quad.Green) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((metal ? .85f + .15f * (float)quad.Blue : (float)quad.Blue) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((float)quad.Alpha * 255f), 0, 255)));
        }
    }
}
