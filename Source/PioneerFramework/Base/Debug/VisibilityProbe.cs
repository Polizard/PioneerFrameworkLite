using System.Text;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 可见性 / 侦测技术快照探针（[V1]，2026-09-16 随施工纪律 [D-b] 落地）。
    ///
    /// **为什么是"打一份日志"而不是"面板读数"**：本探针要验的是"**存档往返存活**"——即
    /// "字段写进 `ExposeData` 了吗"。那类检查要求**存档前 / 读档后各观测一次同一批对象**，
    /// 而面板读数有三处不便：①要先在世界视图点选某个群组 ②一次只看一个 ③读档后还得找回同一个导弹。
    /// 打一份全量快照则只需"点一次、存档、读档、再点一次、对比两份日志"。
    ///
    /// **它验什么**：`IsStealth`（有非零态：导弹 true / 飞船与站 false ⇒ 漏序列化会让导弹读档后变 false，可检出）。
    /// ✅ **`DetectTech` 现在也验得了**（2026-10-01 更新）：它自 2026-09-24 起有写入者（部件级传感器组件），
    /// 2026-10-01 起再加**建筑级投影**（传感器阵列建筑，[V7]/[N7]）⇒ 只要场景里有装了传感器的站/船，
    /// 快照里的"灵/抗压/抗骗"就是**非零**的，"存档前后两份逐行一致"从此是**真判据**（S11 的那条往返）。
    ///
    /// 【边界】只读 + 只打日志。不修改任何状态。
    /// </summary>
    public static class VisibilityProbe
    {
        /// <summary>把全部群组的 faction / 匿踪闸门 / 侦测技术三分量打一份快照到日志。</summary>
        public static void PrintSnapshot()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Log.Warning("[可见性快照] 无 SpaceGameComp（不在太空场景？）");
                return;
            }

            int total = 0;
            int stealth = 0;
            var sb = new StringBuilder();
            sb.Append("[可见性快照] 群组 = 公开/匿踪 · 侦测技术（灵敏度 / 抗压制 / 抗欺骗）");
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (!g.IsActive) continue;
                total++;
                if (g.IsStealth) stealth++;
                sb.Append("\n  G").Append(g.GroupId)
                  .Append(" F").Append(g.FactionId)
                  .Append(g.IsStealth ? " 匿踪" : " 公开")
                  .Append(g.ActivePingEnabled ? " 主动" : " 被动")
                  .Append(" · 灵").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(g.DetectTech.sensitivityMeters / 1000f):F0}").Append("km")
                  .Append(" 抗压").Append(System.Globalization.CultureInfo.CurrentCulture, $"{g.DetectTech.ecmResist:F1}")
                  .Append(" 抗骗").Append(System.Globalization.CultureInfo.CurrentCulture, $"{g.DetectTech.eccmResist:F1}")
                  // [V8] 信号强度：**会有非零值**（尺寸项对任何有实体的组都 > 0）⇒ 这是 [V1] 里
                  // 第一个"真能验出来"的往返判据（不像 DetectTech 全 0 那么空洞）。
                  // 点火前后应有跃升（burning 项 ×20 量级）——冷发射 profile 的第一次游戏内可观测。
                  .Append(" S=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{g.SignalStrength:F3}");
            }
            sb.Append("\n  合计 ").Append(total).Append(" 组，其中匿踪 ").Append(stealth).Append(" 组");
            Log.Message(sb.ToString());
        }
    }
}
