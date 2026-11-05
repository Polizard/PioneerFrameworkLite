using System;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Pure AI contracts. Target keys are opaque and carry no entity/decoy identity.</summary>
    public enum AiAction { Search, Investigate, Approach, Engage, Retreat, Disabled }
    public enum AiRole { Gunship, Missile, ElectronicWarfare }

    public readonly record struct AiContact(int TrackId, DVector3 Position, DVector3 Velocity,
        double ObservedAt, double Sigma, double Radius, bool Usable, bool Hostile,
        bool Radiating, bool RecognizedDecoy, bool Threatening)
    {
        public DVector3 PositionAt(double now) => Position + Velocity * Math.Max(0, now - ObservedAt);
        public bool Valid => TrackId > 0 && Usable && !RecognizedDecoy &&
            double.IsFinite(Position.x) && double.IsFinite(Position.y) && double.IsFinite(Position.z) &&
            double.IsFinite(Velocity.x) && double.IsFinite(Velocity.y) && double.IsFinite(Velocity.z) &&
            double.IsFinite(Sigma) && Sigma >= 0;
    }

    public readonly record struct AiShipState(int GroupId, DVector3 Position, DVector3 Velocity,
        double WeaponRange, double Acceleration, double DeltaV, double ReserveDeltaV,
        double Health, double AmmoFraction, AiRole Role, bool HasWeapons, bool HasRadar, bool HasJammer);

    public readonly record struct AiDecision(AiAction Action, int TrackId, bool Fire,
        bool ActiveRadar, bool Jamming, bool EmitDecoy);
}
