using System;
using System.Collections.Generic;

namespace PioneerFramework.RingWorld
{
    /// <summary>装饰面材质类别：环壳金属、普通灯晕、垂直构件末端周期荧光。</summary>
    public enum RingOrnamentMaterial
    {
        Metal = 0,
        Glow = 1,
        TerminalGlow = 2,
        TerminalCore = 3,
    }

    /// <summary>
    /// 环外装饰几何的一个四边形（纯数据，无 Verse/Unity 依赖）。
    /// 约定与 <see cref="RingVisualQuad"/> 一致：绕序 A-B-C / A-C-D，UV 角点（U0,V0)/(U0,V1)/(U1,V1)/(U1,V0)。
    /// <see cref="Normal"/> = 面朝外法线（用于受光 shader 的 N；不是顶点法线插值近似）。
    /// 颜色通道：金属 = tint 的 rgb、a=1；光晕 = 光色 rgb、a=闪烁相位（0..1）。
    /// </summary>
    public readonly record struct RingOrnamentQuad(
        RingOrnamentMaterial Material,
        DVector3 A, DVector3 B, DVector3 C, DVector3 D,
        DVector3 Normal,
        double U0, double V0, double U1, double V1,
        double Red, double Green, double Blue, double Alpha);

    /// <summary>
    /// 装饰件数量布局（按列数取节奏，且夹在契约区间内）。
    /// 2026-10-17 用户反馈后新增 <see cref="Pillars"/>/<see cref="Pods"/>/<see cref="Panels"/> 三组；
    /// 原 Towers/Fins/Beacons 语义与计数口径不变（既有测试按属性名读取，保持兼容）。
    /// </summary>
    public readonly record struct RingOrnamentLayout(
        int Towers, int Fins, int Beacons, int Pillars, int Pods, int Panels);

    /// <summary>
    /// 环外装饰性构件的纯几何生成器（IMPL §5.2 + 2026-10-17 用户反馈扩展）：
    /// 外挂塔/桅杆、散热鳍/检修臂、自由漂浮信标、**垂直于环平面的信号柱**、外挂货舱/检修舱段、天线阵面板。
    ///
    /// ## 契约（实现期严格照做）
    /// · **确定性**：不使用 Rand/时间；所有抖动来自整数哈希 <see cref="Hash01"/>（同输入逐位同输出）。
    /// · **预算**：任意 <see cref="RingGeometry"/> 的四边形总数 ≤ <see cref="MaxQuads"/>（在 Build 末尾 clamp）。
    ///   最大布局实测 5860 面，包含实体发光灯头、支撑和信标外壳；所有实体面闭合。
    /// · **不侵入可拾取面**：所有几何的径向半径 > Radius（居住面 hex 所在圆柱之外）；
    ///   本层不生成 collider、不参与 tile 映射（见 WorldDrawLayer_RingOrnaments）。
    /// · **坐标**：环本地坐标系（Radius 圆柱、y=轴向、原点=环心），由现有 RingWorldMotion 的 Rotation 补丁统一旋转。
    /// · **视觉纪律（用户反馈 2026-10-17）**：禁止 X 形交叉斜撑——每根桅杆只允许 1 根斜撑、梯级平行；
    ///   新增结构强调径向外凸/轴向上下层次，不做细长交叉线框。
    /// · 纯核：只依赖 System/DVector3/RingGeometry，可被 Pure.Tests 链接。
    /// </summary>
    public static class RingOrnamentGeometry
    {
        /// <summary>装饰四边形总量硬上限（契约 §5.2：任意网格 ≤6000）。</summary>
        public const int MaxQuads = 6000;

        /// <summary>加性光斑（Glow sprite）尺寸相对初版的放大系数（用户反馈"荧光更强"，+20%）。</summary>
        public const double GlowScale = 1.2;

        // 单个构件的四边形数（供测试/预算口径引用）。
        public const int QuadsPerTower = 31;    // 塔身 6 + 横臂 6 + 斜撑 12 + 灯座 6 + 灯晕 1
        public const int QuadsPerFin = 6;       // 薄盒 6 面
        public const int QuadsPerBeacon = 8;    // 实体信标 6 + 灯晕双片 2
        public const int QuadsPerPillar = 50;   // 基座/桅杆/横臂/梯级×2/单斜撑/天线盘/灯座各 6 + 灯晕 2
        public const int QuadsPerPod = 18;      // 舱体/装甲凸块/检修盖板各 6
        // 立板/支撑杆×2/顶部天线碟各 6，加灯座 6 与灯晕 2。
        public const int QuadsPerPanel = 32;

        /// <summary>专用方形金属贴图每次重复覆盖的世界长度；两个 UV 轴使用相同尺度。</summary>
        public const double MetalTextureWorldSize = 32.0;

        /// <summary>将装饰纯几何的等比例 UV 转成环壳 4:1 贴图 UV，像素密度与环壳轴向一致。</summary>
        public static (double U, double V) HullUV(RingGeometry geometry, RingOrnamentQuad quad, double u, double v)
        {
            double scale = MetalTextureWorldSize / (2 * geometry.HalfWidth);
            return (quad.U0 + (u - quad.U0) * scale / 4, quad.V0 + (v - quad.V0) * scale);
        }

        /// <summary>
        /// 按列数算出六类装饰件的数量（256×3 → 16/8/12/12/8/8；1024×12 → 64/32/32/34/16/21）。
        /// 数量只与列数有关且全部 clamp（大环/极端网格不会爆量）。
        /// </summary>
        public static RingOrnamentLayout Layout(RingGeometry geometry)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            int columns = geometry.Topology.Columns;
            return new RingOrnamentLayout(
                Towers: Math.Clamp(columns / 16, 16, 64),
                Fins: Math.Clamp(columns / 32, 8, 32),
                Beacons: Math.Clamp(columns / 32, 12, 48),
                Pillars: Math.Clamp(columns / 24, 12, 34),
                Pods: Math.Clamp(columns / 64, 8, 24),
                Panels: Math.Clamp(columns / 48, 8, 24));
        }

        /// <summary>生成全部装饰四边形（单位=视图单位；NaN/Inf 不产生）。</summary>
        public static IReadOnlyList<RingOrnamentQuad> Build(RingGeometry geometry)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            RingOrnamentLayout layout = Layout(geometry);
            double halfWidth = geometry.RowSpacing * (geometry.Topology.Rows - 1) / 2 + geometry.Spacing / Math.Sqrt(3);
            // 起点**埋进外壳外缘**（壳外缘≈Radius+0.285*Spacing，见 RingIndustrialGeometry.Build 的 back/pipe）：
            // 装饰看起来是"装在壳体上"而不是浮空。下限 1.15 是为新构件预留的向内余量
            // （信号柱天线盘/舱段盖板等在径向上可向内凹 ≤0.9）；所有点仍严格 > Radius，不侵入可拾取面。
            double baseRadius = geometry.Radius + Math.Clamp(geometry.Spacing * 0.22, 1.15, 2.0);
            int capacity = layout.Towers * QuadsPerTower
                + layout.Fins * QuadsPerFin + layout.Beacons * QuadsPerBeacon
                + layout.Pillars * 2 * QuadsPerPillar   // 信号柱按"成对"上限预留
                + layout.Pods * QuadsPerPod + layout.Panels * QuadsPerPanel;
            var quads = new List<RingOrnamentQuad>(Math.Min(MaxQuads, Math.Max(64, capacity)));

            for (int i = 0; i < layout.Towers && quads.Count < MaxQuads; i++)
                AddTower(quads, geometry, layout.Towers, i, baseRadius, halfWidth);
            for (int i = 0; i < layout.Fins && quads.Count < MaxQuads; i++)
                AddFin(quads, geometry, layout.Fins, i, baseRadius, halfWidth);
            for (int i = 0; i < layout.Beacons && quads.Count < MaxQuads; i++)
                AddBeacon(quads, geometry, layout.Beacons, i, halfWidth);
            // ── 2026-10-17 用户反馈新增：垂直环平面的上下结构 + 凹凸层次 ──
            for (int i = 0; i < layout.Pillars && quads.Count < MaxQuads; i++)
                AddSignalPillar(quads, layout.Pillars, i, baseRadius, halfWidth);
            for (int i = 0; i < layout.Pods && quads.Count < MaxQuads; i++)
                AddPod(quads, layout.Pods, i, baseRadius, halfWidth);
            for (int i = 0; i < layout.Panels && quads.Count < MaxQuads; i++)
                AddPanel(quads, layout.Panels, i, baseRadius, halfWidth);

            if (quads.Count > MaxQuads) quads.RemoveRange(MaxQuads, quads.Count - MaxQuads);
            return quads;
        }

        // ── 构件：外挂塔/桅杆（原 task-8） ───────────────────────────────

        private static void AddTower(List<RingOrnamentQuad> quads, RingGeometry geometry, int count, int index,
            double baseRadius, double halfWidth)
        {
            int seed = 0x51ED27 + index * 131;
            double turn = (index + 0.5 + Range(seed + 1, -0.28, 0.28)) / count;
            double yCenter = Range(seed + 2, -0.55, 0.55) * halfWidth;
            double yHalf = Math.Min(halfWidth, Range(seed + 3, 0.28, 0.55) * halfWidth + 0.25);
            double height = Range(seed + 4, 2.0, 8.0);
            double tangentHalf = Range(seed + 5, 0.28, 0.75);
            double tint = Range(seed + 6, 0.55, 0.85);
            double uOffset = Range(seed + 7, 0.0, 0.6);

            // 塔身（径向 6 面盒）
            AddBoxSpan(quads, turn, baseRadius, baseRadius + height, yCenter - yHalf, yCenter + yHalf,
                -tangentHalf, tangentHalf, uOffset, uOffset + 0.16, tint);

            // 横臂（沿切向，径向中段）
            double armRadius = baseRadius + height * 0.62;
            AddBoxSpan(quads, turn, armRadius - 0.18, armRadius + 0.18, yCenter - Math.Min(0.22, yHalf * 0.4),
                yCenter + Math.Min(0.22, yHalf * 0.4), -tangentHalf * 2.4, tangentHalf * 2.4,
                uOffset + 0.16, uOffset + 0.32, tint);

            // 两根实体斜撑，从外壳根部斜向塔身中段，分列切向两侧。
            for (int side = -1; side <= 1; side += 2)
            {
                double sk = side * (tangentHalf + 0.85);
                double yBase = yCenter + side * yHalf * 0.85;
                double yMid = yCenter + side * yHalf * 0.25;
                double rBase = Math.Max(geometry.Radius + 0.85,
                    baseRadius - Math.Min(1.2, geometry.Spacing * 0.12));
                double rMid = baseRadius + height * 0.5;
                DVector3 a = Local(turn, yBase, rBase, sk);
                DVector3 b = Local(turn, yMid, rMid, sk * 0.75);
                AddBeam(quads, a, b, Tangent(turn, side), uOffset + 0.32, tint);
            }

            AddBoxSpan(quads, turn, baseRadius + height - 0.10, baseRadius + height + 0.65,
                yCenter - 0.35, yCenter + 0.35, -0.35, 0.35, uOffset, uOffset + 0.16, tint * 1.1);
            // 顶端红灯（加性 sprite；2026-10-17 用户反馈：尺寸 +20%）
            AddSprite(quads, turn, yCenter, baseRadius + height + 0.70, Range(seed + 8, 0.55, 0.95) * GlowScale,
                1.0, 0.32, 0.045, Range(seed + 9, 0.0, 1.0));
        }

        // ── 构件：散热鳍 / 检修臂（原 task-8） ──────────────────────────

        private static void AddFin(List<RingOrnamentQuad> quads, RingGeometry geometry, int count, int index,
            double baseRadius, double halfWidth)
        {
            int seed = 0x7A31C9 + index * 197;
            double turn = (index + 0.5 + Range(seed + 1, -0.45, 0.45)) / count;
            double yCenter = Range(seed + 2, -0.6, 0.6) * halfWidth;
            double yHalf = Math.Min(halfWidth * 0.7, Range(seed + 3, 0.75, 1.6));
            double height = Range(seed + 4, 3.5, 7.0);
            double tangentHalf = Range(seed + 5, 0.25, 0.35);
            double tint = Range(seed + 6, 0.45, 0.68);
            double uOffset = Range(seed + 7, 0.0, 0.6);
            AddBoxSpan(quads, turn, baseRadius - Math.Min(0.6, geometry.Spacing * 0.06), baseRadius + height,
                yCenter - yHalf, yCenter + yHalf, -tangentHalf, tangentHalf,
                uOffset, uOffset + 0.35, tint);
        }

        // ── 构件：自由漂浮信标（原 task-8） ─────────────────────────────

        private static void AddBeacon(List<RingOrnamentQuad> quads, RingGeometry geometry, int count, int index,
            double halfWidth)
        {
            int seed = 0x2C9E5B + index * 313;
            double turn = (index + 0.5 + Range(seed + 1, -0.45, 0.45)) / count;
            double radius = geometry.Radius + Range(seed + 2, 12.0, 25.0);
            double side = Hash01(seed + 3) < 0.5 ? -1.0 : 1.0;
            double y = side * (halfWidth + Range(seed + 4, 2.0, 6.0));
            double size = Range(seed + 5, 0.6, 1.35) * GlowScale;   // 2026-10-17：+20%
            double phase = Range(seed + 6, 0.0, 1.0);
            bool warm = Hash01(seed + 7) < 0.45;
            double red = warm ? 1.0 : 0.55;
            double green = warm ? 0.55 : 0.82;
            double blue = warm ? 0.18 : 1.0;

            AddBoxSpan(quads, turn, radius - 0.38, radius + 0.38, y - 0.45, y + 0.45,
                -0.38, 0.38, 0.2, 0.4, 0.8);
            DVector3 center = Local(turn, y, radius, 0);
            DVector3 radial = Radial(turn);
            DVector3 tangent = Tangent(turn, 1);
            double h = size * 0.5;
            // 两片灯晕分别放在实体外表面之外，防止写深度的外壳遮掉最亮中心。
            DVector3 radialLight = center + radial * 0.40;
            DVector3 tangentLight = center + tangent * 0.40;
            AddQuad(quads, RingOrnamentMaterial.Glow,
                radialLight - tangent * h - Up * h, radialLight - tangent * h + Up * h,
                radialLight + tangent * h + Up * h, radialLight + tangent * h - Up * h,
                radial, 0.0, 1.0, 0.0, 1.0, red, green, blue, phase);
            AddQuad(quads, RingOrnamentMaterial.Glow,
                tangentLight - radial * h - Up * h, tangentLight - radial * h + Up * h,
                tangentLight + radial * h + Up * h, tangentLight + radial * h - Up * h,
                tangent, 0.0, 1.0, 0.0, 1.0, red, green, blue, phase);
        }

        // ── 构件：信号柱（垂直于环平面；2026-10-17 用户反馈新增） ───────

        /// <summary>
        /// 信号柱：从环带上下边缘沿 ±Y 向外延伸（高度 6–16 单位）。
        /// 35% 的组上下成对，其余按索交替单侧；每根桅杆只有 1 根斜撑、梯级平行 ⇒ 不出现 X 形交叉。
        /// 每组最多 2 根桅杆（≤ QuadsPerPillar×2），预算在 Build 的容量预留里已计入。
        /// </summary>
        private static void AddSignalPillar(List<RingOrnamentQuad> quads, int count, int index,
            double baseRadius, double halfWidth)
        {
            int seed = 0x6B4A11 + index * 251;
            double turn = (index + 0.5 + Range(seed + 1, -0.22, 0.22)) / count;
            double height = Range(seed + 2, 6.0, 16.0);
            double tint = Range(seed + 3, 0.50, 0.78);
            double uOffset = Range(seed + 4, 0.0, 0.10);
            int primary = (index & 1) == 0 ? 1 : -1;
            bool paired = Hash01(seed + 5) < 0.35;
            double phase = Range(seed + 6, 0.0, 1.0);

            AddPillarMast(quads, turn, primary, height, baseRadius, halfWidth, tint, uOffset, seed + 10, phase);
            if (paired)
                AddPillarMast(quads, turn, -primary, height * Range(seed + 7, 0.62, 1.0), baseRadius, halfWidth,
                    tint * 0.9, uOffset, seed + 40, Range(seed + 8, 0.0, 1.0));
        }

        private static void AddPillarMast(List<RingOrnamentQuad> quads, double turn, int side, double height,
            double baseRadius, double halfWidth, double tint, double uOffset, int seed, double phase)
        {
            double dir = side;                                  // +1 向上（+Y），-1 向下
            double yEdge = side * (halfWidth + 0.12);           // 环带边缘外侧起步
            double yBaseOut = yEdge + dir * 1.9;                // 基座块高 1.9
            double yMastOut = yBaseOut + dir * height;          // 桅杆顶端

            // 基座块（压在环带边缘，径向/切向都宽 ⇒ 侧面看有明确层次）
            AddBoxSpan(quads, turn, baseRadius - 0.35, baseRadius + 1.5, yEdge - dir * 0.5, yBaseOut,
                -1.25, 1.25, uOffset, uOffset + 0.12, tint);

            // 实体桅杆（沿 ±Y）
            AddBoxSpan(quads, turn, baseRadius + 0.15, baseRadius + 0.95, yBaseOut - dir * 0.35, yMastOut,
                -0.38, 0.38, uOffset + 0.12, uOffset + 0.26, tint);

            // 中部横臂（切向长、轴向薄）
            double yArm = yBaseOut + dir * height * 0.48;
            AddBoxSpan(quads, turn, baseRadius - 0.30, baseRadius + 1.15, yArm - 0.20, yArm + 0.20,
                -2.1, 2.1, uOffset + 0.26, uOffset + 0.38, tint * 0.9);

            // 梯级使用闭合盒体，侧面和背面均有受光表面。
            for (int rung = 0; rung < 2; rung++)
            {
                double y = yBaseOut + dir * height * (0.26 + 0.10 * rung);
                double rr = baseRadius + 0.95;
                AddBoxSpan(quads, turn, rr - 0.15, rr + 0.30, y - 0.22, y + 0.22,
                    -1.05, 1.05, uOffset + 0.38, uOffset + 0.46, tint * 1.05);
            }

            // 单根斜撑（从基座外侧斜向桅杆中段；只有 1 根 ⇒ 不构成 X 交叉）
            {
                DVector3 a = Local(turn, yEdge + dir * 0.35, baseRadius + 1.35, -1.15);
                DVector3 b = Local(turn, yBaseOut + dir * height * 0.55, baseRadius + 0.75, 0.30);
                AddBeam(quads, a, b, Tangent(turn, -1), uOffset + 0.46, tint * 0.95);
            }

            // 顶部天线盘/板（轴向薄、径向/切向宽 ⇒ 加强"上下有结构"的轮廓）
            double yPlate = yMastOut - dir * 0.35;
            AddBoxSpan(quads, turn, baseRadius - 0.90, baseRadius + 2.10, yPlate - 0.22, yPlate + 0.22,
                -1.70, 1.70, uOffset + 0.52, uOffset + 0.64, tint * 1.1);

            // 顶端警示灯：十字双片（水平片法线 ±Y + 竖直片法线径向 ⇒ 边缘/俯视都可见）
            AddPillarLight(quads, turn, yMastOut + dir * 0.55, baseRadius + 0.55,
                Range(seed + 1, 0.75, 1.15) * GlowScale * 1.4, dir, phase);
        }

        private static void AddPillarLight(List<RingOrnamentQuad> quads, double turn, double y, double radius,
            double size, double dir, double phase)
        {
            // 先构造闭合灯头，再标记为专用自发光材质；六个方向均有可见发光表面。
            int coreStart = quads.Count;
            AddBoxSpan(quads, turn, radius - 0.7, radius + 0.7, y - 0.5, y + 0.5,
                -0.7, 0.7, 0.3, 0.5, 1.0);
            for (int i = coreStart; i < quads.Count; i++)
                quads[i] = quads[i] with { Material = RingOrnamentMaterial.TerminalCore,
                    Red = 0.45, Green = 0.78, Blue = 1.0, Alpha = phase };
            DVector3 center = Local(turn, y, radius, 0);
            DVector3 radial = Radial(turn);
            DVector3 tangent = Tangent(turn, 1);
            double h = Math.Max(10.5, size) * 0.5;
            DVector3 up = dir > 0 ? Up : -Up;
            DVector3 axialLight = center + up * 0.52;
            DVector3 radialLight = center + radial * 0.72;
            // 水平片（朝环平面外侧）
            AddQuad(quads, RingOrnamentMaterial.TerminalGlow,
                axialLight - tangent * h - radial * 1.1, axialLight - tangent * h + radial * 1.1,
                axialLight + tangent * h + radial * 1.1, axialLight + tangent * h - radial * 1.1,
                up, 0.0, 1.0, 0.0, 1.0, 0.45, 0.78, 1.0, phase);
            // 竖直片（朝径向外侧）
            AddQuad(quads, RingOrnamentMaterial.TerminalGlow,
                radialLight - tangent * h - Up * h, radialLight - tangent * h + Up * h,
                radialLight + tangent * h + Up * h, radialLight + tangent * h - Up * h,
                radial, 0.0, 1.0, 0.0, 1.0, 0.45, 0.78, 1.0, phase);
        }

        // ── 构件：外挂货舱/检修舱段（凹凸层次；2026-10-17 新增） ────────

        private static void AddPod(List<RingOrnamentQuad> quads, int count, int index,
            double baseRadius, double halfWidth)
        {
            int seed = 0x1D77A3 + index * 173;
            double turn = (index + 0.5 + Range(seed + 1, -0.40, 0.40)) / count;
            double yCenter = Range(seed + 2, -0.50, 0.50) * halfWidth;
            double yHalf = Math.Min(halfWidth * 0.75, Range(seed + 3, 0.9, 2.0));
            double height = Range(seed + 4, 2.5, 5.0);
            double tangentHalf = Range(seed + 5, 1.1, 2.4);
            double tint = Range(seed + 6, 0.42, 0.66);
            double uOffset = Range(seed + 7, 0.0, 0.10);

            // 主舱段（径向外凸块）
            AddBoxSpan(quads, turn, baseRadius - 0.40, baseRadius + height, yCenter - yHalf, yCenter + yHalf,
                -tangentHalf, tangentHalf, uOffset, uOffset + 0.30, tint);

            // 装甲凸块：主舱外表面再凸一层（侧面看有前后层次）
            AddBoxSpan(quads, turn, baseRadius + height - 0.20, baseRadius + height + height * 0.45,
                yCenter - yHalf * 0.45, yCenter + yHalf * 0.45,
                -tangentHalf * 0.50, tangentHalf * 0.50, uOffset + 0.30, uOffset + 0.44, tint * 1.18);

            // 检修盖板有独立厚度，边缘可以受光。
            double rFace = baseRadius + height + 0.02;
            DVector3 a = Local(turn, yCenter - yHalf * 0.30, rFace, -tangentHalf * 0.30);
            DVector3 b = Local(turn, yCenter - yHalf * 0.30, rFace, tangentHalf * 0.30);
            DVector3 c = Local(turn, yCenter + yHalf * 0.30, rFace, tangentHalf * 0.30);
            DVector3 d = Local(turn, yCenter + yHalf * 0.30, rFace, -tangentHalf * 0.30);
            AddPlate(quads, a, b, c, d, 0.4, uOffset + 0.44, tint * 0.78);
        }

        // ── 构件：天线阵面板（上下层次；2026-10-17 新增） ───────────────

        private static void AddPanel(List<RingOrnamentQuad> quads, int count, int index,
            double baseRadius, double halfWidth)
        {
            int seed = 0x3F2D07 + index * 293;
            double turn = (index + 0.5 + Range(seed + 1, -0.40, 0.40)) / count;
            int side = (index & 1) == 0 ? 1 : -1;
            double dir = side;
            double height = Range(seed + 2, 4.0, 9.0);
            double tangentHalf = Range(seed + 3, 1.2, 2.2);
            double tint = Range(seed + 4, 0.46, 0.70);
            double uOffset = Range(seed + 5, 0.0, 0.10);
            double yEdge = side * (halfWidth + 0.10);
            double yTop = yEdge + dir * height;

            // 竖直立板（径向薄、轴向高、切向宽）
            AddBoxSpan(quads, turn, baseRadius - 0.20, baseRadius + 0.55, yEdge, yTop,
                -tangentHalf, tangentHalf, uOffset, uOffset + 0.30, tint);

            // 两根平行实体支撑杆。
            for (int strut = 0; strut < 2; strut++)
            {
                double t = (strut == 0 ? -1.0 : 1.0) * tangentHalf * 0.62;
                DVector3 a = Local(turn, yEdge + dir * 0.15, baseRadius - 0.45, t);
                DVector3 b = Local(turn, yEdge + dir * height * 0.38, baseRadius + 0.45, t * 0.9);
                AddBeam(quads, a, b, Tangent(turn, strut == 0 ? -1 : 1), uOffset + 0.30, tint * 0.92);
            }

            // 顶部天线碟（径向略微外伸的小盒 ⇒ 侧面看有明确上下层次）
            AddBoxSpan(quads, turn, baseRadius - 0.50, baseRadius + 1.20, yTop - dir * 0.30, yTop + dir * 0.30,
                -tangentHalf * 0.42, tangentHalf * 0.42, uOffset + 0.38, uOffset + 0.52, tint * 1.08);
            AddPillarLight(quads, turn, yTop + dir * 0.75, baseRadius + 0.55,
                Range(seed + 6, 1.2, 1.8) * GlowScale, dir, Range(seed + 7, 0, 1));
        }

        // ── 几何工具 ─────────────────────────────────────────────────────

        private static DVector3 Local(double turn, double y, double radius, double tangentOffset)
        {
            turn -= Math.Floor(turn);
            double angle = turn * Math.Tau;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            return new DVector3(cos * radius - sin * tangentOffset, y, sin * radius + cos * tangentOffset);
        }

        private static DVector3 Radial(double turn) => new(Math.Cos(turn * Math.Tau), 0, Math.Sin(turn * Math.Tau));

        private static DVector3 Tangent(double turn, double sign) =>
            new(-Math.Sin(turn * Math.Tau) * sign, 0, Math.Cos(turn * Math.Tau) * sign);

        private static readonly DVector3 Up = new(0, 1, 0);

        /// <summary>盒体简写：径向 r0..r1 / 轴向 y0..y1 / 切向 t0..t1（区间自动排序，负方向构造也安全）。</summary>
        private static void AddBoxSpan(List<RingOrnamentQuad> quads, double turn, double r0, double r1,
            double y0, double y1, double t0, double t1, double u0, double u1, double tint) =>
            AddBox(quads, turn, Math.Min(r0, r1), Math.Max(r0, r1), Math.Min(y0, y1), Math.Max(y0, y1),
                Math.Min(t0, t1), Math.Max(t0, t1), u0, u1, tint);

        private static void AddBox(List<RingOrnamentQuad> quads, double turn, double r0, double r1,
            double y0, double y1, double t0, double t1, double u0, double u1, double tint)
        {
            DVector3 radial = Radial(turn);
            DVector3 tangent = Tangent(turn, 1);
            DVector3 p00 = Local(turn, y0, r0, t0), p01 = Local(turn, y1, r0, t0);
            DVector3 p10 = Local(turn, y0, r1, t0), p11 = Local(turn, y1, r1, t0);
            DVector3 q00 = Local(turn, y0, r0, t1), q01 = Local(turn, y1, r0, t1);
            DVector3 q10 = Local(turn, y0, r1, t1), q11 = Local(turn, y1, r1, t1);
            // 外径向面
            AddQuad(quads, RingOrnamentMaterial.Metal, p10, p11, q11, q10, radial, u0, u1, 0.0, 1.0, tint);
            // 内径向面
            AddQuad(quads, RingOrnamentMaterial.Metal, p00, q00, q01, p01, -radial, u0, u1, 0.0, 1.0, tint * 0.7);
            // 切向两个侧面
            AddQuad(quads, RingOrnamentMaterial.Metal, p10, p00, p01, p11, -tangent, u0, u1, 0.0, 1.0, tint * 0.85);
            AddQuad(quads, RingOrnamentMaterial.Metal, q10, q11, q01, q00, tangent, u0, u1, 0.0, 1.0, tint * 0.85);
            // 轴向两个端面
            AddQuad(quads, RingOrnamentMaterial.Metal, p11, p01, q01, q11, Up, u0, u1, 0.0, 1.0, tint);
            AddQuad(quads, RingOrnamentMaterial.Metal, p00, p10, q10, q00, -Up, u0, u1, 0.0, 1.0, tint * 0.8);
        }

        private static void AddBeam(List<RingOrnamentQuad> quads, DVector3 start, DVector3 end,
            DVector3 outward, double uvOffset, double tint)
        {
            DVector3 across = DVector3.Cross(outward, end - start).normalized * 0.225;
            AddPlate(quads, start - across, end - across, end + across, start + across,
                0.45, uvOffset, tint);
        }

        private static void AddPlate(List<RingOrnamentQuad> quads, DVector3 a, DVector3 b,
            DVector3 c, DVector3 d, double thickness, double uvOffset, double tint)
        {
            DVector3 delta = DVector3.Cross(b - a, c - a).normalized * (thickness * 0.5);
            DVector3 center = (a + b + c + d) / 4;
            DVector3 ap = a + delta, bp = b + delta, cp = c + delta, dp = d + delta;
            DVector3 am = a - delta, bm = b - delta, cm = c - delta, dm = d - delta;
            Face(ap, bp, cp, dp);
            Face(am, dm, cm, bm);
            Face(ap, am, bm, bp);
            Face(bp, bm, cm, cp);
            Face(cp, cm, dm, dp);
            Face(dp, dm, am, ap);

            void Face(DVector3 p, DVector3 q, DVector3 r, DVector3 s) =>
                AddQuad(quads, RingOrnamentMaterial.Metal, p, q, r, s,
                    (p + q + r + s) / 4 - center, uvOffset, uvOffset, 0, 0, tint);
        }

        private static void AddSprite(List<RingOrnamentQuad> quads, double turn, double y, double radius,
            double size, double red, double green, double blue, double phase)
        {
            // 相机无关的方片：沿径向面向外（cull=None，双面可见）；UV 0..1 供 sprite 模式取径向衰减。
            DVector3 center = Local(turn, y, radius, 0);
            DVector3 radial = Radial(turn);
            DVector3 tangent = Tangent(turn, 1);
            double h = size * 0.5;
            AddQuad(quads, RingOrnamentMaterial.Glow,
                center - tangent * h - Up * h,
                center - tangent * h + Up * h,
                center + tangent * h + Up * h,
                center + tangent * h - Up * h,
                radial, 0.0, 1.0, 0.0, 1.0, red, green, blue, phase);
        }

        /// <summary>
        /// 追加一个四边形：先按 <paramref name="outward"/> 纠正绕序（A-B-C/A-C-D 的正面 = 外法线），
        /// 退化面（零面积）直接丢弃。UV 角点固定 A=(u0,v0) B=(u0,v1) C=(u1,v1) D=(u1,v0)。
        /// </summary>
        private static void AddQuad(List<RingOrnamentQuad> quads, RingOrnamentMaterial material,
            DVector3 a, DVector3 b, DVector3 c, DVector3 d, DVector3 outward,
            double u0, double u1, double v0, double v1,
            double red, double green, double blue, double alpha)
        {
            if (quads.Count >= MaxQuads) return;
            DVector3 normal = DVector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 1e-18) return;
            if (DVector3.Dot(normal, outward) < 0)
            {
                (b, d) = (d, b);
                normal = DVector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 1e-18) return;
            }
            normal = normal.normalized;
            if (material == RingOrnamentMaterial.Metal)
            {
                // 绕序修正之后再测量两个 UV 轴，避免翻面交换 B/D 后再次拉伸。
                // u0 只作为确定性图案偏移；贴图密度与构件尺寸、类别和环预设无关。
                v0 = u0 * 0.37;
                u1 = u0 + (d - a).magnitude / MetalTextureWorldSize;
                v1 = v0 + (b - a).magnitude / MetalTextureWorldSize;
            }
            quads.Add(new RingOrnamentQuad(material, a, b, c, d, normal,
                u0, v0, u1, v1, red, green, blue, alpha));
        }

        private static void AddQuad(List<RingOrnamentQuad> quads, RingOrnamentMaterial material,
            DVector3 a, DVector3 b, DVector3 c, DVector3 d, DVector3 outward,
            double u0, double u1, double v0, double v1, double tint) =>
            AddQuad(quads, material, a, b, c, d, outward, u0, u1, v0, v1, tint, tint, tint * 1.05, 1.0);

        // ── 确定性哈希（无 Rand；整数位运算跨平台逐位一致） ─────────────

        private static double Hash01(int seed)
        {
            unchecked
            {
                uint x = (uint)seed * 0x9E3779B1u;
                x ^= x >> 16;
                x *= 0x85EBCA6Bu;
                x ^= x >> 13;
                x *= 0xC2B2AE35u;
                x ^= x >> 16;
                return x / 4294967296.0;
            }
        }

        private static double Range(int seed, double min, double max) => min + (max - min) * Hash01(seed);
    }
}
