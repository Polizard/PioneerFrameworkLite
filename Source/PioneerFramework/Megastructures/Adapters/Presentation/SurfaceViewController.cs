using System;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    /// <summary>Shape-specific camera constraints in native display units.</summary>
    public interface ISurfaceViewGeometry
    {
        float Radius { get; }
        float Bound { get; }
        float InnerAltitude { get; }
        DVector3 Project(DVector3 localDirection);
        DVector3 NormalAt(DVector3 localPosition);
        Vector3 PresetDirection { get; }
        float PresetAltitude(float aspect);
    }
    public static class SurfaceViewController
    {
        public static FrameworkResult View(PlanetLayer layer,int cell=-1)
        {
            if(Find.WorldGrid==null||!Find.WorldGrid.PlanetLayers.TryGetValue(layer.LayerID,out var current)||!ReferenceEquals(current,layer)||!ArtificialSurfaceLayers.TryDescribe(layer,out var s))return FrameworkResult.Reject("SurfaceMissing");
            if(cell>=s.Geometry.CellCount||cell< -1)return FrameworkResult.Reject("CellMissing");
            PlanetLayer.Selected=layer;CameraJumper.TryShowWorld();
            float altitude=180;
            if(cell>=0)Find.WorldCameraDriver.JumpTo(new PlanetTile(cell,layer));
            else
            {
                if(s.View==null)return FrameworkResult.Reject("ViewProviderMissing");
                float aspect=Screen.height>0?(float)Screen.width/Screen.height:16f/9f;
                altitude=s.View.PresetAltitude(aspect);Find.WorldCameraDriver.JumpTo(s.View.PresetDirection);
            }
            Find.WorldCameraDriver.altitude=altitude;
            AccessTools.Field(typeof(WorldCameraDriver),"desiredAltitude").SetValue(Find.WorldCameraDriver,altitude);
            return FrameworkResult.Ok();
        }
    }
}
