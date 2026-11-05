using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// 成员入编结果（<see cref="ConstellationCore.CheckAdd"/> 输出）。
    /// 拆出原因码而非单 bool：调用方（DMS:NF 载荷部署 / 调试自检）需要区分"数据没找到"与"资格不符"，
    /// 否则部署链只能拿到"没进去"而无从定位。列表顺序 = 判定顺序。
    /// </summary>
    public enum ConstellationAddResult
    {
        /// <summary>入编成功。</summary>
        Ok = 0,
        /// <summary>目标星座不存在（id 拼错 / 星座已被删除）。</summary>
        ConstellationNotFound = 1,
        /// <summary>groupId 不在 SpaceGameComp.groups（群组已销毁 / 尚未注册）。</summary>
        GroupMissing = 2,
        /// <summary>同星座内已有该 groupId（多归属跨星座允许，同星座内不允许重复）。</summary>
        AlreadyMember = 3,
        /// <summary>资格不符：既非圆轨、也非地表锚点（深空自由飞行体 / 导弹 / 机动中的飞船）。</summary>
        NotAdmissible = 4
    }

    /// <summary>
    /// 星座纯核心（零 Verse 依赖，链入纯测试项目）：
    /// 准入判定 + 锚点位置解析 + 多归属反查索引容器。
    /// 组件 <see cref="ConstellationGameComp"/> 只做"读 groups、调这些纯函数、写存档"的编排。
    /// </summary>
    public static class ConstellationCore
    {
        /// <summary>
        /// 成员准入：圆轨（组织语义上的"在轨部署完毕"）**或** 地表锚点。
        /// 排除深空自由飞行体 / 导弹 / 机动中飞船——那些是"正在飞的东西"，不是编组单元。
        /// </summary>
        public static bool IsAdmissible(bool isCircularOrbit, bool isSurfaceAnchor)
            => isCircularOrbit || isSurfaceAnchor;

        /// <summary>
        /// 入编判定（纯函数：只吃布尔量，由调用方查表后传入）。
        /// 判定顺序 = 枚举顺序：星座不存在 → 群组不存在 → 已入编 → 资格不符。
        /// </summary>
        public static ConstellationAddResult CheckAdd(bool constellationExists, bool groupExists,
            bool isCircularOrbit, bool isSurfaceAnchor, bool alreadyMember)
        {
            if (!constellationExists) return ConstellationAddResult.ConstellationNotFound;
            if (!groupExists) return ConstellationAddResult.GroupMissing;
            if (alreadyMember) return ConstellationAddResult.AlreadyMember;
            if (!IsAdmissible(isCircularOrbit, isSurfaceAnchor)) return ConstellationAddResult.NotAdmissible;
            return ConstellationAddResult.Ok;
        }

        /// <summary>
        /// 地表锚点在**系统系**中的位置 = 天体在该系中的位置 + R_body × dir(tile)。
        /// bodyFrameOffset 的正确来源 = SpaceEntityGroup.GetCentralBodyOffset 口径（行星 → 零；卫星 → 绕行星偏移），
        /// 与碰撞 / 圆轨解析 / N 体积分同系——**不是** CelestialBodyManager.GetBodyPositionAbs
        /// （那是递归到恒星的绝对位置，地球绕日 120,000 km，2026-09-10 初版曾误用）。
        /// 坐标系约定的**唯一出处**见 `CelestialBodyManager.GetBodyPositionAbs` 的文档块。
        /// dir 用「天体固连系」的 tile 中心方向（不含自转相位）——锚点是静态初始化的编组占位，
        /// 自转带来的漂移见设计文档 §12（未来组网通信需要精确位置时挂解析刷新）。
        /// dir 退化（零向量）时退回天体中心，不产生 NaN。
        /// </summary>
        public static DVector3 AnchorPositionSystem(DVector3 bodyFrameOffset, DVector3 tileCenterDir, double bodyRadiusMeters)
        {
            if (tileCenterDir.sqrMagnitude < 1e-18)
                return bodyFrameOffset;
            return bodyFrameOffset + tileCenterDir.normalized * bodyRadiusMeters;
        }
    }

    /// <summary>
    /// 多归属反查索引：groupId → 星座 id 集（一个 group 可同时属于多个星座）。
    /// 派生数据（单一事实源永远是星座成员表），本类只保证增删对称、空集清理——防止
    /// "成员已移除但索引还留着 cid"这类单向泄漏把群组销毁收尸引向不存在的星座。
    /// </summary>
    public class ConstellationIndex
    {
        private readonly Dictionary<int, HashSet<int>> byGroup = new();

        /// <summary>索引覆盖的群组数（= 至少属于一个星座的群组数）。</summary>
        public int GroupCount => byGroup.Count;

        /// <summary>清空（读档重建用）。</summary>
        public void Clear() => byGroup.Clear();

        /// <summary>登记归属（重复登记幂等）。</summary>
        public void Add(int groupId, int constellationId)
        {
            if (!byGroup.TryGetValue(groupId, out var set))
            {
                set = new HashSet<int>();
                byGroup[groupId] = set;
            }
            set.Add(constellationId);
        }

        /// <summary>注销归属；集合空则整条移除（空集残留 = 泄漏）。</summary>
        public void Remove(int groupId, int constellationId)
        {
            if (!byGroup.TryGetValue(groupId, out var set)) return;
            set.Remove(constellationId);
            if (set.Count == 0) byGroup.Remove(groupId);
        }

        /// <summary>该群组是否已不属任何星座（锚点释放判据之一）。</summary>
        public bool IsUnreferenced(int groupId)
            => !byGroup.TryGetValue(groupId, out var set) || set.Count == 0;

        /// <summary>该群组所属的全部星座 id（无归属返回空集，非 null）。</summary>
        public IReadOnlyCollection<int> Of(int groupId)
            => byGroup.TryGetValue(groupId, out var set) ? set : EmptySet;

        private static readonly HashSet<int> EmptySet = new();
    }
}
