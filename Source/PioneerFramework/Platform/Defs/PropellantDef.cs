using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 工质（推进剂种类）Def。标识一种可储存、可消耗的推进工质。
    /// 排气速度（比冲）由引擎决定（引擎 Def/组件持有 ve），工质仅作为
    /// 「种类标识 + 存储账户」参与 dV 体系：ΔV = ve × ln(m0/m1)。
    /// </summary>
    public class PropellantDef : Def
    {
    }
}
