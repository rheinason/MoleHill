using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

public class SculptUndoStackTests
{
    private static SculptStrokeUndoRecord Record(int marker)
    {
        var record = new SculptStrokeUndoRecord();
        record.TouchedZ.Add((marker, 0.0, 1.0));
        return record;
    }

    [Fact]
    public void Undo_AfterPush_ReturnsMostRecentRecord()
    {
        var stack = new SculptUndoStack();
        stack.Push(Record(1));
        stack.Push(Record(2));

        Assert.Equal(2, stack.Undo()!.TouchedZ[0].VertexIndex);
        Assert.Equal(1, stack.Undo()!.TouchedZ[0].VertexIndex);
        Assert.Null(stack.Undo());
    }

    [Fact]
    public void Redo_AfterUndo_ReturnsSameRecordInOrder()
    {
        var stack = new SculptUndoStack();
        stack.Push(Record(1));
        stack.Push(Record(2));
        stack.Undo();
        stack.Undo();

        Assert.Equal(1, stack.Redo()!.TouchedZ[0].VertexIndex);
        Assert.Equal(2, stack.Redo()!.TouchedZ[0].VertexIndex);
        Assert.Null(stack.Redo());
    }

    [Fact]
    public void Push_AfterUndo_DropsRedoBranch()
    {
        var stack = new SculptUndoStack();
        stack.Push(Record(1));
        stack.Push(Record(2));
        stack.Undo();
        stack.Push(Record(3));

        Assert.False(stack.CanRedo);
        Assert.Equal(3, stack.Undo()!.TouchedZ[0].VertexIndex);
        Assert.Equal(1, stack.Undo()!.TouchedZ[0].VertexIndex);
        Assert.Null(stack.Undo());
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var stack = new SculptUndoStack();
        stack.Push(Record(1));
        stack.Undo();

        stack.Clear();

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }
}
