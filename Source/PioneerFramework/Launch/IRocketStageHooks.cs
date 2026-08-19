using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 火箭分级/回收/返回钩子。DMS:NF 各型号火箭通过实现此接口接入 PFL 发射生命周期。
    /// PFL 在发射、推进、销毁等关键节点调用这些回调。
    /// </summary>
    public interface IRocketStageHooks
    {
        /// <summary>分离指定级。stageIndex 从 0 开始。</summary>
        void StageSeparate(int stageIndex);

        /// <summary>回收指定级，recoverPos 为地面/地图落点。</summary>
        void StageRecover(int stageIndex, IntVec3 recoverPos, Map map);

        /// <summary>指定级返回指定世界格/地图。</summary>
        void StageReturnHome(int stageIndex, int homeTile);
    }
}
