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

        /// <summary>
        /// **内置组件全量触达**（2026-10-22 部件功能化）：注册点在各自的静态构造器里，而静态构造器
        /// 只有在类型**第一次被碰到**时才跑 ⇒ 只被"目录预置 typeId 字符串"引用的组件（如武器挂点）
        /// 会永远没注册、`Create(typeId)` 恒返回 null。启动时把内置类型逐个碰一遍即补上。
        ///
        /// 新增内置组件 = 这里加一行（与 `PartRole`/`PartRoleTable` 三件套同一处纪律：
        /// 漏了会在 `ValidateRoles` 里红，不会静默）。
        /// </summary>
        public static void RegisterBuiltIns()
        {
            Touch<HullMassComponent>();
            Touch<RoomComponentData>();
            Touch<EngineComponentData>();
            Touch<PropellantTankComponentData>();
            Touch<SensorArrayComponentData>();
            Touch<JammerComponentData>();
            Touch<IntakeComponentData>();
            Touch<PartInertiaComponent>();
            Touch<ReactorComponentData>();
            Touch<RadiatorComponentData>();
            Touch<CargoComponentData>();
            Touch<WeaponMountComponentData>();
            Touch<BatteryComponentData>();
            Touch<ControlCoreComponentData>();
            Touch<PayloadBayComponentData>();
        }

        /// <summary>`new T()` 的唯一目的 = 触发 `T` 的静态构造器（注册点）；实例本身丢弃。</summary>
        private static void Touch<T>() where T : PartComponentData, new() => _ = new T();

        /// <summary>
        /// **每个已注册组件必须声明角色**（§7：缺行 = 启动期报错，不是运行时静默）。
        /// 由 <c>PartRoleTable.Validate()</c> 在启动时调用；测试里也直接调。
        /// </summary>
        public static void ValidateRoles()
        {
            foreach (var factory in factories.Values)
            {
                var component = factory();
                if (component == null || component.Role == PartRole.None)
                    throw new InvalidOperationException(
                        $"[PartRole] 注册组件 {component?.TypeId ?? "<null>"} 没有声明 Role（§7：每个角色必须在表里有一行）");
            }
        }

        public static bool IsRegistered(string typeId) => factories.ContainsKey(typeId);

        /// <summary>全部已注册 TypeId（调试 / UI 增删组件用）。</summary>
        public static IEnumerable<string> RegisteredTypeIds => factories.Keys;

        /// <summary>已注册组件数量。</summary>
        public static int Count => factories.Count;
    }
}
