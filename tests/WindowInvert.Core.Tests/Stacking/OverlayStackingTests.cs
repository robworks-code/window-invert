using WindowInvert.Core.Stacking;
using Xunit;

namespace WindowInvert.Core.Tests.Stacking;

public class OverlayStackingTests
{
    private const nint Anchor = 1000;
    private const nint Overlay = 2000;
    private const nint Button = 3000;

    /// <summary>
    /// Replays the planned moves against a z-order written top-first, so a test can
    /// assert the resulting order rather than the moves that produced it. Mirrors
    /// what <c>SetWindowPos</c> does: remove the window, then re-insert it directly
    /// below <c>PlaceBelow</c> (or at the top for <see cref="OverlayStacking.Top"/>).
    /// </summary>
    private static List<nint> Apply(IEnumerable<StackPlacement> moves, params nint[] initialTopFirst)
    {
        var order = initialTopFirst.ToList();

        foreach (var move in moves)
        {
            Assert.NotEqual(move.Hwnd, move.PlaceBelow);
            order.Remove(move.Hwnd);

            var index = move.PlaceBelow == OverlayStacking.Top
                ? 0
                : order.IndexOf(move.PlaceBelow) + 1;

            Assert.InRange(index, 0, order.Count);
            order.Insert(index, move.Hwnd);
        }

        return order;
    }

    [Fact]
    public void PlanRestack_OverlayAndButton_EndUpDirectlyAboveTheSourceInOrder()
    {
        const nint source = 4000;

        var order = Apply(
            OverlayStacking.PlanRestack(Anchor, Overlay, Button),
            Button, Anchor, source, Overlay);

        Assert.Equal(new nint[] { Anchor, Button, Overlay, source }, order);
    }

    [Fact]
    public void PlanRestack_AnchorIsTheOverlayItself_StillConverges()
    {
        // The steady state after a previous restack: the overlay is already the
        // window directly above the source. Naive code would insert the button below
        // the overlay and stop, leaving the button under the overlay - the exact bug
        // where clicking the toggle makes it vanish under the thing it just created.
        const nint source = 4000;

        var order = Apply(
            OverlayStacking.PlanRestack(Overlay, Overlay, Button),
            Button, Overlay, source);

        Assert.Equal(new nint[] { Button, Overlay, source }, order);
    }

    [Fact]
    public void PlanRestack_AnchorIsTheButtonItself_EmitsNoSelfInsert()
    {
        const nint source = 4000;

        var moves = OverlayStacking.PlanRestack(Button, Overlay, Button);

        Assert.DoesNotContain(moves, m => m.Hwnd == m.PlaceBelow);
        Assert.Equal(new nint[] { Button, Overlay, source }, Apply(moves, Button, Overlay, source));
    }

    [Fact]
    public void PlanRestack_SourceIsAlreadyTopOfZOrder_UsesTop()
    {
        const nint source = 4000;

        var order = Apply(
            OverlayStacking.PlanRestack(OverlayStacking.Top, Overlay, Button),
            source, Overlay, Button);

        Assert.Equal(new nint[] { Button, Overlay, source }, order);
    }

    [Fact]
    public void PlanRestack_ButtonOnly_PlacesItDirectlyBelowTheAnchor()
    {
        const nint source = 4000;

        var moves = OverlayStacking.PlanRestack(Anchor, overlay: 0, button: Button);

        Assert.Equal(new[] { new StackPlacement(Button, Anchor) }, moves);
        Assert.Equal(
            new nint[] { Anchor, Button, source },
            Apply(moves, Anchor, source, Button));
    }

    [Fact]
    public void PlanRestack_OverlayOnly_PlacesItDirectlyBelowTheAnchor()
    {
        const nint source = 4000;

        var moves = OverlayStacking.PlanRestack(Anchor, Overlay, button: 0);

        Assert.Equal(new[] { new StackPlacement(Overlay, Anchor) }, moves);
        Assert.Equal(
            new nint[] { Anchor, Overlay, source },
            Apply(moves, Anchor, source, Overlay));
    }

    [Fact]
    public void PlanRestack_NothingToStack_PlansNothing()
    {
        Assert.Empty(OverlayStacking.PlanRestack(Anchor, overlay: 0, button: 0));
    }
}
