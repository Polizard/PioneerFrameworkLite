using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    /// <summary>Optional retention handshake. Acknowledged interval tokens expire; closed logical epochs cannot be resubmitted.</summary>
    public interface ICelestialPhysicsRetention
    {
        void AcknowledgeTerminalToken(PhysicsIntervalToken token);
        void AcknowledgeClosedEpoch(CelestialEpochState state, IReadOnlyList<string> logicalOperationIds);
    }
}
