# 舰船建筑外观扩展示例

将下列 Def 放入依赖 PFL 的 Mod 的 `1.7/Defs/`。将 `modExtensions` 的 `li` 加入要显示的建筑 ThingDef。若通过 XML 补丁适配现有建筑，先确保有一份 `modExtensions` 容器，再追加 `li`。

```xml
<Defs>
  <PioneerFramework.PFView.ShipBuildingVisuals.ShipBuildingVisualDef>
    <defName>Example_DeckBeaconVisual</defName>
    <label>甲板信标外观</label>
    <nodes>
      <li>
        <id>pedestal</id>
        <mesh>cylinder</mesh>
        <position>(0,0.4,0)</position>
        <scale>(0.6,0.8,0.6)</scale>
      </li>
      <li>
        <id>head</id>
        <mesh>box</mesh>
        <position>(0,0.95,0)</position>
        <scale>(0.4,0.25,0.8)</scale>
        <color>(0.2,0.8,1,1)</color>
        <bindYaw>true</bindYaw>
      </li>
    </nodes>
  </PioneerFramework.PFView.ShipBuildingVisuals.ShipBuildingVisualDef>
</Defs>
```

```xml
<modExtensions>
  <li Class="PioneerFramework.PFView.ShipBuildingVisuals.ShipBuildingVisualExtension">
    <mounts>
      <li>
        <id>beacon</id>
        <visual>Example_DeckBeaconVisual</visual>
        <surface>Top</surface>
        <stateProvider>Example.DeckBeaconSweep.v1</stateProvider>
      </li>
    </mounts>
  </li>
</modExtensions>
```

以下提供者用可暂停的渲染时钟制作装饰性摆动。纯静态信标可省略 `stateProvider` 与 `bindYaw`，直接使用内置静态状态。

```csharp
using System;
using PioneerFramework.PFView.ShipBuildingVisuals;
using Verse;

[StaticConstructorOnStartup]
internal static class DeckBeaconRegistration
{
    static DeckBeaconRegistration()
    {
        ShipBuildingVisualRegistry.Default.RegisterStateProvider(
            "Example.DeckBeaconSweep.v1", new DeckBeaconSweep());
    }

    private sealed class DeckBeaconSweep : IShipBuildingVisualStateProvider
    {
        public VisualState Sample(in VisualStateContext context) => new()
        {
            Yaw = (float)Math.Sin(context.RenderTick * 0.05) * 60f,
            Channels = VisualChannels.Yaw
        };
    }
}
```
