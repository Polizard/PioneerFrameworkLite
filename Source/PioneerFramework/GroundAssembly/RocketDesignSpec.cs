using System.Collections.Generic;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>运输仓类型：无舱体普通运输仓 / 有舱内地图的航天舱室。</summary>
    public enum RocketCabinType
    {
        None,
        Spacecraft
    }

    /// <summary>
    /// 火箭设计规格。这是从「装配界面确认」到「框架施工」到「入轨 SpaceEntity」全程携带的数据对象。
    /// 对应 v1 设计中的 RocketDesignSpec。
    /// </summary>
    public class RocketDesignSpec : IExposable
    {
        /// <summary>火箭型号/模板 DefName。PFL 只提供调试模板，正式型号由 DMS:NF 提供。</summary>
        public string rocketDefName = "PF_Building_Rocket_Recoverable_Basic";

        /// <summary>运输仓类型。航天舱室在首次登船时才生成舱内地图；普通运输仓不生成。</summary>
        public RocketCabinType CabinType = RocketCabinType.Spacecraft;

        /// <summary>框架尺寸（正方形边长，格）。当前限制为正方形。</summary>
        public int frameSize = 3;

        /// <summary>口袋地图尺寸（格）。与框架尺寸相互独立，由型号模板决定。</summary>
        public int mapWidth = 50;

        /// <summary>口袋地图尺寸（格）。</summary>
        public int mapHeight = 50;

        /// <summary>燃料量（抽象单位；后续由 ChemfuelToDeltaVMapper 换算为 dV/工质）。</summary>
        public float fuelAmount;

        /// <summary>Part 配置。</summary>
        public List<DesignPartSpec> parts = new();

        /// <summary>载荷配置。</summary>
        public List<RocketPayloadSpec> payloads = new();

        /// <summary>对接口预留。首发只保存字符串标记，不实现拼接。</summary>
        public List<string> dockingPorts = new();

        public void ExposeData()
        {
            Scribe_Values.Look(ref rocketDefName, "rocketDefName", "PF_Building_Rocket_Recoverable_Basic");
            Scribe_Values.Look(ref CabinType, "cabinType", RocketCabinType.Spacecraft);
            Scribe_Values.Look(ref frameSize, "frameSize", 3);
            Scribe_Values.Look(ref mapWidth, "mapWidth", 50);
            Scribe_Values.Look(ref mapHeight, "mapHeight", 50);
            Scribe_Values.Look(ref fuelAmount, "fuelAmount", 0f);
            Scribe_Collections.Look(ref parts, "parts", LookMode.Deep);
            Scribe_Collections.Look(ref payloads, "payloads", LookMode.Deep);
            Scribe_Collections.Look(ref dockingPorts, "dockingPorts", LookMode.Value);
            if (parts == null) parts = new List<DesignPartSpec>();
            if (payloads == null) payloads = new List<RocketPayloadSpec>();
            if (dockingPorts == null) dockingPorts = new List<string>();
        }

        /// <summary>
        /// 首发校验规则：正方形框架、至少 1 个推进器、至少 1 个储罐。
        /// 载荷数量限制留到接入具体安装位后实现。
        /// </summary>
        public bool IsValid(out string failReason)
        {
            failReason = string.Empty;
            if (frameSize < 1)
            {
                failReason = "框架尺寸必须为正数。";
                return false;
            }

            int thrusterCount = 0;
            int tankCount = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i] == null) continue;
                // 件数 = 站数 × 并联台数（2026-10-29）：并联 6 台是 6 个推进器，不是 1 个。
                if (parts[i].category == "thruster") thrusterCount += RocketDesignRules.PartCountOf(parts[i]);
                if (parts[i].category == "tank") tankCount += RocketDesignRules.PartCountOf(parts[i]);
            }

            if (thrusterCount < 1)
            {
                failReason = "至少需要 1 个推进器。";
                return false;
            }
            if (tankCount < 1)
            {
                failReason = "至少需要 1 个储罐。";
                return false;
            }
            return true;
        }
    }
}
