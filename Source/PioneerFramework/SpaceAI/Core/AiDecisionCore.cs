using System;
using System.Collections.Generic;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Scores observable contacts only. Unknown contacts require investigation or observed threat.</summary>
    public static class AiDecisionCore
    {
        internal static double Score(in AiShipState ship, in AiContact contact, double now)
        {
            double range = (contact.PositionAt(now) - ship.Position).magnitude;
            return (contact.Threatening ? 4 : 0) + (contact.Hostile ? 2 : 0) +
                1 / (1 + range / Math.Max(1,ship.WeaponRange)) - Math.Min(1,contact.Sigma / Math.Max(1,range));
        }
        public static AiDecision Decide(in AiShipState ship, IReadOnlyList<AiContact> contacts, double now)
        {
            int best = -1;
            double score = double.NegativeInfinity;
            for (int i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                if (!c.Valid) continue;
                double s = Score(ship,c,now);
                if (s > score || (s == score && best >= 0 && c.TrackId < contacts[best].TrackId))
                { best = i; score = s; }
            }
            bool emergency = ship.Health < .3 || ship.DeltaV < ship.ReserveDeltaV ||
                (!ship.HasWeapons && (ship.Role != AiRole.ElectronicWarfare || !ship.HasJammer)) ||
                (ship.HasWeapons && ship.AmmoFraction <= 0);
            if (emergency)
                return new(AiAction.Retreat, best < 0 ? 0 : contacts[best].TrackId, false, false,
                    ship.HasJammer && best >= 0, best >= 0);
            if (best < 0) return new(AiAction.Search, 0, false, ship.HasRadar, false, false);
            var target = contacts[best];
            if (!(target.Hostile || target.Threatening))
                return new(AiAction.Investigate, target.TrackId, false, ship.HasRadar, false, false);
            double distance = (target.PositionAt(now) - ship.Position).magnitude;
            bool inRange = ship.HasWeapons && distance <= ship.WeaponRange;
            bool precise = target.Sigma <= Math.Max(target.Radius * 4, ship.WeaponRange * .02);
            return new(inRange ? AiAction.Engage : AiAction.Approach, target.TrackId, inRange && precise,
                ship.HasRadar && (!precise || ship.Role == AiRole.ElectronicWarfare),
                ship.HasJammer && (inRange || ship.Role == AiRole.ElectronicWarfare), target.Threatening);
        }
    }
}
