namespace WindowInvert.Core.Geometry;

public static class OverlayGeometry
{
    // Reserves space for a source window's native minimize/maximize/close
    // caption buttons so the floating toggle button never overlaps them.
    private const int CaptionButtonsReservedWidth = 140;

    public static WindowRect ComputeOverlayRect(WindowRect source) => source;

    /// <summary>
    /// Where the floating toggle button goes for a window occupying
    /// <paramref name="source"/>: flush to the top edge, and inset from the right by
    /// enough to clear the native caption buttons.
    /// <para>
    /// Clamped to the source window's own horizontal extent. Without the clamp, any
    /// window narrower than the reserved width plus the button - about 160 px - put
    /// the button at a negative offset from its own left edge, so the toggle for a
    /// narrow window floated somewhere to the left of it, over whatever unrelated
    /// window happened to be there. Clicking it would then invert a window the
    /// button was not sitting on.
    /// </para>
    /// <para>
    /// For a window narrower than the button itself the clamp collapses to the left
    /// edge and the button overhangs the right. There is nowhere better for it to
    /// go, and it is still attached to the window it belongs to.
    /// </para>
    /// </summary>
    public static WindowRect ComputeTitleBarButtonRect(WindowRect source, int buttonSize)
    {
        var preferred = source.X + source.Width - CaptionButtonsReservedWidth - buttonSize;
        var rightmost = source.X + Math.Max(0, source.Width - buttonSize);
        var x = Math.Clamp(preferred, source.X, rightmost);
        var y = source.Y;
        return new WindowRect(x, y, buttonSize, buttonSize);
    }
}
