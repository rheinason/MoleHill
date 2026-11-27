using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ProfileEditorTests
{
    private const double Tol = 1e-6;

    /// <summary>A straight 100-long path along X with a gentle wobble, the kind a draped curve has.</summary>
    private static double[] WobblyPath(int points = 101)
    {
        var path = new double[points * 3];
        for (int i = 0; i < points; i++)
        {
            double x = 100.0 * i / (points - 1);
            path[i * 3] = x;
            path[i * 3 + 1] = 0.0;
            path[i * 3 + 2] = 10.0 + 0.05 * x + 0.004 * Math.Sin(i * 1.7);
        }

        return path;
    }

    private static ProfileEditor Open(double[] path, double simplify = 0.01, int maxBends = 60)
    {
        double[] stations = LongSectionProfile.PlanStations(path);
        double[] z = Enumerable.Range(0, path.Length / 3).Select(i => path[i * 3 + 2]).ToArray();
        return ProfileEditor.Create(stations, z, Tol, simplify, maxBends)!;
    }

    [Fact]
    public void Create_SimplifiesADrapedCurveToItsGrade()
    {
        ProfileEditor editor = Open(WobblyPath());

        Assert.Equal(2, editor.Bends.Count);
        Assert.Equal(0.05, editor.Grade(0), 2);
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public void Create_KeepsRealBends()
    {
        double[] path = { 0, 0, 0, 50, 0, 5, 100, 0, 0 };
        ProfileEditor editor = Open(path);

        Assert.Equal(3, editor.Bends.Count);
        Assert.Equal(50.0, editor.Bends[1].Station, 12);
        Assert.Equal(5.0, editor.Bends[1].Elevation, 12);
    }

    [Fact]
    public void Create_LoosensUntilWithinTheBendLimit()
    {
        double[] path = new double[201 * 3];
        for (int i = 0; i <= 200; i++)
        {
            path[i * 3] = i;
            path[i * 3 + 2] = i % 2 == 0 ? 0.0 : 1.0; // a saw tooth: every point is a bend at 1 mm
        }

        ProfileEditor editor = Open(path, simplify: 0.001, maxBends: 20);

        Assert.True(editor.Bends.Count <= 20, $"{editor.Bends.Count}");
        Assert.True(editor.SimplifiedWithin > 0.001);
    }

    [Fact]
    public void Create_VerticalStep_IsCountedNotABend()
    {
        double[] path = { 0, 0, 0, 10, 0, 0, 10, 0, 2, 20, 0, 2 };
        ProfileEditor editor = Open(path, simplify: 1e-4);

        Assert.Equal(1, editor.VerticalSteps);
        Assert.True(editor.Bends.Zip(editor.Bends.Skip(1)).All(pair => pair.Second.Station > pair.First.Station));
    }

    [Fact]
    public void Create_NoPlanLength_IsRefused()
    {
        Assert.Null(ProfileEditor.Create(new[] { 0.0, 0.0 }, new[] { 1.0, 2.0 }, Tol, 0.01, 60));
    }

    [Fact]
    public void Move_EndsKeepTheirStations_InteriorStaysBetweenNeighbours()
    {
        ProfileEditor editor = Open(new double[] { 0, 0, 0, 50, 0, 5, 100, 0, 0 });

        ProfileBend end = editor.Move(0, 30.0, 2.0);
        Assert.Equal(0.0, end.Station);
        Assert.Equal(2.0, end.Elevation);

        ProfileBend middle = editor.Move(1, 500.0, 6.0);
        Assert.True(middle.Station < 100.0);
        Assert.True(middle.Station > 99.0);
    }

    [Fact]
    public void Gesture_IsOneUndoStep_AndAnUnmovedGestureIsNone()
    {
        ProfileEditor editor = Open(new double[] { 0, 0, 0, 50, 0, 5, 100, 0, 0 });

        editor.BeginGesture();
        editor.EndGesture();
        Assert.False(editor.CanUndo);

        editor.BeginGesture();
        editor.Move(1, 50.0, 6.0);
        editor.Move(1, 50.0, 7.0);
        editor.Move(1, 50.0, 8.0);
        editor.EndGesture();
        Assert.True(editor.CanUndo);

        Assert.True(editor.Undo());
        Assert.False(editor.CanUndo);
        Assert.Equal(5.0, editor.Bends[1].Elevation);
        Assert.False(editor.HasChanges);

        Assert.True(editor.Redo());
        Assert.Equal(8.0, editor.Bends[1].Elevation);
    }

    [Fact]
    public void Insert_KeepsTheShape_DeleteRemovesInteriorOnly()
    {
        ProfileEditor editor = Open(new double[] { 0, 0, 0, 100, 0, 10, });

        int index = editor.Insert(40.0);
        Assert.Equal(1, index);
        Assert.Equal(4.0, editor.Bends[1].Elevation, 12);
        Assert.True(editor.Bends[1].IsAdded);
        Assert.Equal(-1, editor.Insert(40.0));

        Assert.False(editor.Delete(0));
        Assert.False(editor.Delete(2));
        Assert.True(editor.Delete(1));
        Assert.Equal(2, editor.Bends.Count);
    }

    [Fact]
    public void SetGrade_HoldsTheChosenEnd()
    {
        ProfileEditor editor = Open(new double[] { 0, 0, 0, 50, 0, 5, 100, 0, 0 });

        editor.SetGrade(0, 0.02, holdStart: true);
        Assert.Equal(0.0, editor.Bends[0].Elevation, 12);
        Assert.Equal(1.0, editor.Bends[1].Elevation, 12);

        editor.SetGrade(1, -0.04, holdStart: false);
        Assert.Equal(0.0, editor.Bends[2].Elevation, 12);
        Assert.Equal(2.0, editor.Bends[1].Elevation, 12);
        Assert.Equal(-0.04, editor.Grade(1), 12);
    }

    [Fact]
    public void ApplyToPath_Untouched_ReturnsThePathUnchanged()
    {
        double[] path = WobblyPath();
        ProfileEditor editor = Open(path);

        Assert.Equal(path, editor.ApplyToPath(path));
    }

    [Fact]
    public void ApplyToPath_KeepsDetailOutsideTheEditAndXyEverywhere()
    {
        // Three plain bends, with wobble along the whole length.
        double[] path = WobblyPath();
        for (int i = 0; i < path.Length / 3; i++)
            path[i * 3 + 2] += path[i * 3] <= 50.0 ? path[i * 3] * 0.05 : (100.0 - path[i * 3]) * 0.05;
        ProfileEditor editor = Open(path, simplify: 0.02);
        int last = editor.Bends.Count - 1;

        // Raise only the end bend: only the last segment is touched.
        editor.Move(last, editor.Length, editor.Bends[last].Elevation + 3.0);
        double touchedFrom = editor.Bends[last - 1].Station;
        double[] result = editor.ApplyToPath(path);

        Assert.Equal(path.Length, result.Length);
        for (int i = 0; i < path.Length / 3; i++)
        {
            Assert.Equal(path[i * 3], result[i * 3]);
            Assert.Equal(path[i * 3 + 1], result[i * 3 + 1]);
            if (path[i * 3] <= touchedFrom)
                Assert.Equal(path[i * 3 + 2], result[i * 3 + 2]);
            else
                Assert.Equal(editor.ElevationAt(path[i * 3]), result[i * 3 + 2], 9);
        }
    }

    [Fact]
    public void ApplyToPath_AddedBendBecomesAPathPoint()
    {
        double[] path = { 0, 0, 0, 10, 0, 0, 10, 10, 0 }; // an L, 20 long in plan
        ProfileEditor editor = Open(path);
        int added = editor.Insert(5.0);
        editor.Move(added, 5.0, 3.0);

        double[] result = editor.ApplyToPath(path);

        Assert.Equal(4, result.Length / 3);
        Assert.Equal(new[] { 5.0, 0.0, 3.0 }, result[3..6]);
        // The corner at station 10 keeps its plan position and reads 3 - 3 * 5 / 15.
        Assert.Equal(10.0, result[6], 12);
        Assert.Equal(0.0, result[7], 12);
        Assert.Equal(2.0, result[8], 12);
    }

    [Fact]
    public void ApplyToPath_DeletedBendTouchesBothNeighbouringSpans()
    {
        double[] path = { 0, 0, 0, 25, 0, 0, 50, 0, 5, 75, 0, 0, 100, 0, 0 };
        ProfileEditor editor = Open(path, simplify: 1e-4);
        int peak = editor.Bends.ToList().FindIndex(bend => bend.Station == 50.0);

        Assert.True(editor.Delete(peak));
        IReadOnlyList<(double Start, double End)> touched = editor.TouchedRanges();

        Assert.Single(touched);
        Assert.Equal(25.0, touched[0].Start, 12);
        Assert.Equal(75.0, touched[0].End, 12);
        double[] result = editor.ApplyToPath(path);
        Assert.Equal(0.0, result[8], 12); // the old peak is level now
    }

    [Fact]
    public void ApplyToPath_VerticalStepOutsideTheEditSurvives()
    {
        double[] path = { 0, 0, 0, 10, 0, 0, 10, 0, 2, 20, 0, 2, 30, 0, 2 };
        ProfileEditor editor = Open(path, simplify: 1e-4);
        int last = editor.Bends.Count - 1;
        editor.Move(last, editor.Length, 5.0);

        double[] result = editor.ApplyToPath(path);

        Assert.Equal(0.0, result[5], 12);
        Assert.Equal(2.0, result[8], 12);
        Assert.Equal(5.0, result[^1], 12);
    }
}
