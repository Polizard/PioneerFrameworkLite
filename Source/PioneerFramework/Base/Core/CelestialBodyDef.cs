using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public enum CelestialBodyType
    {
        Star,
        Planet,
        Moon
    }

    /// <summary>
    /// 天体定义。通过 XML 注册，由 DefDatabase 管理。
    /// 物理量使用 double 以兼容高精度计算。
    /// </summary>
    public class CelestialBodyDef : Def
    {
        // ── 基础属性 ──
        /// <summary>数值 ID，运行时快速查找用</summary>
        public int bodyId;
        /// <summary>天体类型</summary>
        public CelestialBodyType bodyType;
        /// <summary>半径（米）</summary>
        public double radius;
        /// <summary>引力参数 GM（m³/s²）</summary>
        public double gravitationalParameter;
        /// <summary>是否有固体表面（能否着陆）</summary>
        public bool hasSurface;
        /// <summary>大气层厚度（米），0 = 无大气</summary>
        public double atmosphereThickness;
        /// <summary>自转周期（游戏 tick），默认 60000 = 1 RimWorld 天。火星约 61500，木星约 21400</summary>
        public double dayLengthTicks = 60000.0;
        /// <summary>对应的 PlanetLayerDef 名称。不填则默认使用 defName 匹配</summary>
        public string planetLayerDefName;

        // ── 轨道参数（Star 类型不需要）──
        /// <summary>轨道半径（米）</summary>
        public double orbitRadius;
        /// <summary>轨道周期（游戏 tick，1 tick = 1 秒）</summary>
        public double orbitPeriod;
        /// <summary>
        /// 轨道平面法线向量（XML 反序列化用 Vector3，运行时通过 OrbitNormalD 获取 DVector3）。
        /// </summary>
        public Vector3 orbitNormal;
        /// <summary>
        /// 轨道平面法线（DVector3 懒加载）。
        /// </summary>
        public DVector3 OrbitNormalD => (DVector3)orbitNormal;
        /// <summary>初始相位（弧度，t=0 时的角度）</summary>
        public double initialPhase;

        // ── XML 交叉引用 ──
        /// <summary>父天体的 defName（XML 引用），空表示直接绕恒星</summary>
        public string parentBodyDefName;

        // ── 运行时解析（不由 XML 直接填充）──
        /// <summary>父天体的 bodyId，-1 表示绕恒星</summary>
        public int ParentBodyId
        {
            get; private set;
        }

        // ── 运行时预加载 ──
        /// <summary>此天体的卫星列表（运行时由 CelestialBodyManager 填充）。</summary>
        [Verse.Unsaved]
        public List<CelestialBodyDef> moonBodies = new List<CelestialBodyDef>();

        /// <summary>
        /// RimWorld Def 系统的引用解析。
        /// 将 parentBodyDefName 解析为实际的 ParentBodyId。
        /// </summary>
        public override void ResolveReferences()
        {
            base.ResolveReferences();
            ParentBodyId = -1;
            if (!string.IsNullOrEmpty(parentBodyDefName))
            {
                var parent = DefDatabase<CelestialBodyDef>.GetNamedSilentFail(parentBodyDefName);
                if (parent != null)
                    ParentBodyId = parent.bodyId;
                else
                    Log.Error(string.Format("[CelestialBodyDef] {0}: parentBodyDefName '{1}' not found.", defName, parentBodyDefName));
            }
        }

        /// <summary>
        /// 是否拥有轨道（恒星返回 false）。
        /// </summary>
        public bool HasOrbit => bodyType != CelestialBodyType.Star;

        /// <summary>
        /// 是否为引力源（有 GM 的天体都算）。
        /// </summary>
        public bool IsGravitySource => gravitationalParameter > 0.0;
    }
}
