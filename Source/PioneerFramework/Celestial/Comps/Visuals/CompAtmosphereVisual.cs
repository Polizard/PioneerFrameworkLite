namespace PioneerFramework.Celestial
{
    public sealed class CompAtmosphereVisual(CelestialCompDescriptor descriptor) : CelestialVisualComp(descriptor) { }
    public sealed class CompProperties_AtmosphereVisual : CelestialVisualCompProperties
    {
        protected override CelestialVisualKind Kind => CelestialVisualKind.Atmosphere;
        public override CelestialBodyComp CreateComp() => new CompAtmosphereVisual(Descriptor);
    }
}
