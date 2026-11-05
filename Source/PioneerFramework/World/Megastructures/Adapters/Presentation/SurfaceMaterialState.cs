using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    /// <summary>Shared per-frame inputs with weak material ownership. Creators retain asset lifetime authority.</summary>
    public sealed class SurfaceMaterialState
    {
        private readonly List<System.WeakReference<Material>> materials=new();
        private readonly int sunId,cameraId,timeId;
        private int lastFrame=int.MinValue;
        private System.WeakReference<Game>? game;
        public Vector3 SunDirection {get;private set;}=new Vector3(.3f,.75f,-.55f).normalized;
        public Vector3 LightPropagationDirection=>-SunDirection;
        public Vector3 CameraPosition {get;private set;}
        public float Time {get;private set;}
        public SurfaceMaterialState(string sunUniform,string cameraUniform,string timeUniform)
        {sunId=Shader.PropertyToID(sunUniform);cameraId=Shader.PropertyToID(cameraUniform);timeId=Shader.PropertyToID(timeUniform);}
        public void Register(Material material)
        {
            if(material==null)return;
            foreach(var reference in materials)if(reference.TryGetTarget(out var existing)&&existing==material)return;
            materials.Add(new(material));
        }
        public void UpdatePerFrame()
        {
            if(game==null||!game.TryGetTarget(out var previous)||!ReferenceEquals(previous,Current.Game))
            {game=Current.Game==null?null:new(Current.Game);lastFrame=int.MinValue;CameraPosition=Vector3.zero;SunDirection=new Vector3(.3f,.75f,-.55f).normalized;}
            int frame=UnityEngine.Time.frameCount;if(frame==lastFrame)return;lastFrame=frame;
            Time=UnityEngine.Time.realtimeSinceStartup;
            if(Find.World!=null){var sun=GenCelestial.CurSunPositionInWorldSpace();if(sun.sqrMagnitude>1e-8f)SunDirection=sun.normalized;}
            var camera=WorldCameraManager.WorldCamera;if(camera!=null)CameraPosition=camera.transform.position;
            var light=LightPropagationDirection;
            for(int i=materials.Count-1;i>=0;i--)
            {
                if(!materials[i].TryGetTarget(out var material)||material==null){materials.RemoveAt(i);continue;}
                material.SetVector(sunId,new(light.x,light.y,light.z,0));
                material.SetVector(cameraId,new(CameraPosition.x,CameraPosition.y,CameraPosition.z,1));
                material.SetVector(timeId,new(Time,0,0,0));
            }
        }
    }
}
