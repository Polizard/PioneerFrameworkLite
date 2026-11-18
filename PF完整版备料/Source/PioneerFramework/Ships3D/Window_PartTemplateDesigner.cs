using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// 部件模板设计器窗口。
    /// 用于创建、编辑、保存、加载、删除部件模板（ShipPartTemplate）。
    /// 几何（形状/尺寸）与组件集（引擎/工质罐/货舱/武器/舱室）分别编辑，
    /// 组件可增删、可叠加，保存为 .part v4 二进制文件。
    /// </summary>
    [StaticConstructorOnStartup]
    public class Window_PartTemplateDesigner : Window
    {
        // ── 3D 预览 ──
        private Camera previewCamera;
        private RenderTexture previewTexture;
        private GameObject cameraObject;
        private GameObject sceneRoot;
        private GameObject previewPartGo;
        private float previewYaw = 30f;
        private float previewPitch = 20f;
        private float previewDistance = 5f;
        private const int SceneLayer = 30;

        // ── 材质（纯几何）──
        private static Material cubeMat, cornerMat;

        // ── 状态 ──
        private ShipPartShape selectedShape = ShipPartShape.Cube;
        private string templateName = "";
        private string editSizeX = "1", editSizeY = "1", editSizeZ = "1";

        /// <summary>编辑中的组件集（用户调整后的快照，保存时落盘）。</summary>
        private List<PartComponentData> editComponents = new List<PartComponentData>();

        private bool showLoadPanel = false;
        private Vector2 loadListScrollPos;

        private int savedMainCameraCullingMask;

        static Window_PartTemplateDesigner()
        {
            cubeMat = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.6f, 1.0f) };
            cornerMat = new Material(Shader.Find("Standard")) { color = new Color(1.0f, 0.5f, 0.2f) };
        }

        public Window_PartTemplateDesigner()
        {
            doCloseButton = true;
            doCloseX = true;
            forcePause = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            resizeable = true;
            draggable = true;
            optionalTitle = "部件模板设计器";
            CreatePreviewResources();
        }

        public override Vector2 InitialSize => new Vector2(900f, 600f);

        private void CreatePreviewResources()
        {
            if (Find.Camera != null)
            {
                savedMainCameraCullingMask = Find.Camera.cullingMask;
                Find.Camera.cullingMask &= ~(1 << SceneLayer);
            }

            cameraObject = new GameObject("PartTemplatePreviewCamera");
            previewCamera = cameraObject.AddComponent<Camera>();
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = new Color(0.08f, 0.08f, 0.12f);
            previewCamera.cullingMask = 1 << SceneLayer;
            previewCamera.orthographic = false;
            previewCamera.fieldOfView = 40f;
            previewCamera.nearClipPlane = 0.1f;
            previewCamera.farClipPlane = 100f;

            previewTexture = new RenderTexture(400, 300, 24);
            previewCamera.targetTexture = previewTexture;

            sceneRoot = new GameObject("PartTemplateScene");
            sceneRoot.layer = SceneLayer;

            var lightObj = new GameObject("PreviewLight");
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.color = new Color(1f, 0.95f, 0.85f);
            light.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            lightObj.transform.SetParent(sceneRoot.transform);

            var light2 = new GameObject("PreviewFill");
            var l2 = light2.AddComponent<Light>();
            l2.type = LightType.Directional;
            l2.intensity = 0.4f;
            l2.color = new Color(0.5f, 0.6f, 1f);
            l2.transform.rotation = Quaternion.Euler(-30f, -120f, 0f);
            light2.transform.SetParent(sceneRoot.transform);

            RebuildPreview();
        }

        private void EnsurePreviewTextureSize(float w, float h)
        {
            int tw = Mathf.Max(1, Mathf.RoundToInt(w));
            int th = Mathf.Max(1, Mathf.RoundToInt(h));
            if (previewTexture == null || previewTexture.width != tw || previewTexture.height != th)
            {
                var newRT = new RenderTexture(tw, th, 24);
                previewCamera.targetTexture = newRT;
                if (previewTexture != null) previewTexture.Release();
                previewTexture = newRT;
            }
        }

        private void RebuildPreview()
        {
            if (previewPartGo != null)
                UnityEngine.Object.Destroy(previewPartGo);

            previewPartGo = new GameObject("PreviewPart");
            previewPartGo.layer = SceneLayer;
            var mf = previewPartGo.AddComponent<MeshFilter>();
            var mr = previewPartGo.AddComponent<MeshRenderer>();

            mf.mesh = ShipPartMeshGenerator.GetMesh(selectedShape);
            mr.material = GetPreviewMaterial(selectedShape);

            Vector3 bounds = GetCurrentBounds();
            previewPartGo.transform.localScale = bounds;
            previewPartGo.transform.SetParent(sceneRoot.transform);
        }

        private Vector3 GetCurrentBounds()
        {
            float x = ParseFloat(editSizeX, 1f);
            float y = ParseFloat(editSizeY, 1f);
            float z = ParseFloat(editSizeZ, 1f);
            return new Vector3(x, y, z);
        }

        private void UpdatePreviewCamera()
        {
            if (previewCamera == null) return;
            var rot = Quaternion.Euler(previewPitch, previewYaw, 0f);
            var offset = rot * new Vector3(0f, 0f, -previewDistance);
            previewCamera.transform.position = offset;
            previewCamera.transform.LookAt(Vector3.zero);
        }

        private static Material GetPreviewMaterial(ShipPartShape shape)
        {
            switch (shape)
            {
                case ShipPartShape.SphericalCorner: return cornerMat;
                default: return cubeMat;
            }
        }

        // ══════════════════════════════════════════════════
        //  窗口内容
        // ══════════════════════════════════════════════════

        public override void DoWindowContents(Rect inRect)
        {
            float previewW = Mathf.Min(350f, inRect.width * 0.4f);
            float rightW = inRect.width - previewW - 10f;
            float contentH = inRect.height - 40f;

            var previewRect = new Rect(inRect.x, inRect.y, previewW, contentH);
            var rightRect = new Rect(inRect.x + previewW + 10f, inRect.y, rightW, contentH);

            // ── 3D 预览 ──
            EnsurePreviewTextureSize(previewRect.width, previewRect.height);
            if (previewRect.Contains(Event.current.mousePosition))
                HandlePreviewCameraInput();

            UpdatePreviewCamera();
            previewCamera.Render();
            if (previewTexture != null)
                GUI.DrawTexture(previewRect, previewTexture);

            string hint = "LMB拖拽: 旋转 | 滚轮: 缩放";
            Text.Anchor = TextAnchor.LowerLeft;
            Widgets.Label(new Rect(inRect.x, inRect.y + contentH + 2f, previewW, 18f), hint);
            Text.Anchor = TextAnchor.UpperLeft;

            // ── 右侧面板 ──
            DrawRightPanel(rightRect);
        }

        private void HandlePreviewCameraInput()
        {
            var e = Event.current;
            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                previewYaw += e.delta.x * 0.5f;
                previewPitch -= e.delta.y * 0.5f;
                previewPitch = Mathf.Clamp(previewPitch, 2f, 88f);
                e.Use();
            }
            else if (e.type == EventType.ScrollWheel)
            {
                previewDistance -= e.delta.y * 1.5f;
                previewDistance = Mathf.Clamp(previewDistance, 1f, 20f);
                e.Use();
            }
        }

        // ══════════════════════════════════════════════════
        //  右侧面板
        // ══════════════════════════════════════════════════

        private void DrawRightPanel(Rect rect)
        {
            var listing = new Listing_Standard();
            listing.Begin(rect);

            // ── 标题 ──
            Text.Font = GameFont.Medium;
            listing.Label("部件模板设计器");
            Text.Font = GameFont.Small;
            listing.GapLine(4f);

            // ── 模板名称 ──
            Rect nameRow = listing.GetRect(26f);
            Widgets.Label(new Rect(nameRow.x, nameRow.y, 45f, 26f), "名称:");
            templateName = Widgets.TextField(
                new Rect(nameRow.x + 47f, nameRow.y, nameRow.width - 100f, 26f), templateName);

            listing.Gap(4f);

            // ── 几何形状 ──
            listing.Label("几何形状:");
            Rect shapeRow = listing.GetRect(30f);
            string[] shapeNames = { "结构", "角球" };
            ShipPartShape[] shapeValues = { ShipPartShape.Cube, ShipPartShape.SphericalCorner };
            float btnW = (shapeRow.width - 8f) / 2f;
            for (int i = 0; i < shapeNames.Length; i++)
            {
                var sr = new Rect(shapeRow.x + i * (btnW + 8f), shapeRow.y, btnW, 28f);
                bool sel = selectedShape == shapeValues[i];
                if (sel) Widgets.DrawBoxSolid(sr, new Color(0.3f, 0.5f, 0.8f, 0.3f));
                if (Widgets.ButtonText(sr, shapeNames[i]))
                {
                    if (selectedShape != shapeValues[i])
                    {
                        selectedShape = shapeValues[i];
                        RebuildPreview();
                    }
                }
            }

            listing.GapLine(4f);

            // ── 尺寸 ──
            listing.Label("尺寸 长(X) 宽(Y) 高(Z):");
            Rect sizeRow = listing.GetRect(24f);
            float thirdW = (sizeRow.width - 8f) / 3f;
            editSizeX = Widgets.TextField(new Rect(sizeRow.x, sizeRow.y, thirdW, 24f), editSizeX);
            editSizeY = Widgets.TextField(new Rect(sizeRow.x + thirdW + 4f, sizeRow.y, thirdW, 24f), editSizeY);
            editSizeZ = Widgets.TextField(new Rect(sizeRow.x + (thirdW + 4f) * 2f, sizeRow.y, thirdW, 24f), editSizeZ);

            listing.GapLine(4f);

            // ── 组件集 ──
            listing.Label("组件（功能角色）:");
            listing.Gap(2f);

            // 添加组件按钮
            Rect addRow = listing.GetRect(28f);
            float addW = 74f;
            if (Widgets.ButtonText(new Rect(addRow.x, addRow.y, addW, 26f), "+ 引擎"))
                editComponents.Add(new EngineComponentData());
            if (Widgets.ButtonText(new Rect(addRow.x + addW + 4f, addRow.y, addW, 26f), "+ 工质罐"))
                editComponents.Add(new PropellantTankComponentData());
            if (Widgets.ButtonText(new Rect(addRow.x + (addW + 4f) * 2f, addRow.y, addW, 26f), "+ 货舱"))
                editComponents.Add(new CargoComponentData());
            if (Widgets.ButtonText(new Rect(addRow.x + (addW + 4f) * 3f, addRow.y, addW, 26f), "+ 武器"))
                editComponents.Add(new WeaponMountComponentData());
            if (Widgets.ButtonText(new Rect(addRow.x + (addW + 4f) * 4f, addRow.y, addW, 26f), "+ 舱室"))
                editComponents.Add(new RoomComponentData());

            listing.Gap(4f);

            // 组件列表
            for (int i = 0; i < editComponents.Count; i++)
            {
                var c = editComponents[i];
                if (c == null) continue;

                Rect header = listing.GetRect(24f);
                Widgets.Label(new Rect(header.x, header.y, header.width - 70f, 24f),
                    "▸ " + GetComponentLabel(c));
                if (Widgets.ButtonText(new Rect(header.x + header.width - 64f, header.y, 60f, 24f), "删除"))
                {
                    editComponents.RemoveAt(i);
                    i--;
                    continue;
                }

                DrawComponentParams(listing, c);
                listing.GapLine(4f);
            }

            listing.Gap(4f);

            // ── 更新预览 / 保存 ──
            if (listing.ButtonText("更新预览"))
            {
                RebuildPreview();
            }

            listing.Gap(4f);

            if (listing.ButtonText("保存模板"))
            {
                SaveCurrentTemplate();
            }

            listing.Gap(4f);

            // ── 读取/删除 ──
            if (listing.ButtonText(showLoadPanel ? "关闭模板列表" : "读取已有模板"))
            {
                showLoadPanel = !showLoadPanel;
            }

            if (showLoadPanel)
            {
                DrawTemplateList(listing);
            }

            listing.End();
        }

        private static string GetComponentLabel(PartComponentData c)
        {
            if (c is EngineComponentData) return "引擎";
            if (c is PropellantTankComponentData) return "工质罐";
            if (c is CargoComponentData) return "货舱";
            if (c is WeaponMountComponentData) return "武器挂载";
            if (c is RoomComponentData) return "舱室";
            return c.TypeId;
        }

        private void DrawComponentParams(Listing_Standard listing, PartComponentData c)
        {
            if (c is EngineComponentData e)
                DrawEngineParams(listing, e);
            else if (c is PropellantTankComponentData t)
                DrawTankParams(listing, t);
            else if (c is CargoComponentData cc)
                DrawCargoParams(listing, cc);
            else if (c is WeaponMountComponentData w)
                DrawWeaponParams(listing, w);
            else if (c is RoomComponentData r)
                DrawRoomParams(listing, r);
        }

        private void DrawEngineParams(Listing_Standard listing, EngineComponentData e)
        {
            listing.Label("引擎参数:");
            Rect row = listing.GetRect(24f);
            float half = (row.width - 8f) / 2f;
            Widgets.Label(new Rect(row.x, row.y, 70f, 24f), "排气速度:");
            FloatInput(new Rect(row.x + 72f, row.y, half - 100f, 24f), ref e.exhaustVelocity);
            Widgets.Label(new Rect(row.x + half - 30f, row.y, 30f, 24f), "m/s");
            Widgets.Label(new Rect(row.x + half + 4f, row.y, 70f, 24f), "工质流量:");
            FloatInput(new Rect(row.x + half + 76f, row.y, half - 104f, 24f), ref e.massFlowRate);
            Widgets.Label(new Rect(row.x + row.width - 30f, row.y, 30f, 24f), "kg/s");
            listing.Gap(2f);
            Rect row2 = listing.GetRect(24f);
            Widgets.Label(new Rect(row2.x, row2.y, 70f, 24f), "自重:");
            FloatInput(new Rect(row2.x + 72f, row2.y, 120f, 24f), ref e.dryMass);
            Widgets.Label(new Rect(row2.x + 196f, row2.y, 30f, 24f), "kg");
        }

        private void DrawTankParams(Listing_Standard listing, PropellantTankComponentData t)
        {
            listing.Label("工质罐参数:");
            Rect row = listing.GetRect(24f);
            float half = (row.width - 8f) / 2f;
            Widgets.Label(new Rect(row.x, row.y, 50f, 24f), "干质量:");
            FloatInput(new Rect(row.x + 52f, row.y, half - 82f, 24f), ref t.dryMass);
            Widgets.Label(new Rect(row.x + half - 26f, row.y, 26f, 24f), "kg");
            Widgets.Label(new Rect(row.x + half + 4f, row.y, 50f, 24f), "湿质量:");
            FloatInput(new Rect(row.x + half + 56f, row.y, half - 86f, 24f), ref t.wetMass);
            Widgets.Label(new Rect(row.x + row.width - 30f, row.y, 30f, 24f), "kg");
        }

        private void DrawCargoParams(Listing_Standard listing, CargoComponentData cc)
        {
            listing.Label("货舱参数:");
            Rect row = listing.GetRect(24f);
            Widgets.Label(new Rect(row.x, row.y, 50f, 24f), "容量:");
            FloatInput(new Rect(row.x + 52f, row.y, row.width - 86f, 24f), ref cc.capacity);
            Widgets.Label(new Rect(row.x + row.width - 30f, row.y, 30f, 24f), "kg");
        }

        private void DrawWeaponParams(Listing_Standard listing, WeaponMountComponentData w)
        {
            listing.Label("武器挂载参数:");
            listing.Gap(2f);
            Rect weaponRow = listing.GetRect(24f);
            Widgets.Label(new Rect(weaponRow.x, weaponRow.y, 70f, 24f), "武器Def:");
            w.weaponDefName = Widgets.TextField(
                new Rect(weaponRow.x + 72f, weaponRow.y, weaponRow.width - 76f, 24f), w.weaponDefName ?? "");
            listing.Gap(4f);

            Rect rotRow = listing.GetRect(24f);
            float half = (rotRow.width - 8f) / 2f;
            Widgets.Label(new Rect(rotRow.x, rotRow.y, 60f, 24f), "旋转速度:");
            FloatInput(new Rect(rotRow.x + 62f, rotRow.y, half - 92f, 24f), ref w.turretRotationSpeed);
            Widgets.Label(new Rect(rotRow.x + half - 26f, rotRow.y, 26f, 24f), "°/s");
            Widgets.Label(new Rect(rotRow.x + half + 4f, rotRow.y, 60f, 24f), "追踪弧角:");
            FloatInput(new Rect(rotRow.x + half + 66f, rotRow.y, half - 96f, 24f), ref w.trackingArc);
            Widgets.Label(new Rect(rotRow.x + rotRow.width - 26f, rotRow.y, 26f, 24f), "°");
            listing.Gap(4f);

            listing.Label("炮口偏移 (XYZ 米):");
            Rect muzzleRow = listing.GetRect(24f);
            float thirdW = (muzzleRow.width - 8f) / 3f;
            float mx = (float)w.muzzleOffset.x;
            FloatInput(new Rect(muzzleRow.x, muzzleRow.y, thirdW, 24f), ref mx);
            float my = (float)w.muzzleOffset.y;
            FloatInput(new Rect(muzzleRow.x + thirdW + 4f, muzzleRow.y, thirdW, 24f), ref my);
            float mz = (float)w.muzzleOffset.z;
            FloatInput(new Rect(muzzleRow.x + (thirdW + 4f) * 2f, muzzleRow.y, thirdW, 24f), ref mz);
            w.muzzleOffset = new DVector3(mx, my, mz);
            listing.Gap(2f);
            Rect row2 = listing.GetRect(24f);
            Widgets.Label(new Rect(row2.x, row2.y, 70f, 24f), "自重:");
            FloatInput(new Rect(row2.x + 72f, row2.y, 120f, 24f), ref w.dryMass);
            Widgets.Label(new Rect(row2.x + 196f, row2.y, 30f, 24f), "kg");
        }

        private void DrawRoomParams(Listing_Standard listing, RoomComponentData r)
        {
            listing.Label("舱室参数:");
            listing.Gap(2f);
            Rect row = listing.GetRect(24f);
            Widgets.Label(new Rect(row.x, row.y, 70f, 24f), "舱室类型:");
            r.roomTypeDefName = Widgets.TextField(
                new Rect(row.x + 74f, row.y, row.width - 78f, 24f), r.roomTypeDefName ?? "");
            listing.Gap(2f);
            Rect row2 = listing.GetRect(24f);
            Widgets.Label(new Rect(row2.x, row2.y, 70f, 24f), "自重:");
            FloatInput(new Rect(row2.x + 72f, row2.y, 120f, 24f), ref r.dryMass);
            Widgets.Label(new Rect(row2.x + 196f, row2.y, 30f, 24f), "kg");
            listing.Gap(2f);
            listing.Label("（内部地图尺寸 = 部件水平尺寸 × 10 米）");
        }

        /// <summary>浮点输入框：成功解析才写回，避免清空后无法重输。</summary>
        private static void FloatInput(Rect rect, ref float value)
        {
            string s = Widgets.TextField(rect, value.ToString("G", CultureInfo.InvariantCulture));
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                value = v;
        }

        private void DrawTemplateList(Listing_Standard listing)
        {
            listing.Gap(4f);
            var templates = ShipPartTemplate.GetSavedTemplates();
            if (templates.Count == 0)
            {
                listing.Label("（暂无已保存的模板）");
                return;
            }

            float listH = Mathf.Min(templates.Count * 30f, 200f);
            Rect loadRect = listing.GetRect(listH);
            float itemH = 28f;
            float totalH = templates.Count * itemH;
            var viewRect = new Rect(0f, 0f, loadRect.width - 16f, totalH);
            Widgets.BeginScrollView(loadRect, ref loadListScrollPos, viewRect);

            for (int i = 0; i < templates.Count; i++)
            {
                string name = templates[i];
                var row = new Rect(0f, i * itemH, viewRect.width, itemH - 2f);

                float delW = 45f;
                float loadW = 45f;
                float nameW = row.width - delW - loadW - 8f;

                // 名称
                Widgets.Label(new Rect(row.x, row.y, nameW, itemH), name);

                // 读取按钮
                if (Widgets.ButtonText(new Rect(row.x + nameW + 4f, row.y, loadW, itemH), "读取"))
                {
                    var tmpl = ShipPartTemplate.LoadFromFile(name);
                    if (tmpl != null)
                    {
                        LoadTemplateIntoUI(tmpl);
                        showLoadPanel = false;
                    }
                }

                // 删除按钮
                if (Widgets.ButtonText(new Rect(row.x + nameW + loadW + 8f, row.y, delW, itemH), "删除"))
                {
                    ShipPartTemplate.DeleteFile(name);
                    Messages.Message("模板 '" + name + "' 已删除", MessageTypeDefOf.TaskCompletion);
                }
            }
            Widgets.EndScrollView();
        }

        private void LoadTemplateIntoUI(ShipPartTemplate tmpl)
        {
            templateName = tmpl.templateName;
            selectedShape = tmpl.shapeType;
            editSizeX = tmpl.bounds.x.ToString("F1");
            editSizeY = tmpl.bounds.y.ToString("F1");
            editSizeZ = tmpl.bounds.z.ToString("F1");

            // 组件集：深拷贝进编辑状态
            editComponents.Clear();
            if (tmpl.components != null)
            {
                for (int i = 0; i < tmpl.components.Count; i++)
                {
                    var cloned = tmpl.components[i].Clone();
                    if (cloned != null)
                        editComponents.Add(cloned);
                }
            }

            RebuildPreview();
            Messages.Message("模板 '" + tmpl.templateName + "' 已加载", MessageTypeDefOf.TaskCompletion);
        }

        private void SaveCurrentTemplate()
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                Messages.Message("请输入模板名称！", MessageTypeDefOf.RejectInput);
                return;
            }

            try
            {
                var tmpl = new ShipPartTemplate
                {
                    shapeType = selectedShape,
                    bounds = GetCurrentBounds(),
                    previewColor = GetPreviewColorForShape(selectedShape),
                    components = new List<PartComponentData>(editComponents)
                };
                tmpl.SaveToFile(templateName);
                Messages.Message("模板 '" + templateName + "' 已保存", MessageTypeDefOf.TaskCompletion);
            }
            catch (Exception ex)
            {
                Log.Error("[PartTemplateDesigner] 保存失败: " + ex.Message);
                Messages.Message("保存失败: " + ex.Message, MessageTypeDefOf.RejectInput);
            }
        }

        private static Color GetPreviewColorForShape(ShipPartShape shape)
        {
            switch (shape)
            {
                case ShipPartShape.SphericalCorner: return new Color(1.0f, 0.5f, 0.2f);
                default: return new Color(0.3f, 0.6f, 1.0f);
            }
        }

        // ══════════════════════════════════════════════════
        //  工具
        // ══════════════════════════════════════════════════

        private static float ParseFloat(string s, float fallback)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
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
            if (previewTexture != null) { previewTexture.Release(); previewTexture = null; }
            if (cameraObject != null) { UnityEngine.Object.Destroy(cameraObject); cameraObject = null; }
            if (sceneRoot != null) { UnityEngine.Object.Destroy(sceneRoot); sceneRoot = null; }
        }
    }
}
