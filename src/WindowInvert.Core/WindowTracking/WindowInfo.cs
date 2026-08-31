using WindowInvert.Core.Geometry;

namespace WindowInvert.Core.WindowTracking;

/// <param name="IsHidden">
/// Whether the window has been hidden or cloaked rather than destroyed.
/// <para>
/// Distinct from <paramref name="IsMinimized"/> because the two arrive by entirely
/// different routes - minimize through the system event range, hide and cloak
/// through the object range - but they mean the same thing to every consumer, which
/// is what <see cref="WindowInfo.IsOnScreen"/> is for.
/// </para>
/// </param>
public readonly record struct WindowInfo(
    nint Hwnd,
    string Title,
    uint ProcessId,
    bool IsMinimized,
    WindowRect Rect,
    bool IsHidden = false)
{
    /// <summary>
    /// Whether the window is currently showing any pixels the user could look at.
    /// <para>
    /// The single question every surface-owning consumer actually has. A window that
    /// is minimized, hidden to a notification area, or cloaked onto another virtual
    /// desktop is equally unlookable, and an overlay or toggle button placed over
    /// where it used to be would be floating over whatever is really there now.
    /// </para>
    /// </summary>
    public bool IsOnScreen => !IsMinimized && !IsHidden;
}
