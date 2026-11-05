namespace PioneerFramework.Celestial
{
    public sealed class CompCloudsVisual(CelestialCompDescriptor descriptor) : CelestialVisualComp(descriptor) { }
    public sealed class CompProperties_CloudsVisual : CelestialVisualCompProperties
    {
        protected override CelestialVisualKind Kind => CelestialVisualKind.Clouds;
        public override CelestialBodyComp CreateComp() => new CompCloudsVisual(Descriptor);
    }
}
