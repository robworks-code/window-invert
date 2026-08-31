namespace WindowInvert.Core.Geometry;

public static class OverlayGeometry
{
    // Reserves space for a source window's native minimize/maximize/close
    // caption buttons so the floating toggle button never overlaps them.
    private const int CaptionButtonsReservedWidth = 140;

    public static WindowRect ComputeOverlayRect(WindowRect source) => source;

    public static WindowRect ComputeTitleBarButtonRect(WindowRect source, int buttonSize)
    {
        var x = source.X + source.Width - CaptionButtonsReservedWidth - buttonSize;
        var y = source.Y;
        return new WindowRect(x, y, buttonSize, buttonSize);
    }
}
