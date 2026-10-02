using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// <see cref="SpaceMapComp"/> 的 PFView 地图浮雕部分：
    /// **逐格分类缓存**（用户 2026-10-02 裁定"缓存在 MapComp 里"）+ 置脏入口。
    ///
    /// 分类（`MapReliefPlan` 的三值）：
    ///   1 结构梁 = 有地板/建筑但不成房间；2 舱室 = 成房间；3 装甲 = 成房间且全屋顶；0 = 不画。
    ///
    /// 缓存纪律（与工质/传感器登记同款）：
    ///   · **不落盘**（纯表现数据，读档后重建）；
    ///   · 事件置脏（地板/屋顶/建筑 spawn-despawn，见 `SpaceMapComp.ReliefPatches.cs`）、**按需重建**
    ///     （PFView 每帧取用时才重建，不在事件里算——事件在第物理步上下文中，别在那儿扫全图）；
    ///   · 重建立即把 `ReliefVersion` 自增：PFView 的网格缓存以版本号判陈旧。
    /// </summary>
    public partial class SpaceMapComp
    {
        // ── 浮雕分类缓存（非序列化）──
        private byte[]? reliefClasses;
        private byte[]? reliefSolid;       // 1 = "不可通行结构格"（墙/机柜…）——归房蔓延只沿它继续
        private int reliefVersion;
        private bool reliefDirty = true;
        private bool reliefHasContent;

        /// <summary>分类版本号：每次成功重建自增。消费侧（PFView 网格缓存）用相等判"可复用"。</summary>
        public int ReliefVersion => reliefVersion;

        /// <summary>本图是否至少有一格可画（全 0 = 空站/纯太空 ⇒ 调用方回退旧表示，别让船凭空消失）。</summary>
        public bool ReliefHasContent => reliefHasContent;

        /// <summary>把本图的浮雕分类标脏（重建推迟到下一次取用）。</summary>
        public void MarkReliefDirty()
        {
            reliefDirty = true;
        }

        /// <summary>任意地图都能安全调用的置脏包装：没有 <see cref="SpaceMapComp"/> 的图（行星地表等）什么都不做。</summary>
        public static void NotifyReliefDirty(Map map)
        {
            if (map == null) return;
            map.GetComponent<SpaceMapComp>()?.MarkReliefDirty();
        }

        /// <summary>
        /// 取逐格分类网格（必要时重建）。长度 = Size.x × Size.z，索引 = z * Size.x + x。
        /// 返回 null = **本帧拿不到可信数据**（房间/区划数据尚未追平，如批量操作期间）⇒ 调用方回退旧表示。
        /// 非 null 但 <see cref="ReliefHasContent"/> 为假 = 本图没有可画内容（同上回退）。
        /// </summary>
        public byte[]? GetReliefClasses()
        {
            if (reliefDirty || reliefClasses == null || reliefClasses.Length != map.Size.x * map.Size.z)
            {
                if (!RebuildRelief())
                    return null;
            }
            return reliefClasses;
        }

        /// <summary>
        /// 逐格分类。要点：
        ///   · **房间数据必须先追平**——区划/房间是 RimWorld 自己的缓存，脏着读会整张图判成"没有房间"；
        ///     追不平（updater 被批量操作临时关掉）就**本轮不重建**、保留旧值，下一帧再试；
        ///   · 墙格/门格不属任何房间（`RegionType.None` 没有区划）⇒ 第二遍把"参与形成房间的结构格"
        ///     归到房间档（用户 2026-10-02："参与形成房间的墙体也计入房间渲染模式"）。
        /// </summary>
        private bool RebuildRelief()
        {
            Map m = map;
            if (m == null)
                return false;

            if (m.regionAndRoomUpdater.AnythingToRebuild)
                m.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            if (m.regionAndRoomUpdater.AnythingToRebuild)
                return false;   // 还脏：等下一帧（updater 被批量操作关掉时会出现，属正常瞬态）

            int w = m.Size.x;
            int h = m.Size.z;
            int cells = w * h;
            if (reliefClasses == null || reliefClasses.Length != cells)
                reliefClasses = new byte[cells];
            if (reliefSolid == null || reliefSolid.Length != cells)
                reliefSolid = new byte[cells];

            bool any = false;

            // ── 第一遍：地板 / 建筑 / 房间 → 分类 ──
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    var cell = new IntVec3(x, 0, z);

                    // "在建的不画"：Frames 也在 edificeGrid 里（帧 def 的 building.isEdifice 随目标 def），
                    // 且其 def.entityDefToBuild != null ⇒ 这两条一起把"在建"筛掉（蓝图不在 edificeGrid）。
                    Building edifice = m.edificeGrid[cell];
                    bool built = edifice != null && !edifice.def.IsFrame && edifice.def.entityDefToBuild == null;

                    bool floored = SpaceTerrainUtility.HasDeckFloor(m.terrainGrid.TerrainAt(cell));

                    byte cls = MapReliefPlan.None;
                    Region? reg = m.regionGrid.GetValidRegionAt_NoRebuild(cell);
                    Room? room = reg?.Room;
                    if (room != null && room.ProperRoom)
                        cls = room.OpenRoofCount == 0 ? MapReliefPlan.Armor : MapReliefPlan.Cabin;
                    else if (built || floored)
                        cls = MapReliefPlan.Beam;

                    int i = z * w + x;
                    reliefClasses[i] = cls;
                    // 可通行格（有区划：地板/门）不参与"沿墙蔓延"——区划只存在于可通行格上。
                    reliefSolid[i] = (cls == MapReliefPlan.Beam && reg == null) ? (byte)1 : (byte)0;
                    if (cls != MapReliefPlan.None)
                        any = true;
                }
            }

            // ── 第二遍：结构格（墙/门/机柜…）归到它围出的房间档 ──
            //   算法本体在纯逻辑层（`MapReliefPlan.PropagateRoomClassToStructure`，有纯测试钉住）：
            //   迭代最大蔓延，第 1 轮任何结构格（含门）可归房，之后只沿不可通行格继续。
            MapReliefPlan.PropagateRoomClassToStructure(reliefClasses, reliefSolid, w, h);

            reliefHasContent = any;
            reliefDirty = false;
            reliefVersion++;
            return true;
        }
    }
}
