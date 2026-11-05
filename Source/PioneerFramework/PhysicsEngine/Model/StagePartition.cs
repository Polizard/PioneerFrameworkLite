using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **按级号切组（纯函数）**（2026-10-27 用户裁定：不需要分离器部件）。
    /// `stageId = 0` 是上面级；**抛级 = 把当前最大的 stageId 整级切走**。
    /// 父组保留 `0..max−1`；没有多于一级（`max == 0`）时不可分离。
    /// </summary>
    public static class StagePartition
    {
        public static int MaxStage(IReadOnlyList<SpacePart> parts)
        {
            int max = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && p.stageId > max) max = p.stageId;
            }
            return max;
        }

        /// <summary>把 `parts` 分成"保留"和"抛掉"两半；`max == 0` 时两半无意义（调用方先判）。</summary>
        public static void Split(IReadOnlyList<SpacePart> parts, int maxStage,
            out List<SpacePart> kept, out List<SpacePart> jettisoned)
        {
            kept = new List<SpacePart>(parts.Count);
            jettisoned = new List<SpacePart>();
            if (maxStage <= 0)   // 单级船：没有可抛的（调用方通常已先判，这里也兜住）
            {
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i] != null) kept.Add(parts[i]);
                return;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                if (p.stageId == maxStage) jettisoned.Add(p);
                else kept.Add(p);
            }
        }

        /// <summary>这一级里出现过的块号（升序；空表 = 该级没有件）。2026-11-03。</summary>
        public static List<int> BlocksOf(IReadOnlyList<SpacePart> parts, int stageId)
        {
            var blocks = new List<int>();
            if (parts == null) return blocks;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.stageId != stageId) continue;
                if (!blocks.Contains(p.blockId)) blocks.Add(p.blockId);
            }
            blocks.Sort();
            return blocks;
        }

        /// <summary>把一个级里的某一块切出来；同级其它块与其余级都留在 kept。2026-11-03。</summary>
        public static void SplitBlock(IReadOnlyList<SpacePart> parts, int stageId, int blockId,
            out List<SpacePart> kept, out List<SpacePart> jettisoned)
        {
            kept = new List<SpacePart>(parts?.Count ?? 0);
            jettisoned = new List<SpacePart>();
            if (parts == null) return;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                if (p.stageId == stageId && p.blockId == blockId) jettisoned.Add(p);
                else kept.Add(p);
            }
        }

        /// <summary>
        /// 活动级集合（C-full，2026-11-03）：最大级 + 标了“并联点火”的下级（可传递）。
        /// 串行火箭仍只有最大级；并联火箭（芯级 + 助推器）一起烧。销毁件不计。
        /// </summary>
        public static HashSet<int> ActiveStages(IReadOnlyList<SpacePart> parts)
        {
            var active = new HashSet<int>();
            if (parts == null) return active;

            int max = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && !p.isDestroyed && p.stageId > max) max = p.stageId;
            }
            active.Add(max);
            for (int s = max - 1; s >= 0; s--)
            {
                if (!active.Contains(s + 1)) continue;
                bool flagged = false;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p != null && !p.isDestroyed && p.stageId == s && p.igniteWithUpperStage)
                    {
                        flagged = true;
                        break;
                    }
                }
                if (flagged) active.Add(s);
            }
            return active;
        }

        /// <summary>该级的标签（同级各行应一致；取第一件；无件 = Jettison）。2026-11-03。</summary>
        public static StageDisposition DispositionOf(IReadOnlyList<SpacePart> parts, int stageId)
        {
            if (parts == null) return StageDisposition.Jettison;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && p.stageId == stageId) return p.stageDisposition;
            }
            return StageDisposition.Jettison;
        }

        /// <summary>“不抛弃”级：成为最大级时拒绝抛级。2026-11-03。</summary>
        public static bool IsKeptStage(IReadOnlyList<SpacePart> parts, int stageId)
            => DispositionOf(parts, stageId) == StageDisposition.Keep;

        /// <summary>
        /// 该级还有挂在它身上的子级在场（`parentStageId == stageId` 且 `stageId` 不同）⇒ 不能抛。
        /// 规则：子级不抛时父级也不抛（只能从叶子往根抛）。2026-11-03。
        /// </summary>
        public static bool HasAttachedChildStage(IReadOnlyList<SpacePart> parts, int stageId)
        {
            if (parts == null) return false;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && p.stageId != stageId && p.parentStageId == stageId) return true;
            }
            return false;
        }
    }
}
