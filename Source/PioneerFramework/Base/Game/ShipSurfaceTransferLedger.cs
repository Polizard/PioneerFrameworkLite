using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>Save-persistent success and recovery-failure record for explicit transfer IDs.</summary>
    public sealed class ShipSurfaceTransferLedger : GameComponent
    {
        private List<string> completed = new();
        private List<string> blocked = new();
        private readonly HashSet<string> active = new();

        public ShipSurfaceTransferLedger(Game game) { }

        public bool IsCompleted(string id) => completed.Contains(id);
        public bool IsBlocked(string id) => blocked.Contains(id);

        public bool TryBegin(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || IsCompleted(id) || IsBlocked(id)) return false;
            return active.Add(id);
        }

        public void Complete(string id)
        {
            active.Remove(id);
            if (!completed.Contains(id)) completed.Add(id);
        }

        public void Cancel(string id) => active.Remove(id);

        public void Block(string id)
        {
            active.Remove(id);
            if (!blocked.Contains(id)) blocked.Add(id);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref completed, "pfShipSurfaceTransferCompleted", LookMode.Value);
            Scribe_Collections.Look(ref blocked, "pfShipSurfaceTransferBlocked", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                completed ??= new List<string>();
                blocked ??= new List<string>();
                active.Clear();
            }
        }
    }
}
