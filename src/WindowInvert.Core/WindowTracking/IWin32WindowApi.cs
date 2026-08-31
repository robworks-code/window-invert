using WindowInvert.Core.Geometry;

namespace WindowInvert.Core.WindowTracking;

public interface IWin32WindowApi
{
    WindowRect GetRect(nint hwnd);
    bool IsMinimized(nint hwnd);
    bool IsVisible(nint hwnd);
    string GetTitle(nint hwnd);
    uint GetProcessId(nint hwnd);
}
