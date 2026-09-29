using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **静态初始化触达测试**（2026-09-27 补，起因是一次真实的红字事故）。
    ///
    /// ## 为什么需要它（这是闸门的一个真实盲区）
    /// `2026-09-27` 我把 `SwarmScaleAcceptanceRunner.FieldExact` 声明写在了 `FieldNames` **之前**：
    /// 静态字段按**文本顺序**初值化 ⇒ 轮到 `FieldExact` 时 `FieldNames` 还是 `null` ⇒ `Enumerable.Select`
    /// 抛 `ArgumentNullException` ⇒ **整个类型的静态构造抛 `TypeInitializationException`**。
    /// 后果：**一进游戏满屏红字**（调试页签每建一次节点就再抛一次，日志里 200 个错误块）。
    ///
    /// **为什么编译器与既有测试都没拦住**：
    /// · 编译器只管语法/类型，**静态初始化顺序是运行期语义** ✗；
    /// · 单元/集成测试**不碰这些调试跑批器**（它们是给游戏内调试页签用的）⇒ 静态构造从没被执行 ✗；
    /// · 闸门 9 步全绿、警告数 = 基线 ⇒ 全绿也照样漏 ✗。
    ///
    /// ## 这个测试做什么
    /// 把**调试/探针一族**的类型逐个**强制跑一遍静态构造**
    /// （<see cref="RuntimeHelpers.RunClassConstructor"/>）⇒ 谁在里面抛，这里当场红。
    /// ⚠ **只覆盖"纯 C#、不依赖 Unity 运行时"的那一族**（名字里带 Debug/Probe/Runner/Acceptance/Sweep/SelfTest）。
    /// 依赖引擎的类型（组件、窗口）不在此列 —— 它们在游戏启动时自然会被碰，且在没有 Unity 的测试宿主里
    /// 触碰它们会因**环境**而非缺陷抛异常（噪声会淹没真信号）。
    ///
    /// ## 边界（不许用沉默冒充覆盖）
    /// 它**不**证明那些类型的逻辑对，只证明**它们的静态初始化不抛**；
    /// 也**不**覆盖"静态初始化之后才被污染的字段"（那是别的病）。
    /// </summary>
    public class StaticInitTouchTests
    {
        private static bool IsDebugInitType(Type t)
        {
            if (t == null || !t.IsClass) return false;
            // ⚠ `Namespace` 可以是 null（全局命名空间的类型）—— 本测试第一版就栽在这里（NRE）。
            // 不用 `string?`（本工程没开 nullable 上下文 ⇒ 会带出 CS8632 新警告，而"新增警告 0"是硬线）。
            string ns = t.Namespace;
            if (ns == null || !ns.StartsWith("PioneerFramework", StringComparison.Ordinal)) return false;
            string n = t.Name;
            foreach (string key in DebugInitNameKeys)
                if (n.IndexOf(key, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static readonly string[] DebugInitNameKeys =
            { "Debug", "Probe", "Runner", "Acceptance", "Sweep", "SelfTest", "Diagnostics" };

        /// <summary>把调试/探针一族的静态构造**全部**跑一遍 ⇒ 任一抛异常即失败（含异常原文）。</summary>
        [Fact]
        public void DebugFamily_StaticInitializers_DoNotThrow()
        {
            var touched = new List<string>();
            var failures = new List<string>();

            foreach (var t in typeof(SpaceGameComp).Assembly.GetTypes())
            {
                if (!IsDebugInitType(t)) continue;
                try
                {
                    RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                    touched.Add(t.FullName);
                }
                catch (Exception ex)
                {
                    // 展开 TypeInitializationException 的内层：真正的根因在 InnerException 里
                    var root = ex is TypeInitializationException && ex.InnerException != null ? ex.InnerException : ex;
                    failures.Add(t.FullName + " ⇒ " + root.GetType().Name + ": " + root.Message);
                }
            }

            Assert.True(touched.Count > 0, "一个调试类型都没触到 ⇒ 名字过滤写错了（本测试会变成空转）");
            Assert.True(failures.Count == 0,
                "静态初始化抛异常（游戏里表现为**一进游戏满屏红字**）：\n  " + string.Join("\n  ", failures)
                + "\n（触达 " + touched.Count + " 个类型）");
        }
    }
}
