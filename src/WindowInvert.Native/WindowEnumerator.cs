using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public static class WindowEnumerator
{
    public static IEnumerable<nint> EnumTopLevelWindows()
    {
        var result = new List<nint>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0)
            {
                return true;
            }

            if (NativeMethods.GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }

            result.Add(hwnd);
            return true;
        }, 0);

        return result;
    }
}
