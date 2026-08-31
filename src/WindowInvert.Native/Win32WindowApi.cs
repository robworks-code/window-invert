using System.Text;
using WindowInvert.Core.Geometry;
using WindowInvert.Core.WindowTracking;
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public sealed class Win32WindowApi : IWin32WindowApi
{
    public WindowRect GetRect(nint hwnd)
    {
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
