using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// **设计规格 → 多 part 列表**（M1 第一刀，2026-10-17；**普适 part**，2026-10-17 用户裁定）。
    ///
    /// 设计规格只是一个**消费者**（当前是火箭设计）；它引用**普适目录** `ShipPartDef`，
    /// 不定义任何火箭专用 part 类型。船的部件、站的部件、将来武器的挂载都走同一套目录/运行时 `SpacePart`。
    ///
    /// 形态：
    /// · **舱盒 part = 地图载体**：保持在 entity 原点（`positionShip = ZERO`）、`linkedMapId` 留给
    ///   `LinkEntityToMap` 填；几何/质量/传感器与 `SpaceMapGenerator.CreateStationEntity` 同一口径。
    /// · **追加件**：按 spec 顺序沿主轴 **−Y（舱盒下方）** 堆叠，长度取目录 def 的 `stackLengthMeters`
    ///   （0 ⇒ boundsY），质量取 `massKg`；它们**不挂图**（`linkedMapId = -1`）。
    /// · **v1 无 3D 自由布局**：位置只由顺序与长度派生（C 路装配窗后置）。
    /// </summary>
    public static class DesignPartAssembler
    {
        /// <summary>单个 spec 条目允许的最大数量（防手滑/坏档造出上千个 part）。</summary>
        public const int MaxPartsPerEntry = 256;

        /// <summary>**整船 part 总数上限**（含舱盒 Part[0]）——2026-10-17 用户裁定"上限到 256 parts"。</summary>
        public const int MaxTotalParts = 256;

        /// <summary>舱盒厚度（m）——与 SpaceMapGenerator 的 50 同源（这里复制常量是为了本类零地图依赖）。</summary>
        public const float CabinThicknessMeters = 50f;

        /// <summary>
        /// 构建完整 part 列表（**舱盒永远第一件**，`LinkEntityToMap` 取首个非销毁 part 的语义不变）。
        /// </summary>
        /// <param name="defLookup">目录查询（测试注入；null ⇒ 走 `DefDatabase&lt;ShipPartDef&gt;`）。</param>
        /// <param name="includeUnknownDefs">目录未命中的条目是否按类别兜底追加。
        /// **生产接线传 false**：只有真 `ShipPartDef` 才追加 ⇒ 既有 DMS/ThingDef 设计零变化；
        /// 调试台架/单测默认 true（覆盖兜底路径）。</param>
        public static List<SpacePart> BuildParts(RocketDesignSpec? spec, int mapWidth, int mapHeight,
            out SpacePart cabin, Func<string, ShipPartDef?>? defLookup = null, bool includeUnknownDefs = true)
        {
            int mw = Math.Max(1, mapWidth);
            int mh = Math.Max(1, mapHeight);

            cabin = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(mw, CabinThicknessMeters, mh),
                hp = 100f,
                // 地图载体：存亡由 Map 内建筑决定（与 CreateStationEntity 同口径）
                isIndestructible = true,
            };
            // 2026-10-17 用户裁定：舱盒质量 = **Map 内容质量**（建筑 + 屋顶），由 `SpaceMapComp`
            // 在建筑/屋顶变化时维护、`LinkEntityToMap` 时写入；不再用 mapW×50×mapH×90 体积公式。
            cabin.AddComponent(new HullMassComponent { mass = 0f });
            cabin.AddComponent(new SensorArrayComponentData
            {
                passiveRangeMeters = 2_000_000f, ecmResist = 1.0f, eccmResist = 1.0f
            });

            var parts = new List<SpacePart> { cabin };
            if (spec?.parts == null) return parts;

            float yCursor = (float)(-cabin.boundsSizeShip.y * 0.5);   // 舱盒底面
            int nextPartId = 2;
            var entryFirstPartId = new List<int>(spec.parts.Count);
            for (int i = 0; i < spec.parts.Count; i++)
            {
                var entry = spec.parts[i];
                entryFirstPartId.Add(-1);
                if (entry == null) continue;

                var def = defLookup != null
                    ? defLookup(entry.defName)
                    : DefDatabase<ShipPartDef>.GetNamedSilentFail(entry.defName);
                if (def == null && !includeUnknownDefs) continue;   // 生产：目录没收录 ⇒ 不追加（零行为变化）
                Resolve(def, entry, out float mass, out DVector3 bounds, out float stackLength, out _);
                int qty = Math.Max(1, Math.Min(entry.quantity, MaxPartsPerEntry));
                InertiaTensor.Shape shape = entry.useShapeOverride
                    ? entry.shapeOverride
                    : (def?.shape ?? InertiaTensor.Shape.Box);
                int parentId = 0;
                if (entry.parentIndex >= 0 && entry.parentIndex < entryFirstPartId.Count)
                    parentId = entryFirstPartId[entry.parentIndex];
                if (parentId <= 0) parentId = 0;   // 父条目尚未构建/越界 ⇒ 视为根

                for (int q = 0; q < qty; q++)
                {
                    if (parts.Count >= MaxTotalParts) break;
                    DVector3 pos = entry.useExplicitPose
                        ? entry.localPosition
                        : new DVector3(0.0, yCursor - stackLength * 0.5f, 0.0);
                    var part = new SpacePart
                    {
                        partId = nextPartId++,
                        defName = "PF_ShipPart_Cube",
                        positionShip = pos,
                        boundsSizeShip = bounds,
                        rotationShip = entry.useExplicitPose ? entry.localRotation : DQuaternion.identity,
                        shape = shape,
                        stageId = entry.stageId,
                        parentId = parentId,
                        hp = 100f,
                    };
                    if (entryFirstPartId[i] < 0) entryFirstPartId[i] = part.partId;
                    part.AddComponent(new HullMassComponent { mass = mass });
                    // B4a：目录命中 ⇒ 把推进元数据挂到 part（运行时聚合仍按 M2 甲留在地图/实体级）
                    if (def != null)
                    {
                        if (def.category == "thruster")
                        {
                            part.AddComponent(new EngineComponentData
                            {
                                exhaustVelocity = def.exhaustVelocity,
                                massFlowRate = def.massFlowRate,
                                isVectoredRcs = def.isVectoredRcs,
                                propellantDefName = def.propellantDefName,
                            });
                        }
                        else if (def.category == "tank")
                        {
                            part.AddComponent(new PropellantTankComponentData
                            {
                                propellantDefName = def.propellantDefName,
                                capacityKg = def.tankCapacityKg,
                            });
                        }
                    }
                    parts.Add(part);
                    if (!entry.useExplicitPose)
                        yCursor -= stackLength;
                }
                if (parts.Count >= MaxTotalParts) break;
            }

            // 习惯默认（**不是机制**）：单 part 船开放、多 part 船封闭。
            // 可在构建后按件覆写（调试件/未来编辑器）；封闭只影响浮雕与 100kg 占位，不改其它机制。
            cabin.isSealed = parts.Count > 1;
            cabin.InvalidateStats();

            return parts;
        }

        /// <summary>目录命中 ⇒ 用 def 的几何/质量；否则按 category 用兜底值（调试 spec 的 Def 不存在时也可用）。</summary>
        private static void Resolve(ShipPartDef? def, DesignPartSpec entry,
            out float mass, out DVector3 bounds, out float stackLength, out string category)
        {
            category = def != null && !string.IsNullOrEmpty(def.category) ? def.category : (entry.category ?? "misc");

            if (def != null && def.massKg > 0f)
            {
                float bx = Math.Max(0.1f, def.boundsX);
                float by = Math.Max(0.1f, def.boundsY);
                float bz = Math.Max(0.1f, def.boundsZ);
                mass = def.massKg;
                bounds = new DVector3(bx, by, bz);
                stackLength = def.stackLengthMeters > 0f ? def.stackLengthMeters : by;
                return;
            }

            switch (category)
            {
                case "thruster":
                    mass = 500f; bounds = new DVector3(2f, 4f, 2f); stackLength = 4f; return;
                case "tank":
                    mass = 200f; bounds = new DVector3(2f, 6f, 2f); stackLength = 6f; return;
                case "stage":
                    mass = 100f; bounds = new DVector3(2f, 2f, 2f); stackLength = 2f; return;
                default:
                    mass = 100f; bounds = new DVector3(2f, 2f, 2f); stackLength = 2f; return;
            }
        }
    }
}
