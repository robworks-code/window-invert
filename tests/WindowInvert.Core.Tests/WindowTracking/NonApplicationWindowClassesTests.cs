using WindowInvert.Core.WindowTracking;
using Xunit;

namespace WindowInvert.Core.Tests.WindowTracking;

public class NonApplicationWindowClassesTests
{
    /// <summary>
    /// Every class here was observed passing the rest of the tracking predicate
    /// on a live desktop, except <c>WorkerW</c>, which is Progman's documented
    /// sibling and appears only in some desktop states.
    /// </summary>
    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("MagUIClass")]
    public void ShellAndAccessibilityWindows_AreNotApplicationWindows(string className)
    {
        Assert.True(NonApplicationWindowClasses.IsNonApplicationWindow(className));
    }

    /// <summary>
    /// The other side of the same enumeration: these are the real application
    /// windows that were passing the predicate alongside the shell ones, and
    /// they are the whole point of the app. A change to the list that catches
    /// any of them is a regression, not a tightening.
    /// </summary>
    [Theory]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("ApplicationFrameWindow")]
    [InlineData("Notepad")]
    public void ApplicationWindows_AreKept(string className)
    {
        Assert.False(NonApplicationWindowClasses.IsNonApplicationWindow(className));
    }

    /// <summary>
    /// Casing must not decide this. An ordinal comparison would fail silently -
    /// the taskbar would simply become invertible again with nothing to say why.
    /// </summary>
    [Theory]
    [InlineData("progman")]
    [InlineData("SHELL_TRAYWND")]
    [InlineData("magUIclass")]
    public void ClassNames_AreMatchedIgnoringCase(string className)
    {
        Assert.True(NonApplicationWindowClasses.IsNonApplicationWindow(className));
    }

    /// <summary>
    /// Fails open, matching the cloak check. A window whose class name cannot be
    /// read stays eligible: wrongly keeping one costs a stray toggle button,
    /// wrongly rejecting one makes a window impossible to invert at all.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnreadableClassName_LeavesTheWindowEligible(string? className)
    {
        Assert.False(NonApplicationWindowClasses.IsNonApplicationWindow(className));
    }
}
