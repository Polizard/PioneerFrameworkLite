using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private static Mesh? starfieldMesh;
        private static Material? starfieldMat;
        private static Color starfieldBaseColor = Color.white;
        private static int starfieldSeed = int.MinValue;

        /// <summary>复用世界星表；天球只平移到相机位置，姿态保持惯性系朝向。</summary>
        private void DrawStarfield()
        {
            if (renderCamera == null) return;
            var world = Find.World;
            if (world == null) return;

            if (starfieldSeed != world.info.Seed)
            {
                starfieldMesh = WorldStarfield.GetOrBuildMesh(world.info.Seed);
                starfieldSeed = world.info.Seed;
            }
            if (starfieldMesh == null) return;

            if (starfieldMat == null)
            {
                var shared = WorldStarfield.GetStarMaterial();
                if (shared == null) return;
                starfieldMat = new Material(shared) { renderQueue = PFViewLayers.Starfield };
                starfieldBaseColor = starfieldMat.color;
            }

            float brightness = Mathf.Clamp01(1f - skyAlpha);
            starfieldMat.color = new Color(starfieldBaseColor.r * brightness, starfieldBaseColor.g * brightness,
                starfieldBaseColor.b * brightness, 1f);
            float scale = Mathf.Max(1f, renderCamera.nearClipPlane * 2f / WorldStarfield.ShellRadius);
            Graphics.DrawMesh(starfieldMesh, Matrix4x4.TRS(renderCamera.transform.position, viewFrameRot,
                Vector3.one * scale), starfieldMat, SceneLayer);
        }
    }
}
