using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// 飞船部件组件基类：纯数据 + 低频事件回调。
    /// 序列化双通道：IExposable（存档 Scribe）+ Write/Read（模板 .part 二进制）。
    /// TypeId 为注册表键（默认取类名）；组件类须在静态构造器调用 ComponentRegistry.Register。
    /// </summary>
    public abstract class PartComponentData : IExposable
    {
        /// <summary>注册表键。默认取类名；重名组件可覆写为自定义 id。</summary>
        public virtual string TypeId => GetType().Name;

        // ── 低频事件回调（不进 tick）──
        public virtual void OnAttached() { }
        public virtual void OnDamaged(float damage) { }
        public virtual void OnDestroyed() { }
        public virtual void PostLoad() { }

        /// <summary>聚合进 ShipStats。每个组件贡献自重与产出/消耗。</summary>
        public virtual void AccumulateStats(ref ShipStats s) { }

        /// <summary>
        /// 深拷贝（模板 → 运行时）。默认走 Write/Read 二进制通道；
        /// 子类有非二进制字段时可覆写。
        /// </summary>
        public virtual PartComponentData? Clone()
        {
            var dst = ComponentRegistry.Create(TypeId);
            if (dst == null) return null;
            using (var ms = new MemoryStream())
            {
                // leaveOpen: BinaryWriter/Reader 不得关闭底层流
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                {
                    Write(w);
                }
                ms.Position = 0;
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
                {
                    dst.Read(r);
                }
            }
            return dst;
        }

        // ── 存档序列化（SpacePart.ExposeData → Scribe_Deep）──
        public abstract void ExposeData();

        // ── 模板文件二进制（.part v4+）──
        public abstract void Write(BinaryWriter w);
        public abstract void Read(BinaryReader r);
    }
}
