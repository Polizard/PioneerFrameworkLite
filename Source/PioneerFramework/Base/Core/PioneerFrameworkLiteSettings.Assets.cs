using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class PioneerFrameworkLiteSettings
    {
        /// <summary>
        /// PFView minimal-asset mode. Off preserves the original/base asset
        /// path; on resolves only the configured prefix and never falls back to
        /// a base high-detail model.
        /// </summary>
        internal bool pfviewMinimalMode;

        /// <summary>
        /// Prefix inserted before the final file-name segment. The default is
        /// ASCII so it is safe in logs, file names and UI text fields.
        /// </summary>
        internal string pfviewMinimalAssetPrefix = PFAssetVariantRules.DefaultPrefix;

        private void ExposeAssetSettings()
        {
            Scribe_Values.Look(ref pfviewMinimalMode, "pfviewMinimalMode", false);
            Scribe_Values.Look(
                ref pfviewMinimalAssetPrefix,
                "pfviewMinimalAssetPrefix",
                PFAssetVariantRules.DefaultPrefix);
            NormalizeAssetSettings();
        }

        internal void NormalizeAssetSettings()
        {
            pfviewMinimalAssetPrefix = PFAssetVariantRules.NormalizePrefix(pfviewMinimalAssetPrefix);
        }
    }

    public partial class PioneerFrameworkLiteMod
    {
        private static void DrawAssetSettings(Listing_Standard listing)
        {
            var settings = Settings!;
            listing.Gap(12f);
            listing.Label("PFV_Asset_MinimalSection".Translate());
            listing.CheckboxLabeled("PFV_Asset_MinimalMode".Translate(), ref settings.pfviewMinimalMode);
            listing.Label("PFV_Asset_MinimalPrefix".Translate());
            settings.pfviewMinimalAssetPrefix = Widgets.TextField(
                listing.GetRect(28f), settings.pfviewMinimalAssetPrefix ?? string.Empty);
            listing.Label("PFV_Asset_MinimalHint".Translate());
        }
    }
}
