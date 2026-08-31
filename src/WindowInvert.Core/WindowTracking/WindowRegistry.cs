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

            case WinEventType.ForegroundChange:
                // No state change tracked for v1; reserved for future use
                // (e.g. z-order-aware overlay stacking).
                break;
        }
    }

    private void TrackIfNew(nint hwnd)
    {
        if (_tracked.ContainsKey(hwnd))
        {
            return;
        }

        if (!_api.IsVisible(hwnd))
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
