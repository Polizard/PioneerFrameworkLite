using System;
using System.Collections.Generic;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

public readonly record struct VisualSurfaceBox(float MinX, float MinZ, float MaxX, float MaxZ, float HalfHeight);

public static class VisualSurface
{
    public static bool TryHeight(IReadOnlyList<VisualSurfaceBox> boxes, float minX, float minZ, float maxX, float maxZ,
        bool bottom, out float height)
    {
        height = 0;
        bool found = false;
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (box.MaxX <= minX || box.MinX >= maxX || box.MaxZ <= minZ || box.MinZ >= maxZ) continue;
            if (!float.IsFinite(box.HalfHeight) || box.HalfHeight <= 0) continue;
            found = true;
            height = MathF.Max(height, box.HalfHeight);
        }
        if (bottom) height = -height;
        return found;
    }
}
