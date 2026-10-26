using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// **PFView 的太阳 = 一个圆片**（2026-10-11 用户口径：「把太阳渲染成一个圆——**大小只和当前行星系环绕太阳的半径有关系**」）。
    ///
    /// 口径（讨论后四条裁定）：
    /// 1. **大小 = 角大小**（不是屏幕像素）：`角半径 = atan(R_sun / d_orbit)` ⇒ 对同一颗行星是定值，
    ///    变焦时它在屏幕上跟着放大（与"太阳是天体"一致）；换行星/改 XML 自动跟着变；
    /// 2. **只改 PFView**（世界视图那条"太阳就画原版"照旧 ⇒ 两边数值不同是**已知且接受**的）；
    /// 3. 圆片固定在**倒数第二层**（除星空盒外任何东西都能遮挡它）；
    /// 4. 倒数第一层 = **星空盒**，复用世界视图那套星表（同种子 ⇒ 同一片星空），
    ///    且**大气里按「透明度 = 1 − 天空 α」淡出**。
    ///
    /// 本文件只有**纯算术**（角大小 + 屏幕像素换算 + 兜底），渲染那一半在 `MainTabWindow_PFView.Scene.cs`。
    /// </summary>
    internal static class PFViewSunDisc
    {
        /// <summary>
        /// 取不到数据时的兜底角半径（度）= 本 mod 地球档的值（3000 km ÷ 120,000 km ≈ 1.43°）。
        /// 只在 `sunRadius` 或 `orbitRadius` 非正（XML 没填/该行星不是绕恒星的）时用。
        /// </summary>
        public const float DefaultAngularRadiusDeg = (float)PFViewSolarCore.DefaultAngularRadiusDegrees;

        /// <summary>让默认天体机位面向背景太阳，保留原有俯仰限位；竖直方向也有正交的 up。</summary>
        public static Quaternion FocusOrbitFrame(Vector3 toSun)
        {
            float lengthSquared = toSun.sqrMagnitude;
            toSun = float.IsFinite(lengthSquared) && lengthSquared > 1e-12f
                ? toSun / Mathf.Sqrt(lengthSquared) : -Vector3.forward;
            return PFViewCameraCore.MinimalRotation(
                PFViewCameraCore.OrbitDir(0f, PFViewCameraCore.DefaultPitch), -toSun,
                PFViewCameraCore.OrbitUp(0f, PFViewCameraCore.DefaultPitch));
        }

        /// <summary>
        /// **太阳的角半径（度）**：`atan(R_sun / d_orbit)`。
        /// `d_orbit` = **当前行星系环绕太阳的半径** = 根行星的 `CelestialBodyDef.orbitRadius`（地球 = 1.2e8 m）。
        /// ⚠ 只在"根行星"上取：飞船在月球时，太阳大小仍由**地球绕日半径**定 ✓（月球绕的是地球，不是太阳）。
        /// 任一参数非正 ⇒ 返回 <see cref="DefaultAngularRadiusDeg"/>（不抛、不返回 0 ⇒ 宁可给个可见的太阳）。
        /// </summary>
        public static float AngularRadiusDeg(double sunRadiusMeters, double orbitRadiusMeters)
        {
            if (sunRadiusMeters <= 0.0 || orbitRadiusMeters <= 0.0) return DefaultAngularRadiusDeg;
            double ang = System.Math.Atan(sunRadiusMeters / orbitRadiusMeters) * (180.0 / System.Math.PI);
            return (float)ang;
        }

        /// <summary>
        /// 角直径换算成**屏幕像素直径**（纯读数用，供日志/核对）：
        /// `px = 2·tan(角直径/2) / (2·tan(fov/2)) × 屏高`。
        /// </summary>
        public static float DiameterPixels(float angularRadiusDeg, float verticalFovDeg, float screenHeight)
        {
            if (verticalFovDeg <= 0f || screenHeight <= 0f) return 0f;
            float r = angularRadiusDeg * Mathf.Deg2Rad;
            float halfFov = verticalFovDeg * 0.5f * Mathf.Deg2Rad;
            return 2f * Mathf.Tan(r) / (2f * Mathf.Tan(halfFov)) * screenHeight;
        }

        /// <summary>
        /// 圆片的**世界尺寸**（视图单位）：放在 `placeDist` 处、要看起来是 `angularRadiusDeg` ⇒ 四边形的边长 = `2·placeDist·tan(角半径)`。
        /// （正交/透视都成立；PFView 用透视相机。）
        /// </summary>
        public static float QuadSizeAtDistance(float placeDist, float angularRadiusDeg)
        {
            if (placeDist <= 0f) return 0f;
            return 2f * placeDist * Mathf.Tan(angularRadiusDeg * Mathf.Deg2Rad);
        }

        /// <summary>
        /// 圆盘贴图里**实心芯**占四边形半宽的比例（贴图生成与摆位尺寸**共用这一个数**）。
        /// 0.90→1.00 之间是柔边（smoothstep 收到 0）。摆位时把四边形放大 1/该值
        /// （见 <see cref="QuadSizeForDisc"/>）⇒ **实心圆盘的角半径 = 名义值**（1.43° 就是 1.43°，不是 0.9×）。
        /// </summary>
        public const float DiscCoreFraction = 0.90f;

        /// <summary>
        /// **给"实心圆盘要看起来是 `angularRadiusDeg`"用的四边形边长**：
        /// 贴图的实心芯只占半宽的 <see cref="DiscCoreFraction"/> ⇒ 四边形要按比例放大，
        /// 好让**芯**（不是外沿柔边）恰好对着名义角半径。
        /// </summary>
        public static float QuadSizeForDisc(float placeDist, float angularRadiusDeg)
        {
            return QuadSizeAtDistance(placeDist, angularRadiusDeg) / DiscCoreFraction;
        }

        /// <summary>
        /// 圆盘贴图的**径向 alpha**（RGB 同乘）——贴图生成与用例共用这一份（纯算术）。
        /// `dx,dy` = 贴图归一化坐标（[-1,1]，中心 = 0）：芯（`r ≤ DiscCoreFraction`）不透明，向外 smoothstep 收到 0。
        ///
        /// ⚠⚠ **2026-10-11 的真病根（"有星空没太阳"的第二层）**：贴图是**径向**的 ⇒ 画它的四边形**必须有 UV**。
        /// 第一版拿仓库里给"纯色 billboard"用的 `quadMesh` 去画 —— 它 `BuildQuadMesh` 里 **只设了顶点/三角形/法线、没设 UV**
        /// ⇒ 四个顶点的 UV 全是 (0,0) ⇒ **整片只采 texel(0,0) = 角上那一点 = alpha 0** ⇒ 全透明 ⇒ "画了但看不见"。
        /// （编队 billboard 用同一个网格没事：`Standard` + 默认白贴图，采哪儿都是白。）
        /// ⇒ 圆片改用自己的 <see cref="DiscQuadGeometry"/>（带 UV）。
        /// </summary>
        public static float DiscAlphaAt(float dx, float dy)
        {
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = r <= DiscCoreFraction ? 1f : Mathf.Clamp01((1f - r) / (1f - DiscCoreFraction));
            return a * a * (3f - 2f * a);            // smoothstep：边缘不出现生硬锯齿
        }

        /// <summary>
        /// **圆片自己的四边形**（顶点 / UV / 三角形，纯数值 ⇒ headless 可钉）。
        ///
        /// 为什么不用现成的 `quadMesh`：**它没有 UV**（见 <see cref="DiscAlphaAt"/> 的注释 —— 那就是"看不见"的真病根）。
        ///
        /// 三条口径：
        /// · 顶点序沿用仓库习惯（0=左下 1=右下 2=左上 3=右上），**UV 与之逐点对应**（径向贴图的圆心落在片心）；
        /// · 三角形**正反各发一遍**（仓库老规矩"绕序不赌"：`Cull Back` 的 shader 会自己挑朝相机那一面，
        ///   双面发就永远不会有"画了却看不见"；同色 + 单片 ⇒ 无双面副作用）；
        /// · 正面法线（−Z 朝向的那一面）与 <see cref="QuadFacingAxis"/> 配套：+Z 背对相机 ⇒ 正面朝相机。
        /// </summary>
        public static void DiscQuadGeometry(out Vector3[] verts, out Vector2[] uvs, out int[] tris)
        {
            verts = [new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),  new Vector3(0.5f, 0.5f, 0f),];
            uvs = [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),];
            // 前三个 = 正面（法线 −Z）；后三个 = 同一片的反面（法线 +Z）⇒ 无论哪套绕序被剔除，都还看得见
            tris = [0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3];
        }

        /// <summary>
        /// **圆片朝向**：把四边形摆正到"正面朝相机"。
        ///
        /// `quadMesh`（`BuildQuadMesh`，三角形 (0,2,1)/(2,3,1)）的**正面法线在局部坐标里是 −Z**
        /// ⇒ "正面朝相机" = 物体的 **+Z 背对相机**（`DrawSystemGroups` 的 group billboard 就是这个写法：
        /// `LookRotation(camera.forward)`，那套在游戏里**见过**）。
        ///
        /// ⚠ **2026-10-11 踩过（用户症状："我看到了星空但没看到太阳"）**：太阳圆片第一版写的是
        /// `LookRotation(camPos − pos)` —— 把 **+Z 转向相机** ⇒ 正面（−Z）背对相机 ⇒ 被
        /// `ShaderDatabase.Transparent` 整片剔除（Unity 资产实测：`Custom/Transparent` 的
        /// **cull = Back**、zWrite = Off —— 星空没事是因为它那个 shader 是 **WorldOverlayAdditiveTwoSided**（cull = Off））。
        /// ⇒ 别改这个符号；要改朝向先读这段。
        /// </summary>
        public static Vector3 QuadFacingAxis(Vector3 camPos, Vector3 placePos)
        {
            Vector3 axis = placePos - camPos;                 // +Z 指向"远离相机"
            if (axis.sqrMagnitude < 1e-12f) return Vector3.forward;
            return axis.normalized;
        }

        /// <summary>给 `LookRotation` 用的安全 up：视线几乎与 `Vector3.up` 平行时换 `Vector3.forward`（避免退化姿态）。</summary>
        public static Vector3 SafeUp(Vector3 facingAxis)
        {
            return Mathf.Abs(Vector3.Dot(facingAxis, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;
        }

        /// <summary>
        /// 星空盒的**整体缩放**：星表网格是按"壳半径 300 单位"造的（原版口径那套），
        /// 但 PFView 的**近裁剪面随相机距离自适应**（可到 5000 单位）⇒ 壳必须挪到近裁剪面之外，
        /// 否则整片星空被裁掉。**整片等比放大**（不是只挪中心）⇒ 星点的**角大小保持不变** ✓。
        /// </summary>
        public static float StarShellScale(float nearClipPlane, float shellRadius)
        {
            if (shellRadius <= 0.01f) return 1f;
            return Mathf.Max(1f, (nearClipPlane * 2f) / shellRadius);
        }
    }
}
