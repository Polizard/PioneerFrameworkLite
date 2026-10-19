using System;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Finite device magazine and simulation-time cooldown shared by player and AI.</summary>
    public sealed class DecoyBudget
    {
        public int Charges;
        public double NextEmission;
        public DecoyBudget(int charges = 0) { Charges = Math.Max(0, charges); }
        public bool Ready(double now) => Charges > 0 && double.IsFinite(now) && now >= NextEmission;
        public bool TrySpend(double now, double cooldown)
        {
            if (!Ready(now) || !double.IsFinite(cooldown) || cooldown < 0) return false;
            Charges--; NextEmission = now + cooldown; return true;
        }
    }
}
