namespace WindowInvert.Core.WindowTracking;

public sealed class WindowRegistry
{
    private readonly IWin32WindowApi _api;
    private readonly Dictionary<nint, WindowInfo> _tracked = new();

    public WindowRegistry(IWin32WindowApi api) => _api = api;

    public IReadOnlyDictionary<nint, WindowInfo> TrackedWindows => _tracked;

    public event Action<WindowInfo>? WindowTracked;
    public event Action<nint>? WindowUntracked;
    public event Action<WindowInfo>? WindowGeometryChanged;
    public event Action<WindowInfo>? WindowVisibilityChanged;

    /// <summary>
    /// Raised when a tracked window's title actually changed - never for a
    /// name-change notification that reports the same text.
    /// <para>
    /// A window's title is deliberately not part of the tracking predicate (see
    /// <see cref="TrackIfNew"/>), so this is how a window that was untitled when it
    /// appeared becomes displayable once it names itself.
    /// </para>
    /// </summary>
    public event Action<WindowInfo>? WindowTitleChanged;

    /// <summary>
    /// Raised with the window that just became foreground, tracked or not.
    /// <para>
    /// Consumed for z-order: activating a window raises it above the unowned
    /// overlay and toggle button that belong to it, which have to be restacked
    /// straight back on top of it. Windows that are not tracked are still reported,
    /// because the consumer - not this type - knows which handles it has surfaces
    /// for.
    /// </para>
    /// </summary>
    public event Action<nint>? WindowForegroundChanged;

    public void Bootstrap(IEnumerable<WindowInfo> initialWindows)
    {
        foreach (var info in initialWindows)
        {
            _tracked[info.Hwnd] = info;
        }
    }

    public void HandleWinEvent(WinEventType type, nint hwnd)
    {
        switch (type)
        {
            case WinEventType.Show:
                TrackIfNew(hwnd);
                break;

            case WinEventType.Destroy:
            case WinEventType.Hide:
                if (_tracked.Remove(hwnd))
                {
                    WindowUntracked?.Invoke(hwnd);
                }
                break;

            case WinEventType.LocationChange:
                UpdateIfTracked(hwnd, (info, api) => info with { Rect = api.GetRect(hwnd) },
                    WindowGeometryChanged);
                break;

            case WinEventType.MinimizeStart:
                UpdateIfTracked(hwnd, (info, _) => info with { IsMinimized = true },
                    WindowVisibilityChanged);
                break;

            case WinEventType.MinimizeEnd:
                UpdateIfTracked(hwnd, (info, _) => info with { IsMinimized = false },
                    WindowVisibilityChanged);
                break;

            case WinEventType.NameChange:
                RefreshTitle(hwnd);
                break;

            case WinEventType.ForegroundChange:
                WindowForegroundChanged?.Invoke(hwnd);
                break;
        }
    }

    /// <summary>
    /// Tracks <paramref name="hwnd"/> if it is a real, visible, top-level window.
    /// <para>
    /// Both conditions matter. The show notification this runs from fires for
    /// tooltips, menu popups, combobox drop lists, owned dialogs and child windows
    /// as readily as for an application window, so visibility alone would track
    /// every transient piece of UI on the desktop - each one briefly acquiring a
    /// floating toggle button and a tray-menu entry, then losing them again.
    /// </para>
    /// <para>
    /// A title is deliberately not required: applications commonly show a window
    /// before naming it, so requiring one here would permanently miss real windows.
    /// A window with no title is tracked and reported through
    /// <see cref="WindowTitleChanged"/> when it acquires one.
    /// </para>
    /// </summary>
    private void TrackIfNew(nint hwnd)
    {
        if (_tracked.ContainsKey(hwnd))
        {
            return;
        }

        if (!_api.IsVisible(hwnd) || !_api.IsTopLevel(hwnd))
        {
            return;
        }

        var info = new WindowInfo(
            hwnd,
            _api.GetTitle(hwnd),
            _api.GetProcessId(hwnd),
            _api.IsMinimized(hwnd),
            _api.GetRect(hwnd));

        _tracked[hwnd] = info;
        WindowTracked?.Invoke(info);
    }

    /// <summary>
    /// Re-reads a tracked window's title, raising <see cref="WindowTitleChanged"/>
    /// only when it really changed. Name-change notifications are noisy - a browser
    /// tab switch or a media player's elapsed-time caption produces a stream of them
    /// - and the consumer rebuilds its menu from this, so an unchanged title must
    /// not cost anything.
    /// </summary>
    private void RefreshTitle(nint hwnd)
    {
        if (!_tracked.TryGetValue(hwnd, out var existing))
        {
            return;
        }

        var title = _api.GetTitle(hwnd);
        if (string.Equals(title, existing.Title, StringComparison.Ordinal))
        {
            return;
        }

        var updated = existing with { Title = title };
        _tracked[hwnd] = updated;
        WindowTitleChanged?.Invoke(updated);
    }

    private void UpdateIfTracked(
        nint hwnd,
        Func<WindowInfo, IWin32WindowApi, WindowInfo> update,
        Action<WindowInfo>? raise)
    {
        if (!_tracked.TryGetValue(hwnd, out var existing))
        {
            return;
        }

        var updated = update(existing, _api);
        _tracked[hwnd] = updated;
        raise?.Invoke(updated);
    }
}
