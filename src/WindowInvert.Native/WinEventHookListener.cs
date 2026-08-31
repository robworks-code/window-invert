using WindowInvert.Core.WindowTracking;
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public sealed class WinEventHookListener
{
    // Kept as a field so the delegate is not garbage-collected while the
    // native hook still holds a reference to it.
    private readonly NativeMethods.WinEventProc _callback;

    /// <summary>EVENT_SYSTEM_FOREGROUND (0x0003) .. EVENT_SYSTEM_MINIMIZEEND (0x0017).</summary>
    private nint _systemHook;

    /// <summary>EVENT_OBJECT_DESTROY (0x8001) .. EVENT_OBJECT_NAMECHANGE (0x800C).</summary>
    private nint _objectHook;

    public event Action<WinEventType, nint>? WindowEvent;

    public WinEventHookListener() => _callback = OnWinEvent;

    /// <summary>
    /// Installs two narrow hooks, one per contiguous run of events this type
    /// actually handles.
    /// <para>
    /// A single EVENT_MIN..EVENT_MAX hook would register for every accessibility
    /// event raised anywhere on the desktop and discard all but eight of them in
    /// managed code. Value changes, state changes, scroll notifications and
    /// per-keystroke text-selection changes from every running application would
    /// then cross the process boundary and wake this app's message loop. Delivery
    /// is out-of-context, so no other application is slowed - the whole cost lands
    /// on this app's UI thread, which is the same thread that repositions every
    /// overlay and toggle button, and the symptom is the overlay visibly lagging
    /// its window during a drag.
    /// </para>
    /// <para>
    /// The two ranges are contiguous runs, so the name-change event that fills in a
    /// late window title costs no extra hook - it is the top of the object range.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Already started, or the operating system refused a hook.
    /// </exception>
    public void Start()
    {
        // With two handles to leak rather than one, a second Start() would silently
        // orphan both of the first pair with no way left to unhook them.
        if (_systemHook != 0 || _objectHook != 0)
        {
            throw new InvalidOperationException(
                $"{nameof(WinEventHookListener)} is already started. Call {nameof(Stop)} first.");
        }

        _systemHook = Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
        if (_systemHook == 0)
        {
            throw new InvalidOperationException("SetWinEventHook failed for the system event range.");
        }

        _objectHook = Hook(NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_NAMECHANGE);
        if (_objectHook == 0)
        {
            // Half-installed is not a state this type may be left in: Stop() would
            // have nothing recorded to undo if the caller gave up here.
            NativeMethods.UnhookWinEvent(_systemHook);
            _systemHook = 0;
            throw new InvalidOperationException("SetWinEventHook failed for the object event range.");
        }
    }

    /// <summary>
    /// WINEVENT_SKIPOWNPROCESS is load-bearing, not a tidiness flag. It is what
    /// keeps this app's own overlays and toggle buttons out of its own window
    /// registry, and it is also what stops the z-order work feeding itself: every
    /// restack raises a location-change event for a window this app owns, which
    /// would otherwise come straight back in and be handled as if a tracked window
    /// had moved.
    /// </summary>
    private nint Hook(uint eventMin, uint eventMax) => NativeMethods.SetWinEventHook(
        eventMin,
        eventMax,
        hmodWinEventProc: 0,
        _callback,
        idProcess: 0,
        idThread: 0,
        NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

    public void Stop()
    {
        if (_systemHook != 0)
        {
            NativeMethods.UnhookWinEvent(_systemHook);
            _systemHook = 0;
        }

        if (_objectHook != 0)
        {
            NativeMethods.UnhookWinEvent(_objectHook);
            _objectHook = 0;
        }
    }

    private void OnWinEvent(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // OBJID_WINDOW / CHILDID_SELF means the event is about the window object
        // itself rather than some accessible element inside it. That is all it
        // means: tooltips, menu popups, combobox drop lists, owned dialogs and
        // child windows all raise events with this object id too. Deciding what is
        // a real top-level window is the tracking predicate's job, not this
        // filter's - see Win32WindowApi.IsTopLevelWindow.
        if (idObject != NativeMethods.OBJID_WINDOW
            || idChild != NativeMethods.CHILDID_SELF
            || hwnd == 0)
        {
            return;
        }

        var mapped = eventType switch
        {
            NativeMethods.EVENT_OBJECT_SHOW => WinEventType.Show,
            NativeMethods.EVENT_OBJECT_HIDE => WinEventType.Hide,
            NativeMethods.EVENT_OBJECT_DESTROY => WinEventType.Destroy,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE => WinEventType.LocationChange,
            NativeMethods.EVENT_OBJECT_NAMECHANGE => WinEventType.NameChange,
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
