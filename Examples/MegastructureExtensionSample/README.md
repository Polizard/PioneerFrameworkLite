# 通用巨构扩展示例

这个独立程序集演示通过公开 C# 契约注册能力、通过 XML 组合设施。它不自动安装到主 Mod，也不创建第二个管理器。

1. 使用项目固定的 SDK 10.0.402 构建 `MegastructureExtensionSample.csproj`；测试工程已引用该项目以验证公开接口。
2. 开发自己的 Mod 时，将 ProjectReference 换为对 PFL 交付 DLL 的引用，并保持依赖 PFL 的加载顺序。
3. 单独加载示例时，将示例目录作为独立 Mod，构建输出中的 `PFL.MegastructureExtensionSample.dll` 放入该示例的 `Assemblies/`。只打包自己的程序集；游戏、Harmony、PFL 的 DLL 由原 Mod 提供。
4. 在 RimWorld 1.7 中同时启用 Harmony、PFL 和此示例；开发者模式打开“巨构”页签，登记示例 Def 并激活。

`SampleCapability.cs` 包含一个计数能力及启动注册。状态只保存在 payload 中，保存恢复后按模拟 tick 继续，查询不推进状态。`CapabilityDescriptor` 的 handlerId 必须在所有扩展中唯一；在首个游戏管理器初始化前完成注册。

`Defs/SampleMegastructure.xml` 使用 FrameFixed 节点，无地图、库存或物理群组依赖。它用于验证框架组合，不包含正式巨构玩法或平衡。

完整本地接口指南位于主工作区 `Docs/MegastructureFrameworkAPI.md`。宿主自动测试 `MegastructureExtensionTests` 验证该能力确实来自另一程序集，并仅通过公开契约运行与恢复。
