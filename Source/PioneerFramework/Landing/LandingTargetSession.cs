using RimWorld.Planet;
namespace PioneerFramework.Landing
{
    public sealed class LandingTargetSession
    {
        public int BodyId { get; private set; }
        public PlanetTile Tile { get; private set; } = PlanetTile.Invalid;
        public bool HasSelection { get; private set; }
        public int Token { get; private set; }
        private bool active = true;
        public LandingTargetSession(int bodyId) { BodyId = bodyId; }
        public void SelectBody(int bodyId) { BodyId = bodyId; HasSelection = false; Tile = PlanetTile.Invalid; Token++; }
        public void SelectTile(PlanetTile tile) { Tile = tile; HasSelection = tile.Valid; }
        public bool Accepts(int token) => active && token == Token;
        public void Close() { active = false; Token++; }
    }
}
