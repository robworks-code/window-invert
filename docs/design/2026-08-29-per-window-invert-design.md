# Per-Window Color Invert Tool - Design

## Problem

Most apps now support system or app-level dark mode, and for the rare
holdout, briefly navigating a light-mode window is tolerable. But some
apps are used regularly and have no dark mode at all, making
light-on-white text painful to use for extended periods. Flipping the
*entire* Windows display to high contrast / inverted colors is too
blunt an instrument - it breaks every other app's normal appearance
for the sake of one.

## Goal

A Windows 11 (Windows 10 support added later if feasible) tool that
applies a live color inversion to a single, user-chosen window, while
leaving the rest of the screen untouched. The inverted window keeps
behaving like a normal window - it can be moved, resized,
minimized/restored, and interacted with normally - the inversion is
purely visual.

## Non-goals (v1)

- "Smart" invert that spares images/photos from inversion. The v1
  invert is a plain full-RGB negation. The rendering pipeline
  (below) is deliberately structured so this can be added later
  without a rewrite, but it is not built now.
- True injection into another app's taskbar jump-list/context menu.
  Windows does not expose a public API for this. The taskbar-adjacent
  need is instead met by a floating per-window title-bar button (see
  UI Surfaces).
- Per-monitor / virtual-desktop correctness beyond "don't crash, hide
  the overlay when we can't place it correctly."
- Elevated-window support. Capturing a window running at a higher
  integrity level (e.g. Task Manager, an elevated installer) from a
  non-elevated process will fail; this is a real OS restriction, not
  something v1 works around.

## Requirements Recap (from brainstorming)

- Trigger via: click-to-select-a-window picker, a tray icon window
  list, and a floating per-window title-bar toggle button.
- Runs as an always-on system tray app (optionally at login).
- Inversion live-tracks the window (move/resize/minimize/restore).
- Any number of windows can be inverted independently and
  simultaneously.
- Windows 11 first; Windows 10 support is a stretch goal, not a
  blocker.
- Plain RGB invert for v1; architecture should not preclude a future
  smart invert.
- Built in C# / .NET 8.

## Architecture

A single always-on tray application (no main window), composed of six
pieces:

### 1. Window Registry / Tracker

Uses `SetWinEventHook` to maintain a live list of top-level windows
(HWND, title, owning process, icon) and to fire on window move,
resize, minimize, restore, close, and foreground change. Every other
component reacts to these events rather than polling.

### 2. Capture Engine

For each window the user has toggled "inverted," opens a
`Windows.Graphics.Capture` session scoped to that specific HWND
(window capture, not a screen-region capture) so it tracks the
window's actual content, including when partially covered by another
window. Frames arrive as `ID3D11Texture2D` textures via a
`Direct3D11CaptureFramePool`.

### 3. Invert Pipeline

Each captured frame is wrapped as a Direct2D bitmap and passed through
a `D2D1ColorMatrixEffect` configured to negate RGB, then composited
via DirectComposition. This is the seam where a future smart-invert
pass (per-region logic instead of one uniform matrix) would slot in,
without needing to touch capture or window-tracking code.

### 4. Overlay Window Manager

One borderless, always-on-top, layered overlay HWND per inverted
window, created with `WS_EX_LAYERED | WS_EX_TRANSPARENT |
WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, positioned and sized to exactly
match its source window's screen rectangle. `WS_EX_TRANSPARENT` makes
it click-through, so mouse and keyboard input pass straight to the
real window underneath - the overlay is a pure visual layer, never an
input target.

The manager keeps each overlay in lockstep with Window Registry
events: move/resize -> reposition, minimize -> hide, restore -> show,
close -> destroy overlay and tear down its capture session.

### 5. Multi-Window State

A simple map of `HWND -> { CaptureSession, OverlayWindow }`. Any
number of windows can be independently inverted at once; each has its
own capture/render loop, so inverted windows animate/update
independently and don't block each other.

### 6. UI Surfaces

- **Tray icon menu**: a checkable list of current top-level windows
  (toggle invert per entry), a "pick a window..." action, Settings,
  Exit.
- **Click-to-pick mode**: cursor becomes a crosshair; the next window
  clicked (resolved via `WindowFromPoint` + `GetAncestor` to the
  top-level owner) toggles invert for that window.
- **Floating title-bar button**: a small always-on-top button rendered
  near each trackable window's own minimize/maximize/close controls,
  offset so it doesn't collide with them, toggling invert for that
  window with one click. Reuses the same tracking/positioning
  mechanism as the invert overlay itself (component 4), just drawing a
  button instead of inverted pixels. This is the practical substitute
  for taskbar-menu integration, which Windows does not allow
  third-party apps to provide.

## Data Flow

1. Window Registry detects a new/changed/closed top-level window and
   raises an event.
2. If that window is in the "inverted" set, the Overlay Window Manager
   repositions, shows, hides, or destroys its overlay accordingly.
3. Independently, each active Capture Engine session's frame-arrived
   callback feeds the Invert Pipeline, which presents the inverted
   frame to its overlay's DirectComposition surface at the next vsync.
4. The title-bar button overlay tracks the same Window Registry events
   as step 2, positioning itself relative to the source window's rect.

## Known Edge Cases (flagged, not solved in v1)

- **Minimized or off-current-virtual-desktop windows**: overlay is
  simply hidden. No crash, but a window on another virtual desktop may
  briefly show a stale overlay position when desktops are switched.
  A documented fix exists (`IVirtualDesktopManager::
  IsWindowOnCurrentVirtualDesktop`) but is a future refinement.
- **Elevated target windows**: capture will fail from our non-elevated
  process; out of scope for v1 (see Non-goals).
- **Per-monitor DPI differences**: overlay positioning math must
  account for per-monitor scaling; called out here so it isn't
  discovered late.

## Testing Approach

- The Window Registry's event-to-state logic and the HWND state map
  (component 5) are ordinary unit-testable logic.
- Overlay positioning/geometry math (rect tracking, title-bar button
  offset placement) is unit-testable independent of any live window.
- The capture/render pipeline itself depends on live GPU/compositor
  behavior that is impractical to meaningfully mock; it will be
  verified by running the app against real target windows, not
  claimed as covered by automated tests.

## Packaging

Single self-contained .NET 8 executable, run from the tray; "start
with Windows" via a Registry Run key or a Scheduled Task (decision
deferred to implementation).

## Windows 10 Compatibility

Target Windows 11 first. `Windows.Graphics.Capture` window-capture
mode and the Magnification-free overlay/click-through mechanism are
both available on Windows 10 (1903+), so Windows 10 support is
expected to be low-cost once v1 works on 11 - confirmed during
implementation rather than assumed here.
