namespace PioneerFramework.Celestial
{
    public sealed class CompStellarVisual(CelestialCompDescriptor descriptor) : CelestialVisualComp(descriptor) { }
    public sealed class CompProperties_StellarVisual : CelestialVisualCompProperties
    {
        protected override CelestialVisualKind Kind => CelestialVisualKind.Stellar;
        public override CelestialBodyComp CreateComp() => new CompStellarVisual(Descriptor);
    }
}
