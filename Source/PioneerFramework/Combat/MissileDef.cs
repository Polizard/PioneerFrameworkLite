using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    /// <summary>
    /// 导弹（自主武器平台）弹体/弹头参数定义（2026-09-06 K2/K11）。
    /// 一个 MissileDef = 一枚标准弹：弹体（无 Map body Part）+ 推进（PropulsionData）+ D15 主推方向型
    /// （局部主推轴 + 锥角 + 魔动量轮转向率）+ 弹头（撞击式伤害 + AoE 爆炸）。
    /// WeaponDef 只保留"发射/齐射/出膛"语义（missileCount/missileSpeed 等），
    /// 经 weapon.def.missileDefName 指向本类。
    /// </summary>
    public class MissileDef : Def
    {
        // ── 弹体（无 Map body Part；SpacePart.localBoundsSize = bodySize）──
        /// <summary>body Part 局部尺寸（m，定 AABB；主推方向型长 body 沿主推轴拉长）。</summary>
        public Vector3 bodySize = new Vector3(2f, 2f, 8f);

        /// <summary>干质量（kg，HullMassComponent）。</summary>
        public float dryMass = 300f;

        /// <summary>body Part HP。</summary>
        public float hp = 200f;

        // ── 推进（发射时注入满罐 PropulsionData）──
        /// <summary>工质 Def 名（PropellantDef）。</summary>
        public string propellantDefName = "PF_Propellant_Hydrogen";

        /// <summary>排气速度 ve（m/s）。</summary>
        public float ve = 3200f;

        /// <summary>质量流率 ṁ（kg/s），F = ve·ṁ。</summary>
        public float massFlow = 60f;

        /// <summary>单枚满罐工质（kg）：发射注入量；解离子弹 capacity 上限与 N 份成本基数（K3=ammo 抽象，燃料链后置）。</summary>
        public float fuelMass = 2000f;

        // ── 终末制导（Attack 执行，K4 表面距口径；2026-09-08 参数化 + 抽纯核心 MissileGuidanceCore）──
        /// <summary>终末火炬接管距离（m，表面距 = 质心距 − 目标 AABB 半径）：≤ 此值导弹自主火炬制导撞击；更远 = 惯性/轨道力学巡航（交会由玩家/引导引擎旧功能管辖）。</summary>
        public float homingEngageDistance = 100000f;

        /// <summary>终末目标接近率（m/s）。**2026-09-08 重定义**：原"desired 速度上限"（旧 PN 语义）作废——
        /// 实际补量受"横差修正时间"约束（guidanceTimeMargin），不是无条件达到。</summary>
        public float terminalSpeed = 450f;

        /// <summary>横向相对速度归零增益（每物理步 dv = −vPerp·gain）。</summary>
        public float guidanceLateralGain = 0.8f;

        /// <summary>接近率阈值（m/s）：接近率已达此值不再补（"只加不减"入口条件）。</summary>
        public float guidanceClosingThreshold = 300f;

        /// <summary>横差修正时间裕度：tGo ≥ 裕度 × 横差/横向加速度 才允许补接近率（越大越保守）。</summary>
        public float guidanceTimeMargin = 2f;

        /// <summary>几何丢失距离裕度（m，最近点基准）：表面距从最近点被拉大超过此值 → 追丢中止（K6）。</summary>
        public float guidanceLostMarginMeters = 30000f;

        /// <summary>燃尽惯性弹寿命（游戏秒 = tick；K5「Def 寿命后自动清理」）：工质尽后计时，
        /// 超时从世界移除——否则惰性弹永久占碰撞池与登记簿（2026-09-08 补）。</summary>
        public float bodyLifetimeSeconds = 600f;

        // ── D15 主推方向型 ──
        /// <summary>是否主推方向型；false = 全向（一般不会用于导弹，留作扩展）。</summary>
        public bool mainThrustDirectional = true;

        /// <summary>主推轴（body 局部，推力方向；默认 -Z 向后）。</summary>
        public Vector3 mainAxisLocal = new Vector3(0f, 0f, -1f);

        /// <summary>矢量锥半角（度，默认 10°，同 DefaultThrustConeCos）。</summary>
        public float coneDegrees = 10f;

        /// <summary>魔动量轮转向率（°/s；默认 20°/s 比船敏捷，可配）。</summary>
        public float maxAngularRateDegrees = 20f;

        // ── 弹头（撞击式，§10）──
        /// <summary>part HP 单发直扣伤害（数十倍于射弹 damage）。</summary>
        public float warheadDamage = 800f;

        /// <summary>3D 弹头 AoE 半径（m，太空口径——成员间距/冲量豁免等不变量以此为基准，勿与 Map 口径混用）。</summary>
        public float blastRadius = 1500f;

        /// <summary>
        /// 弹头 Map 爆炸半径（格，Pocket Map 1 格 = 1 m；2026-09-09 用户定稿 = 5 格）。
        /// 拆站伤害语义（用户同日定稿）：Map 无血条，弹头进图**拆建筑即细化伤害**（站体 part 维持
        /// 不可摧毁）；"某些建筑被毁 → 空间站毁灭"的关键建筑链设计后续再做。与 3D AoE 分参数：
        /// 1500m 打进 60 格 Pocket Map 近乎整图，Map 侧用格子小半径外科手术式拆建筑。
        /// </summary>
        public float mapBlastRadiusCells = 5f;

        /// <summary>3D 弹头 AoE 中心伤害（随距离线性衰减到 0）。</summary>
        public float blastCenterDamage = 600f;

        /// <summary>
        /// 弹头爆炸冲量等效能量（J，ApplyExplosionImpulse 口径：Δv = CollisionImpulseK·√E·(1−d/R)/mass）。
        /// 默认 1e9 ≈ 2300kg 湿弹 @ 1000m/s 撞击动能量级（与船-船动能爆炸同量级）。
        /// </summary>
        public float blastImpulseEnergy = 1e9f;

        /// <summary>弹头射弹 lifetime（s；超时脱靶自然销毁 = miss，K5）。</summary>
        public float warheadLifetimeSeconds = 120f;

        // ── 齐射弹群深度队列（2026-09-08 §5：1 group N 实体，发射散布）──
        /// <summary>弹群队列深度间距上限（m，默认 2km；实际间距 = min(本值, 总长上限/(N−1)) 自适应，
        /// N=8 → 2km）。安全依据 = **弹头爆炸冲量作用半径**（blastRadius 1500m，非仅致死带 1km）：
        /// 后弹在半径内会被前弹爆炸侧推数百 m/s、队形错乱（2026-09-08 弹群仿真复现，纯测试固化）。</summary>
        public float safetySpacingMeters = 2000f;

        /// <summary>弹群队列总长上限（m，默认 16km）：须保证 N=8 时间距仍 ≥ 爆炸半径，
        /// 即 上限/(N−1) &gt; blastRadius（8 枚 → 16km/7 ≈ 2.29km &gt; 1.5km）。</summary>
        public float arraySideLimitMeters = 16000f;

        /// <summary>工质（缓存解析）。</summary>
        public PropellantDef Propellant => DefDatabase<PropellantDef>.GetNamedSilentFail(propellantDefName);

        /// <summary>
        /// 终末制导参数（纯核心口径，2026-09-08）：Def 字段 → MissileGuidanceParams。
        /// 横向加速度 = 主推推力/湿质量（每枚同构，弹群合并态与单枚同值——N 台引擎 N 倍质量）。
        /// 2026-09-14：不可调项（deadband/boostMax）与 lateralAccel 算式改为引用纯核心单一来源
        /// （`MissileGuidanceDefaults`），不再就地写死——此前那两个写死值与纯测试口径不一致。
        /// </summary>
        public MissileGuidanceParams GuidanceParams
        {
            get
            {
                return new MissileGuidanceParams
                {
                    lateralGain = guidanceLateralGain,
                    closingThreshold = guidanceClosingThreshold,
                    cruiseClosing = terminalSpeed,
                    boostMax = MissileGuidanceDefaults.BoostMax,
                    deadband = MissileGuidanceDefaults.Deadband,
                    lateralAccel = MissileGuidanceDefaults.LateralAccelFor(
                        dryMass, fuelMass, ve, massFlow,
                        hasRcs, rcsVe, rcsMassFlow, rcsFuelMass),
                    turnRateRad = maxAngularRateDegrees * Mathf.Deg2Rad,
                    timeMargin = guidanceTimeMargin,
                    lostMarginMeters = guidanceLostMarginMeters
                };
            }
        }

        /// <summary>锥角余弦（对齐/烧判定用）。</summary>
        public float ConeCos => Mathf.Cos(coneDegrees * Mathf.Deg2Rad);

        /// <summary>主推轴（world 计算用，normalized）。</summary>
        public Vector3 MainAxisLocalNormalized => mainAxisLocal.normalized;

        // ── 弱 RCS 副网（全矢量小推力，主推未对准/主网尽时可用；D15/D13 同构）──
        /// <summary>是否带弱 RCS 副网（默认有：主推对准期/修正期仍能出力）。</summary>
        public bool hasRcs = true;

        /// <summary>RCS 工质 Def 名。约束：与主网必须不同 PropellantDef（同工质无法区分消耗）。</summary>
        public string rcsPropellantDefName = "PF_Propellant_Hydrazine";

        /// <summary>RCS 排气速度 ve（m/s，小推力中比冲）。</summary>
        public float rcsVe = 900f;

        /// <summary>RCS 质量流率 ṁ（kg/s）。</summary>
        public float rcsMassFlow = 2f;

        /// <summary>RCS 满罐工质（kg）。</summary>
        public float rcsFuelMass = 120f;

        /// <summary>RCS 工质（缓存解析）。</summary>
        public PropellantDef RcsPropellant => DefDatabase<PropellantDef>.GetNamedSilentFail(rcsPropellantDefName);
    }
}
