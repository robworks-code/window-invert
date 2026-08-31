namespace WindowInvert.Core.WindowTracking;

public enum WinEventType
{
    Show,
    Hide,
    Destroy,
    LocationChange,
    ForegroundChange,
    MinimizeStart,
    MinimizeEnd,
    NameChange,

    /// <summary>
    /// The window still exists and is still <c>WS_VISIBLE</c>, but DWM has stopped
    /// compositing it - a suspended store app, or a window on a virtual desktop the
    /// user has switched away from.
    /// </summary>
    Cloaked,

    /// <summary>
    /// The counterpart to <see cref="Cloaked"/>, and the <i>only</i> notification
    /// that a window on another virtual desktop has come back: switching desktops
    /// never re-raises a show notification, because <c>WS_VISIBLE</c> was set the
    /// whole time. Handled exactly like <see cref="Show"/> for that reason.
    /// </summary>
    Uncloaked,
}
