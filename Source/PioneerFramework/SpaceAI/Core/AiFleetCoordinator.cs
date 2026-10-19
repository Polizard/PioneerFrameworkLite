using System;
using System.Collections.Generic;

namespace PioneerFramework.SpaceAI
{
    public readonly record struct AiFleetMember(int GroupId, AiRole Role, bool Retreating, bool CanCover,
        double RetreatStarted = -1);
    public readonly record struct AiWithdrawalOrder(bool Withdraw, bool Cover);
    /// <summary>Observable fire commitments; no hidden target health or damage queries.</summary>
    public sealed class AiFleetCoordinator
    {
        private readonly List<(int key, double amount, double expires)> reservations = new();
        private readonly Dictionary<int,double> inFlight = new();
        private double nextSalvo, salvoUntil = double.NegativeInfinity, withdrawalAt = double.NaN;
        public void Reserve(int key, double amount, double expires)
        { if (key > 0 && amount > 0) reservations.Add((key, amount, expires)); }
        public void Expire(double now)
        { for (int i = reservations.Count - 1; i >= 0; i--) if (reservations[i].expires <= now) reservations.RemoveAt(i); }
        public void Clear()
        {
            reservations.Clear(); inFlight.Clear(); nextSalvo = 0;
            salvoUntil = double.NegativeInfinity; withdrawalAt = double.NaN;
        }
        public void ClearInFlight() => inFlight.Clear();
        public void SetInFlight(int key, double count)
        {
            if (key <= 0 || !(count > 0)) return;
            inFlight.TryGetValue(key,out double previous); inFlight[key] = previous+count;
        }
        public bool MissileWindowOpen(double now)
        {
            if (now >= nextSalvo) { nextSalvo = now+5; salvoUntil = now+2; }
            return now <= salvoUntil;
        }
        public AiWithdrawalOrder WithdrawalOrder(int groupId,IReadOnlyList<AiFleetMember> members,double now)
        {
            int retreating = 0, coverId = 0; AiRole coverRole = AiRole.Gunship;
            double firstRetreat = now; bool selfRetreating = false;
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member.Retreating)
                {
                    retreating++;
                    if (member.RetreatStarted >= 0) firstRetreat = Math.Min(firstRetreat,member.RetreatStarted);
                    if (member.GroupId == groupId) selfRetreating = true;
                    continue;
                }
                if (!member.CanCover) continue;
                bool support = member.Role == AiRole.ElectronicWarfare;
                bool previousSupport = coverRole == AiRole.ElectronicWarfare;
                if (coverId == 0 || support && !previousSupport || support == previousSupport && member.GroupId < coverId)
                { coverId = member.GroupId; coverRole = member.Role; }
            }
            if (retreating == 0) return default;
            if (double.IsNaN(withdrawalAt)) withdrawalAt = firstRetreat;
            else withdrawalAt = Math.Min(withdrawalAt,firstRetreat);
            bool allWithdraw = retreating*2 >= members.Count;
            bool covering = groupId == coverId && now-withdrawalAt < 15 &&
                (allWithdraw || coverRole == AiRole.ElectronicWarfare);
            return new(selfRetreating || allWithdraw && !covering,covering);
        }
        public int SelectTarget(in AiShipState ship, IReadOnlyList<AiContact> contacts, double now)
        {
            int key = 0;
            double best = double.NegativeInfinity;
            for (int i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                if (!c.Valid || !(c.Hostile || c.Threatening)) continue;
                inFlight.TryGetValue(c.TrackId,out double commitment);
                for (int j = 0; j < reservations.Count; j++)
                    if (reservations[j].key == c.TrackId && reservations[j].expires > now) commitment += reservations[j].amount;
                double score = (1 + (c.Threatening ? 2 : 0)) /
                    ((1 + (c.PositionAt(now) - ship.Position).magnitude / Math.Max(1, ship.WeaponRange)) * (1 + commitment));
                if (score > best || (score == best && c.TrackId < key)) { key = c.TrackId; best = score; }
            }
            return key;
        }
    }
}
