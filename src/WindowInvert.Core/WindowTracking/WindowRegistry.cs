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

    /// <summary>
    /// Raised when a tracked window's <see cref="WindowInfo.IsOnScreen"/> state
    /// changed - minimized, restored, hidden or cloaked.
    /// </summary>
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
            // Uncloaking is handled as a show because it is the only notification a
            // window returning from another virtual desktop ever raises.
            case WinEventType.Show:
            case WinEventType.Uncloaked:
                HandleShown(hwnd);
                break;

            // Deliberately NOT the same as Destroy. Applications that minimize to
            // the notification area (chat clients, media players, sync tools) hide
            // their main window with ShowWindow(SW_HIDE) and show the same window
            // again later, and cloaking is how every window on a virtual desktop
            // behaves when the user switches away from it. Untracking here would
            // discard the user's invert choice on all of them, and it would come
            // back off with nothing to explain why - which for a user who turned
            // inversion on to be able to read the window at all is the feature
            // silently failing, not a tidy-up.
            case WinEventType.Hide:
            case WinEventType.Cloaked:
                MarkHidden(hwnd);
                break;

            case WinEventType.Destroy:
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
    /// Handles a window becoming visible, whether it is new, coming back from being
    /// hidden or cloaked, or a handle the operating system has since reused.
    /// <para>
    /// Re-reading an already-tracked window matters because window handles are
    /// recycled and event delivery is best-effort. If a destroy notification is
    /// dropped under load - out-of-context delivery makes no guarantees - and
    /// Windows then hands the same numeric handle to an unrelated window, the
    /// registry would otherwise keep serving the dead window's title and rect
    /// forever: a wrong tray-menu label, and a toggle button placed from geometry
    /// that belongs to a window that no longer exists.
    /// </para>
    /// <para>
    /// The process id is what settles it. A handle that has changed process is
    /// definitively a different window, so it is untracked and re-tracked, which
    /// gives consumers the destroy they never received and lets them tear down the
    /// old window's surfaces before building the new one's. A handle in the same
    /// process is the same window reappearing, and is refreshed in place.
    /// </para>
    /// </summary>
    private void HandleShown(nint hwnd)
    {
        if (_tracked.TryGetValue(hwnd, out var existing))
        {
            if (_api.GetProcessId(hwnd) == existing.ProcessId)
            {
                Reappear(existing);
                return;
            }

            _tracked.Remove(hwnd);
            WindowUntracked?.Invoke(hwnd);
        }

        TrackIfNew(hwnd);
    }

    /// <summary>
    /// Re-reads everything about a window that is already tracked, raising only the
    /// events whose subject actually changed.
    /// <para>
    /// Show notifications are noisy - applications call <c>ShowWindow</c> on windows
    /// that are already showing - so the common case here has to cost nothing. Only
    /// a genuine difference is reported.
    /// </para>
    /// </summary>
    private void Reappear(WindowInfo existing)
    {
        var hwnd = existing.Hwnd;
        var updated = existing with
        {
            Title = _api.GetTitle(hwnd),
            IsMinimized = _api.IsMinimized(hwnd),
            IsHidden = false,
            Rect = _api.GetRect(hwnd),
        };

        if (updated == existing)
        {
            return;
        }

        _tracked[hwnd] = updated;

        // Geometry first, so a consumer that creates a surface in response to the
        // visibility change creates it at the right place rather than moving it
        // afterwards.
        if (updated.Rect != existing.Rect)
        {
            WindowGeometryChanged?.Invoke(updated);
        }

        if (!string.Equals(updated.Title, existing.Title, StringComparison.Ordinal))
        {
            WindowTitleChanged?.Invoke(updated);
        }

        if (updated.IsOnScreen != existing.IsOnScreen)
        {
            WindowVisibilityChanged?.Invoke(updated);
        }
    }

    /// <summary>
    /// Records that a tracked window has been hidden or cloaked, without untracking
    /// it. Silent when the window is already hidden: hide notifications repeat, and
    /// consumers tear a surface down in response to this.
    /// </summary>
    private void MarkHidden(nint hwnd)
    {
        if (!_tracked.TryGetValue(hwnd, out var existing) || existing.IsHidden)
        {
            return;
        }

        var updated = existing with { IsHidden = true };
        _tracked[hwnd] = updated;

        if (updated.IsOnScreen != existing.IsOnScreen)
        {
            WindowVisibilityChanged?.Invoke(updated);
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
