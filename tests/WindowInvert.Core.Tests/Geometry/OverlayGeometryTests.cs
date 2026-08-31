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
