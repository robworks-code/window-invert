using WindowInvert.Core.Stacking;
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

/// <summary>
/// The Win32 half of overlay z-order: reading where a source window currently sits,
/// and applying the moves <see cref="OverlayStacking"/> planned.
/// </summary>
public static class WindowStacking
{
    /// <summary>
    /// The window directly above <paramref name="hwnd"/>, or
    /// <see cref="OverlayStacking.Top"/> if nothing is above it.
    /// <para>
    /// This is the anchor the overlay and toggle button are hung under. Reading the
    /// window above the source, rather than trying to place something above the
    /// source directly, is what makes the whole operation expressible: Win32 only
    /// offers "put this below that".
    /// </para>
    /// </summary>
    public static nint GetWindowAbove(nint hwnd) =>
        hwnd == 0 ? OverlayStacking.Top : NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV);

    /// <summary>
    /// Moves <paramref name="hwnd"/> so that <paramref name="placeBelow"/> is
    /// directly above it, changing nothing else about the window.
    /// <para>
    /// <c>SWP_NOZORDER</c> is deliberately absent - the z-order change is the entire
    /// point of the call - and <c>SWP_NOACTIVATE</c> is deliberately present,
    /// because none of these windows may ever steal focus from the window the user
    /// is actually working in.
    /// </para>
    /// <para>
    /// This call can move a window between z-order bands as a side effect, and that
    /// is deliberately not relied on. Measured on Windows 11 26200: positioning a
    /// non-topmost window relative to a topmost one grants it <c>WS_EX_TOPMOST</c>,
    /// and positioning a topmost window relative to a non-topmost one takes it away
    /// again. The documentation only promises the first of those, and only for a
    /// window placed above <i>every</i> existing topmost window, so the observed
    /// behaviour is wider than the contract. Band membership is therefore stated
    /// explicitly by <see cref="MatchBand"/> before the anchor is read, rather than
    /// being left to fall out of the ordering.
    /// </para>
    /// </summary>
    public static bool InsertBelow(nint hwnd, nint placeBelow)
    {
        if (hwnd == 0 || hwnd == placeBelow)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            hwnd,
            placeBelow,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Whether <paramref name="hwnd"/> is in the topmost z-order band.
    /// </summary>
    public static bool IsTopmost(nint hwnd) =>
        hwnd != 0
        && (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Puts <paramref name="hwnd"/> in the topmost band, or takes it out, to match
    /// <paramref name="topmost"/>. Returns whether a change was made.
    /// <para>
    /// The two bands are absolute - Windows keeps every topmost window above every
    /// non-topmost one - so a source that is pinned, which is exactly the kind of
    /// window a "make this readable" feature gets pointed at (a picture-in-picture
    /// video, a pinned Task Manager, an always-on-top notes panel), can only be
    /// covered by an overlay that is in the band with it.
    /// </para>
    /// <para>
    /// <b>Measured, and worth being precise about:</b> the ordering pass alone
    /// already achieves this on Windows 11 26200, because positioning a window
    /// relative to a window in the other band moves it into that band implicitly -
    /// both upwards when the reference is topmost and downwards when it is not.
    /// Every configuration tried came out correct without this method. What the
    /// documentation actually promises is narrower than that: only that a window
    /// placed above <i>every</i> existing topmost window becomes topmost. So this
    /// method is not repairing an observed failure; it is removing a dependency on
    /// undocumented behaviour, and making the band a stated intention that a reader
    /// can see rather than a side effect they have to know about.
    /// </para>
    /// <para>
    /// Deliberately a no-op when the band is already right, which is the steady
    /// state - so on the common path this costs one style read and no
    /// <c>SetWindowPos</c> at all. Both <c>HWND_TOPMOST</c> and
    /// <c>HWND_NOTOPMOST</c> also move the window to the top of the band they name,
    /// so calling either unconditionally on every foreground change would reorder
    /// the window and then need the ordering pass to undo it.
    /// </para>
    /// </summary>
    public static bool MatchBand(nint hwnd, bool topmost)
    {
        if (hwnd == 0 || IsTopmost(hwnd) == topmost)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            hwnd,
            topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Moves and resizes <paramref name="hwnd"/> without touching the z-order.
    /// Restacking is a separate, explicitly-ordered pass.
    /// </summary>
    public static bool MoveTo(nint hwnd, int x, int y, int width, int height) =>
        NativeMethods.SetWindowPos(
            hwnd,
            hWndInsertAfter: 0,
            x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
}
