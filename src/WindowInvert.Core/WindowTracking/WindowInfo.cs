using WindowInvert.Core.Geometry;

namespace WindowInvert.Core.WindowTracking;

public readonly record struct WindowInfo(
    nint Hwnd,
    string Title,
    uint ProcessId,
    bool IsMinimized,
    WindowRect Rect);
