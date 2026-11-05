using RimWorld.Planet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public class WorldDrawLayer_MoonUngeneratedPlanetParts : WorldDrawLayer
    {
        public override IEnumerable Regenerate()
        {
            foreach (object obj in base.Regenerate())
            {
                yield return obj;
            }
            Vector3 surfaceViewCenter = Find.WorldGrid.SurfaceViewCenter;
            float surfaceViewAngle = Find.WorldGrid.SurfaceViewAngle;
            if (surfaceViewAngle < 180f)
            {
                List<Vector3> collection;
                List<int> collection2;
                SphereGenerator.Generate(4, this.planetLayer.Radius + -0.16f, -surfaceViewCenter, 180f - Mathf.Min(surfaceViewAngle, 180f) + 10f, out collection, out collection2);
                LayerSubMesh subMesh = base.GetSubMesh(PFMaterials.GetMoonPlanetUngeneratedParts());
                subMesh.verts.AddRange(collection);
                subMesh.tris.AddRange(collection2);
            }
            base.FinalizeMesh(MeshParts.All);
            yield break;
        }
        private const int SubdivisionsCount = 4;

        private const float ViewAngleOffset = 10f;
    }
}
