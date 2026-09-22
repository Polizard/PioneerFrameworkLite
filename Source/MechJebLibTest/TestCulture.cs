/*
 * 本项目自己的测试基础设施 —— **不是移植代码**。
 * （与上游 MechJebLibTest 逐字移植来的文件分开；本文件与上游无一一对应物，故不带上游 SPDX 头。）
 */

using System.Globalization;
using System.Threading;
using MechJebLib.Utils;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("MechJebLibTest.TestCulture", "MechJebLibTest")]

namespace MechJebLibTest
{
    /// <summary>
    /// 测试进程的框架级设置。**本类合并了两件来源不同的事**，因为一个程序集只能声明一个
    /// <c>[assembly: TestFramework]</c>：
    ///
    /// **① 上游 <c>TestInitialization.cs</c> 的行为（逐字保留其一行）**：
    /// <c>ObjectPoolBase.UseGlobal = false</c> —— 用按线程的对象池替代全局池，把池在用例之间隔离。
    /// 上游把它写在 <c>MechJebLibTest.TestInitialization</c> 里；我们把它并进本类，
    /// 因为本类已经是这个程序集的框架钩子。**行为与上游一致。**
    /// （上游那个文件因此不再单独移植；它存在的唯一目的就是这一行。）
    ///
    /// **② 本项目加的区域设置钉死**：
    /// 上游至少有一条断言依赖区域设置 —— <c>StaticTests.ToSITest</c> 断言
    /// <c>double.PositiveInfinity.ToSI() == "Infinity"</c>，而 <c>Statics.ToSI</c> 对非有限值走的是
    /// <c>d.ToString()</c>（**无格式 ⇒ 吃当前文化**）。在 zh-CN 机器上 <c>PositiveInfinitySymbol</c> 是 <c>"∞"</c>，
    /// 于是这条**在非英文机器上必然红**。这不是移植缺陷（移植文件逐字节相同），是上游测试对环境的隐含假设。
    /// 处置与本项目 <c>tools/test-all.ps1</c> 强制 <c>DOTNET_CLI_UI_LANGUAGE=en</c> 同源：让读数与机器无关。
    /// </summary>
    public class TestCulture : XunitTestFramework
    {
        public TestCulture(IMessageSink messageSink) : base(messageSink)
        {
            // ① 上游 TestInitialization.cs 的那一行
            ObjectPoolBase.UseGlobal = false;

            // ② 本项目：把文化钉死为 Invariant
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        }
    }
}
