using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭载荷配置数据。首发只做配置、保存、随火箭入轨；
    /// 不在口袋地图上生成实体，也不做部署/使用逻辑。
    /// </summary>
    public class RocketPayloadSpec : IExposable
    {
        /// <summary>载荷 DefName，由 DMS:NF 内容层定义。</summary>
        public string defName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>载荷类型/安装位标识。</summary>
        public string installPosition = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>数量。</summary>
        public int quantity = 1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref installPosition, "installPosition");
            Scribe_Values.Look(ref quantity, "quantity", 1);
        }
    }
}
