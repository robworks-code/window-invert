using WindowInvert.Core.Geometry;

namespace WindowInvert.Core.WindowTracking;

public interface IWin32WindowApi
{
    WindowRect GetRect(nint hwnd);
    bool IsMinimized(nint hwnd);
    bool IsVisible(nint hwnd);

    /// <summary>
    /// Whether <paramref name="hwnd"/> is a genuine top-level application window -
    /// its own z-order root, and unowned.
    /// <para>
    /// This is the seam that keeps the Win32 predicate out of
    /// <c>WindowInvert.Core</c>. It exists because visibility alone is nowhere near
    /// enough to decide what deserves an overlay: tooltips, menu popups, combobox
    /// drop lists, owned dialogs and child windows shown via <c>ShowWindow</c> all
    /// raise the same show notification as a real window, and every one of them
    /// would otherwise be tracked.
    /// </para>
    /// </summary>
    bool IsTopLevel(nint hwnd);

    string GetTitle(nint hwnd);
    uint GetProcessId(nint hwnd);
}
