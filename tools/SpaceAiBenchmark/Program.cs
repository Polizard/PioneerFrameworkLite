using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using PioneerFramework;
using PioneerFramework.SpaceAI;

var reports = new List<object>();
double checksum = 0;
foreach (int shipCount in new[] {3,6,12})
foreach (int contactCount in new[] {8,56,200})
{
    var contacts = new List<AiContact>();
    for (int i=0;i<contactCount;i++) contacts.Add(new(i+1,new DVector3(15000+i*200,0,i*100),
        new DVector3(-20,0,0),0,20,10,true,true,i%4==0,false,i%7==0));
    var ships = new AiShipState[shipCount];
    for(int i=0;i<shipCount;i++) ships[i]=new(i+1,new DVector3(0,0,i*200),DVector3.zero,100000,3,1500,100,1,1,(AiRole)(i%3),true,true,true);
    var shortlist = new List<AiContact>(4); var fleet = new AiFleetCoordinator();
    var members = new AiFleetMember[shipCount];
    for(int i=0;i<shipCount;i++) members[i]=new(i+1,(AiRole)(i%3),false,true);
    void Work()
    {
        fleet.Clear();
        for(int i=0;i<Math.Min(shipCount,contactCount);i++) fleet.SetInFlight(i+1,2);
        for(int i=0;i<ships.Length;i++)
        {
            AiCandidateShortlist.Fill(ships[i],contacts,0,shortlist);
            var decision=AiDecisionCore.Decide(ships[i],shortlist,0);
            int key=fleet.SelectTarget(ships[i],shortlist,0);fleet.Reserve(key,1,10);
            fleet.MissileWindowOpen(0);fleet.WithdrawalOrder(ships[i].GroupId,members,0);
            if(shortlist.Count>0) checksum+=AiPlanningCore.Plan(ships[i],shortlist[0],decision.Action,0).magnitude;
        }
    }
    for(int i=0;i<1000;i++) Work();
    var elapsed=new double[2000];long before=GC.GetAllocatedBytesForCurrentThread();
    for(int i=0;i<elapsed.Length;i++){long start=Stopwatch.GetTimestamp();Work();elapsed[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;}
    long allocation=GC.GetAllocatedBytesForCurrentThread()-before;
    Array.Sort(elapsed);double sum=0;foreach(double item in elapsed)sum+=item;
    reports.Add(new{shipCount,contactCount,meanMs=sum/elapsed.Length,p95Ms=elapsed[1899],p99Ms=elapsed[1979],maxMs=elapsed[^1],allocatedBytes=allocation});
}
long orbitStart=Stopwatch.GetTimestamp();
for(int i=0;i<1000;i++)
{
    var targetPosition=new DVector3(6100000,0,100000);
    var targetVelocity=new DVector3(-100000,0,6100000).normalized*Math.Sqrt(3.986e14/targetPosition.magnitude);
    var job=new AiOrbitPlanningJob(new DVector3(6000000,0,0),new DVector3(0,0,Math.Sqrt(3.986e14/6000000)),targetPosition,targetVelocity,3.986e14,5500000,2000);
    while(!job.Completed)job.Step();checksum+=job.DeltaV.magnitude;
}
Console.WriteLine(JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),logicalCores=Environment.ProcessorCount,
    workload="Pure decision cycles; excludes host snapshots, maps, firing, collision, rendering. Eight-candidate orbital jobs reported separately.",
    reports,orbitalJobMeanMs=Stopwatch.GetElapsedTime(orbitStart).TotalMilliseconds/1000,checksum},new JsonSerializerOptions{WriteIndented=true}));
