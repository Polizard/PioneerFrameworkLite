using System.Diagnostics;
using Verse;

namespace PioneerFramework
{
    public partial class SpaceEntityGroup
    {
        /// <summary>组级诊断消息（P1-1：与 `PhysicsLogMessage` 同一 `PF_DIAG` 开关，Release 整体切除）。</summary>
        [Conditional("PF_DIAG")]
        private void GroupPhysicsMessage(string text) => Verse.Log.Message(text);
        private void GroupPhysicsWarning(string text) => Verse.Log.Warning(text);
    }
}
