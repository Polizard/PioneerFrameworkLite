using System.Collections.Generic;
using PioneerFramework.GroundAssembly;

namespace PioneerFramework.Payload
{
    /// <summary>
    /// 载荷承载接口。PFL 机制层通过它读取火箭/飞船携带的载荷列表并校验容量。
    /// </summary>
    public interface IPayloadCarrier
    {
        /// <summary>当前已配置载荷。</summary>
        IReadOnlyList<RocketPayloadSpec> Payloads { get; }

        /// <summary>载荷容量（抽象单位；具体容量规则由 DMS:NF 定义）。</summary>
        float PayloadCapacity { get; }

        /// <summary>检查是否还能携带指定载荷。</summary>
        bool CanCarry(RocketPayloadSpec payload);
    }
}
