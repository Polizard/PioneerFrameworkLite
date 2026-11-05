using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 航天器对接端口（第一版仅数据接口，不在本批实现对接逻辑）。
    /// </summary>
    public class DockingPort : IExposable
    {
        public int ownerSpacecraftId = -1;
        public int targetSpacecraftId = -1;
        public bool isConnected;

        public void ExposeData()
        {
            Scribe_Values.Look(ref ownerSpacecraftId, "ownerSpacecraftId", -1);
            Scribe_Values.Look(ref targetSpacecraftId, "targetSpacecraftId", -1);
            Scribe_Values.Look(ref isConnected, "isConnected", false);
        }
    }
}
