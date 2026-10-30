using UnityEngine;

namespace PioneerFramework
{
    /// <summary>带完整 UV 和双面绕序的标记四边形；不包含天体取景或摆位规则。</summary>
    internal static class PFViewQuadGeometry
    {
        public static void Create(out Vector3[] vertices, out Vector2[] uvs, out int[] triangles)
        {
            vertices = [new Vector3(-.5f, -.5f, 0f), new Vector3(.5f, -.5f, 0f),
                new Vector3(-.5f, .5f, 0f), new Vector3(.5f, .5f, 0f)];
            uvs = [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f)];
            triangles = [0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3];
        }
    }
}
