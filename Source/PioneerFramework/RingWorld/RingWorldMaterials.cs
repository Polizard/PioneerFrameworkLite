using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    internal static class RingWorldMaterials
    {
        private static Texture2D? hullTexture;
        private static Material? habitat;
        private static readonly Material?[,] structure = new Material?[4,4];
        private static readonly DVector3 LightDirection = new DVector3(.3,.75,-.55).normalized;

        internal static Material Habitat => habitat ??= MaterialPool.MatFrom(
            hullTexture ??= Load("World/RingWorld/IndustrialHull"),
            ShaderDatabase.WorldOverlayTransparent,Color.white,3500);

        private static Texture2D Load(string path)
        {
            Texture2D texture = ContentFinder<Texture2D>.Get(path);
            if (texture == null) return BaseContent.WhiteTex;
            // Mirror wrapping makes image-edge continuity independent of generated bitmap edge accuracy.
            texture.wrapMode = TextureWrapMode.Mirror;
            return texture;
        }

        internal static Material Structure(RingVisualQuad quad)
        {
            DVector3 normal = DVector3.Cross(quad.B.Position-quad.A.Position,quad.C.Position-quad.A.Position).normalized;
            int shade = quad.Material == RingVisualMaterial.WarningLight ? 3 :
                System.Math.Clamp((int)((DVector3.Dot(normal,LightDirection)+1)*2),0,3);
            int kind = (int)quad.Material;
            if (structure[kind,shade] is Material existing) return existing;
            float brightness = .62f+shade*.12f;
            Color color = quad.Material switch
            {
                RingVisualMaterial.Hull => new Color(.9f,.94f,1f)*brightness,
                RingVisualMaterial.Frame => new Color(.53f,.58f,.64f)*brightness,
                RingVisualMaterial.Pipe => new Color(.38f,.42f,.46f)*brightness,
                _ => new Color(1f,.32f,.045f)
            };
            color.a = 1;
            Texture2D texture = kind <= (int)RingVisualMaterial.Frame ?
                hullTexture ??= Load("World/RingWorld/IndustrialHull") : BaseContent.WhiteTex;
            return structure[kind,shade] = MaterialPool.MatFrom(texture,
                ShaderDatabase.WorldOverlayTransparent,color,3500);
        }
    }
}
