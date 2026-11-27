using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭组装器预览。本地 +Z 指向箭首，X/Y 为径向。
    /// 1.7 宿主在 GUI 阶段不支持即时网格绘制，故在有界纹理内做投影和深度测试。
    /// 两个视口共用零件几何，不接触游戏相机、光照和场景层。
    /// </summary>
    internal sealed class RocketAssemblyViewport : IDisposable
    {
        internal enum CameraMode { Oblique, Side, Axial }

        internal readonly record struct Part(int Row, Vector3 Center, Quaternion Rotation, Vector3 Size,
            InertiaTensor.Shape Shape, string Category, string Appearance, int ParentRow);

        private const int RadialSegments = 16;
        private const int MaxTextureSize = 1024;
        private const int MaxParts = DesignPartAssembler.MaxTotalParts;
        private const float DefaultYaw = 0.58f;
        private const float DefaultPitch = 0.17f;
        private const float OrbitSpeed = 0.009f;
        private const float PiOverTwo = Mathf.PI * 0.5f;

        private static readonly Color32 Background = new(16, 26, 34, 255);
        private static readonly Color32 Grid = new(38, 55, 65, 255);
        private static readonly Color32 CenterLine = new(96, 115, 122, 255);
        private static readonly Color32 Highlight = new(243, 206, 137, 255);
        private static readonly Color32 Recent = new(154, 208, 199, 255);
        private static readonly Color32 Ghost = new(135, 231, 219, 255);
        private static readonly Vector3 LightDirection = new Vector3(-0.46f, -0.78f, 1f).normalized;
        private static readonly (Vector3 Direction, string Label, Color Color)[] Axes =
        [
            (Vector3.right, "X", new Color32(229, 180, 154, 255)),
            (Vector3.up, "Y", new Color32(165, 209, 169, 255)),
            (Vector3.forward, "Z", new Color32(163, 212, 230, 255)),
        ];

        private readonly record struct Ring(float Z, float Radius, float InwardOffset = 0f);

        private static readonly Ring[] ControlProfile = [new(-0.5f, 0.96f), new(-0.25f, 0.94f),
            new(0.32f, 0.66f), new(0.5f, 0.42f)];
        private static readonly Ring[] TankProfile = [new(-0.5f, 0.86f), new(-0.45f, 1f),
            new(0.45f, 1f), new(0.5f, 0.86f)];
        private static readonly Ring[] EngineProfile = [new(-0.5f, 1f), new(-0.38f, 0.91f),
            new(0.1f, 0.53f), new(0.27f, 0.52f), new(0.38f, 0.98f), new(0.5f, 0.98f)];
        private static readonly Ring[] HullProfile = [new(-0.5f, 0.96f), new(-0.46f, 1f),
            new(0.46f, 1f), new(0.5f, 0.96f)];
        private static readonly Ring[] CylinderProfile = [new(-0.5f, 1f), new(0.5f, 1f)];
        private static readonly Ring[] FairingProfile = [new(-0.5f, 1f), new(-0.28f, 1f),
            new(0.12f, 0.88f), new(0.36f, 0.65f), new(0.5f, 0.5f)];
        private static readonly Ring[] BoosterProfile = [new(-0.5f, 0.9f), new(-0.43f, 1f),
            new(0.02f, 1f), new(0.23f, 0.76f, 0.12f), new(0.42f, 0.37f, 0.31f), new(0.5f, 0.14f, 0.44f)];
        private static readonly Ring[] SphereProfile = [new(-0.5f, 0f), new(-0.462f, 0.383f),
            new(-0.354f, 0.707f), new(-0.191f, 0.924f), new(0f, 1f), new(0.191f, 0.924f),
            new(0.354f, 0.707f), new(0.462f, 0.383f), new(0.5f, 0f)];

        private readonly List<Part> model = new();
        private readonly List<Part> preview = new();
        private readonly List<Geometry> solids = new();
        private readonly List<Geometry> ghosts = new();
        private readonly Dictionary<int, Vector3> parentCenters = new();
        private readonly HashSet<int> recentRows = new();
        private readonly ProjectedVertex[] points = new ProjectedVertex[RadialSegments * SphereProfile.Length + 2];
        private readonly Surface mainSurface = new();
        private readonly Surface sideSurface = new();
        private Vector3 center;
        private Vector3 half = new(3f, 3f, 5f);
        private int sceneRevision;
        private int cameraRevision;
        private int selection = -1;
        private CameraMode cameraMode;
        private float yaw = DefaultYaw;
        private float pitch = DefaultPitch;
        private float zoom = 1f;
        private Vector2 pan;
        private int gestureControl;
        private int gestureButton;
        private Vector2 dragPoint;
        private IReadOnlyList<Part>? framedParts;

        internal bool IsCameraDragging => gestureControl != 0;

        internal void SetView(CameraMode mode)
        {
            cameraMode = mode;
            yaw = mode == CameraMode.Oblique ? DefaultYaw : 0f;
            pitch = mode switch
            {
                CameraMode.Oblique => DefaultPitch,
                CameraMode.Axial => PiOverTwo,
                _ => 0f,
            };
            cameraRevision++;
        }

        internal void Fit()
        {
            zoom = 1f;
            pan = Vector2.zero;
            cameraRevision++;
        }

        internal bool HandleCameraInput(Rect rect)
        {
            Event e = Event.current;
            if (e == null) return false;

            if (gestureControl != 0 && GUIUtility.hotControl != gestureControl)
                gestureControl = 0;
            if (e.type == EventType.MouseLeaveWindow)
            {
                ReleaseGesture();
                return false;
            }

            if (gestureControl != 0)
            {
                if (e.type == EventType.MouseDrag)
                {
                    Vector2 delta = e.mousePosition - dragPoint;
                    dragPoint = e.mousePosition;
                    if (gestureButton == 2)
                    {
                        pan += delta;
                    }
                    else
                    {
                        yaw = Mathf.Repeat(yaw - delta.x * OrbitSpeed + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
                        pitch = Mathf.Clamp(pitch + delta.y * OrbitSpeed, -1.56f, 1.56f);
                        cameraMode = CameraMode.Oblique;
                    }
                    cameraRevision++;
                    e.Use();
                    return true;
                }
                if (e.type == EventType.MouseUp && e.button == gestureButton)
                {
                    ReleaseGesture();
                    e.Use();
                    return true;
                }
                if (e.type == EventType.MouseDown || e.type == EventType.MouseUp || e.type == EventType.ScrollWheel)
                {
                    e.Use();
                    return true;
                }
                return false;
            }

            if (!rect.Contains(e.mousePosition) || GUIUtility.hotControl != 0) return false;
            if (e.type == EventType.ContextClick)
            {
                e.Use();
                return true;
            }
            if (e.type == EventType.MouseDown && (e.button == 1 || e.button == 2))
            {
                gestureControl = GUIUtility.GetControlID(FocusType.Passive);
                GUIUtility.hotControl = gestureControl;
                gestureButton = e.button;
                dragPoint = e.mousePosition;
                e.Use();
                return true;
            }
            if (e.type == EventType.ScrollWheel)
            {
                zoom = Mathf.Clamp(zoom * Mathf.Exp(-e.delta.y * 0.075f), 0.42f, 4f);
                cameraRevision++;
                e.Use();
                return true;
            }
            return false;
        }

        internal void Draw(Rect rect, bool planar, IReadOnlyList<Part> parts, IReadOnlyList<Part> ghosts,
            int selectedRow, IReadOnlyCollection<int> recent, IReadOnlyList<Part> framingParts)
        {
            UpdateScene(parts, ghosts, selectedRow, recent, framingParts);
            if (rect.width < 1f || rect.height < 1f || Event.current?.type != EventType.Repaint) return;

            Surface surface = planar ? sideSurface : mainSurface;
            Projection projection = View(rect.size, planar);
            int viewRevision = planar ? 0 : cameraRevision;
            if (surface.NeedsRedraw(rect.size, sceneRevision, viewRevision))
            {
                surface.Begin(rect.size);
                DrawGrid(surface, projection);
                DrawParentLinks(surface, projection, solids, false);
                DrawParentLinks(surface, projection, this.ghosts, true);
                foreach (Geometry part in solids)
                    Rasterize(surface, projection, part, isGhost: false);
                if (this.ghosts.Count > 0)
                {
                    surface.BeginGhosts();
                    foreach (Geometry part in this.ghosts)
                        Rasterize(surface, projection, part, isGhost: true);
                    surface.CompositeGhosts();
                }
                surface.Upload(sceneRevision, viewRevision);
            }

            Color oldColor = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(rect, surface.Texture!, ScaleMode.StretchToFill, false);
            GUI.color = oldColor;
            DrawAxes(rect, projection);
        }

        internal bool TryProject(Rect rect, bool planar, Vector3 position, out Vector2 point)
        {
            ProjectedVertex projected = View(rect.size, planar).Project(position);
            point = rect.position + projected.Point;
            return projected.Valid && rect.Contains(point);
        }

        internal bool TryPick(Rect rect, bool planar, Vector2 point, out int row)
        {
            row = -1;
            if (!rect.Contains(point)) return false;

            Projection projection = View(rect.size, planar);
            Vector2 localPoint = point - rect.position;
            float nearestDepth = float.NegativeInfinity;
            foreach (Geometry part in solids)
            {
                ProjectVertices(projection, part, Vector2.one);
                foreach (Triangle triangle in part.Triangles)
                {
                    if (!projection.IsVisible(triangle.Normal, part.Vertices[triangle.A])) continue;
                    ProjectedVertex a = points[triangle.A];
                    ProjectedVertex b = points[triangle.B];
                    ProjectedVertex c = points[triangle.C];
                    if (!a.Valid || !b.Valid || !c.Valid) continue;
                    var sample = new TriangleSample(a, b, c);
                    if (sample.TryDepth(localPoint, out float depth) && depth > nearestDepth)
                    {
                        nearestDepth = depth;
                        row = part.Part.Row;
                    }
                }
            }
            return row >= 0;
        }

        public void Dispose()
        {
            ReleaseGesture();
            mainSurface.Dispose();
            sideSurface.Dispose();
        }

        internal void ReleaseGesture()
        {
            if (gestureControl != 0 && GUIUtility.hotControl == gestureControl)
                GUIUtility.hotControl = 0;
            gestureControl = 0;
        }

        private void UpdateScene(IReadOnlyList<Part> parts, IReadOnlyList<Part> ghostParts,
            int selectedRow, IReadOnlyCollection<int> recent, IReadOnlyList<Part> framingParts)
        {
            if (Refresh(parts, model, solids))
            {
                parentCenters.Clear();
                foreach (Geometry part in solids)
                    parentCenters.TryAdd(part.Part.Row, part.Part.Center);
                sceneRevision++;
            }
            if (!ReferenceEquals(framedParts, framingParts))
            {
                framedParts = framingParts;
                Vector3 minimum = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 maximum = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                foreach (Part part in framingParts)
                {
                    Vector3 extent = Absolute(part.Rotation * (Vector3.right * (part.Size.x / 2f)))
                        + Absolute(part.Rotation * (Vector3.up * (part.Size.y / 2f)))
                        + Absolute(part.Rotation * (Vector3.forward * (part.Size.z / 2f)));
                    minimum = Vector3.Min(minimum, part.Center - extent);
                    maximum = Vector3.Max(maximum, part.Center + extent);
                }
                center = framingParts.Count == 0 ? Vector3.zero : (minimum + maximum) * 0.5f;
                half = framingParts.Count == 0 ? new Vector3(3f, 3f, 5f) : (maximum - minimum) * 0.5f;
                sceneRevision++;
            }
            if (Refresh(ghostParts, preview, ghosts)) sceneRevision++;
            if (selection != selectedRow || !recentRows.SetEquals(recent))
            {
                selection = selectedRow;
                recentRows.Clear();
                recentRows.UnionWith(recent);
                sceneRevision++;
            }
        }

        private static Vector3 Absolute(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        private static bool Refresh(IReadOnlyList<Part> parts, List<Part> previous, List<Geometry> geometry)
        {
            int count = Math.Min(parts.Count, MaxParts);
            if (count == previous.Count)
            {
                int index = 0;
                while (index < count && parts[index].Equals(previous[index])) index++;
                if (index == count) return false;
            }
            previous.Clear();
            geometry.Clear();
            for (int index = 0; index < count; index++)
            {
                Part part = parts[index];
                previous.Add(part);
                geometry.Add(BuildGeometry(part));
            }
            return true;
        }

        private Projection View(Vector2 size, bool planar)
        {
            float viewYaw = planar ? 0f : yaw;
            float viewPitch = planar ? 0f : pitch;
            Vector3 right = new(Mathf.Cos(viewYaw), Mathf.Sin(viewYaw), 0f);
            Vector3 towardEye = new(right.y * Mathf.Cos(viewPitch), -right.x * Mathf.Cos(viewPitch), Mathf.Sin(viewPitch));
            Vector3 up = Vector3.Cross(towardEye, right);
            float extentX = ProjectedExtent(half, right);
            float extentY = ProjectedExtent(half, up);
            float fit = Mathf.Min(Mathf.Max(1f, size.x - 28f) / (2f * Mathf.Max(3f, extentX)),
                Mathf.Max(1f, size.y - 38f) / (2f * Mathf.Max(4f, extentY)));
            return new Projection(size * 0.5f + (planar ? Vector2.zero : pan), center, right, up, towardEye,
                fit * (planar ? 1f : zoom), Mathf.Max(12f, half.magnitude * 3.15f), planar ? CameraMode.Side : cameraMode);
        }

        private static float ProjectedExtent(Vector3 bounds, Vector3 axis) =>
            bounds.x * Mathf.Abs(axis.x) + bounds.y * Mathf.Abs(axis.y) + bounds.z * Mathf.Abs(axis.z);

        private readonly record struct Projection(Vector2 Origin, Vector3 Center, Vector3 Right, Vector3 Up,
            Vector3 TowardEye, float Scale, float Distance, CameraMode Mode)
        {
            private bool Perspective => Mode == CameraMode.Oblique;

            internal ProjectedVertex Project(Vector3 position)
            {
                Vector3 relative = position - Center;
                float depth = Vector3.Dot(relative, TowardEye);
                float separation = Distance - depth;
                if (Perspective && separation <= 0.01f) return default;
                float factor = Perspective ? Scale * Distance / separation : Scale;
                Vector2 point = Origin + new Vector2(Vector3.Dot(relative, Right), -Vector3.Dot(relative, Up)) * factor;
                // 透视投影在屏幕上插值倒数深度，正交投影直接插值深度。
                return new ProjectedVertex(point, Perspective ? 1f / separation : depth,
                    float.IsFinite(point.x) && float.IsFinite(point.y));
            }

            internal bool IsVisible(Vector3 normal, Vector3 position)
            {
                Vector3 viewer = Perspective ? Center + TowardEye * Distance - position : TowardEye;
                return Vector3.Dot(normal, viewer) > 0f;
            }
        }

        private readonly record struct ProjectedVertex(Vector2 Point, float Depth, bool Valid);
        private readonly record struct Triangle(int A, int B, int C, Vector3 Normal, bool Trim);
        private sealed record Geometry(Part Part, Vector3[] Vertices, List<Triangle> Triangles, Color32 BaseColor);

        private static Geometry BuildGeometry(Part part)
        {
            Ring[] profile = part.Appearance switch
            {
                "booster" => BoosterProfile,
                "fairing" => FairingProfile,
                _ when part.Shape == InertiaTensor.Shape.Sphere => SphereProfile,
                _ => part.Category switch
                {
                    "control" => ControlProfile,
                    "tank" or "rcs-tank" => TankProfile,
                    "thruster" => EngineProfile,
                    _ => part.Shape == InertiaTensor.Shape.Cylinder ? CylinderProfile : HullProfile,
                },
            };
            bool square = profile == HullProfile;
            int sides = square ? 4 : RadialSegments;
            float radialScale = square ? Mathf.Sqrt(2f) : 1f;
            float startAngle = square ? Mathf.PI * 0.25f : 0f;
            int cap = profile.Length * sides;
            Vector3 inward = new Vector3(-part.Center.x, -part.Center.y, 0f).normalized;
            var vertices = new Vector3[cap + 2];
            var triangles = new List<Triangle>(2 * sides * profile.Length);

            Vector3 RingCenter(Ring ring) => part.Center
                + part.Rotation * new Vector3(0f, 0f, ring.Z * part.Size.z)
                + inward * (ring.InwardOffset * part.Size.x);

            for (int ringIndex = 0; ringIndex < profile.Length; ringIndex++)
            {
                Ring ring = profile[ringIndex];
                Vector3 ringCenter = RingCenter(ring);
                for (int side = 0; side < sides; side++)
                {
                    float angle = side * 2f * Mathf.PI / sides + startAngle;
                    float radius = ring.Radius * radialScale * 0.5f;
                    Vector3 radial = new(Mathf.Cos(angle) * part.Size.x * radius, Mathf.Sin(angle) * part.Size.y * radius, 0f);
                    vertices[ringIndex * sides + side] = ringCenter + part.Rotation * radial;
                }
            }
            vertices[cap] = RingCenter(profile[0]);
            vertices[cap + 1] = RingCenter(profile[^1]);

            void Add(int a, int b, int c, bool trim)
            {
                Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;
                if (normal != Vector3.zero)
                    triangles.Add(new Triangle(a, b, c, normal, trim));
            }

            for (int side = 0; side < sides; side++)
            {
                int next = (side + 1) % sides;
                Add(cap, next, side, false);
                for (int ring = 0; ring < profile.Length - 1; ring++)
                {
                    int lower = ring * sides;
                    int upper = lower + sides;
                    bool trim = ring == 0 || ring == profile.Length - 2;
                    Add(lower + side, lower + next, upper + side, trim);
                    Add(lower + next, upper + next, upper + side, trim);
                }
                int top = cap - sides;
                Add(cap + 1, top + side, top + next, true);
            }

            Color32 baseColor = part.Category switch
            {
                "control" => new(204, 216, 201, 255),
                "tank" or "rcs-tank" when profile == BoosterProfile => new(177, 198, 199, 255),
                "tank" or "rcs-tank" => new(151, 181, 194, 255),
                "thruster" or "rcs" => new(175, 144, 113, 255),
                _ => new(128, 158, 148, 255),
            };
            return new Geometry(part, vertices, triangles, baseColor);
        }

        private void ProjectVertices(Projection projection, Geometry part, Vector2 pixelScale)
        {
            for (int i = 0; i < part.Vertices.Length; i++)
            {
                ProjectedVertex point = projection.Project(part.Vertices[i]);
                points[i] = point with { Point = Vector2.Scale(point.Point, pixelScale) };
            }
        }

        private void Rasterize(Surface surface, Projection projection, Geometry part, bool isGhost)
        {
            Color32 paint = isGhost ? Ghost : part.Part.Row == selection ? Highlight :
                recentRows.Contains(part.Part.Row) ? Recent : part.BaseColor;
            ProjectVertices(projection, part, surface.PixelScale);
            foreach (Triangle triangle in part.Triangles)
            {
                if (!projection.IsVisible(triangle.Normal, part.Vertices[triangle.A])) continue;
                ProjectedVertex a = points[triangle.A];
                ProjectedVertex b = points[triangle.B];
                ProjectedVertex c = points[triangle.C];
                if (!a.Valid || !b.Valid || !c.Valid) continue;

                float shade = 0.48f + 0.4f * Mathf.Max(0f, Vector3.Dot(triangle.Normal, LightDirection))
                    + (triangle.Trim ? 0.12f : 0f);
                var color = new Color32((byte)(paint.r * shade), (byte)(paint.g * shade), (byte)(paint.b * shade), 255);
                surface.Fill(a, b, c, color, isGhost);
            }
        }

        private readonly struct TriangleSample
        {
            private readonly ProjectedVertex a;
            private readonly Vector2 ab;
            private readonly Vector2 ac;
            private readonly float depthAb;
            private readonly float depthAc;
            private readonly float inverseArea;

            internal TriangleSample(ProjectedVertex a, ProjectedVertex b, ProjectedVertex c)
            {
                this.a = a;
                ab = b.Point - a.Point;
                ac = c.Point - a.Point;
                depthAb = b.Depth - a.Depth;
                depthAc = c.Depth - a.Depth;
                float area = Cross(ab, ac);
                inverseArea = Mathf.Abs(area) > 1e-5f ? 1f / area : 0f;
            }

            internal bool TryDepth(Vector2 point, out float depth)
            {
                Vector2 relative = point - a.Point;
                float bWeight = Cross(relative, ac) * inverseArea;
                float cWeight = Cross(ab, relative) * inverseArea;
                depth = a.Depth + bWeight * depthAb + cWeight * depthAc;
                return inverseArea != 0f && bWeight >= 0f && cWeight >= 0f && bWeight + cWeight <= 1f;
            }

            private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        }

        private void DrawGrid(Surface surface, Projection projection)
        {
            if (projection.Mode == CameraMode.Side)
            {
                ProjectedVertex origin = projection.Project(new Vector3(0f, 0f, center.z));
                Vector2 gridOrigin = Vector2.Scale(origin.Point, surface.PixelScale);
                float step = Mathf.Max(2f, Mathf.Ceil(24f / (projection.Scale * 2f)) * 2f);
                float dx = step * projection.Scale * surface.PixelScale.x;
                float dy = step * projection.Scale * surface.PixelScale.y;
                for (float x = Mathf.Repeat(gridOrigin.x, dx); x < surface.Width; x += dx)
                    surface.Line(new Vector2(x, 0f), new Vector2(x, surface.Height - 1), Grid);
                for (float y = Mathf.Repeat(gridOrigin.y, dy); y < surface.Height; y += dy)
                    surface.Line(new Vector2(0f, y), new Vector2(surface.Width - 1, y), Grid);
                surface.Line(new Vector2(gridOrigin.x, 0f), new Vector2(gridOrigin.x, surface.Height - 1),
                    CenterLine, dashed: true);
                return;
            }

            float radius = Mathf.Max(6f, Mathf.Ceil(half.x + half.y + 5f));
            float ground = center.z - half.z - 1.6f;
            int divisions = Mathf.Clamp(Mathf.CeilToInt(radius / (radius > 18f ? 4f : 2f)), 1, 16);
            for (int i = -divisions; i <= divisions; i++)
            {
                float offset = radius * i / divisions;
                DrawProjectedLine(surface, projection, new Vector3(center.x + offset, center.y - radius, ground),
                    new Vector3(center.x + offset, center.y + radius, ground), Grid);
                DrawProjectedLine(surface, projection, new Vector3(center.x - radius, center.y + offset, ground),
                    new Vector3(center.x + radius, center.y + offset, ground), Grid);
            }
        }

        private void DrawParentLinks(Surface surface, Projection projection, List<Geometry> geometry, bool isGhost)
        {
            foreach (Geometry instance in geometry)
            {
                Part part = instance.Part;
                if (!isGhost && part.Row != selection && part.ParentRow != selection) continue;
                if (!parentCenters.TryGetValue(part.ParentRow, out Vector3 parent)) continue;
                if (Mathf.Approximately(part.Center.x, parent.x) && Mathf.Approximately(part.Center.y, parent.y)) continue;
                DrawProjectedLine(surface, projection, parent, part.Center, isGhost ? Ghost : Highlight, dashed: true);
            }
        }

        private static void DrawProjectedLine(Surface surface, Projection projection, Vector3 start, Vector3 end,
            Color32 color, bool dashed = false)
        {
            ProjectedVertex a = projection.Project(start);
            ProjectedVertex b = projection.Project(end);
            if (a.Valid && b.Valid)
                surface.Line(Vector2.Scale(a.Point, surface.PixelScale), Vector2.Scale(b.Point, surface.PixelScale), color, dashed);
        }

        private static void DrawAxes(Rect rect, Projection projection)
        {
            if (rect.width < 45f || rect.height < 45f) return;
            GameFont oldFont = Text.Font;
            TextAnchor oldAnchor = Text.Anchor;
            Color oldColor = GUI.color;
            GUI.BeginGroup(rect);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            Vector2 origin = new(21f, rect.height - 23f);
            foreach ((Vector3 axis, string label, Color color) in Axes)
            {
                Vector2 direction = new(Vector3.Dot(axis, projection.Right), -Vector3.Dot(axis, projection.Up));
                if (direction.sqrMagnitude < 0.025f) continue;
                Vector2 end = origin + direction * 17f;
                Widgets.DrawLine(origin, end, color, 1.5f);
                GUI.color = color;
                Widgets.Label(new Rect(end.x + 2f, end.y - 7f, 14f, 16f), label);
                GUI.color = oldColor;
            }
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
            GUI.EndGroup();
        }

        private sealed class Surface : IDisposable
        {
            private Color32[] pixels = [];
            private Color32[] ghostPixels = [];
            private float[] depth = [];
            private Vector2 size;
            private int sceneRevision = -1;
            private int viewRevision = -1;

            internal Texture2D? Texture { get; private set; }
            internal int Width { get; private set; }
            internal int Height { get; private set; }
            internal Vector2 PixelScale { get; private set; }

            internal bool NeedsRedraw(Vector2 requestedSize, int scene, int view) =>
                Texture == null || !size.Equals(requestedSize) || sceneRevision != scene || viewRevision != view;

            internal void Begin(Vector2 requestedSize)
            {
                size = requestedSize;
                float scale = Mathf.Min(Prefs.UIScale * 1.25f, MaxTextureSize / Mathf.Max(size.x, size.y));
                int width = Mathf.Max(1, Mathf.RoundToInt(size.x * scale));
                int height = Mathf.Max(1, Mathf.RoundToInt(size.y * scale));
                if (Texture == null || width != Width || height != Height)
                {
                    if (Texture != null) Object.Destroy(Texture);
                    Width = width;
                    Height = height;
                    pixels = new Color32[width * height];
                    depth = new float[pixels.Length];
                    ghostPixels = [];
                    Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                    {
                        name = "PF_RocketAssemblyViewport",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                    };
                }
                PixelScale = new Vector2(width / size.x, height / size.y);
                Array.Fill(pixels, Background);
                Array.Fill(depth, float.NegativeInfinity);
            }

            internal void BeginGhosts()
            {
                if (ghostPixels.Length != pixels.Length) ghostPixels = new Color32[pixels.Length];
                else Array.Clear(ghostPixels);
            }

            internal void CompositeGhosts()
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 overlay = ghostPixels[i];
                    if (overlay.a == 0) continue;
                    Color32 baseColor = pixels[i];
                    pixels[i] = new Color32((byte)((baseColor.r + overlay.r) / 2), (byte)((baseColor.g + overlay.g) / 2),
                        (byte)((baseColor.b + overlay.b) / 2), 255);
                }
            }

            internal void Fill(ProjectedVertex a, ProjectedVertex b, ProjectedVertex c, Color32 color, bool isGhost)
            {
                float left = Mathf.Min(a.Point.x, Mathf.Min(b.Point.x, c.Point.x));
                float right = Mathf.Max(a.Point.x, Mathf.Max(b.Point.x, c.Point.x));
                float top = Mathf.Min(a.Point.y, Mathf.Min(b.Point.y, c.Point.y));
                float bottom = Mathf.Max(a.Point.y, Mathf.Max(b.Point.y, c.Point.y));
                if (left >= Width || right < 0f || top >= Height || bottom < 0f) return;
                int minX = Mathf.FloorToInt(Mathf.Max(0f, left));
                int maxX = Mathf.CeilToInt(Mathf.Min(Width - 1, right));
                int minY = Mathf.FloorToInt(Mathf.Max(0f, top));
                int maxY = Mathf.CeilToInt(Mathf.Min(Height - 1, bottom));
                var sample = new TriangleSample(a, b, c);
                Color32[] target = isGhost ? ghostPixels : pixels;
                for (int y = minY; y <= maxY; y++)
                {
                    int index = Index(minX, y);
                    for (int x = minX; x <= maxX; x++, index++)
                    {
                        if (!sample.TryDepth(new Vector2(x + 0.5f, y + 0.5f), out float z) || z <= depth[index]) continue;
                        depth[index] = z;
                        target[index] = color;
                    }
                }
            }

            internal void Line(Vector2 start, Vector2 end, Color32 color, bool dashed = false)
            {
                // 在采样前裁剪，平移和缩放不会造成长线遍历或写到视口之外。
                Vector2 delta = end - start;
                float enter = 0f;
                float leave = 1f;
                if (!Clip(-delta.x, start.x, ref enter, ref leave)
                    || !Clip(delta.x, Width - 1 - start.x, ref enter, ref leave)
                    || !Clip(-delta.y, start.y, ref enter, ref leave)
                    || !Clip(delta.y, Height - 1 - start.y, ref enter, ref leave)) return;

                int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y)) * (leave - enter)));
                int dashLength = Mathf.Max(2, Mathf.RoundToInt(4f * PixelScale.x));
                for (int i = 0; i <= steps; i++)
                {
                    if (dashed && i / dashLength % 2 != 0) continue;
                    Vector2 point = start + delta * Mathf.Lerp(enter, leave, (float)i / steps);
                    int x = Mathf.Clamp(Mathf.RoundToInt(point.x), 0, Width - 1);
                    int y = Mathf.Clamp(Mathf.RoundToInt(point.y), 0, Height - 1);
                    pixels[Index(x, y)] = color;
                }
            }

            private static bool Clip(float direction, float distance, ref float enter, ref float leave)
            {
                if (direction == 0f) return distance >= 0f;
                float intersection = distance / direction;
                if (direction < 0f) enter = Mathf.Max(enter, intersection);
                else leave = Mathf.Min(leave, intersection);
                return enter <= leave;
            }

            // 视口坐标从左上往下，Texture2D 的首行位于纹理底部。
            private int Index(int x, int y) => (Height - y - 1) * Width + x;

            internal void Upload(int scene, int view)
            {
                Texture!.SetPixels32(pixels);
                Texture.Apply(false, false);
                sceneRevision = scene;
                viewRevision = view;
            }

            public void Dispose()
            {
                if (Texture != null) Object.Destroy(Texture);
                Texture = null;
                pixels = [];
                ghostPixels = [];
                depth = [];
            }
        }
    }
}
