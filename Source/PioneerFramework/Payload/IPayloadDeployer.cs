using PioneerFramework.GroundAssembly;

namespace PioneerFramework.Payload
{
    /// <summary>
    /// 载荷部署接口：把已入轨的载荷部署为独立 SpaceEntity（卫星/空间站模块等）。
    /// 正式实现由 DMS:NF 内容层填充，PFL 只冻结契约。
    /// </summary>
    public interface IPayloadDeployer
    {
        /// <summary>部署载荷到指定轨道位置，返回新生成的 SpaceEntity。</summary>
        SpaceEntity Deploy(RocketPayloadSpec payload, DVector3 targetOrbitPosition, int factionId);
    }
}
