using System.Xml;
using Verse;

namespace PioneerFramework.Combat
{
    /// <summary>
    /// Preserve the configured name even if its Def is missing. Verse's nullable cross-reference alone
    /// cannot distinguish an absent opt-in from an unresolved explicit reference at fire time.
    /// </summary>
    public sealed class ShotDefReference
    {
        public string defName = "";
        private ShotDef? direct;
        public ShotDef? Resolve() => direct ?? (string.IsNullOrWhiteSpace(defName) ? null : DefDatabase<ShotDef>.GetNamedSilentFail(defName));
        public void LoadDataFromXmlCustom(XmlNode node) => defName = node.InnerText.Trim();
        public static implicit operator ShotDefReference?(ShotDef? definition) => definition == null ? null :
            new ShotDefReference { direct = definition, defName = definition.defName };
    }
}
