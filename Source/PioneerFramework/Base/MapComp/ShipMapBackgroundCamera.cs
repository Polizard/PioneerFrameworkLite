using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **含图编队的地图背景相机**（2026-10-09，用户裁定："相机跟着船走，包括位置和角度"）。
    ///
    /// ## 现场（读码核过，原版 `Assembly-CSharp`）
    /// 我们的三个口袋图生成器都带 `renderWorld=true`（`PF_SpacePocket` / `PF_RoomPocket` / `PF_RocketCabinPocket`）
    /// ⇒ 原版每帧把**世界**画在地图底下当背景（`WorldRendererUtility.WorldBackgroundNow` →
    /// `World.WorldUpdate()` 里的 `renderer.DrawWorldLayers()`）：天穹相机（清成深蓝 + 1500 颗星 + 太阳，挂世界相机下）
    /// → 世界相机（只清深度，画行星）→ 地图相机。地图没盖住的地方（小图四周 / 无甲板的格子）透出的就是它。
    ///
    /// 这台背景相机**本来该由 `WorldCameraDriver.ApplyMapPositionToGameObject()` 摆**：
    /// 位置 = `MapParent.WorldCameraPosition`（原版 = 本图 tile 的球面点，轨道层再沿法线外推
    /// `BackgroundWorldCameraOffset`=160 单位、带 2.5/100 格视差），朝向恒"看向球心"。
    /// 而我们的两个 MapParent（`SpaceStationMapParent` / `SpacecraftMapParent`）为躲开口袋图 tile = -1 的
    /// `GetTileCenter(-1)` 越界，把 `WorldCameraPosition` 写死 `Vector3.zero` ⇒ 那句
    /// `if (vector == Vector3.zero) return;` 直接早退 ⇒ 相机**停在上一次的姿态**
    /// （看过世界地图＝上次那张；没看过＝`Awake` 默认位）＝用户看到的"随便抓了个太空背景"。
    ///
    /// ## 本文件做什么（只做用户要的：位置 + 朝向）
    /// **位置 = 舱底外侧**（沿甲板法线局部 −Y 出船壳）——"我们地图是一张大饼，我们看背景的摄像头
    /// 安在大饼的下方而不是别处"；
    /// **朝向 = 跟着甲板的朝向走**：视线 = 甲板法线外侧（局部 −Y）、镜头上方 = 船头（局部 +Z）
    /// —— 甲板朝天就看正下方那颗行星、甲板侧倾视线跟着侧倾（不把镜头钉死在行星上）。
    /// **换系一律走 `ViewFrame`**（渲染侧唯一换系口，含姿态口 `ToViewRotation`）：位置 =
    /// `group 系统系位置 + entity 局部偏移 + 舱底偏移（按船姿态旋转）` 过 `ToViewUnity`；姿态 =
    /// `船姿态 × 局部固定姿态（绕 +X 转 +90°）` 过 `ToViewRotation`。
    /// 取"地图的所属实体"这条链与别处一致：`SpaceMapComp.ownerEntityId` → `entity` → `entity.GroupId` → `group`。
    ///
    /// ⚠ "下方 = 局部 −Y" 的依据（两条既有约定，不是猜的）：`MapCoordMapper`（地图 xz 平面 = 船体局部 xz、
    /// 局部 y = 船体厚度方向）× `HoverCore` D10（**船体上轴 +Y = 本地铅垂朝外**）⇒ 局部 −Y 既是甲板背面、
    /// 又是朝行星的那一侧。出壳余量只有几十米，在 1 单位 = 50 km 的视图尺度下不可见 —— 这步的意义在
    /// "镜头在壳外、跟着甲板转"，不在那点位移。
    ///
    /// ## 为什么挂 `ApplyMapPositionToGameObject`（而不是别处）
    /// 背景模式下每帧两条路都写这台相机：`Update()` 走 `ApplyMapPositionToGameObject()`（背景分支），
    /// `WorldCameraDriverOnGUI()` 走 `ApplyPositionToGameObject()`（GUI 阶段，**在渲染之后**）。
    /// 渲染前最后一次写是 `Update` 那条 ⇒ postfix 挂在 `ApplyMapPositionToGameObject` 上就是"渲染前最后一手"，
    /// GUI 那条不会把我们盖掉（它写的是下一帧渲染前早被覆盖的位置）。**世界地图自己那条路不受影响**
    /// （世界视图不走本函数；世界视图每帧由 `ApplyPositionToGameObject` 从 `sphereRotation`/`altitude` 重算）。
    ///
    /// ## 唯一的额外一行：接管期间压 near clip（理由与边界）
    /// 世界相机原版 `nearClipPlane = 2` 单位 = **100 km**。相机真的坐到船上之后，低于 100 km 的一切
    /// （含正下方地表）都落在近裁剪面以内 ⇒ 30 km 悬停那种场景背景会是一片空（只剩天穹底色），
    /// 看上去像"没生效"。故接管期间压到 0.01 单位（500 m：连贴地停放的船也只剩一个 500 m 的小洞），
    /// 退出接管立刻恢复原版 2（世界地图那条路吃不到这个改动，避免近/远比 1e8 带来的深度打架）。
    /// </summary>
    internal static class ShipMapBackgroundCamera
    {
        /// <summary>原版世界相机近裁剪面（单位；1 单位 = 50 km）——退出接管要原样还回去。</summary>
        private const float VanillaNearClip = 2f;

        /// <summary>接管期间的近裁剪面：0.01 单位 = 500 m（见类注释"唯一的额外一行"）。</summary>
        private const float BackgroundNearClip = 0.01f;

        /// <summary>相机出船壳的余量（米）：站体厚度 50 m ⇒ 舱底面向外再让开 25 m，确保镜头在壳外。</summary>
        private const float HullStandoffMeters = 25f;

        /// <summary>
        /// 相机在**船体局部系**里的固定姿态（甲板法线外侧 = 局部 −Y，镜头上方 = 船头 = 局部 +Z）。
        /// 值 = 绕局部 +X 转 +90° 的四元数（受管构造，不碰 ECall）：前(0,0,1)→(0,−1,0)、上(0,1,0)→(0,0,1)、右不变。
        /// `internal` 是为了让 `ShipMapBackgroundCameraTests` 钉住这三条映射（改反了当场红）。
        /// </summary>
        internal static readonly Quaternion DeckUndersideRotation = new(0.707106781f, 0f, 0f, 0.707106781f);

        // ── 每帧解析"这张图的船"的缓存。两条纪律都踩在点上：
        //    ① 不许每帧裸 `GetComponent`（[M1]/[O4]）⇒ 组件走 `GameComponentRef`（跨局自动失效），
        //       图→车主实体的映射只在"当前图"变化时解析一次；
        //    ② **不许被静态字段扣住旧局**：换局（新游戏/读档/回主菜单）时立刻把 Map 引用松手，
        //       否则一张图连同全部 Thing 会跟着静态缓存活到进程结束。──
        private static readonly GameComponentRef<SpaceGameComp> spaceGameRef = new();

        private static Game? cachedGame;
        private static Map? cachedMap;
        private static int cachedOwnerEntityId = -1;

        /// <summary>本帧是否由我们接管了世界相机（供 near clip 的"该恢复吗"判断，一帧内成对使用）。</summary>
        private static bool tookOverThisFrame;

        /// <summary>`WorldCameraDriver.ApplyMapPositionToGameObject()` 的后置：接管位置与姿态。</summary>
        internal static void ApplyAfterVanillaMapPosition()
        {
            tookOverThisFrame = false;

            if (!WorldRendererUtility.WorldBackgroundNow) return;   // 只在地图背景模式接管
            if (!TryGetShipPose(out Vector3 posView, out Quaternion rotView)) return;

            var cam = Find.WorldCamera;
            if (cam == null) return;

            cam.transform.position = posView;
            cam.transform.rotation = rotView;
            cam.nearClipPlane = BackgroundNearClip;
            tookOverThisFrame = true;
        }

        /// <summary>
        /// `WorldCameraDriver.Update()` 的后置（每帧跑）：没接管本帧 ⇒ 把 near clip 还原成原版值。
        /// 与 <see cref="ApplyAfterVanillaMapPosition"/> 成对：`Update` 里先调前者（背景分支）再跑后置。
        /// </summary>
        internal static void RestoreNearClipIfNotTakenOver()
        {
            if (tookOverThisFrame) { tookOverThisFrame = false; return; }

            var cam = Find.WorldCamera;
            if (cam != null && cam.nearClipPlane != VanillaNearClip)
                cam.nearClipPlane = VanillaNearClip;
        }

        /// <summary>
        /// 当前地图的"船的位姿"（视图系位置 + 视图系姿态）。取不到（不是含图编队 / 实体已消失 / 组已失活）⇒ false，
        /// 此时**什么都不做**（保持原版行为），绝不猜一个位置。
        /// </summary>
        private static bool TryGetShipPose(out Vector3 posView, out Quaternion rotView)
        {
            posView = Vector3.zero;
            rotView = Quaternion.identity;

            var map = Find.CurrentMap;
            var game = Current.Game;
            if (game == null || map == null)
            {
                cachedGame = null;          // 回主菜单/换局：松手旧图的引用（见字段注释②）
                cachedMap = null;
                cachedOwnerEntityId = -1;
                return false;
            }

            var spaceGame = spaceGameRef.Get();
            if (spaceGame == null) return false;

            if (!ReferenceEquals(game, cachedGame) || !ReferenceEquals(map, cachedMap))
            {
                cachedGame = game;
                cachedMap = map;
                var mapComp = map.GetComponent<SpaceMapComp>();
                cachedOwnerEntityId = mapComp != null ? mapComp.ownerEntityId : -1;
            }
            if (cachedOwnerEntityId < 0) return false;

            var entity = spaceGame.GetEntityById(cachedOwnerEntityId);
            if (entity == null) return false;
            var group = spaceGame.GetGroupById(entity.GroupId);
            if (group == null || !group.IsActive) return false;

            // 位置：组原点（系统系）+ 实体在组内的局部偏移 + **舱底外侧**（沿甲板法线局部 −Y 出船壳）。
            // 用户口径（2026-10-09）："我们地图是一张大饼，我们看背景的摄像头安在大饼的下方而不是别处"。
            // "下方" = 船体局部 **−Y**：`MapCoordMapper`（地图 xz 平面 = 船体局部 xz、局部 y = 厚度）
            // + `HoverCore` D10（船体上轴 +Y = 本地铅垂**朝外**）⇒ −Y 是甲板背面、也是朝行星的那一侧。
            double tick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 groupSystem = group.IsCircularOrbit
                ? group.GetCircularPositionAtTick(tick)
                : group.CurrentOrbit.positionSystem.v;
            Quaternion attitude = SanitizeRotation(group.CurrentOrbit.rotation);

            double hullHalfY = entity.BoundsSize.y * 0.5;
            if (!(hullHalfY > 0.0) || hullHalfY > 500.0) hullHalfY = 25.0;   // 兜底 = 站体 50 m 厚的一半
            DVector3 undersideOffset = DVector3.Rotate(
                new DVector3(0.0, -(hullHalfY + HullStandoffMeters), 0.0), attitude);

            DVector3 shipSystem = groupSystem + entity.Position + undersideOffset;

            posView = ViewFrame.ToViewUnity(shipSystem, tick);
            // 朝向：**跟着甲板的朝向走** —— 视线 = 甲板法线的外侧（局部 −Y），镜头上方 = 船头（局部 +Z）。
            // 于是"甲板朝天"时看到正下方的行星、"甲板侧倾"时视线跟着侧倾（不是把镜头钉死在行星上）。
            rotView = ViewFrame.ToViewRotation(attitude * DeckUndersideRotation, tick);
            return true;
        }

        /// <summary>
        /// 姿态兜底：`(0,0,0,0)` 是"从未置位"的旧四元数（mod 里已登记过：对物理无害、对渲染致命），
        /// 直接喂给 `transform.rotation` 会把相机压成一个点 ⇒ 退化值一律当单位姿态；非单位长再归一化。
        /// </summary>
        private static Quaternion SanitizeRotation(Quaternion q)
        {
            float m = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (m < 1e-6f) return Quaternion.identity;
            if (Mathf.Abs(m - 1f) < 1e-4f) return q;
            float inv = 1f / Mathf.Sqrt(m);
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }
    }

    /// <summary>背景模式每帧摆世界相机的地方（见 <see cref="ShipMapBackgroundCamera"/> 类注释）。</summary>
    [HarmonyPatch(typeof(WorldCameraDriver), nameof(WorldCameraDriver.ApplyMapPositionToGameObject))]
    internal static class WorldCameraDriver_ApplyMapPositionToGameObject_ShipCamera
    {
        private static void Postfix() => ShipMapBackgroundCamera.ApplyAfterVanillaMapPosition();
    }

    /// <summary>退出接管时把世界相机的 near clip 还原（配对见 <see cref="ShipMapBackgroundCamera"/>）。</summary>
    [HarmonyPatch(typeof(WorldCameraDriver), nameof(WorldCameraDriver.Update))]
    internal static class WorldCameraDriver_Update_RestoreNearClip
    {
        private static void Postfix() => ShipMapBackgroundCamera.RestoreNearClipIfNotTakenOver();
    }
}
