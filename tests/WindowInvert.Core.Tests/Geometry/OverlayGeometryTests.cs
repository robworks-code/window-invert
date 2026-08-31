using WindowInvert.Core.Geometry;
using Xunit;

namespace WindowInvert.Core.Tests.Geometry;

public class OverlayGeometryTests
{
    private const int Dpi100 = 96;
    private const int Dpi150 = 144;
    private const int Dpi200 = 192;

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

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, Dpi100);

        // At 100% the button is 20px and 140px is reserved for the native
        // minimize/maximize/close buttons. Flush to the top edge, inset from the
        // right so it does not collide with them. Narrow windows and scaled
        // displays are covered separately below.
        Assert.Equal(800 - 140 - 20, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(20, result.Width);
        Assert.Equal(20, result.Height);
        Assert.InRange(result.X, source.X, source.X + source.Width - result.Width);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_TracksMovedSource()
    {
        var source = new WindowRect(300, 150, 800, 600);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, Dpi100);

        Assert.Equal(300 + 800 - 140 - 20, result.X);
        Assert.Equal(150, result.Y);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_At150Percent_ScalesBothTheButtonAndTheReservedWidth()
    {
        var source = new WindowRect(0, 0, 800, 600);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, Dpi150);

        // The source rect is in physical pixels and the native caption buttons are
        // drawn at the display's scale, so at 150% they occupy about 210 physical
        // pixels, not 140. Reserving the unscaled figure would put this button on
        // top of them, where a misclick closes the window being read.
        Assert.Equal(30, result.Width);
        Assert.Equal(30, result.Height);
        Assert.Equal(800 - 210 - 30, result.X);

        // The gap between this button's right edge and the window's right edge has
        // to be the full scaled reservation, not the unscaled one.
        Assert.Equal(210, source.X + source.Width - (result.X + result.Width));
    }

    [Fact]
    public void ComputeTitleBarButtonRect_At200Percent_ScalesBothMeasurements()
    {
        var source = new WindowRect(0, 0, 1200, 800);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, Dpi200);

        Assert.Equal(40, result.Width);
        Assert.Equal(1200 - 280 - 40, result.X);
        Assert.Equal(280, source.X + source.Width - (result.X + result.Width));
    }

    [Fact]
    public void ComputeTitleBarButtonRect_NarrowSource_StaysWithinTheWindow()
    {
        // 120px wide: the preferred position is 120 - 140 - 20 = -40px from the
        // window's own left edge, so without a clamp the toggle button for this
        // window floats 40px to the LEFT of it, over whatever unrelated window
        // happens to be there.
        var source = new WindowRect(500, 200, 120, 400);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, Dpi100);

        Assert.Equal(500, result.X);
        Assert.Equal(200, result.Y);
        Assert.InRange(result.X, source.X, source.X + source.Width - result.Width);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_NarrowSourceAt150Percent_StillStaysWithinTheWindow()
    {
        // The clamp has to be applied to the scaled figures, not the baseline ones:
        // 260px is wide enough at 100% (260 - 140 - 20 = 100) but not at 150%
        // (260 - 210 - 30 = 20 ... still positive, so push it narrower).
        var source = new WindowRect(500, 200, 200, 400);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, Dpi150);

        Assert.Equal(30, result.Width);
        Assert.Equal(500, result.X);
        Assert.InRange(result.X, source.X, source.X + source.Width - result.Width);
    }

    [Fact]
    public void ComputeTitleBarButtonRect_SourceNarrowerThanTheButton_PinsToTheLeftEdge()
    {
        var source = new WindowRect(50, 60, 10, 400);

        var result = OverlayGeometry.ComputeTitleBarButtonRect(source, Dpi100);

        // Nowhere for it to fit, so it pins to the window's left edge and overhangs
        // to the right rather than drifting off to the left of the window.
        Assert.Equal(50, result.X);
        Assert.Equal(60, result.Y);
    }

    [Theory]
    [InlineData(Dpi100, 20)]
    [InlineData(120, 25)]
    [InlineData(Dpi150, 30)]
    [InlineData(Dpi200, 40)]
    public void ComputeButtonSize_ScalesFromTheBaseline(int dpi, int expected)
    {
        Assert.Equal(expected, OverlayGeometry.ComputeButtonSize(dpi));
    }

    [Fact]
    public void ScaleForDpi_NonPositiveDpi_FallsBackToTheBaselineValue()
    {
        // A failed DPI query must degrade to the unscaled measurement, never to
        // zero - a zero-sized button is an invisible, unclickable one.
        Assert.Equal(20, OverlayGeometry.ScaleForDpi(20, 0));
        Assert.Equal(20, OverlayGeometry.ScaleForDpi(20, -1));
        Assert.Equal(20, OverlayGeometry.ComputeButtonSize(0));
    }
}
