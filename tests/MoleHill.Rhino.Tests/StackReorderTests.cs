using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class StackReorderTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static List<Guid> Items() => new() { A, B, C, D };

    private static List<Guid> Move(Guid source, Guid anchor, bool reversed)
    {
        var items = Items();
        StackReorder.Move(items, id => id, source, anchor, reversed);
        return items;
    }

    // Top-to-bottom stacks (zones, analyses, annotations): the target above a card inserts before it.
    [Fact]
    public void Move_ListOrderTargetAboveCard_InsertsBeforeIt() =>
        Assert.Equal(new[] { A, D, B, C }, Move(D, B, reversed: false));

    [Fact]
    public void Move_ListOrderTail_AppendsAtTheEnd() =>
        Assert.Equal(new[] { B, C, D, A }, Move(A, Guid.Empty, reversed: false));

    // Bottom-to-top stacks (modifiers, item 0 drawn at the bottom): the target above a card inserts after it.
    [Fact]
    public void Move_ReversedTargetAboveCard_InsertsAfterIt() =>
        Assert.Equal(new[] { A, B, D, C }, Move(D, B, reversed: true));

    [Fact]
    public void Move_ReversedTail_MovesToTheBottomCard() =>
        Assert.Equal(new[] { C, A, B, D }, Move(C, Guid.Empty, reversed: true));

    [Fact]
    public void Move_ReversedTargetAboveLowerCard_MovesItemDown() =>
        Assert.Equal(new[] { A, D, B, C }, Move(D, A, reversed: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_OntoItsOwnTarget_LeavesOrderUnchanged(bool reversed) =>
        Assert.Equal(Items(), Move(B, B, reversed));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_UnknownSource_LeavesOrderUnchanged(bool reversed) =>
        Assert.Equal(Items(), Move(Guid.NewGuid(), B, reversed));

    [Fact]
    public void Move_PinnedBase_NothingLandsBelowIt_AndItDoesNotMove()
    {
        // Modifiers are drawn bottom-up; A is the pinned base Triangulate at index 0.
        var items = Items();
        StackReorder.Move(items, id => id, C, Guid.Empty, displayReversed: true, minimumIndex: 1);
        Assert.Equal(new[] { A, C, B, D }, items);

        StackReorder.Move(items, id => id, A, D, displayReversed: true, minimumIndex: 1);
        Assert.Equal(new[] { A, C, B, D }, items);
    }
}
