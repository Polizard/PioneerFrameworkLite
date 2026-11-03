using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// **分离解算的游戏侧输入装配**（2026-10-30）：纯核 <see cref="StageSeparationCore"/> 只吃
    /// `(质量, 体轴位置)`；本类负责把 `SpacePart` 换成那两个数，并且**把质量口径钉死在这里**。
    ///
    /// ⚠ 质量口径必须与 <see cref="InertiaTensor.Compose"/> 一致：**结构质量 + 罐内工质**，
    /// 且损毁件的工质已漏光（只算结构质量）。两条口径一旦分家，`Σ m_i r_i ≠ 0` ⇒ 动量不守恒。
    /// </summary>
    internal static class StageSeparation
    {
        /// <summary>一件的质量（kg）。</summary>
        public static double PartMass(SpacePart? part)
        {
            if (part == null) return 0.0;
            double m = part.Mass;                      // 结构质量（HullMassComponent 等聚合）
            if (part.isDestroyed) return m;            // 损毁：燃料漏光，只剩结构
            var tank = part.GetComponent<PropellantTankComponentData>();
            if (tank != null) m += tank.massKg;
            return m;
        }

        /// <summary>
        /// 把一个实体的全部零件按 `stageId == jettisonStage` 分两半累加（**一次遍历**）。
        /// `jettisonStage` 取不存在的级号（如 -1）⇒ 全部计入 `kept`（装配期算整船质心就用这个）。
        /// </summary>
        public static void Accumulate(SpaceEntity? entity, int jettisonStage,
            ref SeparationMass kept, ref SeparationMass jettisoned)
        {
            if (entity == null) return;
            var parts = entity.PartsList;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                if (p.stageId == jettisonStage)
                    StageSeparationCore.Add(ref jettisoned, PartMass(p), p.positionShip);
                else
                    StageSeparationCore.Add(ref kept, PartMass(p), p.positionShip);
            }
        }
    }
}
