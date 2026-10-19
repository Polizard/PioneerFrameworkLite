using System.Collections.Generic;
using System.Reflection;
using PioneerFramework.SpaceAI;
using Xunit;

namespace PioneerFramework.Tests
{
    public class SpaceAiTargetTests
    {
        [Fact]
        public void WeaponCooldown_CatchesUpHiddenMapWithoutDoubleTick()
        {
            var weapon=new PioneerFramework.Combat.WeaponInstance {def=new PioneerFramework.Combat.WeaponDef(),cooldownRemaining=20};
            weapon.Update(100);Assert.Equal(19,weapon.cooldownRemaining);
            weapon.Update(100);Assert.Equal(19,weapon.cooldownRemaining);
            weapon.Update(110);Assert.Equal(9,weapon.cooldownRemaining);
        }
        [Theory] [InlineData(1,8,1)] [InlineData(3,8,3)] [InlineData(0,8,0)] [InlineData(-1,8,8)]
        public void MissileVolley_CannotExceedFiniteMagazine(int ammo,int requested,int expected)
        {
            var weapon=new PioneerFramework.Combat.WeaponInstance {ammo=ammo};
            Assert.Equal(expected,weapon.AvailableVolleyCount(requested,8));
        }
        [Fact]
        public void RealAndUnrecognizedFalseContact_HaveSameDecisionProjection()
        {
            var real=new Contact { targetGroupId=7,decoyId=-1,position=new DVector3(100,0,0), apparentRadius=10 };
            var fake=real;fake.targetGroupId=-1;fake.decoyId=9;fake.source=ContactSource.Decoy;
            Assert.Equal(ContactTargetService.Project(real,1,false,true),ContactTargetService.Project(fake,1,false,true));
        }

        [Fact]
        public void TrackRegistry_PreservesKeyAndCannotResolveAnotherFaction()
        {
            var registry=new AiTrackRegistry();
            var c=new Contact {targetGroupId=8,decoyId=-1};
            int key=registry.Remember(4,c);
            Assert.Equal(key,registry.Remember(4,c));
            Assert.False(registry.TryGet(5,key,out _));
            Assert.True(registry.TryGet(4,key,out var target));Assert.Equal(8,target.GroupId);
        }

        [Fact]
        public void FalseContactLookup_UsesObserverPictureAndLifetime()
        {
            var sensor=new SensorManager(null);
            var c=new Contact {targetGroupId=-1,decoyId=3,position=new DVector3(1,2,3)};
            var pictures=(Dictionary<int,SensorManager.FactionPicture>)typeof(SensorManager).GetField("pictures",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(sensor);
            var decoys=(Dictionary<int,SensorManager.DecoyRecord>)typeof(SensorManager).GetField("decoys",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(sensor);
            decoys[3]=new SensorManager.DecoyRecord {contact=c,expirePhysics=10,ownerFactionId=2};
            pictures[1]=new SensorManager.FactionPicture();
            pictures[1].tracks.Add(new SensorManager.ContactRecord {contact=c});
            Assert.True(sensor.TryGetDecoyContactAt(1,3,9,out var found));Assert.Equal(c.position,found.position);
            Assert.False(sensor.TryGetDecoyContactAt(2,3,9,out _));
            Assert.False(sensor.TryGetDecoyContactAt(1,3,10,out _));
        }
        [Fact]
        public void TrackRegistry_ExpiresOldIdentitiesWithoutReusingLiveOrRetiredKeys()
        {
            var registry = new AiTrackRegistry();
            var old = new Contact { targetGroupId = 8, decoyId = -1 };
            var live = new Contact { targetGroupId = 9, decoyId = -1 };
            int retired = registry.Remember(4,old,0), active = registry.Remember(4,live,100);
            registry.Prune(121,120);
            Assert.False(registry.TryGet(4,retired,out _));
            Assert.True(registry.TryGet(4,active,out _));
            Assert.Equal(active,registry.Remember(4,live,121));
            Assert.NotEqual(retired,registry.Remember(4,old,121));
        }

        [Fact]
        public void ContactOnlyMissile_PreservesNoEntityTargetIdentity()
        {
            var leader=new AutonomousVehicleManager.VehicleRecord {targetGroupId=-1};
            leader.targetDecoyId=7;leader.contactOnlyAim=true;
            var copy=ContactTargetService.CopyTarget(leader);
            Assert.Equal(7,copy.DecoyId);Assert.Equal(-1,copy.GroupId);
        }
    }
}
