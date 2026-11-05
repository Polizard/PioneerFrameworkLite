using System;
using UnityEngine;

namespace PioneerFramework.ShipSkin
{
    /// <summary>
    /// 2026-10-20：蒙皮的舰体局部坐标。零件合批把顶点变到视图系后以单位矩阵提交，
    /// 因而不能从 DrawMesh 的模型矩阵恢复舰体原点或姿态。每个实体显式传入这组参数；
    /// 地图浮雕也用同一口径。只改变材质数据，保留原网格、矩阵与合批顺序。
    /// </summary>
    internal static class ShipSkinFrame
    {
        internal static void Build(Vector3 viewOrigin, Quaternion rotation, float metersToView, float scale,
            out Vector4 originAndInverseScale, out Vector4 inverseRotation)
        {
            float conversion = float.IsFinite(metersToView) && metersToView > 0f ? metersToView : 0.1f;
            float enlargement = float.IsFinite(scale) && scale > 0f ? scale : 1f;
            float viewScale = conversion * enlargement;
            float inverseScale = float.IsFinite(viewScale) && viewScale >= 1e-6f ? 1f / viewScale : 10f;
            originAndInverseScale = new Vector4(viewOrigin.x, viewOrigin.y, viewOrigin.z, inverseScale);

            double lengthSquared = (double)rotation.x * rotation.x + (double)rotation.y * rotation.y
                + (double)rotation.z * rotation.z + (double)rotation.w * rotation.w;
            if (!double.IsFinite(lengthSquared) || lengthSquared < 1e-12)
            {
                inverseRotation = new Vector4(0f, 0f, 0f, 1f);
                return;
            }

            float inverseLength = (float)(1.0 / Math.Sqrt(lengthSquared));
            inverseRotation = new Vector4(-rotation.x * inverseLength, -rotation.y * inverseLength,
                -rotation.z * inverseLength, rotation.w * inverseLength);
        }

        internal static void Apply(MaterialPropertyBlock block, Vector3 origin, Quaternion rotation,
            float metersToView, float scale)
        {
            Build(origin, rotation, metersToView, scale, out Vector4 originAndScale, out Vector4 inverseRotation);
            block.SetVector("_SkinOrigin", originAndScale);
            block.SetVector("_SkinRotation", inverseRotation);
        }
    }
}
