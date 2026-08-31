namespace WindowInvert.Core.Geometry;

public static class OverlayGeometry
{
    /// <summary>
    /// The DPI both measurements below are expressed at. 96 is Windows' unscaled
    /// baseline - a display at 100%.
    /// </summary>
    public const int BaselineDpi = 96;

    /// <summary>
    /// Width reserved for a source window's native minimize/maximize/close caption
    /// buttons, at <see cref="BaselineDpi"/>, so the floating toggle button never
    /// overlaps them.
    /// </summary>
    private const int CaptionButtonsReservedWidthAtBaseline = 140;

    /// <summary>
    /// Edge length of the floating toggle button, at <see cref="BaselineDpi"/>.
    /// </summary>
    public const int ButtonSizeAtBaseline = 20;

    public static WindowRect ComputeOverlayRect(WindowRect source) => source;

    /// <summary>
    /// Scales a baseline-DPI measurement to <paramref name="dpi"/>, rounding to the
    /// nearest pixel. A non-positive DPI is treated as the baseline, so a failed
    /// DPI query degrades to today's behaviour rather than collapsing the button to
    /// nothing.
    /// </summary>
    public static int ScaleForDpi(int valueAtBaselineDpi, int dpi) =>
        dpi <= 0 || dpi == BaselineDpi
            ? valueAtBaselineDpi
            : (int)Math.Round(valueAtBaselineDpi * (dpi / (double)BaselineDpi), MidpointRounding.AwayFromZero);

    /// <summary>The toggle button's edge length at <paramref name="dpi"/>.</summary>
    public static int ComputeButtonSize(int dpi) => ScaleForDpi(ButtonSizeAtBaseline, dpi);

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
    /// <para>
    /// Both the button and the reserved width scale with <paramref name="dpi"/>.
    /// They have to: <paramref name="source"/> is in physical screen pixels, and the
    /// caption buttons being avoided are drawn at the display's scale, so at 150%
    /// they occupy about 210 physical pixels rather than 140. Reserving the
    /// unscaled figure would put this button directly on top of them - where a
    /// misclick closes the window the user was reading. The DPI is passed in
    /// because this assembly has no access to Win32; the caller supplies it.
    /// </para>
    /// </summary>
    /// <param name="dpi">
    /// The display scale to measure at - the source window's monitor's effective
    /// DPI, where <see cref="BaselineDpi"/> means 100%.
    /// </param>
    public static WindowRect ComputeTitleBarButtonRect(WindowRect source, int dpi)
    {
        var buttonSize = ComputeButtonSize(dpi);
        var reserved = ScaleForDpi(CaptionButtonsReservedWidthAtBaseline, dpi);

        var preferred = source.X + source.Width - reserved - buttonSize;
        var rightmost = source.X + Math.Max(0, source.Width - buttonSize);
        var x = Math.Clamp(preferred, source.X, rightmost);
        var y = source.Y;
        return new WindowRect(x, y, buttonSize, buttonSize);
    }
}
