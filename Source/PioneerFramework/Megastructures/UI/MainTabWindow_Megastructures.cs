using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    public sealed class MainTabWindow_Megastructures:MainTabWindow
    {
        private Vector2 listScroll;private Vector2 detailScroll;
        public override Vector2 RequestedTabSize=>new(1100,720);
        private static MegastructureManager? Manager=>Current.Game?.GetComponent<MegastructureManager>();
        public override void DoWindowContents(Rect inRect)
        {
            var manager=Manager;if(manager==null)return;
            if(Widgets.ButtonText(new Rect(0,0,230,32),"PF_Mega_Create".Translate()))OpenCreateMenu(manager);
            if(Widgets.ButtonText(new Rect(240,0,230,32),"PF_Mega_ImportRings".Translate()))
                foreach(var ring in Find.WorldGrid.PlanetLayers.Values.OfType<RingWorld.RingSurfaceLayer>())Show(manager.ImportRing(ring,Faction.OfPlayer.loadID));
            if(!string.IsNullOrEmpty(manager.LoadError)){Widgets.Label(new Rect(0,40,inRect.width,50),"PF_Mega_LoadBlocked".Translate(manager.LoadError));return;}
            var states=manager.Snapshots.Where(s=>s.Lifecycle!=StructureLifecycle.Retired&&(StructurePermissions.Allows(s,"",Faction.OfPlayer.loadID,StructurePermission.View)||s.Nodes.Any(n=>StructurePermissions.Allows(s,n.Id,Faction.OfPlayer.loadID,StructurePermission.View)))).ToArray();
            var listRect=new Rect(0,42,260,inRect.height-45);var listView=new Rect(0,0,240,Math.Max(listRect.height,states.Length*54));
            Widgets.BeginScrollView(listRect,ref listScroll,listView);
            float y=0;
            foreach(var state in states)
            {
                if(Widgets.ButtonText(new Rect(0,y,230,48),Label(state)+"\n"+("PF_Mega_State_"+state.Lifecycle).Translate()))manager.SelectedInstanceId=state.Id;
                y+=54;
            }
            Widgets.EndScrollView();
            var selected=states.FirstOrDefault(s=>s.Id==manager.SelectedInstanceId)??states.FirstOrDefault();
            if(selected==null){Widgets.Label(new Rect(280,55,700,100),"PF_Mega_Empty".Translate());return;}
            manager.SelectedInstanceId=selected.Id;
            var panel=new Rect(280,42,inRect.width-280,inRect.height-45);var view=new Rect(0,0,panel.width-20,Math.Max(panel.height,220+selected.Nodes.Sum(n=>150+n.Capabilities.Count*30)+selected.Capabilities.Count*30+manager.Resources.Stores.Sum(s=>s.Balances.Count*30)));
            Widgets.BeginScrollView(panel,ref detailScroll,view);DrawDetail(manager,selected,view.width);Widgets.EndScrollView();
        }
        private static string Label(StructureState state)=>DefDatabase<MegastructureDef>.GetNamedSilentFail(state.DefinitionId)?.LabelCap.ToString()??state.Id;
        private static void Show(FrameworkResult result)
        {if(!result.Success)Messages.Message("PF_Mega_Failed".Translate(result.Code),MessageTypeDefOf.RejectInput,false);}
        private static void Command(MegastructureManager manager,StructureState state,StructureCommandKind kind)
        {Show(manager.Execute(new StructureCommand {OperationId=Guid.NewGuid().ToString("N"),InstanceId=state.Id,ActorFactionId=Faction.OfPlayer.loadID,Kind=kind}));}
        private static void DrawDetail(MegastructureManager manager,StructureState state,float width)
        {
            float y=0;Widgets.Label(new Rect(0,y,width,30),Label(state));y+=35;
            Widgets.Label(new Rect(0,y,width,26),"PF_Mega_Summary".Translate(state.Nodes.Count,state.Revision));y+=32;
            if(state.Lifecycle is StructureLifecycle.Created or StructureLifecycle.Suspended)
            {if(Widgets.ButtonText(new Rect(0,y,180,30),"PF_Mega_Activate".Translate()))Command(manager,state,StructureCommandKind.Activate);}
            else if(state.Lifecycle==StructureLifecycle.Active)
            {if(Widgets.ButtonText(new Rect(0,y,180,30),"PF_Mega_Suspend".Translate()))Command(manager,state,StructureCommandKind.Suspend);}
            if(Widgets.ButtonText(new Rect(190,y,180,30),"PF_Mega_Retire".Translate()))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("PF_Mega_RetireConfirm".Translate(),()=>
                {
                    if(state.Lifecycle!=StructureLifecycle.Decommissioning)
                    {var result=manager.Execute(new StructureCommand {OperationId=Guid.NewGuid().ToString("N"),InstanceId=state.Id,ActorFactionId=Faction.OfPlayer.loadID,Kind=StructureCommandKind.BeginRetirement});if(!result.Success){Show(result);return;}}
                    Command(manager,state,StructureCommandKind.FinishRetirement);
                }));
            y+=42;
            foreach(var capability in state.Capabilities){DrawCapability(manager,state,"",capability,ref y,width);}
            foreach(var node in state.Nodes)
            {
                Widgets.DrawLineHorizontal(0,y,width);y+=8;
                Widgets.Label(new Rect(0,y,width,26),"PF_Mega_Node".Translate(node.Id,node.Enabled?"PF_Mega_Enabled".Translate():"PF_Mega_Disabled".Translate()));y+=28;
                if(!string.IsNullOrEmpty(node.BlockedReason)){Widgets.Label(new Rect(0,y,width,26),"PF_Mega_Failed".Translate(node.BlockedReason));y+=28;}
                var ringBinding=node.Bindings.FirstOrDefault(b=>b.Kind=="ring");
                if(ringBinding!=null&&Widgets.ButtonText(new Rect(0,y,170,28),"PF_Mega_View".Translate()))
                {var ring=RingWorld.RingWorldService.FindById(ringBinding.Id);if(ring!=null)RingWorld.RingWorldColonyService.View(ring);}
                if(ringBinding!=null)y+=33;
                foreach(var capability in node.Capabilities)DrawCapability(manager,state,node.Id,capability,ref y,width);
                foreach(var store in manager.Resources.Stores.Where(s=>s.Owner==new NodeAddress(state.Id,node.Id)))foreach(var balance in store.Balances)
                {Widgets.Label(new Rect(0,y,width,26),$"{balance.ResourceId}: {balance.Amount:0.##}/{balance.Capacity:0.##} {balance.Unit}");y+=28;}
                foreach(var store in manager.ThingStores.Where(s=>s.Owner==new NodeAddress(state.Id,node.Id)))
                {Widgets.Label(new Rect(0,y,width,26),"PF_Mega_Items".Translate(store.Count));y+=28;}
                y+=15;
            }
        }
        private static void DrawCapability(MegastructureManager manager,StructureState state,string nodeId,CapabilityState capability,ref float y,float width)
        {
            Widgets.Label(new Rect(0,y,width-180,26),capability.Id+" · "+(capability.Enabled?"PF_Mega_Enabled".Translate():"PF_Mega_Disabled".Translate())+(string.IsNullOrEmpty(capability.BlockedReason)?"":" · "+capability.BlockedReason));
            if(Widgets.ButtonText(new Rect(width-170,y,160,25),capability.Enabled?"PF_Mega_Disable".Translate():"PF_Mega_Activate".Translate()))
                Show(manager.Execute(new StructureCommand {OperationId=Guid.NewGuid().ToString("N"),InstanceId=state.Id,NodeId=nodeId,ActorFactionId=Faction.OfPlayer.loadID,Kind=StructureCommandKind.SetCapabilityEnabled,CapabilityId=capability.Id,Enabled=!capability.Enabled}));
            y+=30;
            if(capability.HandlerId=="pfl.map"&&capability.Enabled&&Widgets.ButtonText(new Rect(0,y,180,27),"PF_Mega_AccessMap".Translate()))
                LongEventHandler.QueueLongEvent(()=>
                {var result=manager.Maps.Access(new(state.Id,nodeId),capability.Id,0,true,Faction.OfPlayer.loadID,out var map);if(result.Success)SpaceMapGenerator.EnterPocketMap(map!);else Show(result);},"GeneratingMap",false,null);
            if(capability.HandlerId=="pfl.map")y+=32;
        }
        private static void OpenCreateMenu(MegastructureManager manager)
        {
            var options=new List<FloatMenuOption>();
            foreach(var def in DefDatabase<MegastructureDef>.AllDefsListForReading.Where(d=>!d.referenceOnly||Prefs.DevMode))
            {
                var captured=def;options.Add(new FloatMenuOption(def.LabelCap,()=>
                {
                    if(captured.nodes.Any(n=>n.spatialMode==SpatialMode.PhysicalGroup))
                    {
                        var groups=Current.Game.GetComponent<SpaceGameComp>().GetAllGroups().Where(g=>g.IsActive&&g.FactionId==Faction.OfPlayer.loadID&&!g.IsSurfaceAnchor).ToArray();
                        if(groups.Length==0){Show(FrameworkResult.Reject("NoOwnedGroup"));return;}
                        Find.WindowStack.Add(new FloatMenu(groups.Select(g=>new FloatMenuOption("PF_Mega_Group".Translate(g.GroupId),()=>Create(manager,captured,g))).ToList()));
                    }
                    else Create(manager,captured,null);
                }));
            }
            if(options.Count>0)Find.WindowStack.Add(new FloatMenu(options));else Messages.Message("PF_Mega_NoDefinitions".Translate(),MessageTypeDefOf.RejectInput,false);
        }
        private static void Create(MegastructureManager manager,MegastructureDef def,SpaceEntityGroup? group)
        {
            var request=new CreateStructureRequest {OperationId=Guid.NewGuid().ToString("N"),InstanceId=def.defName+"_"+Guid.NewGuid().ToString("N"),DefinitionId=def.defName,OwnerFactionId=Faction.OfPlayer.loadID};
            foreach(var node in def.nodes)
            {
                var binding=node.ToTemplate().Spatial;
                if(binding.Mode==SpatialMode.BodyAnchored&&binding.BodyId<0)binding.BodyId=ViewFrame.ViewedBodyId;
                if(binding.Mode==SpatialMode.PhysicalGroup)binding.GroupId=group!.GroupId;
                request.SpatialOverrides[node.key]=binding;
            }
            var result=manager.Create(request);Show(result);if(result.Success)manager.SelectedInstanceId=request.InstanceId;
        }
    }
}
