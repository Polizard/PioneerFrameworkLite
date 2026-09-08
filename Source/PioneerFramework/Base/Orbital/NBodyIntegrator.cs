using System;
using System.Collections.Generic;
#if !PURE_MATH
using RimWorld;
#endif

namespace PioneerFramework
{
    public static class NBodyIntegrator
    {
        public static void ComputeBodyAccelJerk(
            DVector3 scPos, DVector3 scVel,
            DVector3 bodyPos, DVector3 bodyVel,
            double mu,
            out DVector3 acceleration, out DVector3 jerk)
        {
            double rx = bodyPos.x - scPos.x;
            double ry = bodyPos.y - scPos.y;
            double rz = bodyPos.z - scPos.z;
            double vx = bodyVel.x - scVel.x;
            double vy = bodyVel.y - scVel.y;
            double vz = bodyVel.z - scVel.z;
            
            double rSq = rx * rx + ry * ry + rz * rz;
            
            if (rSq < 0.01)
            {
                acceleration = DVector3.zero;
                jerk = DVector3.zero;
                return;
            }
            
            double invR = 1.0 / Math.Sqrt(rSq);
            double invRSq = invR * invR;
            double invR3 = invRSq * invR;
            double muR3 = mu * invR3;
            
            double ax = muR3 * rx;
            double ay = muR3 * ry;
            double az = muR3 * rz;
            
            double rDotV = rx * vx + ry * vy + rz * vz;
            double alpha = 3.0 * rDotV * invRSq;
            
            double jx = muR3 * vx - alpha * ax;
            double jy = muR3 * vy - alpha * ay;
            double jz = muR3 * vz - alpha * az;
            
            acceleration = new DVector3(ax, ay, az);
            jerk = new DVector3(jx, jy, jz);
        }
        
        public static void ThirdOrderTaylorStep(
            DVector3 pos, DVector3 vel,
            DVector3 acc, DVector3 jerk,
            double dt,
            out DVector3 newPos, out DVector3 newVel)
        {
            double c1 = dt;
            double c2 = 0.5 * dt * dt;
            double c3 = c2 * dt / 3.0;
            
            double px = pos.x + c1 * vel.x + c2 * acc.x + c3 * jerk.x;
            double py = pos.y + c1 * vel.y + c2 * acc.y + c3 * jerk.y;
            double pz = pos.z + c1 * vel.z + c2 * acc.z + c3 * jerk.z;
            
            double vx = vel.x + c1 * acc.x + c2 * jerk.x;
            double vy = vel.y + c1 * acc.y + c2 * jerk.y;
            double vz = vel.z + c1 * acc.z + c2 * jerk.z;
            
            newPos = new DVector3(px, py, pz);
            newVel = new DVector3(vx, vy, vz);
        }
        
        private static double EffectiveMu(double defMu) => defMu > 0.0 ? defMu : SpaceConstants.PrimaryGravitationalParameter;
        
#if !PURE_MATH
        public static void MultiBodyAccelJerk(
            DVector3 scPos, DVector3 scVel,
            double tick,
            CelestialBodyDef primaryBody,
            CelestialBodyManager manager,
            HashSet<int> whitelist,
            out DVector3 totalAcc, out DVector3 totalJerk)
        {
            double ax = 0, ay = 0, az = 0;
            double jx = 0, jy = 0, jz = 0;
            
            bool IncludeBody(int bodyId)
            {
                return whitelist == null || whitelist.Contains(bodyId);
            }
            
            if (IncludeBody(primaryBody.bodyId))
            {
                ComputeBodyAccelJerk_Expanded(
                    scPos.x, scPos.y, scPos.z,
                    scVel.x, scVel.y, scVel.z,
                    0.0, 0.0, 0.0,
                    0.0, 0.0, 0.0,
                    EffectiveMu(primaryBody.gravitationalParameter),
                    out var bx, out var by, out var bz,
                    out var bjx, out var bjy, out var bjz);
                ax += bx; ay += by; az += bz;
                jx += bjx; jy += bjy; jz += bjz;
            }
            
            for (int i = 0; i < primaryBody.moonBodies.Count; i++)
            {
                var moon = primaryBody.moonBodies[i];
                if (!IncludeBody(moon.bodyId)) continue;
                
                DVector3 moonPos = manager.GetBodyLocalPosition(moon.bodyId, tick);
                DVector3 moonVel = manager.GetBodyLocalVelocity(moon.bodyId, tick);
                
                ComputeBodyAccelJerk_Expanded(
                    scPos.x, scPos.y, scPos.z,
                    scVel.x, scVel.y, scVel.z,
                    moonPos.x, moonPos.y, moonPos.z,
                    moonVel.x, moonVel.y, moonVel.z,
                    EffectiveMu(moon.gravitationalParameter),
                    out var mx, out var my, out var mz,
                    out var mjx, out var mjy, out var mjz);
                ax += mx; ay += my; az += mz;
                jx += mjx; jy += mjy; jz += mjz;
            }
            
            totalAcc = new DVector3(ax, ay, az);
            totalJerk = new DVector3(jx, jy, jz);
        }
        
        public static double ComputeOrbitKeepingDvRate(
            DVector3 scPos, DVector3 scVel,
            double tick,
            CelestialBodyDef primaryBody,
            CelestialBodyManager manager,
            HashSet<int> whitelist)
        {
            double ax = 0, ay = 0, az = 0;
            
            if (whitelist == null || !whitelist.Contains(primaryBody.bodyId))
            {
                ComputeBodyAccelJerk_Expanded(
                    scPos.x, scPos.y, scPos.z,
                    scVel.x, scVel.y, scVel.z,
                    0.0, 0.0, 0.0,
                    0.0, 0.0, 0.0,
                    EffectiveMu(primaryBody.gravitationalParameter),
                    out var bx, out var by, out var bz,
                    out _, out _, out _);
                ax += bx; ay += by; az += bz;
            }
            
            for (int i = 0; i < primaryBody.moonBodies.Count; i++)
            {
                var moon = primaryBody.moonBodies[i];
                if (whitelist != null && whitelist.Contains(moon.bodyId)) continue;
                
                DVector3 moonPos = manager.GetBodyLocalPosition(moon.bodyId, tick);
                DVector3 moonVel = manager.GetBodyLocalVelocity(moon.bodyId, tick);
                
                ComputeBodyAccelJerk_Expanded(
                    scPos.x, scPos.y, scPos.z,
                    scVel.x, scVel.y, scVel.z,
                    moonPos.x, moonPos.y, moonPos.z,
                    moonVel.x, moonVel.y, moonVel.z,
                    EffectiveMu(moon.gravitationalParameter),
                    out var mx, out var my, out var mz,
                    out _, out _, out _);
                ax += mx; ay += my; az += mz;
            }
            
            return Math.Sqrt(ax * ax + ay * ay + az * az);
        }
#endif
        
        private static void ComputeBodyAccelJerk_Expanded(
            double scX, double scY, double scZ,
            double scVx, double scVy, double scVz,
            double bX, double bY, double bZ,
            double bVx, double bVy, double bVz,
            double mu,
            out double ax, out double ay, out double az,
            out double jx, out double jy, out double jz)
        {
            double rx = bX - scX;
            double ry = bY - scY;
            double rz = bZ - scZ;
            double vx = bVx - scVx;
            double vy = bVy - scVy;
            double vz = bVz - scVz;
            
            double rSq = rx * rx + ry * ry + rz * rz;
            
            if (rSq < 0.01)
            {
                ax = ay = az = jx = jy = jz = 0.0;
                return;
            }
            
            double invR = 1.0 / Math.Sqrt(rSq);
            double invRSq = invR * invR;
            double invR3 = invRSq * invR;
            double muR3 = mu * invR3;
            
            ax = muR3 * rx;
            ay = muR3 * ry;
            az = muR3 * rz;
            
            double rDotV = rx * vx + ry * vy + rz * vz;
            double alpha = 3.0 * rDotV * invRSq;
            
            jx = muR3 * vx - alpha * ax;
            jy = muR3 * vy - alpha * ay;
            jz = muR3 * vz - alpha * az;
        }
    }
}