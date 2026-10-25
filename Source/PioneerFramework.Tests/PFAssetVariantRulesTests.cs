using Xunit;

namespace PioneerFramework.Tests
{
    public class PFAssetVariantRulesTests
    {
        [Fact]
        public void ApplyPrefix_PreservesDirectoryAndExtension()
        {
            Assert.Equal(
                @"Models\X15\MIN_PFL_X15_Hypersonic.obj",
                PFAssetVariantRules.ApplyPrefix(
                    @"Models\X15\PFL_X15_Hypersonic.obj", "MIN_"));
        }

        [Fact]
        public void ApplyPrefix_WorksForContentFinderTexturePath()
        {
            Assert.Equal(
                "PFView/Ships/X15/MIN_X15Albedo",
                PFAssetVariantRules.ApplyPrefix(
                    "PFView/Ships/X15/X15Albedo", "MIN_"));
        }

        [Fact]
        public void ApplyPrefix_SupportsCustomPrefix()
        {
            Assert.Equal(
                "PFView/Ships/Soyuz/LITE_SoyuzNormal",
                PFAssetVariantRules.ApplyPrefix(
                    "PFView/Ships/Soyuz/SoyuzNormal", "LITE_"));
        }

        [Fact]
        public void ApplyPrefix_WithoutDirectory_PrefixesLeaf()
        {
            Assert.Equal(
                "MIN_PFL_CrewDragon.obj",
                PFAssetVariantRules.ApplyPrefix("PFL_CrewDragon.obj", "MIN_"));
        }

        [Fact]
        public void NormalizePrefix_EmptyOrWhitespaceFallsBackToDefault()
        {
            Assert.Equal(PFAssetVariantRules.DefaultPrefix, PFAssetVariantRules.NormalizePrefix(null));
            Assert.Equal(PFAssetVariantRules.DefaultPrefix, PFAssetVariantRules.NormalizePrefix("   "));
            Assert.Equal(PFAssetVariantRules.DefaultPrefix, PFAssetVariantRules.NormalizePrefix("///"));
        }

        [Fact]
        public void NormalizePrefix_RemovesPathSeparators()
        {
            Assert.Equal("MINab", PFAssetVariantRules.NormalizePrefix("MIN/a\\b"));
        }

        [Fact]
        public void NormalizePrefix_TrimsAndCapsLength()
        {
            string normalized = PFAssetVariantRules.NormalizePrefix(
                "  " + new string('X', PFAssetVariantRules.MaxPrefixLength + 8) + "  ");
            Assert.Equal(PFAssetVariantRules.MaxPrefixLength, normalized.Length);
        }
    }
}
