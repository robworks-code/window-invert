namespace WindowInvert.Core.WindowTracking;

/// <summary>
/// Window classes that belong to the desktop shell or to the accessibility
/// stack rather than to an application, and which this app must never treat as
/// something to invert.
/// <para>
/// These are invisible to every other part of the tracking predicate: each one
/// is <c>WS_VISIBLE</c>, unowned, its own <c>GA_ROOT</c>, and not cloaked, so
/// nothing short of the class name distinguishes them from a real application
/// window. Two of them are additionally untitled, which is why the tray menu's
/// title filter appeared to handle this and did not: the click-to-pick path
/// resolves any <i>tracked</i> handle regardless of title, so the taskbar was
/// reachable there even while it was absent from the menu.
/// </para>
/// <para>
/// Kept here, as a pure function over a name, rather than beside the P/Invoke
/// that reads the name: this is the part with a decision in it, and it is the
/// part worth testing.
/// </para>
/// </summary>
public static class NonApplicationWindowClasses
{
    /// <summary>
    /// Compared case-insensitively on purpose. <c>GetClassName</c> returns the
    /// name a class was registered under, so an exact match is the precise
    /// contract - but if any Windows build ever registers one of these with
    /// different casing, an ordinal comparison would stop excluding it silently
    /// and the taskbar would quietly become invertible again. There is no
    /// application window that differs from these names only by case, so
    /// ignoring case costs nothing and removes that failure mode.
    /// </summary>
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        // The desktop itself ("Program Manager"). Clicking bare wallpaper in
        // pick mode landed here, which inverted the desktop instead of
        // reporting that there was nothing to invert.
        "Progman",

        // The wallpaper host the shell creates alongside Progman. Not observed
        // in the enumeration this list was built from - it appears only in some
        // desktop states - but it is Progman's documented sibling and is the
        // same surface.
        "WorkerW",

        // The taskbar, and its counterpart on additional monitors. Both are
        // untitled, so both are absent from the tray menu and both were
        // reachable by clicking them in pick mode.
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",

        // The tray overflow flyout - the panel behind the taskbar's chevron,
        // where this application's own icon lives when it is not promoted. It
        // is titled ("System tray overflow window"), so it was tracked, given a
        // toggle button, and listed in the menu.
        //
        // Tracking it was not merely untidy, it broke the tray menu. The flyout
        // has to be open for the icon to be right-clicked, so it and the menu
        // are on screen together and trade the foreground between them. Every
        // one of those trades ran a restack against the flyout's button, on top
        // of the foreground change that was already dismissing the menu.
        // Windows 11 uses the XamlIsland class; Windows 10's equivalent is
        // NotifyIconOverflowWindow.
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",

        // Windows Magnifier's own window. This one is titled, so it was offered
        // in the tray menu as an ordinary invertible window. Putting a capture
        // session and an inversion overlay on top of the magnifier is a way to
        // disrupt the assistive tool this application exists to work alongside,
        // and inverting its controls serves no purpose.
        "MagUIClass",
    };

    /// <summary>
    /// Whether <paramref name="className"/> names a shell or accessibility
    /// surface rather than an application window. An empty or missing name is
    /// not one of these: the caller cannot read a class name for a window that
    /// has already gone, and a window is better wrongly kept than wrongly
    /// rejected.
    /// </summary>
    public static bool IsNonApplicationWindow(string? className) =>
        !string.IsNullOrEmpty(className) && Classes.Contains(className);
}
