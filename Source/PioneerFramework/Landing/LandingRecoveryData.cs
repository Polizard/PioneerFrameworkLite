using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework.Landing
{
    public enum LandingHandoffState { Available, HandingOff, Delivered }
    public sealed class LandingEntityPayload : IExposable
    {
        public string EntryId = "";
        public int EntityId;
        public SpaceEntity? Entity;
        public double DryMass, FuelMass;
        public double Mass => Math.Max(0, DryMass) + Math.Max(0, FuelMass);
        public void ExposeData()
        {
            Scribe_Values.Look(ref EntryId, "entryId"); Scribe_Values.Look(ref EntityId, "entityId");
            Scribe_Values.Look(ref DryMass, "dryMass"); Scribe_Values.Look(ref FuelMass, "fuelMass"); Scribe_Deep.Look(ref Entity, "entity");
        }
    }
    public sealed class LandingRecoveryEntry : IExposable
    {
        public string EntryId = "";
        public int EntityId;
        public bool TransferredFromSpace;
        public LandingEntityPayload? Payload;
        public LandingHandoffState State;
        public int ReceiverCaravanId = -1;
        public bool HandOff(Func<string, bool> receiverHas, Action<LandingEntityPayload> deliver)
        {
            if (State == LandingHandoffState.Delivered) return true;
            if (receiverHas(EntryId)) { State = LandingHandoffState.Delivered; Payload = null; return true; }
            if (Payload == null) return false;
            State = LandingHandoffState.HandingOff;
            deliver(Payload);
            if (!receiverHas(EntryId)) return false;
            State = LandingHandoffState.Delivered; Payload = null; return true;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref EntryId, "entryId"); Scribe_Values.Look(ref EntityId, "entityId");
            Scribe_Values.Look(ref State, "state"); Scribe_Values.Look(ref TransferredFromSpace, "transferred");
            Scribe_Values.Look(ref ReceiverCaravanId, "receiverCaravan", -1);
            if (TransferredFromSpace) Scribe_Deep.Look(ref Payload, "payload");
        }
    }
    public sealed class LandingRecoveryDef : Def
    {
        public List<string> partDefNames = new List<string>();
        public string texturePath = "UI/Icons/SatelliteIcon";
        public ThingDef? packagedThingDef;
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var error in base.ConfigErrors()) yield return error;
            if (packagedThingDef == null || packagedThingDef.thingClass == null || !typeof(RecoveredSpacecraft).IsAssignableFrom(packagedThingDef.thingClass) || packagedThingDef.stackLimit != 1)
                yield return "packagedThingDef must use RecoveredSpacecraft with stackLimit 1.";
        }
    }
    public sealed class LandingRecoveryReport
    {
        public List<string> TransferredEntryIds = new List<string>();
        public int RemainingCount;
        public LandingReport Result = LandingReport.Ok();
    }
}
