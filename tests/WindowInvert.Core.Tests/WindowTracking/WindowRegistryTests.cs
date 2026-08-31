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
        public Dictionary<nint, bool> TopLevel { get; } = new();
        public Dictionary<nint, string> Titles { get; } = new();
        public Dictionary<nint, uint> ProcessIds { get; } = new();

        public WindowRect GetRect(nint hwnd) => Rects[hwnd];
        public bool IsMinimized(nint hwnd) => Minimized.GetValueOrDefault(hwnd);
        public bool IsVisible(nint hwnd) => Visible.GetValueOrDefault(hwnd, true);
        public bool IsTopLevel(nint hwnd) => TopLevel.GetValueOrDefault(hwnd, true);
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
    public void HandleShow_OwnedWindow_IsNotTracked()
    {
        // An owned window - a tooltip, a menu popup, a combobox drop list, a modal
        // dialog. Every one of these raises the same show notification a real window
        // does, and the live path used to accept all of them.
        var (registry, api) = MakeRegistry();
        const nint owned = 77;
        api.Rects[owned] = new WindowRect(0, 0, 120, 30);
        api.Titles[owned] = "";
        api.TopLevel[owned] = false;

        var tracked = new List<WindowInfo>();
        registry.WindowTracked += info => tracked.Add(info);

        registry.HandleWinEvent(WinEventType.Show, owned);

        Assert.Empty(tracked);
        Assert.False(registry.TrackedWindows.ContainsKey(owned));
    }

    [Fact]
    public void HandleShow_NonRootWindow_IsNotTracked()
    {
        // A child window shown via ShowWindow: its GA_ROOT ancestor is its parent,
        // not itself, so IsTopLevel is false for exactly the same reason.
        var (registry, api) = MakeRegistry();
        const nint child = 88;
        api.Rects[child] = new WindowRect(10, 10, 50, 50);
        api.Titles[child] = "Child";
        api.TopLevel[child] = false;

        registry.HandleWinEvent(WinEventType.Show, child);

        Assert.False(registry.TrackedWindows.ContainsKey(child));
    }

    [Fact]
    public void HandleShow_TopLevelButInvisible_IsNotTracked()
    {
        var (registry, api) = MakeRegistry();
        const nint hidden = 99;
        api.Rects[hidden] = new WindowRect(0, 0, 100, 100);
        api.Visible[hidden] = false;
        api.TopLevel[hidden] = true;

        registry.HandleWinEvent(WinEventType.Show, hidden);

        Assert.False(registry.TrackedWindows.ContainsKey(hidden));
    }

    [Fact]
    public void HandleShow_TopLevelWindowWithNoTitleYet_IsStillTracked()
    {
        // Applications routinely show a window before calling SetWindowText, so a
        // title must not be part of the tracking predicate.
        var (registry, api) = MakeRegistry();
        const nint hwnd = 55;
        api.Rects[hwnd] = new WindowRect(0, 0, 640, 480);
        api.Titles[hwnd] = string.Empty;

        registry.HandleWinEvent(WinEventType.Show, hwnd);

        Assert.True(registry.TrackedWindows.ContainsKey(hwnd));
        Assert.Equal(string.Empty, registry.TrackedWindows[hwnd].Title);
    }

    [Fact]
    public void HandleNameChange_TrackedWindow_UpdatesTitleAndRaisesWindowTitleChanged()
    {
        var (registry, api) = MakeRegistry();
        const nint hwnd = 55;
        api.Rects[hwnd] = new WindowRect(0, 0, 640, 480);
        api.Titles[hwnd] = string.Empty;
        registry.HandleWinEvent(WinEventType.Show, hwnd);

        WindowInfo? renamed = null;
        registry.WindowTitleChanged += info => renamed = info;
        api.Titles[hwnd] = "Untitled - Notepad";

        registry.HandleWinEvent(WinEventType.NameChange, hwnd);

        Assert.NotNull(renamed);
        Assert.Equal("Untitled - Notepad", renamed!.Value.Title);
        Assert.Equal("Untitled - Notepad", registry.TrackedWindows[hwnd].Title);
    }

    [Fact]
    public void HandleNameChange_SameTitle_DoesNotRaise()
    {
        var (registry, api) = MakeRegistry();
        const nint hwnd = 55;
        api.Rects[hwnd] = new WindowRect(0, 0, 640, 480);
        api.Titles[hwnd] = "Stable";
        registry.HandleWinEvent(WinEventType.Show, hwnd);

        var raised = 0;
        registry.WindowTitleChanged += _ => raised++;

        registry.HandleWinEvent(WinEventType.NameChange, hwnd);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void HandleNameChange_UntrackedWindow_IsIgnored()
    {
        var (registry, api) = MakeRegistry();
        api.Titles[123] = "Never tracked";
        var raised = 0;
        registry.WindowTitleChanged += _ => raised++;

        registry.HandleWinEvent(WinEventType.NameChange, hwnd: 123);

        Assert.Equal(0, raised);
        Assert.False(registry.TrackedWindows.ContainsKey(123));
    }

    [Fact]
    public void HandleForegroundChange_RaisesWindowForegroundChangedWithTheHwnd()
    {
        var (registry, _) = MakeRegistry();

        var reported = new List<nint>();
        registry.WindowForegroundChanged += h => reported.Add(h);

        registry.HandleWinEvent(WinEventType.ForegroundChange, hwnd: 4242);

        Assert.Equal(new nint[] { 4242 }, reported);
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
