using System.Reflection;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShotWeaponTests
    {
        [Fact]
        public void InvalidExplicitShotPreventsFireAndDoesNotSpendAmmo()
        {
            var definition = new WeaponDef { defName = "ConfiguredGun" };
            var field = typeof(WeaponDef).GetField("defaultShot");
            Assert.NotNull(field);
            field.SetValue(definition, (ShotDefReference)new ShotDef { defName = "Invalid", label = "invalid" });
            var weapon = new WeaponInstance { def = definition, ammo = 3 };
            Assert.False(weapon.CanFire());
            weapon.Fire();
            Assert.Equal(3, weapon.ammo);
            Assert.Equal(0, weapon.cooldownRemaining);
        }

        [Fact]
        public void MissingLegacySelectionUsesDefaultButUnavailableSavedSelectionFailsClosed()
        {
            var shot = ShotRuntimeTests.Ballistic();
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "SavedGun", defaultShot = shot, allowedShots = new() { shot } }, ammo = 7 };
            Assert.True(weapon.TryGetSelectedShot(out var chosen));
            Assert.Same(shot, chosen);
            typeof(WeaponInstance).GetField("selectedShotName", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(weapon, "RemovedMod_Shot");
            Assert.False(weapon.CanFire());
            Assert.True(weapon.TrySelectShot(shot));
            Assert.True(weapon.CanFire());
            Assert.Equal(7, weapon.ammo);
        }

        [Fact]
        public void SavedUnavailableSelectionCannotBecomeLegacyWhenWeaponConfigurationIsRemoved()
        {
            var weapon = new WeaponInstance { def = new WeaponDef { defName = "OldGun" }, ammo = 3 };
            typeof(WeaponInstance).GetField("selectedShotName", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(weapon, "RemovedShot");
            Assert.False(weapon.TryCreateShot(out _, out _));
            Assert.False(weapon.CanFire());
        }
    }
}
