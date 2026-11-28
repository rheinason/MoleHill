using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class SectionEditTests
{
    private const double Tol = 0.001;

    // A plan path along X from (5, 15): station = x - 5. Heights at stations 0, 6, ..., 36.
    private static readonly double[] Stations = { 0, 6, 12, 18, 24, 30, 36 };
    private static readonly double[] Heights = { 1.55, 2.65, 1.75, 2.95, 2.15, 3.25, 3.35 };

    private static double[] Path(double[] stations, double[] heights) =>
        stations.SelectMany((s, i) => new[] { 5.0 + s, 15.0, heights[i] }).ToArray();

    private static double[] Piece(params (double S, double Z)[] points) => points.SelectMany(p => new[] { p.S, p.Z }).ToArray();

    private static double[] Original(int from = 0, int to = 6) =>
        Piece(Enumerable.Range(from, to - from + 1).Select(i => (Stations[i], Heights[i])).ToArray());

    private static SectionEdit Edit(params double[][] pieces)
    {
        SectionEdit? edit = SectionEdit.Create(Stations, Heights, pieces, Tol, out string? error);
        Assert.True(edit != null, error);
        return edit!;
    }

    private static double[] Zs(double[] path) => Enumerable.Range(0, path.Length / 3).Select(i => path[i * 3 + 2]).ToArray();

    [Fact]
    public void Create_UnchangedSection_ChangesNothing()
    {
        SectionEdit edit = Edit(Original());

        Assert.False(edit.HasChanges);
        Assert.Equal(Path(Stations, Heights), edit.ApplyToPath(Path(Stations, Heights)));
    }

    [Fact]
    public void ApplyToPath_OneGripMovedUp_MovesOnlyThatPoint()
    {
        double[] z = (double[])Heights.Clone();
        z[3] = 3.53;
        double[] result = Edit(Piece(Stations.Select((s, i) => (s, z[i])).ToArray())).ApplyToPath(Path(Stations, Heights));

        Assert.Equal(z, Zs(result));
        Assert.Equal(Path(Stations, z), result);
    }

    [Fact]
    public void ApplyToPath_LastGripMoved_MovesTheEnd()
    {
        double[] z = (double[])Heights.Clone();
        z[6] = 4.0;
        double[] result = Edit(Piece(Stations.Select((s, i) => (s, z[i])).ToArray())).ApplyToPath(Path(Stations, Heights));

        Assert.Equal(z, Zs(result));
    }

    [Fact]
    public void ApplyToPath_TrimmedSectionUnchanged_KeepsTheWholeCurve()
    {
        SectionEdit edit = Edit(Original(0, 3));

        Assert.False(edit.HasChanges);
        Assert.Equal(new[] { (18.0, 36.0) }, edit.Uncovered());
        Assert.Equal(Path(Stations, Heights), edit.ApplyToPath(Path(Stations, Heights)));
    }

    [Fact]
    public void ApplyToPath_TrimmedSectionEdited_LeavesTheTrimmedOffStretch()
    {
        // Only stations 0-18 remain, with the grip at 12 lowered; 18-36 keep their heights.
        double[] result = Edit(Piece((0, 1.55), (6, 2.65), (12, 1.0), (18, 2.95))).ApplyToPath(Path(Stations, Heights));

        Assert.Equal(new[] { 1.55, 2.65, 1.0, 2.95, 2.15, 3.25, 3.35 }, Zs(result));
    }

    [Fact]
    public void Create_SplitIntoTwoTouchingPieces_IsOneUnchangedSection()
    {
        SectionEdit edit = Edit(Original(0, 3), Original(3, 6));

        Assert.False(edit.HasChanges);
        Assert.Empty(edit.Uncovered());
    }

    [Fact]
    public void ApplyToPath_SplicedReplacementDrawnRightToLeft_AddsItsBendsAndKeepsTheRest()
    {
        // Stations 12-24 replaced by a new stretch with a bend at 15, drawn from 24 back to 12. The plan
        // point at 18 stays (it may be a corner in plan) and takes the new stretch's height.
        double[] replacement = Piece((24, 2.15), (15, 2.0), (12, 1.75));
        double[] result = Edit(Original(0, 2), replacement, Original(4, 6)).ApplyToPath(Path(Stations, Heights));

        double[] stationsOut = Enumerable.Range(0, result.Length / 3).Select(i => result[i * 3] - 5.0).ToArray();
        Assert.Equal(new[] { 0.0, 6, 12, 15, 18, 24, 30, 36 }, stationsOut, new ToleranceComparer(1e-9));
        Assert.Equal(new[] { 1.55, 2.65, 1.75, 2.0, 2.05, 2.15, 3.25, 3.35 }, Zs(result), new ToleranceComparer(1e-9));
    }

    [Fact]
    public void ApplyToPath_GripDeleted_PutsThatPointOnTheChord()
    {
        // The grip at 18 removed: the section runs straight from (12, 1.75) to (24, 2.15).
        double[] result = Edit(Piece((0, 1.55), (6, 2.65), (12, 1.75), (24, 2.15), (30, 3.25), (36, 3.35))).ApplyToPath(Path(Stations, Heights));

        Assert.Equal(1.95, Zs(result)[3], 9);
        Assert.Equal(new[] { 1.55, 2.65, 1.75 }, Zs(result).Take(3));
        Assert.Equal(new[] { 2.15, 3.25, 3.35 }, Zs(result).Skip(4));
    }

    [Fact]
    public void ApplyToPath_VerticalStepDrawn_KeepsBothPointsOfTheStep()
    {
        // A wall face at station 15: 2.0 then 2.6 at one station.
        double[] result = Edit(Original(0, 2), Piece((12, 1.75), (15, 2.0), (15, 2.6), (18, 2.95)), Original(3, 6)).ApplyToPath(Path(Stations, Heights));

        int[] atStep = Enumerable.Range(0, result.Length / 3).Where(i => Math.Abs(result[i * 3] - 20.0) < 1e-9).ToArray();
        Assert.Equal(2, atStep.Length);
        Assert.Equal(2.0, result[atStep[0] * 3 + 2], 9);
        Assert.Equal(2.6, result[atStep[1] * 3 + 2], 9);
    }

    [Fact]
    public void ApplyToPath_DenseDrapedCurveEditedInOnePlace_KeepsEveryOtherPointExactly()
    {
        var rng = new Random(7);
        double[] stations = Enumerable.Range(0, 201).Select(i => i * 0.2).ToArray();
        double[] heights = stations.Select(s => 10 + Math.Sin(s / 3) + rng.NextDouble() * 0.05).ToArray();
        double[] edited = (double[])heights.Clone();
        edited[100] += 0.5;
        double[] path = Path(stations, heights);

        SectionEdit? edit = SectionEdit.Create(stations, heights, new[] { Piece(stations.Select((s, i) => (s, edited[i])).ToArray()) }, Tol, out _);
        double[] result = edit!.ApplyToPath(path);

        Assert.Equal(path.Length, result.Length);
        for (int i = 0; i < stations.Length; i++)
            Assert.Equal(i == 100 ? edited[i] : heights[i], result[i * 3 + 2]);
        Assert.Equal(new[] { (19.8, 20.2) }, edit.ChangedRanges, new RangeComparer(1e-9));
    }

    [Fact]
    public void ApplyToPath_CornerInPlan_NewBendFollowsThePlanPath()
    {
        // An L in plan: 10 along X, then 10 along Y. A new bend at station 15 lands on the second leg.
        double[] path = { 0, 0, 0, 10, 0, 1, 10, 10, 2 };
        double[] stations = { 0, 10, 20 }, heights = { 0, 1, 2 };
        SectionEdit? edit = SectionEdit.Create(stations, heights, new[] { Piece((0, 0), (10, 1), (15, 3), (20, 2)) }, Tol, out _);
        double[] result = edit!.ApplyToPath(path);

        Assert.Equal(new double[] { 0, 0, 0, 10, 0, 1, 10, 5, 3, 10, 10, 2 }, result, new ToleranceComparer(1e-9));
    }

    [Fact]
    public void Create_PiecePastTheCurveEnds_IsClipped()
    {
        SectionEdit edit = Edit(Piece((-5, 1.3), (6, 2.65), (12, 1.75), (18, 2.95), (24, 2.15), (30, 3.25), (40, 3.45)));
        double[] result = edit.ApplyToPath(Path(Stations, Heights));

        Assert.Equal(7, result.Length / 3);
        // Cut where the piece crosses stations 0 and 36.
        Assert.Equal(1.3 + 1.35 * 5 / 11, Zs(result)[0], 9);
        Assert.Equal(3.25 + 0.2 * 6 / 10, Zs(result)[6], 9);
    }

    [Fact]
    public void Create_SectionDoublesBack_IsRefusedWithTheStation()
    {
        SectionEdit? edit = SectionEdit.Create(Stations, Heights, new[] { Piece((0, 1.55), (12, 1.75), (9, 2.0), (36, 3.35)) }, Tol, out string? error);

        Assert.Null(edit);
        Assert.Contains("doubles back near station 12", error);
    }

    [Fact]
    public void Create_OverlappingPiecesThatDisagree_AreRefused()
    {
        SectionEdit? edit = SectionEdit.Create(Stations, Heights, new[] { Original(0, 4), Piece((12, 3.0), (36, 3.35)) }, Tol, out string? error);

        Assert.Null(edit);
        Assert.Contains("different heights", error);
    }

    [Fact]
    public void Create_OverlappingPiecesThatAgree_AreAccepted()
    {
        SectionEdit edit = Edit(Original(0, 4), Original(2, 6));

        Assert.False(edit.HasChanges);
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= tolerance;
        public int GetHashCode(double value) => 0;
    }

    private sealed class RangeComparer(double tolerance) : IEqualityComparer<(double Start, double End)>
    {
        public bool Equals((double Start, double End) a, (double Start, double End) b) =>
            Math.Abs(a.Start - b.Start) <= tolerance && Math.Abs(a.End - b.End) <= tolerance;
        public int GetHashCode((double Start, double End) value) => 0;
    }
}
