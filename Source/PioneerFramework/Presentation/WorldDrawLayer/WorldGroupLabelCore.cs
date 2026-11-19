using System;
using System.Collections.Generic;
using UnityEngine;

namespace PioneerFramework;

internal sealed class WorldGroupLabelPins
{
    private readonly List<int> ids = new();
    internal IReadOnlyList<int> Ids => ids;

    internal void Toggle(int id)
    {
        if (!ids.Remove(id)) ids.Add(id);
    }

    internal void Clear() => ids.Clear();
}

internal static class WorldGroupLabelCore
{
    internal readonly record struct Metrics(double Height, double Speed);
    internal readonly record struct Hit(int Id, Vector2 Icon, Rect Label);
    internal sealed record Observation(int BodyId, DVector3 Position, string Title, string Height, string Speed,
        bool Stale, bool Identified);

    internal static Observation PreserveKnown(Observation? previous, Observation current) =>
        !current.Identified && previous is { Identified: true } && previous.BodyId == current.BodyId
            ? previous with { Stale = true } : current;

    internal static int Pick(Vector2 point, IReadOnlyList<Hit> hits)
    {
        float nearest = 20 * 20;
        int id = -1;
        foreach (var hit in hits)
        {
            float distance = (point - hit.Icon).sqrMagnitude;
            if (distance >= nearest) continue;
            nearest = distance;
            id = hit.Id;
        }
        if (id >= 0) return id;
        foreach (var hit in hits)
            if (hit.Label.Contains(point)) return hit.Id;
        return -1;
    }

    internal static Metrics Measure(DVector3 position, DVector3 velocity, DVector3 bodyPosition,
        DVector3 bodyVelocity, double radius) =>
        new((position - bodyPosition).magnitude - radius, (velocity - bodyVelocity).magnitude);

    internal static bool TryPlace(Vector2 icon, Vector2 size, Rect viewport, IReadOnlyList<Rect> occupied, out Rect label)
    {
        label = default;
        if (size.x > viewport.width || size.y > viewport.height) return false;
        for (int row = 0; row < 9; row++)
        {
            int offset = row == 0 ? 0 : (row + 1) / 2 * (row % 2 == 1 ? -1 : 1);
            for (int side = 0; side < 2; side++)
            {
                float x = side == 0 ? icon.x + 20 : icon.x - size.x - 20;
                float y = icon.y - size.y / 2 + offset * (size.y + 8);
                var candidate = new Rect(Math.Clamp(x, viewport.xMin, viewport.xMax - size.x),
                    Math.Clamp(y, viewport.yMin, viewport.yMax - size.y), size.x, size.y);
                bool free = true;
                for (int i = 0; i < occupied.Count; i++)
                    if (candidate.Overlaps(occupied[i])) { free = false; break; }
                if (!free) continue;
                label = candidate;
                return true;
            }
        }
        return false;
    }
}
