using WindowInvert.Core.Geometry;
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

/// <summary>
/// Reads the display scale that another process's window is actually drawn at.
/// </summary>
public static class DisplayScaling
{
    /// <summary>
    /// The effective DPI of the monitor <paramref name="hwnd"/> is on, where
    /// <see cref="OverlayGeometry.BaselineDpi"/> (96) means 100%.
    /// <para>
    /// Deliberately the <b>monitor's</b> effective DPI rather than
    /// <c>GetDpiForWindow</c>, which is the obvious call and the wrong one here.
    /// <c>GetDpiForWindow</c> reports the DPI a window is rendered at <i>according
    /// to its own DPI awareness</i>: 96 for a DPI-unaware application, the system
    /// DPI for a system-aware one. But the caption buttons this measurement exists
    /// to avoid are composed into the frame that
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> reports, in physical screen pixels, and
    /// Windows stretches a less-aware window's whole frame up to the display's
    /// scale. So on a 150% monitor a legacy application's caption buttons really do
    /// occupy about 210 physical pixels while <c>GetDpiForWindow</c> would answer
    /// 96 - and the toggle button would be placed on top of them. The monitor's
    /// effective DPI is the correct multiplier for every awareness level.
    /// </para>
    /// <para>
    /// <c>GetDpiForMonitor</c> returns an HRESULT, so anything other than
    /// <c>S_OK</c> falls back to <c>GetDpiForWindow</c> - still better than
    /// assuming 96, which would under-reserve on exactly the scaled displays this
    /// is for - and then to the baseline.
    /// </para>
    /// </summary>
    public static int GetEffectiveDpi(nint hwnd)
    {
        if (hwnd == 0)
        {
            return OverlayGeometry.BaselineDpi;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != 0
            && NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            return (int)dpiX;
        }

        var windowDpi = NativeMethods.GetDpiForWindow(hwnd);
        return windowDpi > 0 ? (int)windowDpi : OverlayGeometry.BaselineDpi;
    }
}
