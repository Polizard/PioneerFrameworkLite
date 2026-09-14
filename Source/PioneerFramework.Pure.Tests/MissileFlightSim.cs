using System;
using System.Collections.Generic;
using PioneerFramework;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 导弹体状态 + 单步推进（D15 主推方向型物理的简化镜像，与 SpaceGameComp 无 Map burn 分支同口径）：
    /// 姿态以 maxAngularRate 追 burn 方向、主网点火门槛 1.5°（DefaultThrustAlignCos）、
    /// 未对准只有 RCS 全矢量小推力、质量随工质耗减、每步施加 a·dt（受 dv 需求截断 = remainingBurnDV 语义）。
    /// 命中判定用**扫掠段-球最近距离**（900m/步 × 57m 靶的点采样会漏检，同游戏侧 §0.3-2 教训）。
    /// </summary>
    public sealed class MissileBody
    {
        public DVector3 pos, vel;
        public DVector3 mainAxis;
        public double mass, fuel, rcs;

        /// <summary>诊断：主网点火步数 / RCS 点火步数（“主网无法对准点火”症状的量化）。</summary>
        public int mainFireSteps, rcsFireSteps;

        public double dryMass = 300.0, fuelMass = 2000.0, rcsFuelMass = 120.0;
        public double ve = 3200.0, massFlow = 60.0, rcsVe = 900.0, rcsMassFlow = 2.0;
        public double maxTurnRateRad = 20.0 * Math.PI / 180.0;

        public static MissileBody Spawn(DVector3 pos, DVector3 vel)
        {
            var b = new MissileBody { pos = pos, vel = vel };
            b.mass = b.dryMass + b.fuelMass + b.rcsFuelMass;
            b.fuel = b.fuelMass;
            b.rcs = b.rcsFuelMass;
            b.mainAxis = vel.magnitude > 1e-9 ? vel.normalized : new DVector3(0, 0, 1);
            return b;
        }

        private static DVector3 RotateAroundAxis(DVector3 v, DVector3 axis, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return v * c + DVector3.Cross(axis, v) * s + axis * (DVector3.Dot(axis, v) * (1.0 - c));
        }

        /// <summary>施加一次制导指令（dv 需求方向 + 幅度，主网对准判定决定用主推还是 RCS）。</summary>
        public void ApplyGuidance(DVector3 dv, double dt)
        {
            double dvMag = dv.magnitude;
            if (dvMag <= 1e-9) return;
            DVector3 burnDir = dv / dvMag;
            double dot = DVector3.Dot(mainAxis, burnDir);
            if (dot > 1.0) dot = 1.0;
            if (dot < -1.0) dot = -1.0;
            double rot = Math.Min(Math.Acos(dot), maxTurnRateRad * dt);
            if (rot > 1e-12)
            {
                DVector3 axis = DVector3.Cross(mainAxis, burnDir);
                double axisLen = axis.magnitude;
                mainAxis = axisLen < 1e-12 ? burnDir : RotateAroundAxis(mainAxis, axis / axisLen, rot);
            }

            bool mainAligned = DVector3.Dot(mainAxis, burnDir) >= Math.Cos(1.5 * Math.PI / 180.0);
            double thrust, mf, available;
            if (mainAligned && fuel > 0.0) { thrust = ve * massFlow; mf = massFlow; available = fuel; mainFireSteps++; }
            else if (rcs > 0.0) { thrust = rcsVe * rcsMassFlow; mf = rcsMassFlow; available = rcs; rcsFireSteps++; }
            else return;

            double a = thrust / mass;
            double dvStep = Math.Min(a * dt, dvMag);
            vel += burnDir * dvStep;
            double used = Math.Min(mf * dt * (dvStep / (a * dt)), available);
            if (mainAligned && fuel > 0.0) fuel -= used; else rcs -= used;
            mass -= used;
        }

        public void Integrate(double dt) => pos += vel * dt;

        /// <summary>爆炸径向冲量（CombatManager.ApplyExplosionImpulse 同式）；返回施加的 Δv（0 = 半径外）。</summary>
        public double ApplyBlastImpulse(DVector3 center, double R, double E, double K)
        {
            DVector3 to = pos - center;
            double dist = to.magnitude;
            if (dist > R || dist < 1e-6) return 0.0;
            double dv = K * Math.Sqrt(E) * (1.0 - dist / R) / mass;
            vel += (to / dist) * dv;
            return dv;
        }
    }

    /// <summary>单弹对单靶仿真器。</summary>
    public sealed class MissileFlightSim
    {
        public DVector3 mPos, mVel, tPos, tVel;
        public double targetRadius = 57.0;
        public double dt = 2.0;
        public int maxSteps = 4000;
        public bool legacyLaw;
        /// <summary>诊断：首步制导读到的位置偏移（复现解离当步读旧组原点）。</summary>
        public DVector3 firstStepGuidancePosOffset;

        /// <summary>覆盖单枚工质注入量（kg；&lt;0 = 用标准弹的 2000kg）。用于"燃料不足"这类**硬约束**负测试。</summary>
        public double fuelMassOverride = -1.0;

        public struct Result
        {
            public bool hit;
            public bool aborted;
            public double missDistance;
            public double minSurfaceDist;
            public double timeToHit;
            public double abortTime;
            public double fuelUsed;
            public int mainFireSteps;
            public int rcsFireSteps;
        }

        public static MissileFlightSim Crossing(double range, double closing, double lateral, bool legacy)
        {
            return new MissileFlightSim
            {
                mPos = DVector3.zero,
                mVel = new DVector3(closing, lateral, 0),
                tPos = new DVector3(range, 0, 0),
                tVel = DVector3.zero,
                legacyLaw = legacy
            };
        }

        /// <summary>位置偏置接近：弹在 (0,offset,0)，速度沿 +x，靶在 (range,0,0)——深度队列横向 jitter 几何。</summary>
        public static MissileFlightSim OffsetApproach(double range, double closing, double offset, bool legacy)
        {
            return new MissileFlightSim
            {
                mPos = new DVector3(0, offset, 0),
                mVel = new DVector3(closing, 0, 0),
                tPos = new DVector3(range, 0, 0),
                tVel = DVector3.zero,
                legacyLaw = legacy
            };
        }

        public static double SweptClosestApproach(DVector3 relStart, DVector3 relEnd)
        {
            DVector3 seg = relEnd - relStart;
            double segLen2 = seg.sqrMagnitude;
            double tStar = segLen2 > 1e-12 ? -DVector3.Dot(relStart, seg) / segLen2 : 0.0;
            if (tStar < 0.0) tStar = 0.0;
            if (tStar > 1.0) tStar = 1.0;
            return (relStart + seg * tStar).magnitude;
        }

        public Result Run(in MissileGuidanceParams p)
        {
            var r = new Result
            {
                missDistance = double.PositiveInfinity,
                minSurfaceDist = double.PositiveInfinity,
                timeToHit = -1.0,
                abortTime = -1.0
            };
            var body = MissileBody.Spawn(mPos, mVel);
            if (fuelMassOverride >= 0.0)
            {
                body.fuelMass = fuelMassOverride;
                body.fuel = fuelMassOverride;
                body.mass = body.dryMass + fuelMassOverride + body.rcsFuelMass;
            }
            DVector3 target = tPos;
            double t = 0.0;
            var gs = new MissileGuidanceState();

            for (int i = 0; i < maxSteps; i++)
            {
                DVector3 toT = target - body.pos;
                double dist = toT.magnitude;
                if (dist < r.missDistance) r.missDistance = dist;
                double sd = dist - targetRadius;
                if (sd < r.minSurfaceDist) r.minSurfaceDist = sd;
                if (dist <= targetRadius)
                {
                    r.hit = true;
                    r.timeToHit = t;
                    break;
                }

                DVector3 dv;
                bool burn = true;
                if (legacyLaw)
                {
                    DVector3 los0 = toT / dist;
                    DVector3 vRel0 = body.vel - tVel;
                    double closing0 = DVector3.Dot(vRel0, los0);
                    DVector3 vPerp0 = vRel0 - los0 * closing0;
                    dv = -vPerp0 * 0.8;
                    if (closing0 < 300.0)
                        dv += los0 * Math.Min(450.0 - closing0, 600.0);
                }
                else
                {
                    DVector3 gpos = i == 0 ? body.pos + firstStepGuidancePosOffset : body.pos;
                    var step = MissileGuidanceCore.ComputeStep(gpos, body.vel, target, tVel, targetRadius, p, gs);
                    gs = step.state;
                    dv = step.dv;
                    if (step.action == MissileGuidanceAction.Abort)
                    {
                        r.aborted = true;
                        r.abortTime = t;
                        break;
                    }
                    burn = step.action == MissileGuidanceAction.Burn;
                }
                if (burn) body.ApplyGuidance(dv, dt);

                DVector3 relStart = body.pos - target;
                DVector3 relEnd = (body.pos + body.vel * dt) - (target + tVel * dt);
                double swept = SweptClosestApproach(relStart, relEnd);
                double sweptSd = swept - targetRadius;
                if (sweptSd < r.minSurfaceDist) r.minSurfaceDist = sweptSd;
                if (swept <= targetRadius)
                {
                    r.hit = true;
                    r.timeToHit = t + dt;
                    if (swept < r.missDistance) r.missDistance = swept;
                    break;
                }

                body.Integrate(dt);
                target += tVel * dt;
                t += dt;
            }
            r.fuelUsed = body.fuelMass - body.fuel;
            r.mainFireSteps = body.mainFireSteps;
            r.rcsFireSteps = body.rcsFireSteps;
            return r;
        }
    }

    /// <summary>
    /// 弹群齐射仿真：N 枚沿深度队列（同速、错位）同步推进，前弹命中时对仍在飞的后弹施加弹头爆炸冲量
    /// （AoE 线性衰减，§③ 根因复现）——用于验证「队列间距必须超出爆炸冲量作用半径」。
    /// </summary>
    public sealed class VolleyFlightSim
    {
        public int count = 8;
        public double spacing = 1428.571;
        public double rangeToTarget = 11200.0;
        public double muzzleSpeed = 150.0;
        public double jitterFactor = 0.2;
        public double blastRadius = 1500.0, blastEnergy = 1e9, impulseK = 500.0;
        public double targetRadius = 57.0;
        public double dt = 2.0;
        public int maxSteps = 4000;
        public bool legacyLaw;

        public struct Result
        {
            public int hits;
            public int kicked;        // 被爆炸冲量作用过的弹数
            public double maxKick;    // 最大冲量 Δv（m/s）
            public int aborts;
        }

        /// <summary>冲量事件明细（诊断用）。</summary>
        public readonly System.Collections.Generic.List<string> KickLog = new System.Collections.Generic.List<string>();

        public Result Run(in MissileGuidanceParams p)
        {
            var bodies = new List<MissileBody>();
            var states = new List<MissileGuidanceState>();
            var alive = new List<bool>();
            double mid = (count - 1) * 0.5 * spacing;
            DVector3 target = new DVector3(rangeToTarget, 0, 0);
            for (int k = 0; k < count; k++)
            {
                double along = (count - 1 - k) * spacing - mid; // k=0 = 队首（最靠近靶）
                double jitter = (k % 2 == 0 ? -jitterFactor : jitterFactor) * spacing;
                bodies.Add(MissileBody.Spawn(new DVector3(along, jitter, 0), new DVector3(muzzleSpeed, 0, 0)));
                states.Add(new MissileGuidanceState());
                alive.Add(true);
            }

            var res = new Result();
            for (int step = 0; step < maxSteps && res.hits + res.aborts < count; step++)
            {
                for (int k = 0; k < count; k++)
                {
                    if (!alive[k]) continue;
                    var body = bodies[k];
                    DVector3 toT = target - body.pos;
                    double dist = toT.magnitude;

                    // 扫掠命中判定（连续；相对运动段-球）——先推进再判，命中点取本步扫掠
                    DVector3 relStart = body.pos - target;
                    DVector3 relEnd = (body.pos + body.vel * dt) - target;   // 靶静止：省掉恒零的 DVector3.zero*dt（2026-09-14 清理）
                    if (MissileFlightSim.SweptClosestApproach(relStart, relEnd) <= targetRadius || dist <= targetRadius)
                    {
                        alive[k] = false;
                        res.hits++;
                        // 前弹爆炸：对仍在飞的后弹施加径向冲量（AoE 线性衰减，§③ 根因）
                        for (int j = 0; j < count; j++)
                        {
                            if (!alive[j]) continue;
                            double dToBlast = (bodies[j].pos - body.pos).magnitude;
                            double kickDv = bodies[j].ApplyBlastImpulse(body.pos, blastRadius, blastEnergy, impulseK);
                            if (kickDv > 0.0)
                                KickLog.Add(string.Format("step={0} victim={1} dist={2:F0} mass={3:F0} dv={4:F0}", step, j, dToBlast, bodies[j].mass, kickDv));
                            if (kickDv > 0.0)
                            {
                                res.kicked++;
                                if (kickDv > res.maxKick) res.maxKick = kickDv;
                            }
                        }
                        continue;
                    }

                    DVector3 dv;
                    bool burn = true;
                    if (legacyLaw)
                    {
                        DVector3 los0 = toT / dist;
                        DVector3 vRel0 = body.vel - DVector3.zero;
                        double closing0 = DVector3.Dot(vRel0, los0);
                        DVector3 vPerp0 = vRel0 - los0 * closing0;
                        dv = -vPerp0 * 0.8;
                        if (closing0 < 300.0) dv += los0 * Math.Min(450.0 - closing0, 600.0);
                    }
                    else
                    {
                        var s = MissileGuidanceCore.ComputeStep(body.pos, body.vel, target, DVector3.zero, targetRadius, p, states[k]);
                        states[k] = s.state;
                        dv = s.dv;
                        if (s.action == MissileGuidanceAction.Abort)
                        {
                            alive[k] = false;
                            res.aborts++;
                            continue;
                        }
                        burn = s.action == MissileGuidanceAction.Burn;
                    }
                    if (burn) body.ApplyGuidance(dv, dt);
                    body.Integrate(dt);
                }
                // 目标静止（弹群场景靶近似共轨）
            }
            return res;
        }
    }
}
