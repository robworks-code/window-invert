using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public static class WindowEnumerator
{
    /// <summary>
    /// Every window that currently satisfies the same predicate the live tracking
    /// path applies - visible, and top-level by
    /// <see cref="Win32WindowApi.IsTopLevelWindow"/>.
    /// <para>
    /// This used to additionally require a non-empty title. It no longer does, so
    /// that the startup and live paths track exactly the same set. A window that is
    /// running but untitled at startup would otherwise never be tracked, and the
    /// name-change notification that fills in a late title only refreshes windows
    /// that are already tracked - so it could never recover. The title now gates
    /// only what is displayed.
    /// </para>
    /// </summary>
    public static IEnumerable<nint> EnumTopLevelWindows()
    {
        var result = new List<nint>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && Win32WindowApi.IsTopLevelWindow(hwnd))
            {
                result.Add(hwnd);
            }

            return true;
        }, 0);

        return result;
    }
}
