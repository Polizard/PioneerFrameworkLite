using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    public class ProjectileInstance : ISpatialEntity
    {
        private static int idCounter = 1;

        public int id;
        public int sourceGroupId;
        public int factionId;
        public WeaponDef weaponDef;
        public DVector3 position;
        public DVector3 velocity;
        public float remainingLife;
        public float damage;
        public float armorPenetration;
        public bool affectedByGravity;
        public float flybyDistance;
        public bool trailEffect;
        public DVector3 prevPosition;
        public int lastUpdateRealTick;      // 物理更新时的真实 tick（渲染插值用）
        public int prevUpdateRealTick;      // 上一次物理更新的真实 tick（碰撞检测求目标参考系用）
        /// <summary>上次物理更新时的 physicsTick（游戏世界秒），射弹积分用其差值做时间步长。</summary>
        public double prevPhysicsTick;
        /// <summary>
        /// 上一步物理更新时的 physicsTick（碰撞检测目标参考系回退用）。
        /// prevPhysicsTick 在每次更新时被覆盖为当前值，回退检测必须用此字段，否则 dt 恒为 0。
        /// </summary>
        public double prevUpdatePhysicsTick;
        /// <summary>生成时的世界系位置（调试日志用）。</summary>
        public DVector3 spawnPosition;
        /// <summary>生成时的 physicsTick：无重力射弹位置 = spawnPos + velocity×(physicsTick - spawnPhysicsTick)，
        /// 渲染据此解析计算，任何帧率下都是平滑连续轨迹（不依赖离散步进点）。</summary>
        public double spawnPhysicsTick;
        /// <summary>累计飞行游戏世界秒（调试日志用，验证时间基准）。</summary>
        public double totalFlightSeconds;

        public int fineCx, fineCy, fineCz;
        public int systemBodyId = -1;

        // ── 弹头射弹（导弹撞击转化生成，《导弹系统设计》§10）──
        /// <summary>是否弹头射弹（导弹销毁瞬间生成、接管命中结算：命中后额外触发 3D AoE 爆炸）。</summary>
        public bool isWarhead;
        /// <summary>Map 范围爆炸半径（格，MissileDef.mapBlastRadiusCells；与 3D blastRadius 分口径）。</summary>
        public float mapBlastRadiusCells;
        /// <summary>弹头 3D AoE 半径（m）。</summary>
        public float blastRadius;
        /// <summary>弹头 3D AoE 中心伤害（随距离线性衰减到 0）。</summary>
        public float blastCenterDamage;
        /// <summary>弹头爆炸冲量等效能量（J，ApplyExplosionImpulse 用）。</summary>
        public float blastImpulseEnergy;

        /// <summary>
        /// 粗网格（SpatialGrid3D，1000km 格）登记坐标。与细网格 fineCx/fineCy/fineCz（20km 格）分离，
        /// 避免粗细网格通过共享字段互相践踏（原实现 LastCellCoord 映射到 fineCx，粗网格写覆盖细网格坐标，
        /// 导致射弹脱靶后细网格旧格删不掉、粗网格注销失败，双向泄漏）。
        /// </summary>
        private (int cx, int cy, int cz) coarseCellCoord;
        public (int cx, int cy, int cz) LastCellCoord
        {
            get => coarseCellCoord;
            set => coarseCellCoord = value;
        }

        public ProjectileInstance()
        {
            id = idCounter++;
        }

        // ── ISpatialEntity ──
        public int EntityId => id;
        public int GroupId => sourceGroupId;
        public int FactionId => factionId;
        public DVector3 Position
        {
            get => position;
            set => position = value;
        }
        public bool IsSpatialDirty { get; set; }

        public void UpdatePosition(float deltaTime, DVector3 gravityAcceleration)
        {
            prevPosition = position;

            if (affectedByGravity)
            {
                velocity += gravityAcceleration * deltaTime;
            }

            position += velocity * deltaTime;
            remainingLife -= deltaTime;
            totalFlightSeconds += deltaTime;
        }

        public bool IsAlive()
        {
            return remainingLife > 0;
        }

        public static void ExposeIdCounter()
        {
            Scribe_Values.Look(ref idCounter, "projectileIdCounter", 1);
        }
    }
}
