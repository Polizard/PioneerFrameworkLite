using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>RCS 工质罐标签（2026-10-26 裁定）：一个罐服务全船 RCS，效率写在标签上。</summary>
    public class RcsTankTagTests
    {
        [Fact]
        public void Default_IsNotRcsSupply_AndEfficiencyIsPoint8()
        {
            var t = new PropellantTankComponentData();
            Assert.False(t.rcsSupply);
            Assert.Equal(0.8f, t.rcsEfficiency, 6);
        }

        [Fact]
        public void Preset_TagAndEfficiency_AreClamped()
        {
            var t = new PropellantTankComponentData();
            Assert.True(t.ApplyPreset("rcsSupply", "true"));
            Assert.True(t.ApplyPreset("rcsEfficiency", "1.7"));
            Assert.True(t.rcsSupply);
            Assert.Equal(1.0f, t.rcsEfficiency, 6);

            Assert.True(t.ApplyPreset("rcsEfficiency", "-0.2"));
            Assert.Equal(0.0f, t.rcsEfficiency, 6);

            Assert.False(t.ApplyPreset("rcsSupply", "notabool"));
        }

        [Fact]
        public void Clone_CopiesTagAndEfficiency()
        {
            var t = new PropellantTankComponentData { rcsSupply = true, rcsEfficiency = 0.65f, capacityKg = 100.0 };
            var c = (PropellantTankComponentData)t.Clone()!;
            Assert.True(c.rcsSupply);
            Assert.Equal(0.65f, c.rcsEfficiency, 6);
            Assert.Equal(100.0, c.capacityKg, 6);
        }
    }
}
