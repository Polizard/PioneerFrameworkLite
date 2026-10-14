using System.Collections.Generic;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭 Part 的配置数据。Part 不是地面实体，也不是口袋地图上的可视建筑，
    /// 它以数据形式存在于 RocketDesignSpec 中，施工/发射/入轨时由机制层消费。
    /// </summary>
    public class RocketPartSpec : IExposable
    {
        /// <summary>Part 的 DefName / 模板 ID，由 DMS:NF 型号定义填充。</summary>
        public string defName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>Part 类型，例如 thruster / tank / stage / payload-mount。</summary>
        public string category = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>数量。框架施工代表这些 Part 被一起建造。</summary>
        public int quantity = 1;

        /// <summary>安装位置描述。首发只做保存和透传，后续可用于自定义布局。</summary>
        public string installPosition = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref category, "category");
            Scribe_Values.Look(ref quantity, "quantity", 1);
            Scribe_Values.Look(ref installPosition, "installPosition");
        }
    }
}
