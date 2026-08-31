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
