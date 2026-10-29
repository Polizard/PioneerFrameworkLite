namespace PioneerFramework.Celestial
{
    public abstract class CelestialVisualComp(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialVisualCapability
    {
        public CelestialVisualDescription GetVisualDescription()
        { var properties = (VisualResolvedProperties)Context.Properties; return new(Descriptor.Key, properties.Kind, properties.Profile); }
    }
    public sealed class CompCelestialVisual(CelestialCompDescriptor descriptor) : CelestialVisualComp(descriptor) { }
    public sealed class CompProperties_CelestialVisual : CelestialVisualCompProperties
    {
        protected override CelestialVisualKind Kind => CelestialVisualKind.Surface;
        public override CelestialBodyComp CreateComp() => new CompCelestialVisual(Descriptor);
    }
}
