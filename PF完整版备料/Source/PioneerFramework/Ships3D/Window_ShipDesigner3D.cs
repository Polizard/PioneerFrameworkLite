using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Components;

namespace PioneerFramework
{
    [StaticConstructorOnStartup]
    public class Window_ShipDesigner3D : Window
    {
        // ── 外部引用 ──
        private readonly CompShipDesigner comp;

        // ── 3D 渲染资源 ──
        private Camera renderCamera;
        private RenderTexture renderTexture;
        private GameObject cameraObject;
        private GameObject sceneRoot;

        // ── 相机控制 ──
        private Vector3 cameraTarget = Vector3.zero;
        private float cameraDistance = 8f;
        private float cameraYaw = 30f;
        private float cameraPitch = 25f;

        // ── 部件列表 UI ──
        private Vector2 partListScrollPos;
        private int selectedPartIndex = -1;

        // ── 参数输入 ──
        private string newShapeType = "Cube";
        private string editPosX = "0", editPosY = "0", editPosZ = "0";
        private string editRotX = "0", editRotY = "0", editRotZ = "0";
        private string editSizeX = "", editSizeY = "", editSizeZ = "";

        /// <summary>新增部件时选择的模板名（空 = 使用同名 Def 预置组件）。</summary>
        private string selectedTemplateName = "";
        private Vector2 templateListScrollPos;

        private bool isNewPartMode = true;

        // ── 蓝图管理 ──
        private string shipNameInput = "未命名飞船";
        private string saveDesignName = "";
        private Vector2 blueprintListScrollPos;
        private bool showLoadPanel = false;
        private bool showSavePanel = false;

        // ── 3D 场景对象 ──
        private readonly List<GameObject> partDisplayObjects = new List<GameObject>();
        private GameObject frameworkWireframeObj;
        private LineRenderer frameworkLineRenderer;
        private GameObject selectionHighlightObj;
        private LineRenderer selectionLineRenderer;

        // ── 材质（静态初始化，纯几何）──
        private static Material cubeMat;
        private static Material cornerMat;

        private const int SceneLayer = 30;
        private int savedMainCameraCullingMask;

        static Window_ShipDesigner3D()
        {
            cubeMat = new Material(Shader.Find("Standard"));
            cubeMat.color = new Color(0.3f, 0.6f, 1.0f);

            cornerMat = new Material(Shader.Find("Standard"));
            cornerMat.color = new Color(1.0f, 0.5f, 0.2f);
        }

        private static readonly Vector3 FrameworkSize = new Vector3(6f, 4f, 8f);

        public Window_ShipDesigner3D(CompShipDesigner comp)
        {
            this.comp = comp;
            doCloseButton = true;
            doCloseX = true;
            forcePause = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            resizeable = true;
            draggable = true;
            optionalTitle = "3D 飞船搭建";
            shipNameInput = comp.CurrentShip.shipName ?? "未命名飞船";
            CreateRenderResources();
            RebuildScene();
        }

        public override Vector2 InitialSize => new Vector2(UI.screenWidth * 0.8f, UI.screenHeight * 0.8f);

        // ══════════════════════════════════════════════════
        //  渲染资源管理
        // ══════════════════════════════════════════════════

        private void CreateRenderResources()
        {
            if (Find.Camera != null)
            {
                savedMainCameraCullingMask = Find.Camera.cullingMask;
                Find.Camera.cullingMask &= ~(1 << SceneLayer);
            }

            cameraObject = new GameObject("ShipDesignerCamera");
            renderCamera = cameraObject.AddComponent<Camera>();
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = Color.black;
            renderCamera.cullingMask = 1 << SceneLayer;
            renderCamera.orthographic = false;
            renderCamera.fieldOfView = 40f;
            renderCamera.nearClipPlane = 0.1f;
            renderCamera.farClipPlane = 100f;

            float initW = Mathf.Max(1f, 1100f * 0.65f);
            float initH = Mathf.Max(1f, 750f - 80f);
            renderTexture = new RenderTexture((int)initW, (int)initH, 24);
            renderCamera.targetTexture = renderTexture;

            sceneRoot = new GameObject("ShipDesignerScene");
            sceneRoot.layer = SceneLayer;

            var lightObj = new GameObject("ShipDesignerLight");
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.8f;
            light.color = new Color(1f, 0.95f, 0.85f);
            light.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            lightObj.transform.SetParent(sceneRoot.transform);

            var light2 = new GameObject("ShipDesignerLightFill");
            var l2 = light2.AddComponent<Light>();
            l2.type = LightType.Directional;
            l2.intensity = 0.6f;
            l2.color = new Color(0.6f, 0.7f, 1f);
            l2.transform.rotation = Quaternion.Euler(-30f, -120f, 0f);
            light2.transform.SetParent(sceneRoot.transform);

            InitMaterialsInstance();

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

        private void InitMaterialsInstance()
        {
            if (cubeMat == null) { cubeMat = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.6f, 1.0f) }; }
            if (cornerMat == null) { cornerMat = new Material(Shader.Find("Standard")) { color = new Color(1.0f, 0.5f, 0.2f) }; }
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

        private Material GetMaterialForShape(ShipPartShape shape)
        {
            switch (shape)
            {
                case ShipPartShape.SphericalCorner: return cornerMat;
                default: return cubeMat;
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

        private void UpdateCameraTransform()
        {
            if (renderCamera == null) return;
            var rot = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
            var offset = rot * new Vector3(0f, 0f, -cameraDistance);
            renderCamera.transform.position = cameraTarget + offset;
            renderCamera.transform.LookAt(cameraTarget);
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

            if (renderTexture == null ||
                renderTexture.width != Mathf.RoundToInt(viewRect.width) ||
                renderTexture.height != Mathf.RoundToInt(viewRect.height))
            {
                EnsureRenderTextureSize(viewRect.width, viewRect.height);
            }

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

            string hint = "LMB拖拽: 旋转视角 | 滚轮: 缩放 | RMB拖拽: 平移";
            Text.Anchor = TextAnchor.LowerLeft;
            Widgets.Label(new Rect(inRect.x, inRect.y + contentHeight + 2f, leftWidth, 18f), hint);
            Text.Anchor = TextAnchor.UpperLeft;

            DrawRightPanel(rightRect);
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
            int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4 };
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
        //  右侧面板
        // ══════════════════════════════════════════════════

        private void DrawRightPanel(Rect rect)
        {
            var listing = new Listing_Standard();
            listing.Begin(rect);

            // ── 飞船名称 ──
            Text.Font = GameFont.Medium;
            listing.Label("飞船设计");
            Text.Font = GameFont.Small;
            Rect nameRow = listing.GetRect(26f);
            float halfW = (nameRow.width - 4f) / 2f;
            Widgets.Label(new Rect(nameRow.x, nameRow.y, 40f, 26f), "名称:");
            shipNameInput = Widgets.TextField(new Rect(nameRow.x + 42f, nameRow.y, halfW - 42f, 26f), shipNameInput);
            comp.CurrentShip.shipName = shipNameInput;

            listing.GapLine(4f);

            // ── 蓝图管理按钮 ──
            if (listing.ButtonText("储存设计"))
            {
                showSavePanel = !showSavePanel;
                showLoadPanel = false;
                if (showSavePanel) saveDesignName = shipNameInput;
            }
            if (listing.ButtonText("读取设计"))
            {
                showLoadPanel = !showLoadPanel;
                showSavePanel = false;
            }

            // ── 保存面板 ──
            if (showSavePanel)
            {
                DrawSavePanel(listing);
            }

            // ── 读取面板 ──
            if (showLoadPanel)
            {
                DrawLoadPanel(listing);
            }

            listing.GapLine(6f);

            // ── 部件列表 ──
            Text.Font = GameFont.Medium;
            listing.Label("部件列表");
            Text.Font = GameFont.Small;
            float listHeight = Mathf.Min(rect.height * 0.25f, 150f);
            var listRect = listing.GetRect(listHeight);
            DrawPartList(listRect);

            listing.GapLine(6f);

            // ── 选中部件参数 ──
            listing.Label("部件参数");
            DrawPartParams(listing);

            listing.GapLine(6f);

            // ── 操作按钮 ──
            DrawActionButtons(listing);

            listing.End();
        }

        // ══════════════════════════════════════════════════
        //  蓝图保存/读取/删除面板
        // ══════════════════════════════════════════════════

        private void DrawSavePanel(Listing_Standard listing)
        {
            listing.Gap(2f);
            Widgets.Label(listing.GetRect(20f), "设计名称:");
            Rect saveRow = listing.GetRect(26f);
            float btnW = 60f;
            saveDesignName = Widgets.TextField(
                new Rect(saveRow.x, saveRow.y, saveRow.width - btnW - 4f, 26f), saveDesignName);
            if (Widgets.ButtonText(new Rect(saveRow.x + saveRow.width - btnW, saveRow.y, btnW, 26f), "保存"))
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
                        Log.Error("[ShipDesigner] 保存失败: " + ex.Message);
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

                // 名称标签
                Widgets.Label(new Rect(row.x, row.y, nameW, itemH), blueprints[i]);

                // 读取按钮
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
                            isNewPartMode = true;
                            showLoadPanel = false;
                            RebuildScene();
                            Messages.Message("设计 '" + blueprints[i] + "' 读取成功", MessageTypeDefOf.TaskCompletion);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[ShipDesigner] 读取失败: " + ex.Message);
                        Messages.Message("读取失败: " + ex.Message, MessageTypeDefOf.RejectInput);
                    }
                }

                // 删除按钮
                if (Widgets.ButtonText(new Rect(row.x + nameW + loadBtnW + 8f, row.y, delBtnW, itemH), "删除"))
                {
                    try
                    {
                        Ship3D.DeleteFile(blueprints[i]);
                        Messages.Message("设计 '" + blueprints[i] + "' 已删除", MessageTypeDefOf.TaskCompletion);
                    }
                    catch (Exception ex)
                    {
                        Messages.Message("删除失败: " + ex.Message, MessageTypeDefOf.RejectInput);
                    }
                }
            }
            Widgets.EndScrollView();
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

                    string typeLabel = GetShapeLabel(p.ShapeType);
                    string extraInfo = GetPartExtraInfo(p);
                    string label = string.Format("[{0}] {1} {2} ({3:F1},{4:F1},{5:F1})",
                        i, typeLabel, extraInfo, p.position.x, p.position.y, p.position.z);

                    if (Widgets.ButtonText(row, label))
                    {
                        SelectPart(i);
                    }
                }
            }
            Widgets.EndScrollView();
        }

        private void SelectPart(int index)
        {
            selectedPartIndex = index;
            isNewPartMode = false;
            var p = comp.CurrentShip.parts[index];
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
            else
            {
                var tmpl = p.ResolveTemplate();
                if (tmpl != null)
                {
                    editSizeX = tmpl.bounds.x.ToString("F2");
                    editSizeY = tmpl.bounds.y.ToString("F2");
                    editSizeZ = tmpl.bounds.z.ToString("F2");
                }
                else
                {
                    editSizeX = "";
                    editSizeY = "";
                    editSizeZ = "";
                }
            }
        }

        private static string GetShapeLabel(ShipPartShape shape)
        {
            switch (shape)
            {
                case ShipPartShape.SphericalCorner: return "角球";
                default: return "结构";
            }
        }

        private static string GetPartExtraInfo(ShipPartInstance p)
        {
            var comps = p.Components;
            if (comps != null && comps.Count > 0)
            {
                var parts = new List<string>(comps.Count);
                for (int i = 0; i < comps.Count; i++)
                    parts.Add(GetComponentLabel(comps[i]));
                return string.Join("+", parts.ToArray());
            }

            Vector3 b = p.EffectiveBounds;
            return string.Format("{0:F1}x{1:F1}x{2:F1}", b.x, b.y, b.z);
        }

        private static string GetComponentLabel(PartComponentData c)
        {
            if (c is EngineComponentData) return "引擎";
            if (c is PropellantTankComponentData) return "工质罐";
            if (c is CargoComponentData) return "货舱";
            if (c is WeaponMountComponentData) return "武器";
            if (c is RoomComponentData) return "舱室";
            return c.TypeId;
        }

        // ══════════════════════════════════════════════════
        //  部件参数面板（含部件设计器）
        // ══════════════════════════════════════════════════

        private void DrawPartParams(Listing_Standard listing)
        {
            if (!isNewPartMode && (selectedPartIndex < 0 || selectedPartIndex >= comp.CurrentShip.parts.Count))
            {
                listing.Label("（未选中部件）");
                return;
            }

            // ── 部件类型选择（仅新增模式）──
            if (isNewPartMode)
            {
                DrawShapeTypeSelector(listing);
            }
            else
            {
                var p = comp.CurrentShip.parts[selectedPartIndex];
                listing.Label("类型: " + GetShapeLabel(p.ShapeType));
            }

            ShipPartShape currentShape = GetCurrentShape();

            // ── 尺寸输入（长宽高）──
            listing.Gap(2f);
            listing.Label("尺寸 长(X) 宽(Y) 高(Z):");
            Rect sizeRow = listing.GetRect(24f);
            float thirdW = (sizeRow.width - 8f) / 3f;
            editSizeX = Widgets.TextField(new Rect(sizeRow.x, sizeRow.y, thirdW, 24f), editSizeX);
            editSizeY = Widgets.TextField(new Rect(sizeRow.x + thirdW + 4f, sizeRow.y, thirdW, 24f), editSizeY);
            editSizeZ = Widgets.TextField(new Rect(sizeRow.x + (thirdW + 4f) * 2f, sizeRow.y, thirdW, 24f), editSizeZ);

            // ── 位置输入 ──
            listing.Gap(2f);
            listing.Label("位置 (X Y Z):");
            Rect posRow = listing.GetRect(24f);
            editPosX = Widgets.TextField(new Rect(posRow.x, posRow.y, thirdW, 24f), editPosX);
            editPosY = Widgets.TextField(new Rect(posRow.x + thirdW + 4f, posRow.y, thirdW, 24f), editPosY);
            editPosZ = Widgets.TextField(new Rect(posRow.x + (thirdW + 4f) * 2f, posRow.y, thirdW, 24f), editPosZ);

            // ── 旋转输入 ──
            listing.Gap(2f);
            listing.Label("旋转 (俯仰 偏航 滚转):");
            Rect rotRow = listing.GetRect(24f);
            editRotX = Widgets.TextField(new Rect(rotRow.x, rotRow.y, thirdW, 24f), editRotX);
            editRotY = Widgets.TextField(new Rect(rotRow.x + thirdW + 4f, rotRow.y, thirdW, 24f), editRotY);
            editRotZ = Widgets.TextField(new Rect(rotRow.x + (thirdW + 4f) * 2f, rotRow.y, thirdW, 24f), editRotZ);

            // ── 应用按钮 ──
            listing.Gap(4f);
            if (listing.ButtonText("应用参数"))
            {
                ApplyParamsToSelected();
            }
        }

        private void DrawShapeTypeSelector(Listing_Standard listing)
        {
            listing.Label("新增部件: 几何形状 + 模板");
            listing.Gap(2f);

            // 几何形状
            string[] shapeNames = { "结构", "角球" };
            string[] shapeKeys = { "Cube", "SphericalCorner" };
            Rect row = listing.GetRect(28f);
            float btnW = (row.width - 8f) / 2f;
            for (int i = 0; i < shapeNames.Length; i++)
            {
                var r = new Rect(row.x + i * (btnW + 8f), row.y, btnW, 28f);
                bool sel = newShapeType == shapeKeys[i];
                if (sel) Widgets.DrawBoxSolid(r, new Color(0.3f, 0.5f, 0.8f, 0.3f));
                if (Widgets.ButtonText(r, shapeNames[i]))
                {
                    newShapeType = shapeKeys[i];
                }
            }

            listing.Gap(4f);
            listing.Label("模板（空 = 用 Def 预置组件）:");

            var templates = ShipPartTemplate.GetSavedTemplates();
            float listH = Mathf.Min(templates.Count * 26f, 150f);
            Rect loadRect = listing.GetRect(listH);
            var viewRect = new Rect(0f, 0f, loadRect.width - 16f, templates.Count * 24f);
            Widgets.BeginScrollView(loadRect, ref templateListScrollPos, viewRect);

            // "不使用模板"选项
            Rect noneRow = new Rect(0f, 0f, viewRect.width, 22f);
            bool noneSel = string.IsNullOrEmpty(selectedTemplateName);
            if (noneSel) Widgets.DrawBoxSolid(noneRow, new Color(0.3f, 0.5f, 0.8f, 0.3f));
            if (Widgets.ButtonText(noneRow, "(使用默认 Def 预置)"))
                selectedTemplateName = "";

            for (int i = 0; i < templates.Count; i++)
            {
                string name = templates[i];
                var rowRect = new Rect(0f, (i + 1) * 24f, viewRect.width, 22f);
                bool sel = selectedTemplateName == name;
                if (sel) Widgets.DrawBoxSolid(rowRect, new Color(0.3f, 0.5f, 0.8f, 0.3f));
                if (Widgets.ButtonText(rowRect, name))
                    selectedTemplateName = name;
            }
            Widgets.EndScrollView();

            listing.Gap(2f);
            if (templates.Count == 0)
                listing.Label("（暂无模板，请先在部件模板设计器中创建）");
        }

        private void ResetNewPartDefaults()
        {
            editPosX = "0"; editPosY = "1"; editPosZ = "0";
            editRotX = "0"; editRotY = "0"; editRotZ = "0";
            editSizeX = ""; editSizeY = ""; editSizeZ = "";
            selectedTemplateName = "";
        }

        private ShipPartShape GetCurrentShape()
        {
            if (isNewPartMode)
            {
                switch (newShapeType)
                {
                    case "SphericalCorner": return ShipPartShape.SphericalCorner;
                    default: return ShipPartShape.Cube;
                }
            }
            else if (selectedPartIndex >= 0 && selectedPartIndex < comp.CurrentShip.parts.Count)
            {
                return comp.CurrentShip.parts[selectedPartIndex].ShapeType;
            }
            return ShipPartShape.Cube;
        }

        // ══════════════════════════════════════════════════
        //  操作按钮
        // ══════════════════════════════════════════════════

        private void DrawActionButtons(Listing_Standard listing)
        {
            listing.Gap(4f);
            Text.Font = GameFont.Medium;
            listing.Label("操作");
            Text.Font = GameFont.Small;

            // 新增模式按钮
            DrawShapeButtonsRow(listing);

            listing.Gap(4f);

            // 删除选中
            if (selectedPartIndex >= 0 && selectedPartIndex < comp.CurrentShip.parts.Count)
            {
                if (listing.ButtonText("删除选中部件"))
                {
                    comp.CurrentShip.parts.RemoveAt(selectedPartIndex);
                    selectedPartIndex = -1;
                    isNewPartMode = true;
                    RebuildScene();
                }
            }

            // 清空全部
            if (listing.ButtonText("清空全部"))
            {
                comp.CurrentShip.parts.Clear();
                selectedPartIndex = -1;
                isNewPartMode = true;
                RebuildScene();
            }
        }

        private void DrawShapeButtonsRow(Listing_Standard listing)
        {
            // 几何形状按钮（功能角色由模板组件决定，在参数面板中选择模板）
            string[] shapeNames = { "结构", "角球" };
            string[] shapeKeys = { "Cube", "SphericalCorner" };

            Rect row1 = listing.GetRect(28f);
            float btnW = (row1.width - 8f) / 2f;
            for (int i = 0; i < 2; i++)
            {
                var r = new Rect(row1.x + i * (btnW + 8f), row1.y, btnW, 28f);
                bool sel = newShapeType == shapeKeys[i];
                if (sel) Widgets.DrawBoxSolid(r, new Color(0.3f, 0.5f, 0.8f, 0.3f));
                if (Widgets.ButtonText(r, shapeNames[i]))
                {
                    newShapeType = shapeKeys[i];
                    ResetNewPartDefaults();
                    isNewPartMode = true;
                    selectedPartIndex = -1;
                }
            }
        }

        // ══════════════════════════════════════════════════
        //  参数应用
        // ══════════════════════════════════════════════════

        private void ApplyParamsToSelected()
        {
            float px = ParseFloat(editPosX, 0f);
            float py = ParseFloat(editPosY, 0f);
            float pz = ParseFloat(editPosZ, 0f);
            float rx = ParseFloat(editRotX, 0f);
            float ry = ParseFloat(editRotY, 0f);
            float rz = ParseFloat(editRotZ, 0f);
            float sx = ParseFloat(editSizeX, -1f);
            float sy = ParseFloat(editSizeY, -1f);
            float sz = ParseFloat(editSizeZ, -1f);

            var ship = comp.CurrentShip;

            if (isNewPartMode)
            {
                // 模板名：优先用户选择，否则用几何对应的 Def 名（预置组件兜底）
                string tmplName = !string.IsNullOrEmpty(selectedTemplateName)
                    ? selectedTemplateName
                    : ShipPartDefs.GetDefaultDefName(GetCurrentShape());

                var part = new ShipPartInstance
                {
                    templateName = tmplName,
                    position = new Vector3(px, py, pz),
                    rotation = Quaternion.Euler(rx, ry, rz),
                    hp = 100f
                };

                // 自定义尺寸
                if (sx > 0 && sy > 0 && sz > 0)
                    part.customBounds = new Vector3(sx, sy, sz);

                ship.parts.Add(part);
                selectedPartIndex = ship.parts.Count - 1;
                isNewPartMode = false;
            }
            else if (selectedPartIndex >= 0 && selectedPartIndex < ship.parts.Count)
            {
                var p = ship.parts[selectedPartIndex];
                p.position = new Vector3(px, py, pz);
                p.rotation = Quaternion.Euler(rx, ry, rz);

                if (sx > 0 && sy > 0 && sz > 0)
                    p.customBounds = new Vector3(sx, sy, sz);
                else
                    p.customBounds = Vector3.zero;
            }

            RebuildScene();
        }

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
