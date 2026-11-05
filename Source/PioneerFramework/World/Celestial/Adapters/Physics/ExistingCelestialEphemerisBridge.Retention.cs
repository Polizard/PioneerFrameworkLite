using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class ExistingCelestialEphemerisBridge : ICelestialPhysicsRetention
    {
        private CelestialEpochState? receiptEpochs;
        public void AcknowledgeTerminalToken(PhysicsIntervalToken token)
        {
            lock (gate)
            {
                var prepared = GetToken(token);
                if (prepared.Status == 0) throw new InvalidOperationException("IntervalStillPrepared");
                tokens.Remove(token.TokenId);
            }
        }
        public void AcknowledgeClosedEpoch(CelestialEpochState state, IReadOnlyList<string> logicalOperationIds)
        {
            lock (gate)
            {
                if (active != null) throw new InvalidOperationException("IntervalStillPrepared");
                var validated = new CelestialOperationEpochs(); validated.Restore(state);
                foreach (string logical in logicalOperationIds)
                    foreach (string prefix in new[] { "physics/create/", "physics/remove/", "physics/compensate/" })
                    {
                        string id = prefix + Uri.EscapeDataString(logical);
                        if (operations.TryGetValue(id, out var entry) && entry.Receipt.Status == PhysicsOperationStatus.Pending)
                            throw new InvalidOperationException("PhysicsOperationStillPending");
                    }
                receiptEpochs = validated.Capture();
                foreach (string logical in logicalOperationIds)
                    foreach (string prefix in new[] { "physics/create/", "physics/remove/", "physics/compensate/" }) operations.Remove(prefix + Uri.EscapeDataString(logical));
            }
        }
        private bool IsOperationExpired(string id)
        {
            if (receiptEpochs == null) return false;
            string logical = id;
            foreach (string prefix in new[] { "physics/create/", "physics/remove/", "physics/compensate/" })
                if (id.StartsWith(prefix, StringComparison.Ordinal)) { logical = Uri.UnescapeDataString(id[prefix.Length..]); break; }
            return CelestialOperationEpochs.TryGetEpoch(logical, out long epoch) && epoch < receiptEpochs.NextEpoch && !receiptEpochs.OpenEpochs.Contains(epoch);
        }
    }
}
