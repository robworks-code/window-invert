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

        // For a window this wide, the preferred position is already inside the
        // source: flush to the top edge, and offset left from the right edge so it
        // does not collide with the native minimize/maximize/close buttons (~140px
        // reserved). Narrow windows are a different matter - see below.
        Assert.Equal(800 - 140 - 24, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(24, result.Width);
        Assert.Equal(24, result.Height);
        Assert.InRange(result.X, source.X, source.X + source.Width - 24);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_NarrowSource_StaysWithinTheWindow()
    {
        // 120px wide: the preferred position is 120 - 140 - 24 = -44px from the
        // window's own left edge, so without a clamp the toggle button for this
        // window floats 44px to the LEFT of it, over whatever unrelated window
        // happens to be there.
        var source = new WindowRect(500, 200, 120, 400);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, buttonSize: 24);

        Assert.Equal(500, result.X);
        Assert.Equal(200, result.Y);
        Assert.InRange(result.X, source.X, source.X + source.Width - 24);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_SourceNarrowerThanTheButton_PinsToTheLeftEdge()
    {
        var source = new WindowRect(50, 60, 10, 400);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, buttonSize: 24);

        // Nowhere for it to fit, so it pins to the window's left edge and overhangs
        // to the right rather than drifting off to the left of the window.
        Assert.Equal(50, result.X);
        Assert.Equal(60, result.Y);
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
