using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 3D 飞船装配器窗口。
    /// 从已保存的部件模板（ShipPartTemplate）中选择型号，
    /// 然后在 3D 空间中摆放，最终保存为飞船设计（Ship3D）。
    /// </summary>
    [StaticConstructorOnStartup]
    public class Window_ShipAssembler3D : Window
    {
        private readonly CompShipAssembler comp;

        // ── 3D 渲染 ──
        private Camera renderCamera;
        private RenderTexture renderTexture;
        private GameObject cameraObject;
        private GameObject sceneRoot;
        private readonly List<GameObject> partDisplayObjects = new List<GameObject>();
        private GameObject frameworkWireframeObj;
        private LineRenderer frameworkLineRenderer;
        private GameObject selectionHighlightObj;
        private LineRenderer selectionLineRenderer;

        // ── 相机控制 ──
        private Vector3 cameraTarget = Vector3.zero;
        private float cameraDistance = 8f;
        private float cameraYaw = 30f;
        private float cameraPitch = 25f;

        // ── 状态 ──
        private int selectedPartIndex = -1;
        private Vector2 partListScrollPos;

        // ── 模板选择 ──
        private ShipPartShape selectedShape = ShipPartShape.Cube;
        private string selectedTemplateName = "";
        private List<ShipPartTemplate> availableTemplates = new List<ShipPartTemplate>();
        private Vector2 templateListScrollPos;

        // ── 参数编辑 ──
        private string editPosX = "0", editPosY = "0", editPosZ = "0";
        private string editRotX = "0", editRotY = "0", editRotZ = "0";
        private string editSizeX = "", editSizeY = "", editSizeZ = "";

        // ── 蓝图管理 ──
        private string shipNameInput = "未命名飞船";
        private string saveDesignName = "";
        private Vector2 blueprintListScrollPos;
        private bool showLoadPanel = false;
        private bool showSavePanel = false;

        // ── 材质 ──
        private static Material cubeMat, cornerMat;
        private const int SceneLayer = 30;
        private int savedMainCameraCullingMask;

        private static readonly Vector3 FrameworkSize = new Vector3(6f, 4f, 8f);

        static Window_ShipAssembler3D()
        {
            cubeMat = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.6f, 1.0f) };
            cornerMat = new Material(Shader.Find("Standard")) { color = new Color(1.0f, 0.5f, 0.2f) };
        }

        public Window_ShipAssembler3D(CompShipAssembler comp)
        {
            this.comp = comp;
            doCloseButton = true;
            doCloseX = true;
            forcePause = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            resizeable = true;
            draggable = true;
            optionalTitle = "3D 飞船装配";
            shipNameInput = comp.CurrentShip.shipName ?? "未命名飞船";
            CreateRenderResources();
            RebuildScene();
        }

        public override Vector2 InitialSize => new Vector2(UI.screenWidth * 0.8f, UI.screenHeight * 0.8f);

        // ══════════════════════════════════════════════════
        //  渲染资源
        // ══════════════════════════════════════════════════

        private void CreateRenderResources()
        {
            if (Find.Camera != null)
            {
                savedMainCameraCullingMask = Find.Camera.cullingMask;
                Find.Camera.cullingMask &= ~(1 << SceneLayer);
            }

            cameraObject = new GameObject("ShipAssemblerCamera");
            renderCamera = cameraObject.AddComponent<Camera>();
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = Color.black;
            renderCamera.cullingMask = 1 << SceneLayer;
            renderCamera.orthographic = false;
            renderCamera.fieldOfView = 40f;
            renderCamera.nearClipPlane = 0.1f;
            renderCamera.farClipPlane = 100f;

            float w = Mathf.Max(1f, 1100f * 0.65f);
            float h = Mathf.Max(1f, 750f - 80f);
            renderTexture = new RenderTexture((int)w, (int)h, 24);
            renderCamera.targetTexture = renderTexture;

            sceneRoot = new GameObject("ShipAssemblerScene");
            sceneRoot.layer = SceneLayer;

            var lightObj = new GameObject("AssemblerLight");
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.8f;
            light.color = new Color(1f, 0.95f, 0.85f);
            light.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            lightObj.transform.SetParent(sceneRoot.transform);

            var light2 = new GameObject("AssemblerFill");
            var l2 = light2.AddComponent<Light>();
            l2.type = LightType.Directional;
            l2.intensity = 0.6f;
            l2.color = new Color(0.6f, 0.7f, 1f);
            l2.transform.rotation = Quaternion.Euler(-30f, -120f, 0f);
            light2.transform.SetParent(sceneRoot.transform);

            // 框架线框
            frameworkWireframeObj = new GameObject("FrameworkWireframe");
            frameworkWireframeObj.layer = SceneLayer;
            frameworkWireframeObj.transform.SetParent(sceneRoot.transform);
            frameworkLineRenderer = frameworkWireframeObj.AddComponent<LineRenderer>();
            var fMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            fMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            fMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            fMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            fMat.SetInt("_ZWrite", 0);
            frameworkLineRenderer.material = fMat;
            frameworkLineRenderer.startColor = new Color(0.3f, 0.8f, 0.3f, 0.4f);
            frameworkLineRenderer.endColor = new Color(0.3f, 0.8f, 0.3f, 0.4f);
            frameworkLineRenderer.startWidth = 0.05f;
            frameworkLineRenderer.endWidth = 0.05f;
            frameworkLineRenderer.loop = false;

            // 选中高亮
            selectionHighlightObj = new GameObject("SelectionHighlight");
            selectionHighlightObj.layer = SceneLayer;
            selectionHighlightObj.transform.SetParent(sceneRoot.transform);
            selectionLineRenderer = selectionHighlightObj.AddComponent<LineRenderer>();
            var sMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            sMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            sMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            sMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            sMat.SetInt("_ZWrite", 0);
            selectionLineRenderer.material = sMat;
            selectionLineRenderer.startColor = Color.yellow;
            selectionLineRenderer.endColor = Color.yellow;
            selectionLineRenderer.startWidth = 0.05f;
            selectionLineRenderer.endWidth = 0.05f;
            selectionLineRenderer.loop = false;
        }

        private void EnsureRenderTextureSize(float w, float h)
        {
            int tw = Mathf.Max(1, Mathf.RoundToInt(w));
            int th = Mathf.Max(1, Mathf.RoundToInt(h));
            if (renderTexture == null || renderTexture.width != tw || renderTexture.height != th)
            {
                var newRT = new RenderTexture(tw, th, 24);
                renderCamera.targetTexture = newRT;
                if (renderTexture != null) renderTexture.Release();
                renderTexture = newRT;
            }
        }

        private void RebuildScene()
        {
            foreach (var go in partDisplayObjects)
                UnityEngine.Object.Destroy(go);
            partDisplayObjects.Clear();

            var ship = comp.CurrentShip;
            for (int i = 0; i < ship.parts.Count; i++)
            {
                var p = ship.parts[i];
                var go = new GameObject("Part_" + i);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();

                mf.mesh = ShipPartMeshGenerator.GetMesh(p.ShapeType);
                mr.material = GetMaterialForShape(p.ShapeType);

                Vector3 bounds = p.EffectiveBounds;
                go.transform.localScale = bounds;
                go.transform.position = p.position;
                go.transform.rotation = p.rotation;
                go.transform.SetParent(sceneRoot.transform);
                go.layer = SceneLayer;

                partDisplayObjects.Add(go);
            }
        }

        private static Material GetMaterialForShape(ShipPartShape shape)
        {
            switch (shape)
            {
                case ShipPartShape.SphericalCorner: return cornerMat;
                default: return cubeMat;
            }
        }

        // ══════════════════════════════════════════════════
        //  相机控制
        // ══════════════════════════════════════════════════

        private void HandleCameraInput()
        {
            var e = Event.current;
            if (e.type == EventType.MouseDrag)
            {
                if (e.button == 0)
                {
                    cameraYaw += e.delta.x * 0.5f;
                    cameraPitch -= e.delta.y * 0.5f;
                    cameraPitch = Mathf.Clamp(cameraPitch, 2f, 88f);
                    e.Use();
                }
                else if (e.button == 1)
                {
                    var right = renderCamera.transform.right;
                    var up = renderCamera.transform.up;
                    float speed = cameraDistance * 0.008f;
                    cameraTarget += right * (-e.delta.x * speed) + up * (e.delta.y * speed);
                    e.Use();
                }
            }
            else if (e.type == EventType.ScrollWheel)
            {
                cameraDistance -= e.delta.y * 1.5f;
                cameraDistance = Mathf.Clamp(cameraDistance, 1f, 40f);
                e.Use();
            }
        }

        private void UpdateCameraTransform()
        {
            if (renderCamera == null) return;
            var rot = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
            var offset = rot * new Vector3(0f, 0f, -cameraDistance);
            renderCamera.transform.position = cameraTarget + offset;
            renderCamera.transform.LookAt(cameraTarget);
        }

        private void UpdateFrameworkWireframe()
        {
            if (frameworkLineRenderer == null) return;
            var hs = FrameworkSize * 0.5f;
            Vector3[] corners = {
                new(-hs.x, -hs.y, -hs.z), new( hs.x, -hs.y, -hs.z),
                new( hs.x,  hs.y, -hs.z), new(-hs.x,  hs.y, -hs.z),
                new(-hs.x, -hs.y,  hs.z), new( hs.x, -hs.y,  hs.z),
                new( hs.x,  hs.y,  hs.z), new(-hs.x,  hs.y,  hs.z),
            };
            int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            Vector3[] positions = new Vector3[edges.Length];
            for (int i = 0; i < edges.Length; i++)
                positions[i] = corners[edges[i]];
            frameworkLineRenderer.positionCount = positions.Length;
            frameworkLineRenderer.SetPositions(positions);
        }

        private void UpdateSelectionHighlight()
        {
            if (selectionLineRenderer == null) return;
            if (selectedPartIndex < 0 || selectedPartIndex >= partDisplayObjects.Count)
            {
                selectionLineRenderer.positionCount = 0;
                return;
            }
            var go = partDisplayObjects[selectedPartIndex];
            if (go == null) { selectionLineRenderer.positionCount = 0; return; }

            var e = go.transform.lossyScale * 0.5f;
            var c = go.transform.position;
            Vector3[] v = {
                c + new Vector3(-e.x, -e.y, -e.z), c + new Vector3( e.x, -e.y, -e.z),
                c + new Vector3( e.x,  e.y, -e.z), c + new Vector3(-e.x,  e.y, -e.z),
                c + new Vector3(-e.x, -e.y,  e.z), c + new Vector3( e.x, -e.y,  e.z),
                c + new Vector3( e.x,  e.y,  e.z), c + new Vector3(-e.x,  e.y,  e.z),
            };
            int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            Vector3[] positions = new Vector3[edges.Length];
            for (int i = 0; i < edges.Length; i++)
                positions[i] = v[edges[i]];
            selectionLineRenderer.positionCount = positions.Length;
            selectionLineRenderer.SetPositions(positions);
        }

        // ══════════════════════════════════════════════════
        //  窗口内容
        // ══════════════════════════════════════════════════

        public override void DoWindowContents(Rect inRect)
        {
            float rightWidth = Mathf.Max(260f, inRect.width * 0.35f);
            float leftWidth = inRect.width - rightWidth - 10f;
            float contentHeight = inRect.height - 40f;

            var viewRect = new Rect(inRect.x, inRect.y, leftWidth, contentHeight);
            var rightRect = new Rect(inRect.x + leftWidth + 10f, inRect.y, rightWidth, contentHeight);

            EnsureRenderTextureSize(viewRect.width, viewRect.height);

            if (viewRect.Contains(Event.current.mousePosition))
                HandleCameraInput();

            UpdateCameraTransform();
            UpdateFrameworkWireframe();
            UpdateSelectionHighlight();
            renderCamera.Render();

            if (renderTexture != null)
                GUI.DrawTexture(viewRect, renderTexture);
            else
                Widgets.Label(viewRect, "RenderTexture 未就绪");

            string hint = "LMB拖拽: 旋转 | 滚轮: 缩放 | RMB拖拽: 平移";
            Text.Anchor = TextAnchor.LowerLeft;
            Widgets.Label(new Rect(inRect.x, inRect.y + contentHeight + 2f, leftWidth, 18f), hint);
            Text.Anchor = TextAnchor.UpperLeft;

            DrawRightPanel(rightRect);
        }

        // ══════════════════════════════════════════════════
        //  右侧面板
        // ══════════════════════════════════════════════════

        private void DrawRightPanel(Rect rect)
        {
            var listing = new Listing_Standard();
            listing.Begin(rect);

            // ── 飞船名称 ──
            Text.Font = GameFont.Medium;
            listing.Label("飞船装配");
            Text.Font = GameFont.Small;
            Rect nameRow = listing.GetRect(26f);
            Widgets.Label(new Rect(nameRow.x, nameRow.y, 40f, 26f), "名称:");
            shipNameInput = Widgets.TextField(
                new Rect(nameRow.x + 42f, nameRow.y, nameRow.width - 48f, 26f), shipNameInput);
            comp.CurrentShip.shipName = shipNameInput;

            listing.GapLine(4f);

            // ── 蓝图管理 ──
            if (listing.ButtonText("储存设计")) { showSavePanel = !showSavePanel; showLoadPanel = false; if (showSavePanel) saveDesignName = shipNameInput; }
            if (listing.ButtonText("读取设计")) { showLoadPanel = !showLoadPanel; showSavePanel = false; }

            if (showSavePanel) DrawSavePanel(listing);
            if (showLoadPanel) DrawLoadPanel(listing);

            listing.GapLine(4f);

            // ── 部件类型选择 + 模板列表 ──
            DrawShapeAndTemplateSelector(listing);

            listing.GapLine(4f);

            // ── 位置/旋转/尺寸 ──
            DrawPlacementParams(listing);

            listing.Gap(4f);
            if (listing.ButtonText("添加部件"))
                ApplyAddPart();

            listing.GapLine(4f);

            // ── 部件列表 ──
            Text.Font = GameFont.Medium;
            listing.Label("已装配部件");
            Text.Font = GameFont.Small;
            float listH = Mathf.Min(rect.height * 0.2f, 120f);
            DrawPartList(listing.GetRect(listH));

            listing.GapLine(4f);

            // ── 操作 ──
            if (selectedPartIndex >= 0 && selectedPartIndex < comp.CurrentShip.parts.Count)
            {
                if (listing.ButtonText("更新选中部件")) ApplyUpdatePart();
                if (listing.ButtonText("删除选中部件"))
                {
                    comp.CurrentShip.parts.RemoveAt(selectedPartIndex);
                    selectedPartIndex = -1;
                    RebuildScene();
                }
            }
            if (listing.ButtonText("清空全部"))
            {
                comp.CurrentShip.parts.Clear();
                selectedPartIndex = -1;
                RebuildScene();
            }

            listing.End();
        }

        // ══════════════════════════════════════════════════
        //  形状 + 模板选择
        // ══════════════════════════════════════════════════

        private void DrawShapeAndTemplateSelector(Listing_Standard listing)
        {
            listing.Label("选择部件型号:");

            // 形状类型按钮（纯几何；功能角色由模板组件决定）
            Rect shapeRow = listing.GetRect(28f);
            string[] shapeNames = { "结构", "角球" };
            ShipPartShape[] shapeValues = { ShipPartShape.Cube, ShipPartShape.SphericalCorner };

            float btnW = (shapeRow.width - 4f) / 2f;
            for (int i = 0; i < 2; i++)
            {
                var sr = new Rect(shapeRow.x + i * (btnW + 4f), shapeRow.y, btnW, 28f);
                bool sel = selectedShape == shapeValues[i];
                if (sel) Widgets.DrawBoxSolid(sr, new Color(0.3f, 0.5f, 0.8f, 0.3f));
                if (Widgets.ButtonText(sr, shapeNames[i]))
                {
                    if (selectedShape != shapeValues[i])
                    {
                        selectedShape = shapeValues[i];
                        selectedTemplateName = "";
                        RefreshAvailableTemplates();
                    }
                }
            }

            listing.Gap(4f);

            // 模板列表
            RefreshAvailableTemplates();
            if (availableTemplates.Count == 0)
            {
                listing.Label("（该类型暂无已保存的模板，请先用部件设计器创建）");
            }
            else
            {
                listing.Label(string.Format("可用型号 ({0}):", availableTemplates.Count));
                float tListH = Mathf.Min(availableTemplates.Count * 26f, 130f);
                Rect tRect = listing.GetRect(tListH);
                float itemH = 26f;
                float totalH = availableTemplates.Count * itemH;
                var viewRect = new Rect(0f, 0f, tRect.width - 16f, totalH);
                Widgets.BeginScrollView(tRect, ref templateListScrollPos, viewRect);

                for (int i = 0; i < availableTemplates.Count; i++)
                {
                    var tmpl = availableTemplates[i];
                    var row = new Rect(0f, i * itemH, viewRect.width, itemH - 2f);
                    bool isSel = selectedTemplateName == tmpl.templateName;
                    if (isSel) Widgets.DrawBoxSolid(row, new Color(0.2f, 0.5f, 0.3f, 0.4f));

                    string info = string.Format("{0} ({1:F1}x{2:F1}x{3:F1})",
                        tmpl.templateName, tmpl.bounds.x, tmpl.bounds.y, tmpl.bounds.z);

                    if (Widgets.ButtonText(row, info))
                    {
                        selectedTemplateName = tmpl.templateName;
                        // 自动填入模板尺寸
                        editSizeX = tmpl.bounds.x.ToString("F1");
                        editSizeY = tmpl.bounds.y.ToString("F1");
                        editSizeZ = tmpl.bounds.z.ToString("F1");
                    }
                }
                Widgets.EndScrollView();
            }
        }

        private void RefreshAvailableTemplates()
        {
            if (availableTemplates.Count == 0 || availableTemplates[0].shapeType != selectedShape)
            {
                availableTemplates = ShipPartTemplate.GetTemplatesByShape(selectedShape);
            }
        }

        // ══════════════════════════════════════════════════
        //  位置/旋转/尺寸参数
        // ══════════════════════════════════════════════════

        private void DrawPlacementParams(Listing_Standard listing)
        {
            listing.Label("位置 (X Y Z):");
            Rect posRow = listing.GetRect(24f);
            float thirdW = (posRow.width - 8f) / 3f;
            editPosX = Widgets.TextField(new Rect(posRow.x, posRow.y, thirdW, 24f), editPosX);
            editPosY = Widgets.TextField(new Rect(posRow.x + thirdW + 4f, posRow.y, thirdW, 24f), editPosY);
            editPosZ = Widgets.TextField(new Rect(posRow.x + (thirdW + 4f) * 2f, posRow.y, thirdW, 24f), editPosZ);

            listing.Gap(2f);
            listing.Label("旋转 (俯仰 偏航 滚转):");
            Rect rotRow = listing.GetRect(24f);
            editRotX = Widgets.TextField(new Rect(rotRow.x, rotRow.y, thirdW, 24f), editRotX);
            editRotY = Widgets.TextField(new Rect(rotRow.x + thirdW + 4f, rotRow.y, thirdW, 24f), editRotY);
            editRotZ = Widgets.TextField(new Rect(rotRow.x + (thirdW + 4f) * 2f, rotRow.y, thirdW, 24f), editRotZ);

            listing.Gap(2f);
            listing.Label("尺寸覆盖 (留空=使用模板默认):");
            Rect sizeRow = listing.GetRect(24f);
            editSizeX = Widgets.TextField(new Rect(sizeRow.x, sizeRow.y, thirdW, 24f), editSizeX);
            editSizeY = Widgets.TextField(new Rect(sizeRow.x + thirdW + 4f, sizeRow.y, thirdW, 24f), editSizeY);
            editSizeZ = Widgets.TextField(new Rect(sizeRow.x + (thirdW + 4f) * 2f, sizeRow.y, thirdW, 24f), editSizeZ);
        }

        // ══════════════════════════════════════════════════
        //  部件列表
        // ══════════════════════════════════════════════════

        private void DrawPartList(Rect rect)
        {
            var ship = comp.CurrentShip;
            float lineH = 26f;
            float totalH = Mathf.Max(lineH, ship.parts.Count * lineH);
            var viewRect = new Rect(0f, 0f, rect.width - 16f, totalH);
            Widgets.BeginScrollView(rect, ref partListScrollPos, viewRect);

            if (ship.parts.Count == 0)
            {
                Widgets.Label(new Rect(4f, 0f, viewRect.width, lineH), "（尚无部件）");
            }
            else
            {
                for (int i = 0; i < ship.parts.Count; i++)
                {
                    var p = ship.parts[i];
                    var row = new Rect(0f, i * lineH, viewRect.width, lineH - 2f);
                    if (i == selectedPartIndex)
                        Widgets.DrawBoxSolid(row, new Color(0.3f, 0.3f, 0.5f, 0.5f));

                    string label = string.Format("[{0}] {1} ({2:F1},{3:F1},{4:F1})",
                        i, p.templateName, p.position.x, p.position.y, p.position.z);

                    if (Widgets.ButtonText(row, label))
                        SelectPart(i);
                }
            }
            Widgets.EndScrollView();
        }

        private void SelectPart(int index)
        {
            selectedPartIndex = index;
            var p = comp.CurrentShip.parts[index];

            // 解析模板以获取形状
            var tmpl = p.ResolveTemplate();
            if (tmpl != null)
            {
                selectedShape = tmpl.shapeType;
                selectedTemplateName = tmpl.templateName;
                RefreshAvailableTemplates();
            }

            editPosX = p.position.x.ToString("F2");
            editPosY = p.position.y.ToString("F2");
            editPosZ = p.position.z.ToString("F2");
            var euler = p.rotation.eulerAngles;
            editRotX = euler.x.ToString("F1");
            editRotY = euler.y.ToString("F1");
            editRotZ = euler.z.ToString("F1");

            if (p.customBounds != Vector3.zero)
            {
                editSizeX = p.customBounds.x.ToString("F2");
                editSizeY = p.customBounds.y.ToString("F2");
                editSizeZ = p.customBounds.z.ToString("F2");
            }
            else if (tmpl != null)
            {
                editSizeX = tmpl.bounds.x.ToString("F2");
                editSizeY = tmpl.bounds.y.ToString("F2");
                editSizeZ = tmpl.bounds.z.ToString("F2");
            }
        }

        // ══════════════════════════════════════════════════
        //  操作：添加/更新
        // ══════════════════════════════════════════════════

        private void ApplyAddPart()
        {
            if (string.IsNullOrWhiteSpace(selectedTemplateName))
            {
                Messages.Message("请先选择一个部件型号（模板）！", MessageTypeDefOf.RejectInput);
                return;
            }

            float px = ParseFloat(editPosX, 0f);
            float py = ParseFloat(editPosY, 0f);
            float pz = ParseFloat(editPosZ, 0f);
            float rx = ParseFloat(editRotX, 0f);
            float ry = ParseFloat(editRotY, 0f);
            float rz = ParseFloat(editRotZ, 0f);
            float sx = ParseFloat(editSizeX, -1f);
            float sy = ParseFloat(editSizeY, -1f);
            float sz = ParseFloat(editSizeZ, -1f);

            var part = new ShipPartInstance
            {
                templateName = selectedTemplateName,
                position = new Vector3(px, py, pz),
                rotation = Quaternion.Euler(rx, ry, rz),
                hp = 100f
            };

            if (sx > 0 && sy > 0 && sz > 0)
                part.customBounds = new Vector3(sx, sy, sz);

            comp.CurrentShip.parts.Add(part);
            selectedPartIndex = comp.CurrentShip.parts.Count - 1;
            RebuildScene();
        }

        private void ApplyUpdatePart()
        {
            if (selectedPartIndex < 0 || selectedPartIndex >= comp.CurrentShip.parts.Count)
                return;

            float px = ParseFloat(editPosX, 0f);
            float py = ParseFloat(editPosY, 0f);
            float pz = ParseFloat(editPosZ, 0f);
            float rx = ParseFloat(editRotX, 0f);
            float ry = ParseFloat(editRotY, 0f);
            float rz = ParseFloat(editRotZ, 0f);
            float sx = ParseFloat(editSizeX, -1f);
            float sy = ParseFloat(editSizeY, -1f);
            float sz = ParseFloat(editSizeZ, -1f);

            var p = comp.CurrentShip.parts[selectedPartIndex];
            p.position = new Vector3(px, py, pz);
            p.rotation = Quaternion.Euler(rx, ry, rz);
            p.templateName = selectedTemplateName;

            if (sx > 0 && sy > 0 && sz > 0)
                p.customBounds = new Vector3(sx, sy, sz);
            else
                p.customBounds = Vector3.zero;

            RebuildScene();
        }

        // ══════════════════════════════════════════════════
        //  蓝图面板
        // ══════════════════════════════════════════════════

        private void DrawSavePanel(Listing_Standard listing)
        {
            listing.Gap(2f);
            Widgets.Label(listing.GetRect(20f), "设计名称:");
            Rect row = listing.GetRect(26f);
            float btnW = 60f;
            saveDesignName = Widgets.TextField(
                new Rect(row.x, row.y, row.width - btnW - 4f, 26f), saveDesignName);
            if (Widgets.ButtonText(new Rect(row.x + row.width - btnW, row.y, btnW, 26f), "保存"))
            {
                if (!string.IsNullOrWhiteSpace(saveDesignName))
                {
                    try
                    {
                        comp.CurrentShip.shipName = shipNameInput;
                        comp.CurrentShip.SaveToFile(saveDesignName);
                        Messages.Message("设计 '" + saveDesignName + "' 已保存", MessageTypeDefOf.TaskCompletion);
                        showSavePanel = false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[ShipAssembler] 保存失败: " + ex.Message);
                        Messages.Message("保存失败: " + ex.Message, MessageTypeDefOf.RejectInput);
                    }
                }
            }
        }

        private void DrawLoadPanel(Listing_Standard listing)
        {
            listing.Gap(2f);
            var blueprints = Ship3D.GetSavedBlueprints();
            if (blueprints.Count == 0)
            {
                listing.Label("（暂无已保存的设计）");
                return;
            }

            float listH = Mathf.Min(blueprints.Count * 30f, 150f);
            Rect loadRect = listing.GetRect(listH);
            float itemH = 28f;
            float totalH = blueprints.Count * itemH;
            var viewRect = new Rect(0f, 0f, loadRect.width - 16f, totalH);
            Widgets.BeginScrollView(loadRect, ref blueprintListScrollPos, viewRect);

            for (int i = 0; i < blueprints.Count; i++)
            {
                var row = new Rect(0f, i * itemH, viewRect.width, itemH - 2f);
                float delBtnW = 50f;
                float loadBtnW = 50f;
                float nameW = row.width - delBtnW - loadBtnW - 8f;

                Widgets.Label(new Rect(row.x, row.y, nameW, itemH), blueprints[i]);

                if (Widgets.ButtonText(new Rect(row.x + nameW + 4f, row.y, loadBtnW, itemH), "读取"))
                {
                    try
                    {
                        var loaded = Ship3D.LoadFromFile(blueprints[i]);
                        if (loaded != null)
                        {
                            comp.CurrentShip.parts.Clear();
                            comp.CurrentShip.parts.AddRange(loaded.parts);
                            comp.CurrentShip.shipName = loaded.shipName;
                            shipNameInput = loaded.shipName ?? "未命名飞船";
                            selectedPartIndex = -1;
                            showLoadPanel = false;
                            RebuildScene();
                            Messages.Message("设计 '" + blueprints[i] + "' 读取成功", MessageTypeDefOf.TaskCompletion);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[ShipAssembler] 读取失败: " + ex.Message);
                        Messages.Message("读取失败: " + ex.Message, MessageTypeDefOf.RejectInput);
                    }
                }

                if (Widgets.ButtonText(new Rect(row.x + nameW + loadBtnW + 8f, row.y, delBtnW, itemH), "删除"))
                {
                    try
                    {
                        Ship3D.DeleteFile(blueprints[i]);
                        Messages.Message("设计 '" + blueprints[i] + "' 已删除", MessageTypeDefOf.TaskCompletion);
                    }
                    catch (Exception ex) { Messages.Message("删除失败: " + ex.Message, MessageTypeDefOf.RejectInput); }
                }
            }
            Widgets.EndScrollView();
        }

        // ══════════════════════════════════════════════════
        //  工具
        // ══════════════════════════════════════════════════

        private static float ParseFloat(string s, float fallback)
        {
            if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
                return v;
            return fallback;
        }

        // ══════════════════════════════════════════════════
        //  清理
        // ══════════════════════════════════════════════════

        public override void PostClose()
        {
            base.PostClose();
            if (Find.Camera != null && savedMainCameraCullingMask != 0)
                Find.Camera.cullingMask = savedMainCameraCullingMask;
            if (renderTexture != null) { renderTexture.Release(); renderTexture = null; }
            if (cameraObject != null) { UnityEngine.Object.Destroy(cameraObject); cameraObject = null; }
            if (sceneRoot != null) { UnityEngine.Object.Destroy(sceneRoot); sceneRoot = null; }
            partDisplayObjects.Clear();
            frameworkWireframeObj = null;
            frameworkLineRenderer = null;
            selectionHighlightObj = null;
            selectionLineRenderer = null;
        }
    }
}
