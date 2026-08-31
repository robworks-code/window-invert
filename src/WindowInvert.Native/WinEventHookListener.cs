using WindowInvert.Core.WindowTracking;
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public sealed class WinEventHookListener
{
    // Kept as a field so the delegate is not garbage-collected while the
    // native hook still holds a reference to it.
    private readonly NativeMethods.WinEventProc _callback;
    private nint _hook;

    public event Action<WinEventType, nint>? WindowEvent;

    public WinEventHookListener() => _callback = OnWinEvent;

    public void Start()
    {
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_MIN,
            NativeMethods.EVENT_MAX,
            hmodWinEventProc: 0,
            _callback,
            idProcess: 0,
            idThread: 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    public void Stop()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = 0;
        }
    }

    private void OnWinEvent(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // idObject != 0 (OBJID_WINDOW) events are for child UI elements,
        // not the top-level window itself - ignore them.
        if (idObject != 0 || hwnd == 0)
        {
            return;
        }

        var mapped = eventType switch
        {
            NativeMethods.EVENT_OBJECT_SHOW => WinEventType.Show,
            NativeMethods.EVENT_OBJECT_HIDE => WinEventType.Hide,
            NativeMethods.EVENT_OBJECT_DESTROY => WinEventType.Destroy,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE => WinEventType.LocationChange,
            NativeMethods.EVENT_SYSTEM_FOREGROUND => WinEventType.ForegroundChange,
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART => WinEventType.MinimizeStart,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND => WinEventType.MinimizeEnd,
            _ => (WinEventType?)null,
        };

        if (mapped is { } type)
        {
            WindowEvent?.Invoke(type, hwnd);
        }
    }
}
