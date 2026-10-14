using System;
using System.Collections.Generic;

namespace PioneerFramework.Components
{
    /// <summary>
    /// 部件组件注册表（无反射）。组件类在静态构造器显式注册，键为 TypeId。
    /// 仅在低频路径调用：建船 / 读档 / 模板加载 / 组件增删。
    /// </summary>
    public static class ComponentRegistry
    {
        private static readonly Dictionary<string, Func<PartComponentData>> factories =
            new();

        /// <summary>注册组件类型。TypeId 默认取类名。</summary>
        public static void Register<T>() where T : PartComponentData, new()
        {
            var instance = new T();
            factories[instance.TypeId] = () => new T();
        }

        /// <summary>按 TypeId 实例化组件；未注册返回 null。</summary>
        public static PartComponentData? Create(string typeId)
        {
            if (!factories.TryGetValue(typeId, out var factory))
                return null;
            return factory();
        }

        public static bool IsRegistered(string typeId) => factories.ContainsKey(typeId);

        /// <summary>全部已注册 TypeId（调试 / UI 增删组件用）。</summary>
        public static IEnumerable<string> RegisteredTypeIds => factories.Keys;

        /// <summary>已注册组件数量。</summary>
        public static int Count => factories.Count;
    }
}
