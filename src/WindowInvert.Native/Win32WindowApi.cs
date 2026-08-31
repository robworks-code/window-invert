using System.Runtime.InteropServices;
using System.Text;
using WindowInvert.Core.Geometry;
using WindowInvert.Core.WindowTracking;
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public sealed class Win32WindowApi : IWin32WindowApi
{
    /// <summary>
    /// The window's <i>visible</i> bounds.
    /// <para>
    /// Deliberately DWM's extended frame bounds rather than
    /// <c>GetWindowRect</c>. Since Windows 10 a window's <c>GetWindowRect</c>
    /// includes an invisible resize border - typically 0 px at the top and about
    /// 7 px on the sides and bottom - which nothing ever draws into. That matters
    /// here because it is the rect the invert overlay is sized and positioned
    /// from, while <c>Windows.Graphics.Capture</c> hands back only the composed,
    /// visible pixels. Using <c>GetWindowRect</c> therefore made the overlay a
    /// few pixels larger than its own content on three sides, which the renderer
    /// then had to letterbox and resample every frame - permanent softening of
    /// text, plus a vertical offset because the border is not symmetric. The two
    /// agree once both are the visible frame, so the renderer takes its
    /// pixel-for-pixel path instead.
    /// </para>
    /// <para>
    /// Both APIs return physical screen pixels, so there is no unit mismatch.
    /// The DWM call fails for a window that has no composed frame yet; any
    /// non-<c>S_OK</c> falls back to <c>GetWindowRect</c>, which is always at
    /// least approximately right.
    /// </para>
    /// </summary>
    public WindowRect GetRect(nint hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && frame.Right > frame.Left
            && frame.Bottom > frame.Top)
        {
            return new WindowRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        }

        NativeMethods.GetWindowRect(hwnd, out var rect);
        return new WindowRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public bool IsMinimized(nint hwnd) => NativeMethods.IsIconic(hwnd);

    public bool IsVisible(nint hwnd) => NativeMethods.IsWindowVisible(hwnd);

    public bool IsTopLevel(nint hwnd) => IsTopLevelWindow(hwnd);

    /// <summary>
    /// The single definition of "a window this app will put an overlay on":
    /// its own <c>GA_ROOT</c> ancestor, and no owner.
    /// <para>
    /// <c>GA_ROOT</c> rejects child windows - a child's root is its top-level
    /// parent, never itself. <c>GW_OWNER</c> rejects tooltips, menu popups,
    /// combobox drop lists and dialogs, which are top-level by
    /// <c>GA_ROOT</c> but are owned by the window they belong to.
    /// </para>
    /// <para>
    /// Deliberately shared by <see cref="WindowEnumerator"/> (the startup path) and
    /// by <c>WindowRegistry</c> through <see cref="IsTopLevel"/> (the live path).
    /// The two used to disagree - the startup path filtered properly while the live
    /// path accepted anything visible - so hovering anything with a tooltip added a
    /// tracked "window", a floating toggle button and a blank tray-menu entry, then
    /// removed them again a moment later.
    /// </para>
    /// <para>
    /// Visibility is checked separately by each caller, and a title is deliberately
    /// <b>not</b> required here: many applications raise their show notification
    /// before calling <c>SetWindowText</c>, so requiring a title at this point would
    /// permanently miss real windows. The title is a display concern, evaluated
    /// live where the tray menu and the toggle button are built.
    /// </para>
    /// </summary>
    internal static bool IsTopLevelWindow(nint hwnd) =>
        hwnd != 0
        && NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) == hwnd
        && NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == 0;

    public string GetTitle(nint hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public uint GetProcessId(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }
}
