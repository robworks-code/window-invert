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
