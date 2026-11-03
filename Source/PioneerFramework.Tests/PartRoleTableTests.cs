using System;
using System.Collections.Generic;
using PioneerFramework.Combat;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **§7 统一失效语义的判据**（2026-10-22 部件功能化）。
    ///
    /// 钉住三件事：
    /// · **表完整**：`PartRole` 的每个值在 `PartRoleTable` 里都有一行（缺行 = 启动期抛，不是运行时静默）；
    /// · **组件声明角色**：注册进 `ComponentRegistry` 的组件 `Role != None`（否则启动期抛）；
    /// · **词表不悬空**：每个角色至少有一个内置组件认领（加了角色却没实现 = 红）。
    /// 另加一条行为判据：一件上挂两台引擎、整件被打坏 ⇒ **两台都出局**（旧实现只出局第一台）。
    /// </summary>
    public class PartRoleTableTests
    {
        [Fact]
        public void BuiltIns_RolesAndTable_AreComplete()
        {
            ComponentRegistry.RegisterBuiltIns();

            PartRoleTable.Validate();   // 任一角色缺行 / 任一注册组件没声明角色 ⇒ 这里抛

            Assert.True(ComponentRegistry.Count >= 12, $"内置组件数={ComponentRegistry.Count}（应为 12）");
        }

        [Fact]
        public void EveryRole_IsClaimedBySomeBuiltInComponent()
        {
            ComponentRegistry.RegisterBuiltIns();

            var claimed = new HashSet<PartRole>();
            foreach (string typeId in ComponentRegistry.RegisteredTypeIds)
            {
                var component = ComponentRegistry.Create(typeId);
                Assert.NotNull(component);
                if (component!.Role != PartRole.None) claimed.Add(component.Role);
            }

            foreach (PartRole role in Enum.GetValues<PartRole>())
            {
                if (role == PartRole.None) continue;
                Assert.True(claimed.Contains(role), $"角色 {role} 没有任何内置组件认领（词表悬空）");
            }
        }

        [Fact]
        public void NewFunctionalComponents_AreRegistered()
        {
            ComponentRegistry.RegisterBuiltIns();

            Assert.True(ComponentRegistry.IsRegistered("ReactorComponentData"));
            Assert.True(ComponentRegistry.IsRegistered("RadiatorComponentData"));
            Assert.True(ComponentRegistry.IsRegistered("CargoComponentData"));
            Assert.True(ComponentRegistry.IsRegistered("WeaponMountComponentData"));
        }

        [Fact]
        public void DestroyedPart_WithTwoEngines_RemovesBothFlows()
        {
            var def = new PropellantDef { defName = "Q" };
            var e = new SpaceEntity(7, 0);

            var hull = new SpacePart { partId = 1, defName = "hull", boundsSizeShip = new DVector3(4, 4, 4) };
            hull.AddComponent(new HullMassComponent { mass = 1000f });

            var enginePart = new SpacePart { partId = 2, defName = "engine", boundsSizeShip = new DVector3(2, 2, 2) };
            enginePart.AddComponent(new HullMassComponent { mass = 100f });
            enginePart.AddComponent(new EngineComponentData { exhaustVelocity = 3000f, massFlowRate = 10f, propellantDefName = "Q" });
            enginePart.AddComponent(new EngineComponentData { exhaustVelocity = 3000f, massFlowRate = 10f, propellantDefName = "Q" });

            e.SetParts(new List<SpacePart> { hull, enginePart });
            e.AddPropulsion(new PropulsionData
            {
                propellant = def,
                ve = 3000f,
                massFlow = 40f,
                mass = 0.0,
                capacity = 0.0,
            });
            Assert.Equal(40f, e.Networks[0].MassFlow, 3);

            enginePart.ApplyDamage(9999f);
            e.MarkPartDestroyed(enginePart.partId);

            // 两台各 10 ⇒ 40 − 20 = 20（旧实现按 GetComponent 只拿到第一台，会停在 30）
            Assert.Equal(20f, e.Networks[0].MassFlow, 3);
        }
    }
}
