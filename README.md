# Window Invert

Applies a live color inversion to a single chosen window on
Windows 11, and leaves the rest of the screen alone.

Most applications have a dark mode now, and for the occasional
holdout, passing through a light window is tolerable. A few
apps that get used all day have no dark mode at all, and
light-on-white text is painful to read for hours. Flipping the
whole display to inverted colors or high contrast fixes that
one window by breaking every other one.

This inverts just the window you pick. It keeps behaving like a
normal window - move it, resize it, minimize and restore it,
type in it. Only the pixels change.

Any number of windows can be inverted at once, independently.

## Running it

It lives in the notification area, at the right-hand end of the
taskbar. There is no main window.

Three ways to invert a window:

- Right-click the tray icon and open the **Windows** submenu.
  Every window it can see is listed; click one to toggle it.
- Right-click the tray icon and choose **Pick a window...**,
  then click the window you want.
- Click the floating toggle button that sits beside a window's
  own caption buttons.

The tray menu also has **Start with Windows**, which registers
the app to launch at login, and **Exit**.

## Limitations

These are real constraints rather than open bugs.

**Elevated windows cannot be inverted.** Task Manager, an
installer running as administrator, anything at a higher
integrity level - a normal-privilege process is not allowed to
capture them. This is an operating system restriction.

**The inversion is a plain RGB negation.** Photographs and
images inside an inverted window come out as negatives. The
rendering pipeline is built so a smarter, content-aware invert
can be added later without tearing up the capture path, but
that is not what this does today.

**Windows 11 is the target.** Windows 10 may work and is not
tested.

**Multiple monitors and virtual desktops** are handled well
enough not to misbehave, and no further than that.

## Building

Requires the .NET 8 SDK.

```
dotnet build WindowInvert.sln
dotnet test WindowInvert.sln
```

A standalone executable that does not need .NET installed:

```
dotnet publish src/WindowInvert.App/WindowInvert.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/win-x64
```

That writes `dist/win-x64/WindowInvert.App.exe`.

## Layout

**WindowInvert.Core** - window tracking, invert state, z-order
and geometry. Targets plain `net8.0` and calls nothing
platform-specific, which is what makes it testable.

**WindowInvert.Native** - the Win32 and DirectX surface:
`SetWinEventHook` listening, `Windows.Graphics.Capture`
sessions, the Direct2D color-matrix invert, DirectComposition,
and DPI.

**WindowInvert.App** - the tray icon, the overlay and toggle
button windows, the click-to-pick overlay, and startup
registration.

Design notes are in `docs/design/`.

## Status

This is the first working version. It builds clean and the
tracking, state and stacking logic is unit tested, but it has
not been through much real use yet.

## License

MIT. See `LICENSE`.
