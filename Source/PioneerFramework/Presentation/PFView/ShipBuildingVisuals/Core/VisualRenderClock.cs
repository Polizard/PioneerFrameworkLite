namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>Keeps the fractional render sample when the host clock drops it on pause.</summary>
internal sealed class VisualRenderClock
{
    private double tick;
    private bool initialized;

    public double Sample(double renderTick, bool paused)
    {
        if (double.IsFinite(renderTick) && (!initialized || (!paused && renderTick >= tick)))
        {
            tick = renderTick;
            initialized = true;
        }
        return tick;
    }

    public void Reset()
    {
        tick = 0;
        initialized = false;
    }
}
