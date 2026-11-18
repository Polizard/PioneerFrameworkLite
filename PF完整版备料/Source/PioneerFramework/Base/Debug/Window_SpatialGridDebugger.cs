using UnityEngine;
using Verse;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework
{
    public class Window_SpatialGridDebugger : Window
    {
        private SpaceGameComp spaceGame;
        private Camera renderCamera;
        private RenderTexture renderTexture;
        private GameObject cameraObject;
        private Vector3 cameraTargetUnity = Vector3.zero;
        private float cameraDistanceUnity = 150f;
        private float cameraYaw = 45f;
        private float cameraPitch = 30f;
        private Dictionary<(int cx, int cy, int cz), int> nonEmptyCells;
        private bool showOriginCell = true;

        // 右侧列表滚动位置
        private Vector2 cellListScrollPos = Vector2.zero;

        // 缓存窗口大小，用于重建 RenderTexture
        private Rect lastWindowRect;

        private static readonly double CELL_SIZE_UNITY = SpaceConstants.CellSizeMeters * SpaceConstants.UnitsPerMeter;

        public Window_SpatialGridDebugger(SpaceGameComp comp)
        {
            spaceGame = comp;
            doCloseButton = true;
            doCloseX = true;
            forcePause = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            resizeable = true;          // 允许拖动改变窗口大小
            draggable = true;           // 允许拖动窗口
            windowRect = new Rect(100f, 100f, 1000f, 700f); // 稍宽一些以容纳右侧列表
            optionalTitle = "3D Spatial Grid Debugger (1 unit = 50 km)";
            CreateRenderResources();
            lastWindowRect = windowRect;
        }

        private void CreateRenderResources()
        {
            cameraObject = new GameObject("SpatialGridDebugCamera");
            renderCamera = cameraObject.AddComponent<Camera>();
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = Color.black;
            renderCamera.cullingMask = 0;
            renderCamera.orthographic = false;
            renderCamera.fieldOfView = 60f;
            renderCamera.nearClipPlane = 0.1f;
            renderCamera.farClipPlane = 2000f;
            // 初始分辨率，之后会随窗口大小调整
            int width = Mathf.Max(1, Mathf.RoundToInt(windowRect.width - 260f));
            int height = Mathf.Max(1, Mathf.RoundToInt(windowRect.height - 80f));
            renderTexture = new RenderTexture(width, height, 24);
            renderCamera.targetTexture = renderTexture;
        }

        private void EnsureRenderTextureSize(Rect currentRect)
        {
            int targetWidth = Mathf.Max(1, Mathf.RoundToInt(currentRect.width - 260f));
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt(currentRect.height - 80f));
            if (renderTexture != null && (renderTexture.width != targetWidth || renderTexture.height != targetHeight))
            {
                // 重新创建合适大小的 RenderTexture
                var newRT = new RenderTexture(targetWidth, targetHeight, 24);
                renderCamera.targetTexture = newRT;
                renderTexture.Release();
                renderTexture = newRT;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 如果窗口大小改变，调整 RenderTexture
            if (windowRect != lastWindowRect)
            {
                EnsureRenderTextureSize(inRect);
                lastWindowRect = windowRect;
            }

            // 每帧直接从网格获取最新区块数据
            if (spaceGame != null)
            {
                nonEmptyCells = new Dictionary<(int, int, int), int>();
                var planetGrids = spaceGame.GetPlanetGrids();
                if (planetGrids != null)
                {
                    foreach (var kv in planetGrids)
                    {
                        kv.Value.ApplyChanges();
                        foreach (var cellKv in kv.Value.GetAllNonEmptyCells())
                        {
                            if (nonEmptyCells.ContainsKey(cellKv.Key))
                                nonEmptyCells[cellKv.Key] += cellKv.Value.Count;
                            else
                                nonEmptyCells[cellKv.Key] = cellKv.Value.Count;
                        }
                    }
                }
            }

            // 布局：左侧 3D 视图，右侧区块列表
            float leftWidth = inRect.width - 260f;
            Rect viewRect = new Rect(inRect.x, inRect.y, leftWidth, inRect.height - 40f);
            Rect listRect = new Rect(inRect.x + leftWidth + 5f, inRect.y, 250f, inRect.height - 40f);
            Rect buttonRect = new Rect(inRect.x, inRect.y + viewRect.height, inRect.width, 30f);

            // 显示信息（在视图上方）
            string infoText = "";
            var firstGroup = spaceGame?.GetAllGroups().FirstOrDefault();
            if (firstGroup != null)
            {
                infoText = string.Format("Group {0}: pos=({1:F0},{2:F0},{3:F0})m vel=({4:F1},{5:F1},{6:F1})m/s",
                    firstGroup.GroupId,
                    firstGroup.CurrentOrbit.position.x, firstGroup.CurrentOrbit.position.y, firstGroup.CurrentOrbit.position.z,
                    firstGroup.CurrentOrbit.velocity.x, firstGroup.CurrentOrbit.velocity.y, firstGroup.CurrentOrbit.velocity.z);
                var firstEntityId = firstGroup.GetEntityIds().FirstOrDefault();
                var entity = spaceGame.GetEntityById(firstEntityId);
                if (entity != null)
                {
                    var group = spaceGame.GetGroupById(entity.GroupId);
                    if (group != null)
                    {
                        var cell = group.LastCellCoord;
                        infoText += string.Format(" | Group cell: ({0},{1},{2})", cell.cx, cell.cy, cell.cz);
                    }
                }
            }
            else
            {
                var firstEntity = spaceGame?.GetAllEntities().FirstOrDefault();
                if (firstEntity != null)
                {
                    infoText = string.Format("Entity {0}: pos=({1:F0},{2:F0},{3:F0})m vel=({4:F1},{5:F1},{6:F1})m/s",
                        firstEntity.EntityId,
                        firstEntity.Position.x, firstEntity.Position.y, firstEntity.Position.z,
                        firstEntity.Velocity.x, firstEntity.Velocity.y, firstEntity.Velocity.z);
                    var group = spaceGame.GetGroupById(firstEntity.GroupId);
                    if (group != null)
                    {
                        var cell = group.LastCellCoord;
                        infoText += string.Format(" | Group cell: ({0},{1},{2})", cell.cx, cell.cy, cell.cz);
                    }
                }
            }
            int gridCells = 0;
            var grids = spaceGame?.GetPlanetGrids();
            if (grids != null)
                foreach (var pg in grids.Values)
                    gridCells += pg.CellCount;
            infoText += string.Format(" | Grid cells: {0} | Displayed cells: {1}", gridCells, nonEmptyCells?.Count ?? 0);

            Rect infoRect = new Rect(inRect.x, inRect.y, leftWidth, 40f);
            Widgets.Label(infoRect, infoText);

            // 调整视图区域（避开信息栏）
            viewRect.yMin += 40f;
            listRect.yMin += 40f;

            // 相机控制
            if (viewRect.Contains(Event.current.mousePosition))
                HandleCameraInput();
            UpdateCameraTransform();

            // 渲染 3D 视图
            if (renderCamera != null && renderTexture != null)
            {
                RenderCameraWithGrid();
                GUI.DrawTexture(viewRect, renderTexture);
            }
            else
                Widgets.Label(viewRect, "Camera not ready");

            // 右侧区块列表
            DrawCellList(listRect);

            // 底部按钮
            if (Widgets.ButtonText(new Rect(buttonRect.x, buttonRect.y, 100f, 30f), "Reset Camera"))
            {
                cameraTargetUnity = Vector3.zero;
                cameraDistanceUnity = 150f;
                cameraYaw = 45f;
                cameraPitch = 30f;
            }
            Rect toggleRect = new Rect(buttonRect.x + 120f, buttonRect.y, 150f, 30f);
            Widgets.CheckboxLabeled(toggleRect, "Show origin cell", ref showOriginCell);
        }

        private void DrawCellList(Rect rect)
        {
            if (nonEmptyCells == null || nonEmptyCells.Count == 0)
            {
                Widgets.Label(rect, "No non-empty cells.");
                return;
            }

            // 按区块坐标排序显示
            var sorted = nonEmptyCells.OrderBy(kv => kv.Key.cx).ThenBy(kv => kv.Key.cy).ThenBy(kv => kv.Key.cz).ToList();

            float lineHeight = 20f;
            float totalHeight = sorted.Count * lineHeight;
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, totalHeight);
            Widgets.BeginScrollView(rect, ref cellListScrollPos, viewRect);

            float y = 0f;
            foreach (var kv in sorted)
            {
                var cell = kv.Key;
                int count = kv.Value;
                string label = string.Format("({0},{1},{2}) : {3}", cell.cx, cell.cy, cell.cz, count);
                Rect labelRect = new Rect(0f, y, viewRect.width, lineHeight);
                Widgets.Label(labelRect, label);
                y += lineHeight;
            }
            Widgets.EndScrollView();
        }

        private void HandleCameraInput()
        {
            Event e = Event.current;
            if (e.type == EventType.MouseDrag)
            {
                if (e.button == 0)
                {
                    cameraYaw += e.delta.x * 0.5f;
                    cameraPitch -= e.delta.y * 0.5f;
                    cameraPitch = Mathf.Clamp(cameraPitch, 5f, 85f);
                    e.Use();
                }
                else if (e.button == 1)
                {
                    Vector3 right = renderCamera.transform.right;
                    Vector3 up = renderCamera.transform.up;
                    float moveSpeed = cameraDistanceUnity * 0.01f;
                    cameraTargetUnity += right * (-e.delta.x * moveSpeed) + up * (e.delta.y * moveSpeed);
                    e.Use();
                }
            }
            else if (e.type == EventType.ScrollWheel)
            {
                cameraDistanceUnity -= e.delta.y * 5f;
                cameraDistanceUnity = Mathf.Clamp(cameraDistanceUnity, 30f, 800f);
                e.Use();
            }
        }

        private void UpdateCameraTransform()
        {
            if (renderCamera == null) return;
            Quaternion rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0);
            Vector3 offset = rotation * new Vector3(0, 0, -cameraDistanceUnity);
            renderCamera.transform.position = cameraTargetUnity + offset;
            renderCamera.transform.LookAt(cameraTargetUnity);
        }

        private void RenderCameraWithGrid()
        {
            Camera.onPostRender -= OnPostRenderDrawGrid;
            if (nonEmptyCells != null || showOriginCell)
                Camera.onPostRender += OnPostRenderDrawGrid;
            renderCamera.Render();
        }

        private void OnPostRenderDrawGrid(Camera cam)
        {
            if (cam != renderCamera) return;

            Material lineMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            if (lineMat == null)
            {
                lineMat = new Material(Shader.Find("Unlit/Color"));
                lineMat.color = Color.green;
            }
            lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMat.SetInt("_ZWrite", 0);
            lineMat.SetPass(0);

            GL.PushMatrix();
            GL.Begin(GL.LINES);

            if (nonEmptyCells != null)
            {
                GL.Color(Color.green);
                foreach (var kv in nonEmptyCells)
                    DrawCellCubeUnity(kv.Key);
            }

            if (showOriginCell)
            {
                GL.Color(Color.red);
                DrawCellCubeUnity((0, 0, 0));
            }

            GL.End();
            GL.PopMatrix();
        }

        private void DrawCellCubeUnity((int cx, int cy, int cz) cellCoord)
        {
            DVector3 centerMeters = new DVector3(cellCoord.cx * SpaceConstants.CellSizeMeters,
                                                cellCoord.cy * SpaceConstants.CellSizeMeters,
                                                cellCoord.cz * SpaceConstants.CellSizeMeters);
            Vector3 centerUnity = (Vector3)(centerMeters * SpaceConstants.UnitsPerMeter);
            float halfSizeUnity = (float)(CELL_SIZE_UNITY * 0.5f);
            Vector3 min = centerUnity - new Vector3(halfSizeUnity, halfSizeUnity, halfSizeUnity);
            Vector3 max = centerUnity + new Vector3(halfSizeUnity, halfSizeUnity, halfSizeUnity);

            DrawLine(new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z));
            DrawLine(new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z));
            DrawLine(new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z));
            DrawLine(new Vector3(min.x, min.y, max.z), new Vector3(min.x, min.y, min.z));
            DrawLine(new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z));
            DrawLine(new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z));
            DrawLine(new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z));
            DrawLine(new Vector3(min.x, max.y, max.z), new Vector3(min.x, max.y, min.z));
            DrawLine(new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z));
            DrawLine(new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z));
            DrawLine(new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z));
            DrawLine(new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z));
        }

        private void DrawLine(Vector3 start, Vector3 end)
        {
            GL.Vertex(start);
            GL.Vertex(end);
        }

        public override void PostClose()
        {
            base.PostClose();
            Camera.onPostRender -= OnPostRenderDrawGrid;
            if (renderTexture != null) renderTexture.Release();
            if (cameraObject != null) Object.Destroy(cameraObject);
        }
    }
}