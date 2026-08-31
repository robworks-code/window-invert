# Per-Window Color Invert Tool - MVP Implementation Plan

**Goal:** A Windows 11 tray application that can invert the colors of any single chosen window live, tracking it as it moves/resizes/minimizes, without affecting the rest of the screen.

**Architecture:** A .NET 8 tray app (`WindowInvert.App`) composes three layers: pure/testable state and geometry logic (`WindowInvert.Core`), thin Win32/WinRT/DirectX interop (`WindowInvert.Native`), and the app's own UI surfaces (tray menu, click-to-pick, floating title-bar buttons, overlay windows). Window tracking is event-driven via `SetWinEventHook`; per-window inversion is rendered by capturing each target window's live pixels with `Windows.Graphics.Capture`, negating them with a `D2D1ColorMatrixEffect`, and presenting the result through a click-through, always-on-top overlay window positioned exactly over the source window.

**Tech Stack:** C# / .NET 8 (`net8.0-windows10.0.19041.0`), Windows Forms (tray icon + message loop + `NativeWindow` for overlay windows), Vortice.Windows (D3D11/D2D1/DirectComposition/DXGI bindings), WinRT `Windows.Graphics.Capture` via CsWinRT projections, xUnit for `WindowInvert.Core` tests.

**Spec:** `docs/design/2026-08-29-per-window-invert-design.md`

## Global Constraints

- Windows 11 is the only supported target for this plan; Windows 10 compatibility is out of scope here (spec: "Windows 10 Compatibility").
- v1 invert is a plain full-RGB negation only - no image-region detection (spec: "Non-goals").
- No taskbar jump-list/context-menu injection - the floating title-bar button is the substitute (spec: "Non-goals").
- Elevated target windows are a known, unsolved limitation - do not attempt to work around this (spec: "Non-goals", "Known Edge Cases").
- `WindowInvert.Core` must have zero Win32/WinRT/DirectX dependencies so it stays unit-testable without a live window/GPU (spec: "Testing Approach").
- P/Invoke and WinRT/DirectX interop signatures in this plan are written to be correct, but are inherently harder to verify without a live compiler/SDK than pure C# - if a native call's exact signature does not match the installed Windows SDK/CsWin32 output, fix the signature to match the SDK rather than reinterpreting the surrounding design.

---

## File Structure

```
WindowInvert.sln
src/
  WindowInvert.Core/                          (net8.0 - pure logic, no OS dependency)
    Geometry/OverlayGeometry.cs
    Geometry/WindowRect.cs
    WindowTracking/WindowInfo.cs
    WindowTracking/IWin32WindowApi.cs
    WindowTracking/WinEventType.cs
    WindowTracking/WindowRegistry.cs
    InvertState/InvertedWindowSet.cs
    WindowInvert.Core.csproj
  WindowInvert.Native/                         (net8.0-windows10.0.19041.0 - thin OS interop)
    Interop/NativeMethods.cs
    Win32WindowApi.cs
    WindowEnumerator.cs
    WinEventHookListener.cs
    CaptureEngine.cs
    InvertRenderer.cs
    WindowInvert.Native.csproj
  WindowInvert.App/                            (net8.0-windows10.0.19041.0, WinExe - composition root + UI)
    Program.cs
    TrayApplicationContext.cs
    InvertOverlayWindow.cs
    TitleBarButtonWindow.cs
    WindowPickerOverlay.cs
    StartupRegistration.cs
    WindowInvert.App.csproj
tests/
  WindowInvert.Core.Tests/                     (xUnit)
    Geometry/OverlayGeometryTests.cs
    WindowTracking/WindowRegistryTests.cs
    InvertState/InvertedWindowSetTests.cs
    WindowInvert.Core.Tests.csproj
```

`WindowInvert.Core` never references `WindowInvert.Native`. `WindowInvert.Native` never references `WindowInvert.App`. `WindowInvert.App` references both. This keeps the state-machine and geometry logic testable in isolation, as required by the spec's Testing Approach section.

---

### Task 1: Solution scaffold and minimal tray app shell

**Files:**
- Create: `WindowInvert.sln`
- Create: `src/WindowInvert.Core/WindowInvert.Core.csproj`
- Create: `src/WindowInvert.Native/WindowInvert.Native.csproj`
- Create: `src/WindowInvert.App/WindowInvert.App.csproj`
- Create: `src/WindowInvert.App/Program.cs`
- Create: `src/WindowInvert.App/TrayApplicationContext.cs`
- Create: `tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj`

**Interfaces:**
- Produces: a buildable, runnable solution; `TrayApplicationContext : ApplicationContext` as the app's composition root, extended by later tasks.

- [ ] **Step 1: Create the solution and projects**

```bash
cd "C:\Users\ringo\git\accessibility-selective-window-contrast"
dotnet new sln -n WindowInvert
dotnet new classlib -n WindowInvert.Core -o src/WindowInvert.Core -f net8.0
dotnet new classlib -n WindowInvert.Native -o src/WindowInvert.Native -f net8.0-windows10.0.19041.0
dotnet new winforms -n WindowInvert.App -o src/WindowInvert.App -f net8.0-windows10.0.19041.0
dotnet new xunit -n WindowInvert.Core.Tests -o tests/WindowInvert.Core.Tests -f net8.0
dotnet sln add src/WindowInvert.Core/WindowInvert.Core.csproj
dotnet sln add src/WindowInvert.Native/WindowInvert.Native.csproj
dotnet sln add src/WindowInvert.App/WindowInvert.App.csproj
dotnet sln add tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj
dotnet add src/WindowInvert.Native/WindowInvert.Native.csproj reference src/WindowInvert.Core/WindowInvert.Core.csproj
dotnet add src/WindowInvert.App/WindowInvert.App.csproj reference src/WindowInvert.Core/WindowInvert.Core.csproj
dotnet add src/WindowInvert.App/WindowInvert.App.csproj reference src/WindowInvert.Native/WindowInvert.Native.csproj
dotnet add tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj reference src/WindowInvert.Core/WindowInvert.Core.csproj
```

- [ ] **Step 2: Enable unsafe/allow-COM interop where needed and set nullable/implicit usings**

In `src/WindowInvert.Native/WindowInvert.Native.csproj`, inside the existing `<PropertyGroup>`, add:

```xml
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<Nullable>enable</Nullable>
```

Do the same `<Nullable>enable</Nullable>` addition in `src/WindowInvert.Core/WindowInvert.Core.csproj` and `src/WindowInvert.App/WindowInvert.App.csproj`.

- [ ] **Step 3: Replace `Program.cs` with a tray-only entry point**

```csharp
// src/WindowInvert.App/Program.cs
namespace WindowInvert.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
```

- [ ] **Step 4: Write the minimal `TrayApplicationContext`**

```csharp
// src/WindowInvert.App/TrayApplicationContext.cs
namespace WindowInvert.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Window Invert",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
```

- [ ] **Step 5: Build and run to verify the tray icon appears**

Run: `dotnet build WindowInvert.sln`
Expected: build succeeds with no errors.

Run: `dotnet run --project src/WindowInvert.App/WindowInvert.App.csproj`
Expected: no visible main window; a new icon appears in the system tray notification area; right-clicking it shows a menu with "Exit"; clicking "Exit" closes the app and removes the tray icon. Verify this manually (there is no meaningful automated test for "a tray icon appeared" - this is a genuine smoke test, not a stand-in for one).

- [ ] **Step 6: Commit**

```bash
git add WindowInvert.sln src/ tests/
git commit -m "Scaffold solution and minimal tray app shell"
```

---

### Task 2: Core geometry pure functions

**Files:**
- Create: `src/WindowInvert.Core/Geometry/WindowRect.cs`
- Create: `src/WindowInvert.Core/Geometry/OverlayGeometry.cs`
- Test: `tests/WindowInvert.Core.Tests/Geometry/OverlayGeometryTests.cs`

**Interfaces:**
- Produces: `WindowInvert.Core.Geometry.WindowRect` (readonly record struct: `int X, int Y, int Width, int Height`, computed `Right`/`Bottom`), `OverlayGeometry.ComputeOverlayRect(WindowRect source) -> WindowRect`, `OverlayGeometry.ComputeTitleBarButtonRect(WindowRect source, int buttonSize) -> WindowRect`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/WindowInvert.Core.Tests/Geometry/OverlayGeometryTests.cs
using WindowInvert.Core.Geometry;
using Xunit;

namespace WindowInvert.Core.Tests.Geometry;

public class OverlayGeometryTests
{
    [Fact]
    public void ComputeOverlayRect_MatchesSourceExactly()
    {
        var source = new WindowRect(100, 200, 800, 600);

        var result = OverlayGeometry.ComputeOverlayRect(source);

        Assert.Equal(source, result);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_PlacesButtonInsideTopRightCorner()
    {
        var source = new WindowRect(0, 0, 800, 600);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, buttonSize: 24);

        // Sits fully inside the source window, flush to the top edge, and
        // offset left from the right edge so it doesn't collide with the
        // native minimize/maximize/close buttons (~140px reserved).
        Assert.Equal(800 - 140 - 24, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(24, result.Width);
        Assert.Equal(24, result.Height);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_TracksMovedSource()
    {
        var source = new WindowRect(300, 150, 800, 600);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, buttonSize: 24);

        Assert.Equal(300 + 800 - 140 - 24, result.X);
        Assert.Equal(150, result.Y);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj`
Expected: FAIL to compile - `WindowRect` and `OverlayGeometry` do not exist yet.

- [ ] **Step 3: Implement `WindowRect`**

```csharp
// src/WindowInvert.Core/Geometry/WindowRect.cs
namespace WindowInvert.Core.Geometry;

public readonly record struct WindowRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
```

- [ ] **Step 4: Implement `OverlayGeometry`**

```csharp
// src/WindowInvert.Core/Geometry/OverlayGeometry.cs
namespace WindowInvert.Core.Geometry;

public static class OverlayGeometry
{
    // Reserves space for a source window's native minimize/maximize/close
    // caption buttons so the floating toggle button never overlaps them.
    private const int CaptionButtonsReservedWidth = 140;

    public static WindowRect ComputeOverlayRect(WindowRect source) => source;

    public static WindowRect ComputeTitleBarButtonRect(WindowRect source, int buttonSize)
    {
        var x = source.X + source.Width - CaptionButtonsReservedWidth - buttonSize;
        var y = source.Y;
        return new WindowRect(x, y, buttonSize, buttonSize);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/WindowInvert.Core/Geometry tests/WindowInvert.Core.Tests/Geometry
git commit -m "Add pure overlay and title-bar-button geometry functions"
```

---

### Task 3: WindowRegistry state machine

**Files:**
- Create: `src/WindowInvert.Core/WindowTracking/WindowInfo.cs`
- Create: `src/WindowInvert.Core/WindowTracking/IWin32WindowApi.cs`
- Create: `src/WindowInvert.Core/WindowTracking/WinEventType.cs`
- Create: `src/WindowInvert.Core/WindowTracking/WindowRegistry.cs`
- Test: `tests/WindowInvert.Core.Tests/WindowTracking/WindowRegistryTests.cs`

**Interfaces:**
- Consumes: `WindowInvert.Core.Geometry.WindowRect` (Task 2).
- Produces: `WindowInfo` record (`nint Hwnd, string Title, uint ProcessId, bool IsMinimized, WindowRect Rect`), `IWin32WindowApi` (`WindowRect GetRect(nint hwnd)`, `bool IsMinimized(nint hwnd)`, `bool IsVisible(nint hwnd)`, `string GetTitle(nint hwnd)`, `uint GetProcessId(nint hwnd)`), `WinEventType` enum (`Show, Hide, Destroy, LocationChange, ForegroundChange, MinimizeStart, MinimizeEnd`), `WindowRegistry` class with constructor `(IWin32WindowApi api)`, methods `Bootstrap(IEnumerable<WindowInfo> initialWindows)` and `HandleWinEvent(WinEventType type, nint hwnd)`, events `WindowTracked`, `WindowUntracked`, `WindowGeometryChanged`, `WindowVisibilityChanged` (all `Action<...>`), and property `TrackedWindows` (`IReadOnlyDictionary<nint, WindowInfo>`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/WindowInvert.Core.Tests/WindowTracking/WindowRegistryTests.cs
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj`
Expected: FAIL to compile - the types referenced don't exist yet.

- [ ] **Step 3: Implement the supporting types**

```csharp
// src/WindowInvert.Core/WindowTracking/WindowInfo.cs
using WindowInvert.Core.Geometry;

namespace WindowInvert.Core.WindowTracking;

public readonly record struct WindowInfo(
    nint Hwnd,
    string Title,
    uint ProcessId,
    bool IsMinimized,
    WindowRect Rect);
```

```csharp
// src/WindowInvert.Core/WindowTracking/IWin32WindowApi.cs
using WindowInvert.Core.Geometry;

namespace WindowInvert.Core.WindowTracking;

public interface IWin32WindowApi
{
    WindowRect GetRect(nint hwnd);
    bool IsMinimized(nint hwnd);
    bool IsVisible(nint hwnd);
    string GetTitle(nint hwnd);
    uint GetProcessId(nint hwnd);
}
```

```csharp
// src/WindowInvert.Core/WindowTracking/WinEventType.cs
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
}
```

- [ ] **Step 4: Implement `WindowRegistry`**

```csharp
// src/WindowInvert.Core/WindowTracking/WindowRegistry.cs
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj`
Expected: PASS (all `WindowRegistryTests` + previous tests).

- [ ] **Step 6: Commit**

```bash
git add src/WindowInvert.Core/WindowTracking tests/WindowInvert.Core.Tests/WindowTracking
git commit -m "Add WindowRegistry state machine for tracked top-level windows"
```

---

### Task 4: InvertedWindowSet (multi-window toggle state)

**Files:**
- Create: `src/WindowInvert.Core/InvertState/InvertedWindowSet.cs`
- Test: `tests/WindowInvert.Core.Tests/InvertState/InvertedWindowSetTests.cs`

**Interfaces:**
- Produces: `InvertedWindowSet` class with `bool Toggle(nint hwnd)` (returns new state), `bool IsInverted(nint hwnd)`, `void Remove(nint hwnd)`, `IReadOnlyCollection<nint> InvertedHandles`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/WindowInvert.Core.Tests/InvertState/InvertedWindowSetTests.cs
using WindowInvert.Core.InvertState;
using Xunit;

namespace WindowInvert.Core.Tests.InvertState;

public class InvertedWindowSetTests
{
    [Fact]
    public void Toggle_OnUntoggledWindow_MarksItInvertedAndReturnsTrue()
    {
        var set = new InvertedWindowSet();

        var result = set.Toggle(hwnd: 1);

        Assert.True(result);
        Assert.True(set.IsInverted(1));
        Assert.Contains(1, set.InvertedHandles);
    }

    [Fact]
    public void Toggle_OnAlreadyInvertedWindow_UnmarksItAndReturnsFalse()
    {
        var set = new InvertedWindowSet();
        set.Toggle(1);

        var result = set.Toggle(1);

        Assert.False(result);
        Assert.False(set.IsInverted(1));
        Assert.DoesNotContain(1, set.InvertedHandles);
    }

    [Fact]
    public void Remove_InvertedWindow_UnmarksItWithoutError()
    {
        var set = new InvertedWindowSet();
        set.Toggle(1);

        set.Remove(1);

        Assert.False(set.IsInverted(1));
    }

    [Fact]
    public void IndependentWindows_ToggleIndependently()
    {
        var set = new InvertedWindowSet();

        set.Toggle(1);
        set.Toggle(2);

        Assert.True(set.IsInverted(1));
        Assert.True(set.IsInverted(2));
        Assert.Equal(2, set.InvertedHandles.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj`
Expected: FAIL to compile - `InvertedWindowSet` doesn't exist yet.

- [ ] **Step 3: Implement `InvertedWindowSet`**

```csharp
// src/WindowInvert.Core/InvertState/InvertedWindowSet.cs
namespace WindowInvert.Core.InvertState;

public sealed class InvertedWindowSet
{
    private readonly HashSet<nint> _inverted = new();

    public IReadOnlyCollection<nint> InvertedHandles => _inverted;

    public bool IsInverted(nint hwnd) => _inverted.Contains(hwnd);

    public bool Toggle(nint hwnd)
    {
        if (_inverted.Remove(hwnd))
        {
            return false;
        }

        _inverted.Add(hwnd);
        return true;
    }

    public void Remove(nint hwnd) => _inverted.Remove(hwnd);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WindowInvert.Core.Tests/WindowInvert.Core.Tests.csproj`
Expected: PASS (all tests so far).

- [ ] **Step 5: Commit**

```bash
git add src/WindowInvert.Core/InvertState tests/WindowInvert.Core.Tests/InvertState
git commit -m "Add InvertedWindowSet for independent multi-window toggle state"
```

---

### Task 5: Native Win32 window API, enumeration, and WinEvent hook listener

**Files:**
- Create: `src/WindowInvert.Native/Interop/NativeMethods.cs`
- Create: `src/WindowInvert.Native/Win32WindowApi.cs`
- Create: `src/WindowInvert.Native/WindowEnumerator.cs`
- Create: `src/WindowInvert.Native/WinEventHookListener.cs`

**Interfaces:**
- Consumes: `WindowInvert.Core.Geometry.WindowRect` (Task 2), `WindowInvert.Core.WindowTracking.IWin32WindowApi`, `WinEventType` (Task 3).
- Produces: `Win32WindowApi : IWin32WindowApi`; `WindowEnumerator.EnumTopLevelWindows() -> IEnumerable<nint>`; `WinEventHookListener` with `event Action<WinEventType, nint>? WindowEvent`, `void Start()`, `void Stop()`.

This task has no automated tests - it is a thin wrapper over Win32 APIs with no meaningful way to unit test without a live window/desktop (per spec: "Testing Approach"). It compiles standalone here; its behavior is verified once wired into the running app in Task 6.

- [ ] **Step 1: Declare the P/Invoke surface**

```csharp
// src/WindowInvert.Native/Interop/NativeMethods.cs
using System.Runtime.InteropServices;

namespace WindowInvert.Native.Interop;

internal static class NativeMethods
{
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    public delegate void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(System.Drawing.Point Point);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    public const uint GA_ROOT = 2;
    public const uint GW_OWNER = 4;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_MIN = 0x0001;
    public const uint EVENT_MAX = 0x7FFFFFFF;
}
```

- [ ] **Step 2: Implement `Win32WindowApi`**

```csharp
// src/WindowInvert.Native/Win32WindowApi.cs
using System.Text;
using WindowInvert.Core.Geometry;
using WindowInvert.Core.WindowTracking;
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public sealed class Win32WindowApi : IWin32WindowApi
{
    public WindowRect GetRect(nint hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out var rect);
        return new WindowRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public bool IsMinimized(nint hwnd) => NativeMethods.IsIconic(hwnd);

    public bool IsVisible(nint hwnd) => NativeMethods.IsWindowVisible(hwnd);

    public string GetTitle(nint hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public uint GetProcessId(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }
}
```

- [ ] **Step 3: Implement `WindowEnumerator`**

A top-level window is enumerable, visible, has a non-empty title, and has no owner (owned popups/tool windows are excluded so the tray's window list only shows things a user would recognize as "a window").

```csharp
// src/WindowInvert.Native/WindowEnumerator.cs
using WindowInvert.Native.Interop;

namespace WindowInvert.Native;

public static class WindowEnumerator
{
    public static IEnumerable<nint> EnumTopLevelWindows()
    {
        var result = new List<nint>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0)
            {
                return true;
            }

            if (NativeMethods.GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }

            result.Add(hwnd);
            return true;
        }, 0);

        return result;
    }
}
```

- [ ] **Step 4: Implement `WinEventHookListener`**

```csharp
// src/WindowInvert.Native/WinEventHookListener.cs
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
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build src/WindowInvert.Native/WindowInvert.Native.csproj`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/WindowInvert.Native
git commit -m "Add Win32 window API, enumerator, and WinEvent hook listener"
```

---

### Task 6: Wire WindowRegistry into the tray app with a live window list menu

**Files:**
- Modify: `src/WindowInvert.App/TrayApplicationContext.cs`

**Interfaces:**
- Consumes: `WindowRegistry`, `InvertedWindowSet` (Core), `Win32WindowApi`, `WindowEnumerator`, `WinEventHookListener` (Native).

- [ ] **Step 1: Extend `TrayApplicationContext` to bootstrap and subscribe the registry**

```csharp
// src/WindowInvert.App/TrayApplicationContext.cs
using WindowInvert.Core.InvertState;
using WindowInvert.Core.WindowTracking;
using WindowInvert.Native;

namespace WindowInvert.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _windowsMenu;
    private readonly WindowRegistry _registry;
    private readonly InvertedWindowSet _invertedWindows = new();
    private readonly WinEventHookListener _hook = new();

    public TrayApplicationContext()
    {
        _registry = new WindowRegistry(new Win32WindowApi());

        _windowsMenu = new ToolStripMenuItem("Windows");
        _menu = new ContextMenuStrip();
        _menu.Items.Add(_windowsMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Window Invert",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _registry.WindowTracked += _ => RebuildWindowsMenu();
        _registry.WindowUntracked += hwnd =>
        {
            _invertedWindows.Remove(hwnd);
            RebuildWindowsMenu();
        };

        _hook.WindowEvent += (type, hwnd) => _registry.HandleWinEvent(type, hwnd);

        BootstrapRegistry();
        _hook.Start();
        RebuildWindowsMenu();
    }

    private void BootstrapRegistry()
    {
        var api = new Win32WindowApi();
        var initial = WindowEnumerator.EnumTopLevelWindows()
            .Select(hwnd => new WindowInfo(
                hwnd,
                api.GetTitle(hwnd),
                api.GetProcessId(hwnd),
                api.IsMinimized(hwnd),
                api.GetRect(hwnd)));

        _registry.Bootstrap(initial);
    }

    private void RebuildWindowsMenu()
    {
        _windowsMenu.DropDownItems.Clear();

        foreach (var info in _registry.TrackedWindows.Values.OrderBy(w => w.Title))
        {
            var item = new ToolStripMenuItem(info.Title)
            {
                Checked = _invertedWindows.IsInverted(info.Hwnd),
                CheckOnClick = false,
            };

            item.Click += (_, _) =>
            {
                item.Checked = _invertedWindows.Toggle(info.Hwnd);
            };

            _windowsMenu.DropDownItems.Add(item);
        }

        if (_windowsMenu.DropDownItems.Count == 0)
        {
            _windowsMenu.DropDownItems.Add(new ToolStripMenuItem("(no windows found)") { Enabled = false });
        }
    }

    protected override void ExitThreadCore()
    {
        _hook.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
```

- [ ] **Step 2: Manually verify the live window list**

Run: `dotnet run --project src/WindowInvert.App/WindowInvert.App.csproj`

With the app running, open Notepad and Calculator. Right-click the tray icon and expand "Windows".
Expected: both "Notepad" and "Calculator" appear as checkable menu entries, alongside other already-open top-level windows. Clicking an entry checks it (no visible invert effect yet - that starts in Task 7). Close Notepad.
Expected: reopening the tray menu no longer shows "Notepad" - `WindowUntracked` removed it live.

- [ ] **Step 3: Commit**

```bash
git add src/WindowInvert.App/TrayApplicationContext.cs
git commit -m "Wire WindowRegistry into tray app with a live, toggleable window list"
```

---

### Task 7: Click-through overlay window with placeholder fill

**Files:**
- Create: `src/WindowInvert.App/InvertOverlayWindow.cs`
- Modify: `src/WindowInvert.App/TrayApplicationContext.cs`

**Interfaces:**
- Consumes: `WindowInvert.Core.Geometry.WindowRect`, `OverlayGeometry` (Task 2), `WindowRegistry`, `InvertedWindowSet` (Core).
- Produces: `InvertOverlayWindow : NativeWindow` with constructor `(WindowRect initial)`, `void Reposition(WindowRect sourceRect)`, `void Show()`, `void Hide()`, `void Destroy()`.

This task proves window mechanics (positioning, click-through, lifecycle tracking) using a solid placeholder fill. Task 11 replaces the fill with the real inverted capture.

- [ ] **Step 1: Implement `InvertOverlayWindow`**

```csharp
// src/WindowInvert.App/InvertOverlayWindow.cs
using System.Runtime.InteropServices;
using WindowInvert.Core.Geometry;

namespace WindowInvert.App;

internal sealed class InvertOverlayWindow : NativeWindow
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;

    public InvertOverlayWindow(WindowRect initial)
    {
        var overlayRect = OverlayGeometry.ComputeOverlayRect(initial);

        var cp = new CreateParams
        {
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = overlayRect.X,
            Y = overlayRect.Y,
            Width = overlayRect.Width,
            Height = overlayRect.Height,
        };

        CreateHandle(cp);

        // Placeholder fill: semi-transparent so the underlying window is
        // still visible beneath it while proving positioning/click-through
        // ahead of the real invert pipeline (Task 11).
        SetLayeredWindowAttributes(Handle, 0, 160, LWA_ALPHA);
    }

    public void Reposition(WindowRect sourceRect)
    {
        var overlayRect = OverlayGeometry.ComputeOverlayRect(sourceRect);
        SetWindowPos(Handle, HWND_TOPMOST, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height, SWP_NOACTIVATE);
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    public void Destroy() => DestroyHandle();
}
```

- [ ] **Step 2: Wire overlay creation/teardown into `TrayApplicationContext`**

Replace the toggle handler in `RebuildWindowsMenu` and add overlay-lifecycle plumbing:

```csharp
// In TrayApplicationContext: add a field
private readonly Dictionary<nint, InvertOverlayWindow> _overlays = new();
```

```csharp
// Replace the item.Click handler inside RebuildWindowsMenu with:
item.Click += (_, _) =>
{
    var isNowInverted = _invertedWindows.Toggle(info.Hwnd);
    item.Checked = isNowInverted;

    if (isNowInverted)
    {
        var overlay = new InvertOverlayWindow(info.Rect);
        overlay.Show();
        _overlays[info.Hwnd] = overlay;
    }
    else if (_overlays.Remove(info.Hwnd, out var overlay))
    {
        overlay.Destroy();
    }
};
```

```csharp
// Add geometry/visibility tracking and teardown-on-close, wired in the constructor:
_registry.WindowGeometryChanged += info =>
{
    if (_overlays.TryGetValue(info.Hwnd, out var overlay))
    {
        overlay.Reposition(info.Rect);
    }
};

_registry.WindowVisibilityChanged += info =>
{
    if (_overlays.TryGetValue(info.Hwnd, out var overlay))
    {
        if (info.IsMinimized) overlay.Hide();
        else overlay.Show();
    }
};
```

```csharp
// In the existing _registry.WindowUntracked handler, also destroy the overlay:
_registry.WindowUntracked += hwnd =>
{
    _invertedWindows.Remove(hwnd);
    if (_overlays.Remove(hwnd, out var overlay))
    {
        overlay.Destroy();
    }
    RebuildWindowsMenu();
};
```

- [ ] **Step 3: Manually verify overlay mechanics**

Run: `dotnet run --project src/WindowInvert.App/WindowInvert.App.csproj`

Open Notepad. From the tray menu, toggle "Notepad" on.
Expected: a semi-transparent gray rectangle appears exactly over the Notepad window.
Move and resize Notepad.
Expected: the overlay tracks it live.
Type into Notepad while the overlay is showing.
Expected: keystrokes reach Notepad normally - the overlay is click-through.
Minimize and restore Notepad.
Expected: overlay hides and reappears in sync.
Toggle "Notepad" off from the tray menu.
Expected: overlay disappears immediately.
Toggle it on again, then close Notepad via its own close button.
Expected: overlay disappears automatically (via `WindowUntracked`).

- [ ] **Step 4: Commit**

```bash
git add src/WindowInvert.App/InvertOverlayWindow.cs src/WindowInvert.App/TrayApplicationContext.cs
git commit -m "Add click-through overlay window tracking toggled source windows"
```

---

### Task 8: Floating title-bar toggle button

**Files:**
- Create: `src/WindowInvert.App/TitleBarButtonWindow.cs`
- Modify: `src/WindowInvert.App/TrayApplicationContext.cs`

**Interfaces:**
- Consumes: `OverlayGeometry.ComputeTitleBarButtonRect` (Task 2), `WindowRegistry`, `InvertedWindowSet` (Core).
- Produces: `TitleBarButtonWindow : NativeWindow` with constructor `(WindowRect sourceRect, Action onClicked)`, `void Reposition(WindowRect sourceRect)`, `void SetToggledVisual(bool isToggled)`, `void Show()`, `void Hide()`, `void Destroy()`.

- [ ] **Step 1: Implement `TitleBarButtonWindow`**

Unlike the invert overlay, this window must receive its own clicks, so it is **not** `WS_EX_TRANSPARENT`. It paints a small colored square via GDI and toggles color on click.

```csharp
// src/WindowInvert.App/TitleBarButtonWindow.cs
using System.Runtime.InteropServices;
using WindowInvert.Core.Geometry;

namespace WindowInvert.App;

internal sealed class TitleBarButtonWindow : NativeWindow
{
    private const int ButtonSize = 20;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_PAINT = 0x000F;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly Action _onClicked;
    private bool _isToggled;

    public TitleBarButtonWindow(WindowRect sourceRect, Action onClicked)
    {
        _onClicked = onClicked;
        var buttonRect = OverlayGeometry.ComputeTitleBarButtonRect(sourceRect, ButtonSize);

        var cp = new CreateParams
        {
            Style = WS_POPUP,
            ExStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = buttonRect.X,
            Y = buttonRect.Y,
            Width = buttonRect.Width,
            Height = buttonRect.Height,
        };

        CreateHandle(cp);
    }

    public void Reposition(WindowRect sourceRect)
    {
        var buttonRect = OverlayGeometry.ComputeTitleBarButtonRect(sourceRect, ButtonSize);
        SetWindowPos(Handle, HWND_TOPMOST, buttonRect.X, buttonRect.Y, buttonRect.Width, buttonRect.Height, SWP_NOACTIVATE);
    }

    public void SetToggledVisual(bool isToggled)
    {
        _isToggled = isToggled;
        InvalidateRect(Handle, 0, true);
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    public void Destroy() => DestroyHandle();

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_LBUTTONUP:
                _onClicked();
                return;

            case WM_PAINT:
                using (var g = Graphics.FromHwnd(Handle))
                {
                    g.Clear(_isToggled ? Color.OrangeRed : Color.DimGray);
                }
                return;
        }

        base.WndProc(ref m);
    }
}
```

- [ ] **Step 2: Wire one title-bar button per tracked window into `TrayApplicationContext`**

```csharp
// Add a field
private readonly Dictionary<nint, TitleBarButtonWindow> _titleBarButtons = new();
```

```csharp
// Extract the toggle logic used by both the menu item and the button into one method:
private void ToggleInvert(nint hwnd, WindowRect currentRect)
{
    var isNowInverted = _invertedWindows.Toggle(hwnd);

    if (isNowInverted)
    {
        var overlay = new InvertOverlayWindow(currentRect);
        overlay.Show();
        _overlays[hwnd] = overlay;
    }
    else if (_overlays.Remove(hwnd, out var overlay))
    {
        overlay.Destroy();
    }

    if (_titleBarButtons.TryGetValue(hwnd, out var button))
    {
        button.SetToggledVisual(isNowInverted);
    }
}
```

```csharp
// Replace the tray menu item's Click handler body with:
item.Click += (_, _) =>
{
    ToggleInvert(info.Hwnd, info.Rect);
    item.Checked = _invertedWindows.IsInverted(info.Hwnd);
};
```

```csharp
// In the _registry.WindowTracked handler, also create a title-bar button:
_registry.WindowTracked += info =>
{
    var button = new TitleBarButtonWindow(info.Rect, () => ToggleInvert(info.Hwnd, _registry.TrackedWindows[info.Hwnd].Rect));
    button.Show();
    _titleBarButtons[info.Hwnd] = button;

    RebuildWindowsMenu();
};
```

```csharp
// In WindowGeometryChanged, also reposition the button:
_registry.WindowGeometryChanged += info =>
{
    if (_overlays.TryGetValue(info.Hwnd, out var overlay))
    {
        overlay.Reposition(info.Rect);
    }

    if (_titleBarButtons.TryGetValue(info.Hwnd, out var button))
    {
        button.Reposition(info.Rect);
    }
};
```

```csharp
// In WindowVisibilityChanged, also hide/show the button:
_registry.WindowVisibilityChanged += info =>
{
    if (_overlays.TryGetValue(info.Hwnd, out var overlay))
    {
        if (info.IsMinimized) overlay.Hide();
        else overlay.Show();
    }

    if (_titleBarButtons.TryGetValue(info.Hwnd, out var button))
    {
        if (info.IsMinimized) button.Hide();
        else button.Show();
    }
};
```

```csharp
// In WindowUntracked, also destroy the button:
_registry.WindowUntracked += hwnd =>
{
    _invertedWindows.Remove(hwnd);
    if (_overlays.Remove(hwnd, out var overlay))
    {
        overlay.Destroy();
    }
    if (_titleBarButtons.Remove(hwnd, out var button))
    {
        button.Destroy();
    }
    RebuildWindowsMenu();
};
```

Bootstrapped windows don't fire `WindowTracked` (matching Task 3's `Bootstrap` contract), so `BootstrapRegistry` must create their title-bar buttons itself. Add this at the end of `BootstrapRegistry`, after the existing `_registry.Bootstrap(initial);` line:

```csharp
foreach (var info in _registry.TrackedWindows.Values)
{
    var button = new TitleBarButtonWindow(info.Rect, () => ToggleInvert(info.Hwnd, _registry.TrackedWindows[info.Hwnd].Rect));
    button.Show();
    _titleBarButtons[info.Hwnd] = button;
}
```

- [ ] **Step 3: Manually verify the title-bar button**

Run: `dotnet run --project src/WindowInvert.App/WindowInvert.App.csproj`

Open Notepad.
Expected: a small gray square button appears near its top-right corner, left of its native caption buttons.
Click it.
Expected: it turns orange-red, and the placeholder invert overlay from Task 7 appears; the tray menu's "Notepad" entry is also now checked.
Click the button again.
Expected: it returns to gray, overlay disappears, tray menu entry unchecks.
Move/resize Notepad.
Expected: the button tracks it, staying clear of the native caption buttons.

- [ ] **Step 4: Commit**

```bash
git add src/WindowInvert.App/TitleBarButtonWindow.cs src/WindowInvert.App/TrayApplicationContext.cs
git commit -m "Add floating title-bar toggle button per tracked window"
```

---

### Task 9: Click-to-pick crosshair mode

**Files:**
- Create: `src/WindowInvert.App/WindowPickerOverlay.cs`
- Modify: `src/WindowInvert.App/TrayApplicationContext.cs`

**Interfaces:**
- Consumes: `WindowRegistry.TrackedWindows` (Core), the `ToggleInvert` method (Task 8).
- Produces: `WindowPickerOverlay : NativeWindow` with constructor `(Action<nint> onWindowPicked)`, `void Show()`.

- [ ] **Step 1: Implement `WindowPickerOverlay`**

A full-screen, topmost, transparent-but-clickable window covering the primary screen's virtual bounds; on click it resolves the top-level window under the cursor and reports it, then closes itself.

```csharp
// src/WindowInvert.App/WindowPickerOverlay.cs
using System.Runtime.InteropServices;

namespace WindowInvert.App;

internal sealed class WindowPickerOverlay : NativeWindow
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOW = 5;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(System.Drawing.Point Point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    private readonly Action<nint> _onWindowPicked;

    public WindowPickerOverlay(Action<nint> onWindowPicked)
    {
        _onWindowPicked = onWindowPicked;
        var bounds = SystemInformation.VirtualScreen;

        var cp = new CreateParams
        {
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
        };

        CreateHandle(cp);

        // Fully transparent, but still receives clicks (no WS_EX_TRANSPARENT).
        SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
        Cursor.Current = Cursors.Cross;
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_LBUTTONUP)
        {
            GetCursorPos(out var screenPoint);
            DestroyHandle();

            var hit = WindowFromPoint(screenPoint);
            if (hit != 0)
            {
                var topLevel = GetAncestor(hit, GA_ROOT);
                _onWindowPicked(topLevel != 0 ? topLevel : hit);
            }

            return;
        }

        base.WndProc(ref m);
    }
}
```

- [ ] **Step 2: Add a "Pick a window..." tray menu action**

```csharp
// In the TrayApplicationContext constructor, before adding "Exit":
_menu.Items.Add("Pick a window...", null, (_, _) =>
{
    var picker = new WindowPickerOverlay(hwnd =>
    {
        if (_registry.TrackedWindows.TryGetValue(hwnd, out var info))
        {
            ToggleInvert(hwnd, info.Rect);
            RebuildWindowsMenu();
        }
    });
    picker.Show();
});
```

- [ ] **Step 3: Manually verify click-to-pick**

Run: `dotnet run --project src/WindowInvert.App/WindowInvert.App.csproj`

From the tray menu, choose "Pick a window...".
Expected: cursor becomes a crosshair.
Click on a visible window (e.g. Notepad).
Expected: that window toggles inverted (placeholder overlay appears), matching the same behavior as clicking its title-bar button.
Repeat, clicking on the desktop background (not a tracked window).
Expected: nothing toggles, no crash.

- [ ] **Step 4: Commit**

```bash
git add src/WindowInvert.App/WindowPickerOverlay.cs src/WindowInvert.App/TrayApplicationContext.cs
git commit -m "Add click-to-pick crosshair mode for toggling invert"
```

---

### Task 10: Windows.Graphics.Capture engine per window

**Files:**
- Modify: `src/WindowInvert.Native/WindowInvert.Native.csproj`
- Create: `src/WindowInvert.Native/CaptureEngine.cs`

**Interfaces:**
- Produces: `CaptureEngine` with `event Action<Vortice.Direct3D11.ID3D11Texture2D>? FrameArrived`, `void Start(nint hwnd)`, `void Stop()`, `ID3D11Device? Device` (the device frames were captured on, consumed by Task 11's `InvertRenderer`).

- [ ] **Step 1: Add the required NuGet packages**

```bash
cd "C:\Users\ringo\git\accessibility-selective-window-contrast"
dotnet add src/WindowInvert.Native/WindowInvert.Native.csproj package Vortice.Direct3D11
dotnet add src/WindowInvert.Native/WindowInvert.Native.csproj package Vortice.DXGI
dotnet add src/WindowInvert.Native/WindowInvert.Native.csproj package Microsoft.Windows.SDK.Contracts
```

In `WindowInvert.Native.csproj`'s `<PropertyGroup>`, add:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<CsWinRTIncludes>Windows.Graphics.Capture;Windows.Graphics.DirectX;Windows.Graphics.DirectX.Direct3D11</CsWinRTIncludes>
```

(`CsWinRTIncludes` requires the `Microsoft.Windows.SDK.Contracts` package's build-time WinMD projection; this generates the C# projections for `Windows.Graphics.Capture` used below.)

- [ ] **Step 2: Implement `CaptureEngine`**

```csharp
// src/WindowInvert.Native/CaptureEngine.cs
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowInvert.Native;

public sealed class CaptureEngine : IDisposable
{
    private ID3D11Device? _d3dDevice;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    public event Action<ID3D11Texture2D>? FrameArrived;

    // Exposed so InvertRenderer can build its D2D device context on the
    // exact same D3D11 device that produced the captured frames -
    // CreateBitmapFromDxgiSurface requires the surface and the D2D
    // device to share one underlying D3D11 device, or it fails at
    // runtime. A second, independently-created device is not interchangeable.
    public ID3D11Device? Device => _d3dDevice;

    public void Start(nint hwnd)
    {
        Stop();

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _d3dDevice);

        _winrtDevice = CreateDirect3DDeviceFromD3D11Device(_d3dDevice!);
        _item = CaptureHelper.CreateItemForWindow(hwnd);

        _framePool = Direct3D11CaptureFramePool.Create(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            _item.Size);

        _framePool.FrameArrived += (pool, _) =>
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var texture = Direct3D11Helper.CreateD3D11Texture2DFromSurface(frame.Surface);
            FrameArrived?.Invoke(texture);
        };

        _session = _framePool.CreateCaptureSession(_item);
        _session.StartCapture();
    }

    public void Stop()
    {
        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        _item = null;
        _winrtDevice = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    public void Dispose() => Stop();

    private static IDirect3DDevice CreateDirect3DDeviceFromD3D11Device(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        return Direct3D11Helper.CreateDirect3DDeviceFromDXGIDevice(dxgiDevice);
    }
}
```

`CaptureHelper.CreateItemForWindow` and `Direct3D11Helper` (the `IGraphicsCaptureItemInterop` / `IDirect3DDxgiInterfaceAccess` COM interop shims needed to bridge WinRT capture types and raw D3D11/DXGI objects) are small, well-known interop helpers with no first-party NuGet package - implement them now as part of this step:

```csharp
// src/WindowInvert.Native/CaptureHelper.cs
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace WindowInvert.Native;

internal static class CaptureHelper
{
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
    }

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = typeof(GraphicsCaptureItem).GUID;
        var itemPointer = factory.CreateForWindow(hwnd, ref iid);
        return GraphicsCaptureItem.FromAbi(itemPointer);
    }
}
```

```csharp
// src/WindowInvert.Native/Direct3D11Helper.cs
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowInvert.Native;

internal static class Direct3D11Helper
{
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static IDirect3DDevice CreateDirect3DDeviceFromDXGIDevice(IDXGIDevice dxgiDevice)
    {
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var devicePointer);
        return IDirect3DDevice.FromAbi(devicePointer);
    }

    public static ID3D11Texture2D CreateD3D11Texture2DFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        var texturePointer = access.GetInterface(ref iid);
        return new ID3D11Texture2D(texturePointer);
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/WindowInvert.Native/WindowInvert.Native.csproj`
Expected: build succeeds. (This step is the primary check available before live testing - WinRT/COM interop signatures are easy to get subtly wrong, and a clean build here catches type-level mistakes before Step 4's manual run.)

- [ ] **Step 4: Manually verify frames actually arrive**

Temporarily wire a one-line trace in `TrayApplicationContext`'s `ToggleInvert` (only for this verification - remove it again in Step 5 once confirmed): when a window becomes inverted, `new CaptureEngine()` targeting `hwnd`, subscribe `FrameArrived` to `Debug.WriteLine($"frame {texture.Description.Width}x{texture.Description.Height}")`, call `Start(hwnd)`; on un-invert call `Stop()`.

Run the app under a debugger (or with DebugView attached), toggle invert on a window.
Expected: a steady stream of `frame WxH` trace lines matching the window's size, updating as the window is resized.

- [ ] **Step 5: Remove the temporary trace wiring**

Revert the trace-only changes from Step 4 - `CaptureEngine` itself stays; only the throwaway `Debug.WriteLine` wiring in `TrayApplicationContext` is removed, since Task 11 wires `CaptureEngine` in for real.

- [ ] **Step 6: Commit**

```bash
git add src/WindowInvert.Native/CaptureEngine.cs src/WindowInvert.Native/CaptureHelper.cs src/WindowInvert.Native/Direct3D11Helper.cs src/WindowInvert.Native/WindowInvert.Native.csproj
git commit -m "Add Windows.Graphics.Capture engine producing per-window D3D11 frames"
```

---

### Task 11: Invert render pipeline replacing the placeholder overlay fill

**Files:**
- Modify: `src/WindowInvert.Native/WindowInvert.Native.csproj`
- Create: `src/WindowInvert.Native/InvertRenderer.cs`
- Modify: `src/WindowInvert.App/InvertOverlayWindow.cs`
- Modify: `src/WindowInvert.App/TrayApplicationContext.cs`

**Interfaces:**
- Consumes: `CaptureEngine` (Task 10).
- Produces: `InvertRenderer` with `void AttachToOverlay(nint overlayHwnd, CaptureEngine engine)`, `void Dispose()`.

- [ ] **Step 1: Add the Direct2D/DirectComposition package**

```bash
cd "C:\Users\ringo\git\accessibility-selective-window-contrast"
dotnet add src/WindowInvert.Native/WindowInvert.Native.csproj package Vortice.Direct2D1
dotnet add src/WindowInvert.Native/WindowInvert.Native.csproj package Vortice.DirectComposition
```

- [ ] **Step 2: Implement `InvertRenderer`**

For each captured D3D11 frame, wrap it as a D2D bitmap, run it through a `ColorMatrixEffect` configured to negate RGB (and pass alpha through unchanged), and draw it into a DirectComposition visual bound to the overlay window - this is the seam described in the spec as where a future smart-invert pass would slot in, replacing only the effect graph below, not the capture or window-tracking code around it.

```csharp
// src/WindowInvert.Native/InvertRenderer.cs
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WindowInvert.Native;

public sealed class InvertRenderer : IDisposable
{
    // Negates RGB, passes alpha through unchanged:
    // R' = -R + 1, G' = -G + 1, B' = -B + 1, A' = A
    private static readonly Matrix5x4 InvertMatrix = new(
        -1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        0, 0, 0, 1,
        1, 1, 1, 0);

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private IDCompositionDevice? _compDevice;
    private IDCompositionTarget? _compTarget;
    private IDCompositionVisual? _compVisual;
    private IDXGISwapChain1? _swapChain;
    private ColorMatrixEffect? _invertEffect;
    private CaptureEngine? _engine;

    public void AttachToOverlay(nint overlayHwnd, CaptureEngine engine)
    {
        Dispose();
        _engine = engine;

        // Must reuse the exact device CaptureEngine captured frames on -
        // see the comment on CaptureEngine.Device. Call engine.Start(hwnd)
        // before AttachToOverlay so this is populated.
        _d3dDevice = engine.Device ?? throw new InvalidOperationException(
            "CaptureEngine must be started (engine.Start(hwnd)) before AttachToOverlay.");
        _d3dContext = _d3dDevice.ImmediateContext;

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice, new ID2D1CreationProperties());
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        _invertEffect = new ColorMatrixEffect(_d2dContext) { Matrix = InvertMatrix };

        DComposition.DCompositionCreateDevice(dxgiDevice, out _compDevice);
        _compTarget = _compDevice!.CreateTargetForHwnd(overlayHwnd, topmost: true);
        _compVisual = _compDevice.CreateVisual();
        _compTarget.SetRoot(_compVisual);

        using var dxgiFactory = dxgiDevice.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = 0,
            Height = 0,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            SampleDescription = new SampleDescription(1, 0),
        };
        _swapChain = dxgiFactory.CreateSwapChainForComposition(_d3dDevice, desc);
        _compVisual.SetContent(_swapChain);
        _compDevice.Commit();

        engine.FrameArrived += OnFrameArrived;
    }

    private void OnFrameArrived(ID3D11Texture2D frame)
    {
        using (frame)
        using var backBuffer = _swapChain!.GetBuffer<IDXGISurface>(0);
        using var d2dBitmap = _d2dContext!.CreateBitmapFromDxgiSurface(backBuffer, new BitmapProperties1
        {
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
            PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
        });

        using var frameSurface = frame.QueryInterface<IDXGISurface>();
        using var sourceBitmap = _d2dContext.CreateBitmapFromDxgiSurface(frameSurface, new BitmapProperties1
        {
            BitmapOptions = BitmapOptions.None,
            PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
        });

        _invertEffect!.SetInput(0, sourceBitmap, true);

        _d2dContext.Target = d2dBitmap;
        _d2dContext.BeginDraw();
        _d2dContext.Clear(null);
        _d2dContext.DrawImage(_invertEffect);
        _d2dContext.EndDraw();

        _swapChain.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        if (_engine is not null)
        {
            _engine.FrameArrived -= OnFrameArrived;
            _engine = null;
        }

        _invertEffect?.Dispose();
        _swapChain?.Dispose();
        _compVisual?.Dispose();
        _compTarget?.Dispose();
        _compDevice?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        // _d3dDevice is owned by CaptureEngine (shared, not created here) -
        // do not dispose it; CaptureEngine.Stop()/Dispose() owns its lifetime.

        _invertEffect = null;
        _swapChain = null;
        _compVisual = null;
        _compTarget = null;
        _compDevice = null;
        _d2dContext = null;
        _d2dDevice = null;
        _d3dDevice = null;
    }
}
```

- [ ] **Step 3: Remove `InvertOverlayWindow`'s placeholder fill and attach the real pipeline**

In `src/WindowInvert.App/InvertOverlayWindow.cs`, remove the `SetLayeredWindowAttributes(...)` call from the constructor (DirectComposition owns presentation on this HWND now - the layered/transparent styles stay for click-through, but the manual alpha fill is no longer needed) and add:

```csharp
// Add fields and a constructor parameter
private readonly Native.CaptureEngine _captureEngine = new();
private readonly Native.InvertRenderer _renderer = new();

// At the end of the constructor, after CreateHandle(cp):
_captureEngine.Start(sourceHwnd);
_renderer.AttachToOverlay(Handle, _captureEngine);
```

This requires threading the original source window's `nint sourceHwnd` into `InvertOverlayWindow`'s constructor (it already receives the source's `WindowRect`; add `nint sourceHwnd` as a second parameter) and updating its one call site, in `TrayApplicationContext.ToggleInvert` (`new InvertOverlayWindow(currentRect, hwnd)`) - Task 8 already consolidated overlay creation into that single method, so no other call site exists.

Update `Destroy()` to also dispose the new fields:

```csharp
public void Destroy()
{
    _renderer.Dispose();
    _captureEngine.Dispose();
    DestroyHandle();
}
```

- [ ] **Step 4: Manually verify real inversion**

Run: `dotnet run --project src/WindowInvert.App/WindowInvert.App.csproj`

Open Notepad, type some black-on-white text.
Toggle invert on Notepad (via tray menu, title-bar button, or click-to-pick).
Expected: the overlay now shows Notepad's actual content with colors negated (white background becomes black, black text becomes white), not a flat placeholder color.
Type more text / scroll.
Expected: the overlay updates live to match.
Move/resize/minimize/restore.
Expected: same tracking behavior as Task 7, now with real content.
Click through the overlay (type into Notepad while it's inverted).
Expected: still works - click-through is unaffected by the renderer change.

- [ ] **Step 5: Commit**

```bash
git add src/WindowInvert.Native/InvertRenderer.cs src/WindowInvert.Native/WindowInvert.Native.csproj src/WindowInvert.App/InvertOverlayWindow.cs src/WindowInvert.App/TrayApplicationContext.cs
git commit -m "Replace placeholder overlay fill with live captured-and-inverted rendering"
```

---

### Task 12: Startup registration and self-contained packaging

**Files:**
- Create: `src/WindowInvert.App/StartupRegistration.cs`
- Modify: `src/WindowInvert.App/TrayApplicationContext.cs`

**Interfaces:**
- Produces: `StartupRegistration` (static) with `bool IsEnabled { get; }`, `void Enable()`, `void Disable()`.

- [ ] **Step 1: Implement `StartupRegistration`**

```csharp
// src/WindowInvert.App/StartupRegistration.cs
using Microsoft.Win32;

namespace WindowInvert.App;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowInvert";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, Environment.ProcessPath ?? Application.ExecutablePath);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

- [ ] **Step 2: Add a "Start with Windows" checkable tray menu item**

```csharp
// In the TrayApplicationContext constructor, before "Pick a window...":
var startupItem = new ToolStripMenuItem("Start with Windows")
{
    Checked = StartupRegistration.IsEnabled,
    CheckOnClick = false,
};
startupItem.Click += (_, _) =>
{
    if (startupItem.Checked)
    {
        StartupRegistration.Disable();
    }
    else
    {
        StartupRegistration.Enable();
    }
    startupItem.Checked = StartupRegistration.IsEnabled;
};
_menu.Items.Add(startupItem);
```

- [ ] **Step 3: Manually verify the Registry Run key**

Run: `dotnet run --project src/WindowInvert.App/WindowInvert.App.csproj`

From the tray menu, click "Start with Windows".

Run: `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v WindowInvert`
Expected: a value pointing at the running executable's path.

Click "Start with Windows" again to disable it.

Run: `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v WindowInvert`
Expected: `ERROR: The system was unable to find the specified registry key or value.`

- [ ] **Step 4: Publish a self-contained executable**

```bash
cd "C:\Users\ringo\git\accessibility-selective-window-contrast"
dotnet publish src/WindowInvert.App/WindowInvert.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/win-x64
```

Expected: `dist/win-x64/WindowInvert.App.exe` exists and runs standalone (double-click it, or `./dist/win-x64/WindowInvert.App.exe` from a terminal) without requiring a separately installed .NET runtime.

- [ ] **Step 5: Commit**

```bash
git add src/WindowInvert.App/StartupRegistration.cs src/WindowInvert.App/TrayApplicationContext.cs
git commit -m "Add start-with-Windows registration and self-contained publish"
```
