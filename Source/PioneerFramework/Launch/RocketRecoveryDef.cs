using System.Collections.Generic;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 可回收火箭的回收/重建配置。
    /// DMS:NF 的 CV 型号在内容层填写各自 Def；PFL 只提供机制和基础测试 Def。
    /// </summary>
    public class RocketRecoveryDef : Def
    {
        /// <summary>对应的可回收火箭 ThingDef（也可用 defName 与火箭 defName 同名来关联）。</summary>
        public ThingDef launchedRocketDef;

        /// <summary>可选的未完工框架 ThingDef；为空时回退到火箭 ThingDef 自动生成的 frameDef。</summary>
        public ThingDef frameDef;

        /// <summary>发射后回收下来的返回舱/第一级 ThingDef。</summary>
        public ThingDef returnedThingDef;

        /// <summary>返回舱入轨/再入时使用的 Skyfaller ThingDef。</summary>
        public ThingDef incomingSkyfallerDef;

        /// <summary>落地后未开舱的 ActivePod ThingDef。</summary>
        public ThingDef activePodDef;

        /// <summary>重建该火箭所需的材料表。</summary>
        public List<ThingDefCountClass> rebuildCost = new List<ThingDefCountClass>();
    }
}
