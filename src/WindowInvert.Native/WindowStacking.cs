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
    /// Nothing here asks for the topmost band. A window placed below a non-topmost
    /// window loses topmost status, which is what keeps the overlay from floating
    /// over unrelated windows; and if the source window is itself topmost, the
    /// overlay inherits that position by being stacked relative to it rather than
    /// by being given the style outright.
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
