using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **载荷舱**（2026-10-26；用户口径：注册了载荷 group 的 part，销毁自己并在原地放出同速度的 Group）。
    ///
    /// 数据形态（已裁 **A 注册表 + 载荷无 Map**）：本组件只存 `payloadDesignId`；
    /// 载荷的零件表存在 `SpaceGameComp` 的注册表里（id → `RocketDesignSpec` 深拷，随存档落盘）。
    ///
    /// 部署语义（下一个施工步）：装配载荷零件（**不带舱盒**）→ 新 Group 逐字段拷贝父组轨道状态
    /// （位置/速度/姿态/角速度）→ `RegisterGroup` → 父舰 `RemovePart(本舱)`。
    /// 分离是**正常移除**，不走 `MarkPartDestroyed`（不触发漏光/关键件失能）。
    ///
    /// 失效：角色 `PartRole.Payload` 只是聚合出局（舱被打坏 ⇒ 载荷放不出来，注册表条目保留）。
    /// </summary>
    public class PayloadBayComponentData : PartComponentData
    {
        /// <summary>载荷设计 id（`SpaceGameComp.PayloadRegistry` 的键；0 = 未绑定）。</summary>
        public int payloadDesignId;

        static PayloadBayComponentData()
        {
            ComponentRegistry.Register<PayloadBayComponentData>();
        }

        public override PartRole Role => PartRole.Payload;

        public override bool ApplyPreset(string key, string value)
        {
            if (key != "payloadDesignId") return false;
            if (!TryParseInt(value, out int id)) return false;
            payloadDesignId = id;
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref payloadDesignId, "payloadDesignId", 0);
        }

        public override void Write(BinaryWriter w) => w.Write(payloadDesignId);
        public override void Read(BinaryReader r) => payloadDesignId = r.ReadInt32();
    }
}
