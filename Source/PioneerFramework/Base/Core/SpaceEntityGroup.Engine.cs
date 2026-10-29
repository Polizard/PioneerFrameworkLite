using System.Collections.Generic;
using PioneerFramework.Components;

namespace PioneerFramework
{
    public partial class SpaceEntityGroup
    {
        /// <summary>
        /// **B4b：本群 engine part 的偏心推力力矩**（绕群质心，世界系）。
        /// 引擎按 part 上的 `EngineComponentData.massFlowRate` 分摊总推力；位置 = entity.Position +
        /// 旋转后的 part.positionShip − 群质心。**只读数/探针**：魔法档仍由姿态控制精确接管，
        /// 物理档（批 2）再消费这个力矩。
        /// </summary>
        public DVector3 GetEngineThrustTorque(DVector3 thrustDirectionSystem, double totalThrustN)
        {
            var sg = (preparedPhysicsHost ?? SpaceGameComp.GetCached());
            if (sg == null) return DVector3.zero;

            var inertia = InertiaResult;
            DVector3 com = new(inertia.comX, inertia.comY, inertia.comZ);
            DVector3 dirBody = DVector3.Rotate(thrustDirectionSystem, DQuaternion.Inverse(CurrentOrbit.rotation));

            var points = new List<(DVector3 r, float flow)>(16);
            foreach (int eid in GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null) continue;
                var parts = e.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part == null || part.isDestroyed) continue;
                    var engine = part.GetComponent<EngineComponentData>();
                    if (engine == null || engine.massFlowRate <= 0f) continue;
                    DVector3 r = e.Position + DVector3.Rotate(part.positionShip, e.Rotation) - com;
                    points.Add((r, engine.massFlowRate));
                }
            }

            DVector3 torqueBody = EngineThrust.Torque(points, dirBody, totalThrustN);
            return DVector3.Rotate(torqueBody, CurrentOrbit.rotation);
        }
    }
}
