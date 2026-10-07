using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBuildSchedulingPolicyTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void SelectDue_NothingDue_ReturnsMinusOne()
    {
        var requests = new List<(DateTime, TerrainBuildMode)> { (At(100), TerrainBuildMode.Final) };
        Assert.Equal(-1, TerrainBuildSchedulingPolicy.SelectDue(requests, At(99)));
    }

    [Fact]
    public void SelectDue_Empty_ReturnsMinusOne() =>
        Assert.Equal(-1, TerrainBuildSchedulingPolicy.SelectDue(new List<(DateTime, TerrainBuildMode)>(), At(0)));

    [Fact]
    public void SelectDue_PicksOldestDue()
    {
        var requests = new List<(DateTime, TerrainBuildMode)>
        {
            (At(50), TerrainBuildMode.Final), (At(10), TerrainBuildMode.Final), (At(500), TerrainBuildMode.Final)
        };
        Assert.Equal(1, TerrainBuildSchedulingPolicy.SelectDue(requests, At(100)));
    }

    [Fact]
    public void SelectDue_TieOnDueTime_PreviewBeforeFinal()
    {
        var requests = new List<(DateTime, TerrainBuildMode)>
        {
            (At(10), TerrainBuildMode.Final), (At(10), TerrainBuildMode.Preview)
        };
        Assert.Equal(1, TerrainBuildSchedulingPolicy.SelectDue(requests, At(10)));
    }

    [Fact]
    public void SelectDue_FullTie_KeepsListOrder()
    {
        var requests = new List<(DateTime, TerrainBuildMode)>
        {
            (At(10), TerrainBuildMode.Final), (At(10), TerrainBuildMode.Final)
        };
        Assert.Equal(0, TerrainBuildSchedulingPolicy.SelectDue(requests, At(10)));
    }

    [Theory]
    [InlineData(true, true, false, 0, 1, (int)TerrainDispatchOutcome.DeferForSculpt)]
    [InlineData(false, false, false, 0, 1, (int)TerrainDispatchOutcome.DropTargetGone)]
    [InlineData(false, true, true, 0, 1, (int)TerrainDispatchOutcome.DeferBusy)]
    [InlineData(false, true, false, 5, 5, (int)TerrainDispatchOutcome.DropSkipped)]
    [InlineData(false, true, false, 4, 5, (int)TerrainDispatchOutcome.Dispatch)]
    [InlineData(true, false, true, 9, 1, (int)TerrainDispatchOutcome.DeferForSculpt)] // sculpt is checked first
    public void DecideDispatch_Matrix(bool sculpt, bool exists, bool busy, long skipped, long version, int expected) =>
        Assert.Equal((TerrainDispatchOutcome)expected, TerrainBuildSchedulingPolicy.DecideDispatch(sculpt, exists, busy, skipped, version));

    [Theory]
    [InlineData(1, 2, 3, 3, false, false, true, (int)TerrainCompletionOutcome.DiscardStaleGeneration)]
    [InlineData(2, 2, 3, 4, false, false, true, (int)TerrainCompletionOutcome.SupersededMayPublish)]
    [InlineData(2, 2, 3, 4, true, false, true, (int)TerrainCompletionOutcome.SupersededDiscard)]
    [InlineData(2, 2, 3, 4, false, true, false, (int)TerrainCompletionOutcome.SupersededDiscard)]
    [InlineData(2, 2, 3, 3, true, false, false, (int)TerrainCompletionOutcome.Cancelled)]
    [InlineData(2, 2, 3, 3, false, true, false, (int)TerrainCompletionOutcome.Failed)]
    [InlineData(2, 2, 3, 3, false, false, false, (int)TerrainCompletionOutcome.Failed)]
    [InlineData(2, 2, 3, 3, false, false, true, (int)TerrainCompletionOutcome.Apply)]
    public void DecideCompletion_Matrix(
        long resultGen, long currentGen, long resultVersion, long requested, bool canceled, bool error, bool build,
        int expected) =>
        Assert.Equal((TerrainCompletionOutcome)expected, TerrainBuildSchedulingPolicy.DecideCompletion(
            resultGen, currentGen, resultVersion, requested, canceled, error, build));

    /// <summary>A minimal model of one terrain's queue, driven only by the policy decisions.</summary>
    private sealed class Model
    {
        public long Requested, Running, Generation;
        public long Skipped => 0;
        public bool Building;
        public DateTime? PendingDue;
        public long PendingVersion;
        public readonly List<string> Log = new();

        public void Edit(DateTime due)
        {
            Requested++;
            PendingDue = due;
            PendingVersion = Requested;
        }

        public bool TryDispatch(DateTime now)
        {
            if (PendingDue is not { } due ||
                TerrainBuildSchedulingPolicy.SelectDue(new List<(DateTime, TerrainBuildMode)> { (due, TerrainBuildMode.Final) }, now) < 0)
                return false;

            var outcome = TerrainBuildSchedulingPolicy.DecideDispatch(false, true, Building, Skipped, PendingVersion);
            if (outcome != TerrainDispatchOutcome.Dispatch)
                return false;

            PendingDue = null;
            Building = true;
            Running = Requested;
            Generation++;
            Log.Add($"dispatch #{Running} gen {Generation}");
            return true;
        }

        public TerrainCompletionOutcome Complete(long version, long generation, bool canceled = false)
        {
            Building = false;
            var outcome = TerrainBuildSchedulingPolicy.DecideCompletion(generation, Generation, version, Requested, canceled, false, !canceled);
            Log.Add($"complete #{version}: {outcome}");
            return outcome;
        }
    }

    [Fact]
    public void Sequence_RapidEditsDuringBuild_RequestIsNotStrandedAfterCancellation()
    {
        var m = new Model();
        m.Edit(At(0));
        Assert.True(m.TryDispatch(At(0)));               // build #1 starts
        m.Edit(At(10));                                  // edit while busy
        Assert.False(m.TryDispatch(At(20)));             // busy: deferred, still pending
        Assert.NotNull(m.PendingDue);

        Assert.Equal(TerrainCompletionOutcome.SupersededDiscard, m.Complete(1, m.Generation, canceled: true));
        Assert.True(m.TryDispatch(At(30)));              // the overdue request is dispatched, not lost
        Assert.Equal(TerrainCompletionOutcome.Apply, m.Complete(2, m.Generation));
    }

    [Fact]
    public void Sequence_SupersededRunToCompletion_MayPublishThenFinalApplies()
    {
        var m = new Model();
        m.Edit(At(0));
        m.TryDispatch(At(0));
        long firstGen = m.Generation;
        m.Edit(At(5));
        Assert.Equal(TerrainCompletionOutcome.SupersededMayPublish, m.Complete(1, firstGen));
        Assert.True(m.TryDispatch(At(6)));
        Assert.Equal(TerrainCompletionOutcome.Apply, m.Complete(2, m.Generation));
    }

    [Fact]
    public void Sequence_ResetWhileBuilding_LateResultIsDiscardedAsStale()
    {
        var m = new Model();
        m.Edit(At(0));
        m.TryDispatch(At(0));
        long oldGen = m.Generation;
        m.Generation++; // a reset/new worker took over
        Assert.Equal(TerrainCompletionOutcome.DiscardStaleGeneration, m.Complete(1, oldGen));
    }

    [Fact]
    public void Sequence_OutOfOrderCompletion_OlderWorkerCannotApply()
    {
        var m = new Model();
        m.Edit(At(0));
        m.TryDispatch(At(0));
        long gen1 = m.Generation;
        m.Building = false;                              // worker 1 retired by a forced rebuild
        m.Edit(At(1));
        m.TryDispatch(At(1));                            // worker 2, generation advanced
        Assert.Equal(TerrainCompletionOutcome.DiscardStaleGeneration, m.Complete(1, gen1));
        Assert.Equal(TerrainCompletionOutcome.Apply, m.Complete(2, m.Generation));
    }
}
