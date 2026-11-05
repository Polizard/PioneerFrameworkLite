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
    /// · **追加件**：按 spec 顺序沿主轴 **−Z（箭尾方向，机头 = +Z）** 堆叠，长度取目录 def 的
    ///   `stackLengthMeters`（0 ⇒ boundsY），质量取 `massKg`；横向保留目录的 `boundsX`/`boundsZ`。
    ///   （2026-10-28 前是沿 −Y 堆 ⇒ 与 +Z 机头差 90°，整船"横着飞"，气动迎风面虚高一个数量级。）
    /// · **并联（2026-10-29）**：一条 spec 的 `quantity` = 轴向站数，`parallelCount` = 每站并排几件；
    ///   同一站的 N 件共享 z，横向按 <see cref="ParallelLayout"/> 的环均匀分布（推力对称 ⇒ 合力矩为 0），
    ///   轴向只推进一次。`parallelCount = 1` ⇒ 偏移恒为 0，既有设计与判据逐位不变。
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
            out SpacePart cabin, Func<string, ShipPartDef?>? defLookup = null, bool includeUnknownDefs = true,
            bool includeCabin = true, float stageGapMeters = 0f)
        {
            int mw = Math.Max(1, mapWidth);
            int mh = Math.Max(1, mapHeight);

            var parts = new List<SpacePart>();
            float zCursor = 0f;
            if (includeCabin)
            {
                cabin = new SpacePart
                {
                    partId = 1,
                    defName = "PF_ShipPart_Cube",
                    positionShip = DVector3.zero,
                    // 2026-10-28（用户令"横着飞的，改吧"）：厚度沿 **−Z（箭尾方向）**，
                    // 与"机头 = +Z"的推力轴一致；旧口径把厚度放在 Y 上，整船等于侧躺。
                    boundsSizeShip = new DVector3(mw, mh, CabinThicknessMeters),
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
                parts.Add(cabin);
                if (spec?.parts == null) return parts;
                zCursor = (float)(-cabin.boundsSizeShip.z * 0.5);   // 舱盒底面
            }
            else
            {
                // 载荷（2026-10-26 裁定：无 Map）：**不造舱盒**，零件沿 **−Z（箭尾）** 从 0 起堆。
                cabin = null!;
                if (spec?.parts == null) return parts;
            }

            int nextPartId = includeCabin ? 2 : 1;
            var entryFirstPartId = new List<int>(spec.parts.Count);
            var entryFirstPartStage = new List<int>(spec.parts.Count);   // 2026-11-03：父件级烘焙
            int prevStageId = int.MinValue;   // 上一处**真正落下零件**的条目级号（级间空隙用）
            for (int i = 0; i < spec.parts.Count; i++)
            {
                var entry = spec.parts[i];
                entryFirstPartId.Add(-1);
                entryFirstPartStage.Add(int.MinValue);
                if (entry == null) continue;

                var def = defLookup != null
                    ? defLookup(entry.defName)
                    : DefDatabase<ShipPartDef>.GetNamedSilentFail(entry.defName);
                if (def == null && !includeUnknownDefs) continue;   // 生产：目录没收录 ⇒ 不追加（零行为变化）
                Resolve(def, entry, out float mass, out DVector3 bounds, out float stackLength, out _);
                // **轴向换算（2026-10-28）**：目录的 `boundsY` = 沿堆叠方向的长度 ⇒ 落到飞船系要挪到 **Z**，
                // 横向两个尺寸保持 X / Y。旧口径把长度留在 Y 上而机头是 +Z ⇒ 组装出的火箭"横着飞"
                // （气动迎风面走 4×40 的大面，实测爬升 4 km/s 只换 800 m/s）。
                bounds = new DVector3(bounds.x, bounds.z, bounds.y);
                // **级间空隙（2026-10-30 用户方案）**：级号一变就多让出一段空当 ⇒ 抛级瞬间两组不接触
                // （窄相 `depth > 0` 才算接触、宽相无 margin ⇒ 一对接触都不会产生）。
                // 只对"按堆叠推进"的条目生效；`useExplicitPose` 的位姿由调用方权威，不动它的坐标。
                if (!entry.useExplicitPose && stageGapMeters > 0f
                    && prevStageId != int.MinValue && entry.stageId != prevStageId)
                {
                    zCursor -= stageGapMeters;
                }
                prevStageId = entry.stageId;
                int qty = Math.Max(1, Math.Min(entry.quantity, MaxPartsPerEntry));
                // **并联（2026-10-29）**：`quantity` = 轴向站数，`parallelCount` = 每站并排几件。
                // 同一站的 N 件共享同一个 z，横向按 `ParallelLayout` 的环均匀分布（推力对称 ⇒ 合力矩为 0）。
                // parallelCount = 1 时偏移恒为 0 ⇒ 既有设计/测试逐位零变化。
                int parallel = RocketDesignRules.ParallelOf(entry);
                var offX = new float[parallel];
                var offY = new float[parallel];
                if (parallel > 1)
                    ParallelLayout.Offsets(parallel, (float)Math.Max(bounds.x, bounds.y), offX, offY);
                InertiaTensor.Shape shape = entry.useShapeOverride
                    ? entry.shapeOverride
                    : (def?.shape ?? InertiaTensor.Shape.Box);
                int parentId = 0;
                if (entry.parentIndex >= 0 && entry.parentIndex < entryFirstPartId.Count)
                    parentId = entryFirstPartId[entry.parentIndex];
                if (parentId <= 0) parentId = 0;   // 父条目尚未构建/越界 ⇒ 视为根
                int parentStageId = -1;
                if (entry.parentIndex >= 0 && entry.parentIndex < entryFirstPartStage.Count
                    && entryFirstPartStage[entry.parentIndex] != int.MinValue)
                    parentStageId = entryFirstPartStage[entry.parentIndex];

                for (int q = 0; q < qty; q++)
                {
                    // **整站原子**：装不下这一站就整站不装。半个环 = 推力不对称（残余力矩），
                    // 比少一站更糟；上限仍是 MaxTotalParts。
                    if (parts.Count + parallel > MaxTotalParts) break;
                    // 本轴向站的中心 z：并联的 N 件全部落在这一层
                    double stationZ = zCursor - stackLength * 0.5f;
                    for (int c = 0; c < parallel; c++)
                    {
                        // 显式位姿（DMS:NF 预留）由调用方权威：并联件共用同一 pose，不再叠环偏移。
                        DVector3 pos = entry.useExplicitPose
                            ? entry.localPosition
                            : new DVector3(offX[c], offY[c], stationZ);
                        var part = new SpacePart
                        {
                            partId = nextPartId++,
                            defName = "PF_ShipPart_Cube",
                            positionShip = pos,
                            boundsSizeShip = bounds,
                            rotationShip = entry.useExplicitPose ? entry.localRotation : DQuaternion.identity,
                            shape = shape,
                            stageId = entry.stageId,
                            blockId = entry.blockId,
                            stageDisposition = DispositionOf(spec, entry.stageId),
                            igniteWithUpperStage = IgnitesWithUpper(spec, entry.stageId),
                            parentStageId = parentStageId,
                            parentId = parentId,
                            hp = 100f,
                            // §11.1 装甲：只抄数据（不改质量/几何）；厚度 0 或无材料 ⇒ 无装甲（旧行为）
                            armorMaterial = def?.armorMaterial,
                            armorThicknessMm = def?.armorThicknessMm ?? 0f,
                        };
                        if (entryFirstPartId[i] < 0) entryFirstPartId[i] = part.partId;
                        if (entryFirstPartStage[i] == int.MinValue) entryFirstPartStage[i] = entry.stageId;
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
                                    powerDrawWatts = def.powerDrawWatts,   // §5：欠电 ⇒ 拒绝点火（0 = 不用电）
                                    heatProductionWatts = def.heatProductionWatts,   // 目录显式热源（2026-10-26 裁定 A）
                                    heatDissipationWatts = def.heatDissipationWatts, // 喷口散热（2026-10-27）
                                });
                            }
                            else if (def.category == "tank")
                            {
                                // 2026-10-28（纯零件发射）：**未指定初装量 ⇒ 按满罐**。
                                // 燃料的权威现在是零件自身（`massKg`），设计级 `fuelAmount` 只作镜像；
                                // 老目录件（如调试罐 `tankInitialKg = 0`）不补这一下就是"空罐上发射台"。
                                double initial = def.tankInitialKg > 0f ? def.tankInitialKg : def.tankCapacityKg;
                                part.AddComponent(new PropellantTankComponentData
                                {
                                    propellantDefName = def.propellantDefName,
                                    capacityKg = def.tankCapacityKg,
                                    massKg = initial,
                                });
                            }
                            else if (def.category == "rcs")
                            {
                                // RCS 推力器（2026-10-27，A 路线）：独立工质（肼），全矢量、跳过主推轴聚合。
                                part.AddComponent(new EngineComponentData
                                {
                                    exhaustVelocity = 1500f,
                                    massFlowRate = 2f,
                                    isVectoredRcs = true,
                                    propellantDefName = "PF_Propellant_Hydrazine",
                                });
                            }
                            else if (def.category == "rcs-tank")
                            {
                                // RCS 小罐：打 rcsSupply 标签 ⇒ 进 RCS 网络，主发动机烧不到它。
                                part.AddComponent(new PropellantTankComponentData
                                {
                                    propellantDefName = "PF_Propellant_Hydrazine",
                                    capacityKg = def.tankCapacityKg > 0f ? def.tankCapacityKg : 200f,
                                    massKg = def.tankInitialKg > 0f ? def.tankInitialKg : def.tankCapacityKg,
                                    rcsSupply = true,
                                });
                            }
                            else if (def.category == "control")
                            {
                                // 控制核心（2026-10-26）：装过就粘住；损毁 ⇒ 永久失控。
                                part.AddComponent(new ControlCoreComponentData());
                            }
                            else if (def.category == "payload-bay")
                            {
                                // 载荷舱（2026-10-26）：payloadDesignId 由注册表分配，运行时绑定（0 = 未绑定）。
                                part.AddComponent(new PayloadBayComponentData());
                            }

                            // §4 目录预置：通用 typeId + 参数键值（新角色走这里，不加 Def 专用字段）。
                            // 顺序 = XML 书写顺序，接在"结构质量 + 类别组件"之后 ⇒ 聚合求和顺序确定。
                            if (def.presets != null)
                            {
                                for (int pi = 0; pi < def.presets.Count; pi++)
                                {
                                    var preset = def.presets[pi];
                                    if (preset == null) continue;
                                    preset.ApplyTo(part);   // 失败会 Log.Error；不把坏条目静默吞掉
                                }
                            }
                        }
                        parts.Add(part);
                    }
                    if (!entry.useExplicitPose)
                        zCursor -= stackLength;   // 一个轴向站只推进一次（并联件同层）
                }
                if (parts.Count >= MaxTotalParts) break;
            }

            // 习惯默认（**不是机制**）：单 part 船开放、多 part 船封闭。
            // 可在构建后按件覆写（调试件/未来编辑器）；封闭只影响浮雕与 100kg 占位，不改其它机制。
            // 载荷（无舱盒）没有这个默认。
            if (includeCabin)
            {
                cabin.isSealed = parts.Count > 1;
                cabin.InvalidateStats();
            }

            return parts;
        }

        /// <summary>设计期的级标签 → 运行时枚举（2026-11-03）；未标 = Jettison（旧行为）。</summary>
        private static StageDisposition DispositionOf(RocketDesignSpec? spec, int stageId)
        {
            if (spec?.keepStageIds != null && spec.keepStageIds.Contains(stageId))
                return StageDisposition.Keep;
            if (spec?.recoverableStageIds != null && spec.recoverableStageIds.Contains(stageId))
                return StageDisposition.JettisonRecoverable;
            return StageDisposition.Jettison;
        }

        /// <summary>并联点火（C-full）：该级是否与上一级同时点火（2026-11-03）。</summary>
        private static bool IgnitesWithUpper(RocketDesignSpec? spec, int stageId)
            => spec?.parallelIgnitionStageIds != null && spec.parallelIgnitionStageIds.Contains(stageId);

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
