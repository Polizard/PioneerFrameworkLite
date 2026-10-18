using RimWorld.Planet;
namespace PioneerFramework.Landing
{
    public enum LandingTargetAction { None, PrepareMaps, BeginDescent }

    public sealed class LandingTargetSession
    {
        public int BodyId { get; private set; }
        public PlanetTile Tile { get; private set; } = PlanetTile.Invalid;
        public bool HasSelection { get; private set; }
        public int Token { get; private set; }
        private bool active = true;
        public LandingTargetSession(int bodyId) { BodyId = bodyId; }
        public void SelectBody(int bodyId) { BodyId = bodyId; HasSelection = false; Tile = PlanetTile.Invalid; Token++; }
        public void SelectTile(PlanetTile tile)
        {
            if (Tile == tile) return;
            Tile = tile; HasSelection = tile.Valid; Token++;
        }
        public bool Accepts(int token) => active && token == Token;
        public LandingTargetAction GetAction(bool requiresPreparation, bool validQuote)
        {
            if (!active) return LandingTargetAction.None;
            if (requiresPreparation) return LandingTargetAction.PrepareMaps;
            return HasSelection && validQuote ? LandingTargetAction.BeginDescent : LandingTargetAction.None;
        }
        public void Close() { active = false; Token++; }
    }
}
