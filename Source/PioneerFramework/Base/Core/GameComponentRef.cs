using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// Game 组件引用缓存（2026-09-10 性能第 5 条）。
    /// Verse.Game.GetComponent&lt;T&gt; 是组件表线性扫描 + 逐项类型转换，热路径（每物理步 / 每弹 /
    /// 每船同步 / 每建筑 tick）逐次调用开销可观；组件表在 Game 生命周期内不增删，故按 Game 缓存。
    /// 跨局安全：记录所属 Game 实例，Current.Game 换成另一实例（新游戏 / 读档）时自动失效重建。
    /// 未命中（组件未注册 / 无局）不写缓存 —— 调用方下次调用会重试，与既有 Get* 助手同语义。
    /// </summary>
    public sealed class GameComponentRef<T> where T : GameComponent
    {
        private Game? game;
        private T? value;

        /// <summary>与 Current.Game?.GetComponent&lt;T&gt;() 同口径：无局或组件缺失时返回 null。</summary>
        public T Get()
        {
            var g = Current.Game;
            if (g == null) return null!;
            if (!ReferenceEquals(game, g))
            {
                game = g;
                value = null;
                if (PerfProbe.Enabled) PerfProbe.CompRefGameSwitch(typeof(T).Name);
            }
            if (value == null)
            {
                value = g.GetComponent<T>();
                // 未命中不写缓存：组件尚未注册时下次重试（探针区分"实查成功"与"组件缺失"）
                if (PerfProbe.Enabled) PerfProbe.CompRefMiss(typeof(T).Name, value != null);
            }
            else if (PerfProbe.Enabled)
            {
                PerfProbe.CompRefHit(typeof(T).Name); // 命中 = 省下的一次组件表线性扫描
                if (PerfProbe.ShadowChecks && PerfProbe.ShouldShadowCompRef(typeof(T).Name))
                    PerfProbe.CompRefShadow(typeof(T).Name, value, g.GetComponent<T>());
            }
            return value!;
        }
    }
}
