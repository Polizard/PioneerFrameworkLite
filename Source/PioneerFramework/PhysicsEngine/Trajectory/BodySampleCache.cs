namespace PioneerFramework
{
    /// <summary>
    /// 天体轨道采样缓存（按天体槽位记住"最近一次算过的 tick → 位置/速度"）。
    ///
    /// **存在理由**：天体位置是 (bodyId, tick) 的纯函数（轨道参数来自静态 Def），而同一个 tick 内
    /// 它会被反复查询——射弹弹道每段每引力源查一次（第 8 条，世界系）、A0 之后的群组引力场
    /// 每个群组每步查一次（第 24 条，局部系）。命中即省下一次递归父链 + 每级 Cos/Sin。
    ///
    /// ⚠ **设计约束（关键，2026-09-14 踩过）**：群组物理跑在 `Parallel.For` 里
    /// （`SpaceGameComp.PARALLEL_PHYSICS_MIN_GROUPS` 起），所以本类型会被**多个工作线程并发读写**。
    /// 由此定下两条硬规矩：
    ///
    ///  ① **值体必须是不可变引用对象**，槽位只做**一次引用赋值**（.NET 保证引用赋值原子）。
    ///     这样读者拿到的永远是"某一次完整写入的 (tick, pos, vel)"三元组，不可能出现
    ///     "tick 是新写入的、位置分量还是上一次的"。
    ///     ——旧实现是 `Dictionary<int,(double tick, DVector3 v)>`：`ValueTuple` 是 32 字节**值类型**，
    ///     字段逐个拷贝（x/y/z 分多次写），**不是原子写**。游戏内实测 3 次"命中 tick 逐位相同
    ///     但值差 5.3 / 15.8 / 31.5 m"（t=509.033/525.100/526.800，天体 2 = 月球）。
    ///     因为算式是纯函数（同一 tick 必然同一结果），这种"命中却不同值"在数学上只可能来自撕裂写入。
    ///     注意那时**没有**任何读值逻辑出错：纯函数 + tick 逐位相等的命中判定是对的，错的是共享内存的写入方式。
    ///
    ///  ② **槽位是数组下标，不是字典键**。bodyId → 槽位的映射在 `EnsureInitialized` 里建好后
    ///     **只读**（并发读字典是安全的），而写只落在数组元素上。这样并发期不发生任何字典写入
    ///     —— 并发写 `Dictionary` 不只是撕裂值的问题，还可能丢更新、破坏内部桶链。
    /// </summary>
    public sealed class BodySampleCache
    {
        /// <summary>一次完整采样：tick + 该 tick 的位置/速度。构造后不再改动（不可变 ⇒ 读者不会看到半成品）。</summary>
        private sealed class Sample
        {
            public readonly double tick;
            public readonly DVector3 pos;
            public readonly DVector3 vel;

            public Sample(double tick, DVector3 pos, DVector3 vel)
            {
                this.tick = tick;
                this.pos = pos;
                this.vel = vel;
            }
        }

        private Sample[] slots = new Sample[4];

        /// <summary>按槽位数扩容。**只在初始化期（单线程）调用**；并发期不改变数组身份。</summary>
        public void EnsureCapacity(int count)
        {
            if (count <= slots.Length) return;
            slots = new Sample[System.Math.Max(count, slots.Length * 2)];
        }

        /// <summary>清空全部槽位（天体表重建 / 读档时调用：缓存按"tick 逐位相等"命中，而读旧档会让 tick 回退）。</summary>
        public void Reset()
        {
            var empty = new Sample[slots.Length];
            slots = empty;
        }

        /// <summary>
        /// 取该槽位在 tick 时刻的采样。**只有 tick 逐位相等才算命中**——所以命中即"这份值确实算自该 tick"。
        /// </summary>
        public bool TryGet(int slot, double tick, out DVector3 pos, out DVector3 vel)
        {
            Sample? s = ((uint)slot < (uint)slots.Length) ? slots[slot] : null;
            if (s != null && s.tick == tick)
            {
                pos = s.pos;
                vel = s.vel;
                return true;
            }
            pos = DVector3.zero;
            vel = DVector3.zero;
            return false;
        }

        /// <summary>写入一次采样（单次引用赋值 = 原子）。并发写同一槽位：最后一个生效，每个读者看到的都是完整的某一版。</summary>
        public void Store(int slot, double tick, DVector3 pos, DVector3 vel)
        {
            if ((uint)slot >= (uint)slots.Length) return;
            slots[slot] = new Sample(tick, pos, vel);
        }
    }
}
