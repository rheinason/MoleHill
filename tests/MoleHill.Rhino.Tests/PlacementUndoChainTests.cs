using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

// Event sequences below are the ones Rhino 8.35 raised in the 2026-11-02 live run (Move, live
// placement, _Undo, _Redo); see PlacementUndoChain for the observed rules they encode.
public class PlacementUndoChainTests
{
    private static void Command(PlacementUndoChain chain, uint serial, bool empty = false)
    {
        chain.RecordingBegan(serial);
        chain.RecordingEnded(serial);
        if (empty)
            chain.Purged(serial);
    }

    private static bool Undo(PlacementUndoChain chain, uint undoCommand, uint undone)
    {
        chain.RecordingBegan(undoCommand);
        chain.ReplayBegan();
        bool cascade = chain.UndoEnded(undone);
        chain.Purged(undone);
        chain.RecordingEnded(undoCommand);
        return cascade;
    }

    private static bool Redo(PlacementUndoChain chain, uint redoCommand, uint undoCommand)
    {
        chain.RecordingBegan(redoCommand);
        chain.ReplayBegan();
        bool cascade = chain.RedoEnded(undoCommand);
        chain.Purged(undoCommand);
        chain.RecordingEnded(redoCommand);
        return cascade;
    }

    /// <summary>Move (4), placement (5) paired beneath it.</summary>
    private static PlacementUndoChain MoveThenPlace()
    {
        var chain = new PlacementUndoChain();
        Command(chain, 3);
        Command(chain, 4);
        uint trigger = chain.Top;
        Command(chain, 5);
        Assert.True(chain.Pair(5, trigger));
        return chain;
    }

    [Fact]
    public void Pair_PlacementDirectlyAboveTrigger_IsAccepted()
    {
        PlacementUndoChain chain = MoveThenPlace();
        Assert.Equal(1, chain.PairCount);
        Assert.Equal(5u, chain.Top);
    }

    [Fact]
    public void Pair_RecordBetweenTriggerAndPlacement_IsRejected()
    {
        var chain = new PlacementUndoChain();
        Command(chain, 10);
        Command(chain, 11); // unrelated command
        Command(chain, 12);

        Assert.False(chain.Pair(12, 10));
    }

    [Fact]
    public void Pair_EmptyCommandBetween_IsAccepted()
    {
        var chain = new PlacementUndoChain();
        Command(chain, 10);
        Command(chain, 11, empty: true); // a read-only script: begun, ended, purged
        Command(chain, 12);

        Assert.True(chain.Pair(12, 10));
    }

    [Fact]
    public void Undo_Placement_CascadesToTriggerOnly()
    {
        PlacementUndoChain chain = MoveThenPlace();

        Assert.True(Undo(chain, 6, 5));   // user's _Undo of the placement asks for one more
        Assert.Equal(4u, chain.Top);      // _Undo's own record is not an undo-list entry
        Assert.False(Undo(chain, 7, 4));  // the cascade undoes the Move and stops
        Assert.Equal(3u, chain.Top);
    }

    [Fact]
    public void Redo_AfterCascadedUndo_CascadesToPlacementAndRestoresStack()
    {
        PlacementUndoChain chain = MoveThenPlace();
        Undo(chain, 6, 5);
        Undo(chain, 7, 4);
        Command(chain, 8, empty: true);

        Assert.True(Redo(chain, 9, 7));   // Redo names the cascade's _Undo record, which undid the Move
        Assert.Equal(9u, chain.Top);      // and the Move is undoable again under the _Redo record
        Assert.False(Redo(chain, 10, 6)); // the cascaded Redo restores the placement
        Assert.Equal(10u, chain.Top);

        // The pair follows both changes to their new serials, for as many round trips as asked.
        Assert.True(Undo(chain, 11, 10));
        Assert.False(Undo(chain, 12, 9));
        Assert.True(Redo(chain, 13, 12));
        Assert.False(Redo(chain, 14, 11));
        Assert.True(Undo(chain, 15, 14));
    }

    [Fact]
    public void Pair_TriggerRedoneUnderNewSerial_PairsWithThatSerial()
    {
        var chain = new PlacementUndoChain();
        Command(chain, 3);
        Command(chain, 4);                // Move
        Undo(chain, 5, 4);
        Redo(chain, 6, 5);                // the Move is now serial 6
        Command(chain, 7);                // a placement after the redone Move

        Assert.True(chain.Pair(7, 6));
        Assert.True(Undo(chain, 8, 7));
        Assert.False(Undo(chain, 9, 6));
        Assert.True(Redo(chain, 10, 9));
    }

    [Fact]
    public void Undo_UnpairedRecord_RequestsNothing()
    {
        var chain = new PlacementUndoChain();
        Command(chain, 10);
        Command(chain, 11);

        Assert.False(Undo(chain, 12, 11));
        Assert.Equal(10u, chain.Top);
        Assert.False(Redo(chain, 13, 12));
    }

    [Fact]
    public void Undo_ChainedPlacements_CascadeOneLinkAtATime()
    {
        PlacementUndoChain chain = MoveThenPlace();
        Command(chain, 6);
        Assert.True(chain.Pair(6, 5));

        Assert.True(Undo(chain, 7, 6));
        Assert.True(Undo(chain, 8, 5));
        Assert.False(Undo(chain, 9, 4));
        Assert.True(Redo(chain, 10, 9));
        Assert.True(Redo(chain, 11, 8));
        Assert.False(Redo(chain, 12, 7));
        Assert.Equal(12u, chain.Top);
    }

    [Fact]
    public void Redo_UnknownSerial_RequestsNothing()
    {
        PlacementUndoChain chain = MoveThenPlace();
        Assert.False(chain.RedoEnded(99));
        Assert.Equal(5u, chain.Top);
    }
}
