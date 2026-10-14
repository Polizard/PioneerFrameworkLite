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
        public WeaponDef weaponDef = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        /// <summary>**系统系**位置（批6 S3-3c：类型承载系 —— 混系会编译不过，见 `FrameVec3.cs`）。</summary>
        public SystemVec3 position;
        /// <summary>**系统系**速度。</summary>
        public SystemVec3 velocity;
        public float remainingLife;
        public float damage;
        public float armorPenetration;
        public bool affectedByGravity;
        public float flybyDistance;
        public bool trailEffect;
        /// <summary>本步起点（扫掠线段起点），**系统系**。</summary>
        public SystemVec3 prevPosition;
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
        public SystemVec3 spawnPosition;
        /// <summary>生成时的速度：与 spawnPosition 一起构成"无引力直线"，用于量出引力造成的实际弯曲量
        /// （A2 的定量断言：实际弯曲应 ≈ ½·|a|·t²）。</summary>
        public SystemVec3 spawnVelocity;
        /// <summary>生成时的 physicsTick：无重力射弹位置 = spawnPos + velocity×(physicsTick - spawnPhysicsTick)，
        /// 渲染据此解析计算，任何帧率下都是平滑连续轨迹（不依赖离散步进点）。</summary>
        public double spawnPhysicsTick;
        /// <summary>累计飞行游戏世界秒（调试日志用，验证时间基准）。</summary>
        public double totalFlightSeconds;

        // ── A2 段传播的探针计数（**按弹**而非全局：全局比会被并发弹头数放大，2026-09-14 实测误报 1/55）──
        /// <summary>本弹走过的物理步数。</summary>
        public int gravitySteps;
        /// <summary>本弹重建传播段的次数（应 ≈ gravitySteps / 120）。</summary>
        public int gravityRebuilds;

        /// <summary>
        /// 引力射弹的传播段（A2）：**2 物理秒一段**，段内三阶泰勒插值。
        /// 引力（含递归取天体位置）从"每物理步一次"降为"每段一次"，段内位置直取多项式；
        /// 每个物理步仍会得到精确位置（多项式求值），扫掠式碰撞检测不受影响。
        /// 瞬态，不序列化（射弹本身不存档）。
        /// </summary>
        public TaylorSegment gravitySegment;

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
        /// <summary>⚠ `ISpatialEntity.Position` 是**未标类型的**接口（A3 的标本：`SpaceEntityGroup` 在系统系、
        /// `SpaceEntity` 在船体局部 —— 同一个接口成员两个系）⇒ 本实现**显式跨界**：暴露的是**系统系**。</summary>
        public DVector3 Position
        {
            get => position.v;
            set => position = (SystemVec3)value;
        }
        public bool IsSpatialDirty { get; set; }

        /// <summary>本步起点（扫掠碰撞检测的线段起点）。两条积分路径都先调它。</summary>
        public void BeginStep()
        {
            prevPosition = position;
        }

        /// <summary>推进寿命与飞行计时（与位置更新解耦：线性路径与泰勒段路径共用，避免两处书账）。</summary>
        public void AdvanceLife(double deltaTime)
        {
            remainingLife -= (float)deltaTime;
            totalFlightSeconds += deltaTime;
        }

        /// <summary>无重力（线性）积分一步。引力射弹走 TaylorSegment 路径，不经过这里。</summary>
        public void UpdatePosition(float deltaTime)
        {
            BeginStep();
            position += velocity * deltaTime;
            AdvanceLife(deltaTime);
        }

        /// <summary>按当前传播段在 physicsTick 处求值，写回位置/速度（段内插值飞行）。</summary>
        public void ApplyGravitySegment(double physicsTick)
        {
            gravitySegment.Evaluate(physicsTick - gravitySegment.baseTick, out var p, out var v);
            position = (SystemVec3)p;   // 跨界：传播段给裸三分量（系统系）
            velocity = (SystemVec3)v;
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
