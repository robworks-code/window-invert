using WindowInvert.Core.Geometry;
using WindowInvert.Core.WindowTracking;
using Xunit;

namespace WindowInvert.Core.Tests.WindowTracking;

public class WindowRegistryTests
{
    private sealed class FakeWin32WindowApi : IWin32WindowApi
    {
        public Dictionary<nint, WindowRect> Rects { get; } = new();
        public Dictionary<nint, bool> Minimized { get; } = new();
        public Dictionary<nint, bool> Visible { get; } = new();
        public Dictionary<nint, string> Titles { get; } = new();
        public Dictionary<nint, uint> ProcessIds { get; } = new();

        public WindowRect GetRect(nint hwnd) => Rects[hwnd];
        public bool IsMinimized(nint hwnd) => Minimized.GetValueOrDefault(hwnd);
        public bool IsVisible(nint hwnd) => Visible.GetValueOrDefault(hwnd, true);
        public string GetTitle(nint hwnd) => Titles.GetValueOrDefault(hwnd, string.Empty);
        public uint GetProcessId(nint hwnd) => ProcessIds.GetValueOrDefault(hwnd);
    }

    private static (WindowRegistry registry, FakeWin32WindowApi api) MakeRegistry()
    {
        var api = new FakeWin32WindowApi();
        return (new WindowRegistry(api), api);
    }

    [Fact]
    public void HandleShow_UnknownWindow_TracksItAndRaisesWindowTracked()
    {
        var (registry, api) = MakeRegistry();
        const nint hwnd = 42;
        api.Rects[hwnd] = new WindowRect(0, 0, 100, 100);
        api.Titles[hwnd] = "Notepad";
        api.ProcessIds[hwnd] = 1234;

        WindowInfo? tracked = null;
        registry.WindowTracked += info => tracked = info;

        registry.HandleWinEvent(WinEventType.Show, hwnd);

        Assert.NotNull(tracked);
        Assert.Equal("Notepad", tracked!.Value.Title);
        Assert.True(registry.TrackedWindows.ContainsKey(hwnd));
    }

    [Fact]
    public void HandleDestroy_TrackedWindow_UntracksItAndRaisesWindowUntracked()
    {
        var (registry, api) = MakeRegistry();
        const nint hwnd = 42;
        api.Rects[hwnd] = new WindowRect(0, 0, 100, 100);
        registry.HandleWinEvent(WinEventType.Show, hwnd);

        nint? untracked = null;
        registry.WindowUntracked += h => untracked = h;

        registry.HandleWinEvent(WinEventType.Destroy, hwnd);

        Assert.Equal(hwnd, untracked);
        Assert.False(registry.TrackedWindows.ContainsKey(hwnd));
    }

    [Fact]
    public void HandleLocationChange_TrackedWindow_RaisesWindowGeometryChangedWithNewRect()
    {
        var (registry, api) = MakeRegistry();
        const nint hwnd = 42;
        api.Rects[hwnd] = new WindowRect(0, 0, 100, 100);
        registry.HandleWinEvent(WinEventType.Show, hwnd);

        api.Rects[hwnd] = new WindowRect(50, 60, 100, 100);
        WindowInfo? changed = null;
        registry.WindowGeometryChanged += info => changed = info;

        registry.HandleWinEvent(WinEventType.LocationChange, hwnd);

        Assert.Equal(new WindowRect(50, 60, 100, 100), changed!.Value.Rect);
    }

    [Fact]
    public void HandleMinimizeStartThenEnd_TrackedWindow_TogglesIsMinimizedAndRaisesVisibilityChanged()
    {
        var (registry, api) = MakeRegistry();
        const nint hwnd = 42;
        api.Rects[hwnd] = new WindowRect(0, 0, 100, 100);
        registry.HandleWinEvent(WinEventType.Show, hwnd);

        var visibilityEvents = new List<bool>();
        registry.WindowVisibilityChanged += info => visibilityEvents.Add(info.IsMinimized);

        registry.HandleWinEvent(WinEventType.MinimizeStart, hwnd);
        registry.HandleWinEvent(WinEventType.MinimizeEnd, hwnd);

        Assert.Equal(new[] { true, false }, visibilityEvents);
        Assert.False(registry.TrackedWindows[hwnd].IsMinimized);
    }

    [Fact]
    public void HandleEventForUntrackedWindow_OtherThanShow_IsIgnored()
    {
        var (registry, api) = MakeRegistry();
        var raised = false;
        registry.WindowGeometryChanged += _ => raised = true;

        registry.HandleWinEvent(WinEventType.LocationChange, hwnd: 999);

        Assert.False(raised);
    }

    [Fact]
    public void Bootstrap_SeedsTrackedWindowsWithoutRaisingEvents()
    {
        var (registry, _) = MakeRegistry();
        var raised = false;
        registry.WindowTracked += _ => raised = true;
        var seed = new[] { new WindowInfo(7, "Calc", 1, false, new WindowRect(0, 0, 10, 10)) };

        registry.Bootstrap(seed);

        Assert.False(raised);
        Assert.True(registry.TrackedWindows.ContainsKey(7));
    }
}
