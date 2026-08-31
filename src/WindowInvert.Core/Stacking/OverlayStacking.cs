namespace WindowInvert.Core.Stacking;

/// <summary>
/// One window move in the z-order: put <paramref name="Hwnd"/> directly below
/// <paramref name="PlaceBelow"/>.
/// </summary>
/// <param name="Hwnd">The window to move.</param>
/// <param name="PlaceBelow">
/// The window that must end up directly above <paramref name="Hwnd"/>, or
/// <see cref="OverlayStacking.Top"/> for "nothing above it".
/// <para>
/// This is what Win32's <c>SetWindowPos</c> calls <c>hWndInsertAfter</c>, and the
/// name is worth being blunt about because that one is easy to read backwards:
/// the documented meaning is "the window to <i>precede</i> the positioned window
/// in the z-order", so the positioned window ends up <b>below</b> it, not above.
/// Reading it the other way round puts the invert overlay underneath the window
/// it is inverting, where the source covers it completely and the feature looks
/// simply broken.
/// </para>
/// </param>
public readonly record struct StackPlacement(nint Hwnd, nint PlaceBelow);

/// <summary>
/// Works out how to restack a source window's overlay and toggle button back
/// directly on top of it.
/// <para>
/// The overlay used to live in the topmost band, which meant a rectangle of live
/// inverted content floated over every unrelated window on the screen - and since
/// the overlay is click-through, the user could be reading one window while typing
/// into another. Two overlapping inverted windows made the pair unusable. The
/// overlay instead sits immediately above its own source and nowhere else, so it is
/// occluded exactly when its source is.
/// </para>
/// <para>
/// Neither the overlay nor the button can be <i>owned</i> by the source - window
/// ownership does not cross a process boundary - so Windows will not keep them
/// above it. They have to be re-asserted whenever the source is raised.
/// </para>
/// </summary>
public static class OverlayStacking
{
    /// <summary>
    /// Win32's <c>HWND_TOP</c>: place at the top of the z-order rather than below a
    /// particular window. Used when the source window is already the topmost window
    /// there is, so there is nothing to hang the stack under.
    /// </summary>
    public const nint Top = 0;

    /// <summary>
    /// The moves that leave <c>button</c> directly above <c>overlay</c>, and
    /// <c>overlay</c> directly above the source window, in that order.
    /// </summary>
    /// <param name="anchor">
    /// The window currently directly above the source - Win32's
    /// <c>GetWindow(source, GW_HWNDPREV)</c> - or <see cref="Top"/> if the source is
    /// already at the top of the z-order.
    /// </param>
    /// <param name="overlay">The invert overlay's handle, or 0 if the source is not inverted.</param>
    /// <param name="button">The floating toggle button's handle, or 0 if it has none.</param>
    /// <remarks>
    /// <para>
    /// The stack is built <i>downwards</i> from the anchor rather than upwards from
    /// the source, because every move is expressed as "put this below that". Sliding
    /// the button in under the anchor and then the overlay in under the button
    /// leaves the source directly under the overlay without ever having to name the
    /// source in a move - which matters, because naming it would mean asking Windows
    /// to place a window above another one, and that is the direction
    /// <c>SetWindowPos</c> does not offer.
    /// </para>
    /// <para>
    /// The anchor is frequently one of these two windows already - the usual steady
    /// state is that they are exactly where they belong. A window may not be
    /// inserted below itself, so a move that would say so is dropped; the remaining
    /// moves still converge on the same order. Worked through for an anchor that is
    /// the overlay, starting from [overlay, source]: the button goes below the
    /// overlay giving [overlay, button, source], then the overlay goes below the
    /// button giving [button, overlay, source].
    /// </para>
    /// </remarks>
    public static IReadOnlyList<StackPlacement> PlanRestack(nint anchor, nint overlay, nint button)
    {
        var moves = new List<StackPlacement>(2);
        var above = anchor;

        foreach (var hwnd in new[] { button, overlay })
        {
            if (hwnd == 0)
            {
                continue;
            }

            if (hwnd != above)
            {
                moves.Add(new StackPlacement(hwnd, above));
            }

            above = hwnd;
        }

        return moves;
    }
}
