using System.Collections.Generic;
using PioneerFramework.Components;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **目录预置的组件实例**（2026-10-22 部件功能化 §4）。
    ///
    /// 设计裁定的来龙去脉：08-05 文档要在 Def 里放"预置组件集"；10-17 裁定 Def 只做技术目录；
    /// 10-22 往回推一格 —— 但**不做专用字段**。所以这里只有一张通用的 `typeId + 参数键值` 表：
    /// 组件类自己用 `ApplyPreset(key, value)` 解释参数 ⇒ **加新部件角色不改 Def 类型、不改框架代码**。
    ///
    /// 判据（§4）：同一份预置装配出的 `SpacePart`，与手写 `AddComponent` 的结果**逐字段相等**
    /// （`PartComponentPresetTests` 钉住）。
    ///
    /// XML 形态（`<li>` 不带 `Class` ⇒ 用字段的声明类型 `PartComponentPreset`）：
    /// <code>
    /// &lt;presets&gt;
    ///   &lt;li&gt;
    ///     &lt;typeId&gt;ReactorComponentData&lt;/typeId&gt;
    ///     &lt;parameters&gt;
    ///       &lt;li&gt;&lt;key&gt;powerGenWatts&lt;/key&gt;&lt;value&gt;120000&lt;/value&gt;&lt;/li&gt;
    ///     &lt;/parameters&gt;
    ///   &lt;/li&gt;
    /// &lt;/presets&gt;
    /// </code>
    /// 证据（RS/rw17）：`Verse.DirectXmlToObjectNew.ResolveTypeForNode(defaultType, node)` 在**没有**
    /// `Class` 属性时直接返回字段的声明类型 ⇒ 不需要 `Class`、也不需要把类型注册进游戏程序集清单。
    /// </summary>
    public class PartComponentPreset
    {
        /// <summary>组件注册表键（默认 = 组件类名，见 `PartComponentData.TypeId`）。</summary>
        public string typeId = "";

        /// <summary>参数键值对，**按书写顺序**逐个交给组件的 `ApplyPreset`（顺序会影响同字段多次赋值）。</summary>
        public List<PartComponentPresetParam> parameters = new();

        /// <summary>
        /// 造一个组件实例、按顺序解释参数、成功才挂到 part 上（`AddComponent` 会置脏 stats）。
        /// 失败（typeId 未注册 / 参数不认识）⇒ `Log.Error` + 返回 false：**不静默留默认值**
        /// （同族事故：XML 元素拼错 ⇒ RimWorld 只报一行、值静默留在默认，见 `validate_config.py` 的 WHY）。
        /// </summary>
        public bool ApplyTo(SpacePart part)
        {
            if (part == null) return false;
            if (string.IsNullOrEmpty(typeId))
            {
                Log.Error("[ShipPartDef] 预置条目缺 <typeId>，整条忽略。");
                return false;
            }

            PartComponentData? component = ComponentRegistry.Create(typeId);
            if (component == null)
            {
                Log.Error($"[ShipPartDef] 预置组件的 typeId=\"{typeId}\" 未注册"
                    + "（内置组件见 ComponentRegistry.RegisterBuiltIns；自定义组件须在自己的静态构造器注册）。");
                return false;
            }

            if (parameters != null)
            {
                for (int i = 0; i < parameters.Count; i++)
                {
                    var param = parameters[i];
                    if (param == null || string.IsNullOrEmpty(param.key)) continue;
                    if (!component.ApplyPreset(param.key, param.value ?? ""))
                    {
                        Log.Error($"[ShipPartDef] 组件 {typeId} 不认识预置参数 \"{param.key}\"（值 \"{param.value}\"）"
                            + "—— 字段名拼错会让这个值静默留在默认。");
                        return false;
                    }
                }
            }

            part.AddComponent(component);
            return true;
        }
    }

    /// <summary>预置参数的一条键值（字符串形态；数值由组件按自己的字段类型解析）。</summary>
    public class PartComponentPresetParam
    {
        /// <summary>参数名（各组件在自己的 `ApplyPreset` 里定义，通常 = 字段名）。</summary>
        public string key = "";

        /// <summary>参数值（字符串；数值解析固定 InvariantCulture）。</summary>
        public string value = "";
    }
}
