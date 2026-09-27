using MoleHill.Core.Analysis;
using Xunit;
using Verdict = MoleHill.Core.Analysis.GradientComplianceAnalyzer.FaceVerdict;

namespace MoleHill.Core.Tests;

public class GradientComplianceAnalyzerTests
{
    /// <summary>ADA §304.2 / §405.7.1: level areas no steeper than 1:48 in any direction.</summary>
    private const double OneIn48 = 1.0 / 48.0;

    /// <summary>
    /// A square grid of <paramref name="cells"/> × <paramref name="cells"/> unit cells, two triangles per
    /// cell, with each vertex at <paramref name="height"/>(x, y).
    /// </summary>
    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) Grid(
        int cells, Func<double, double, double> height)
    {
        int side = cells + 1;
        var vertices = new double[side * side * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                int v = (j * side) + i;
                vertices[v * 3] = i;
                vertices[(v * 3) + 1] = j;
                vertices[(v * 3) + 2] = height(i, j);
            }
        }

        var faces = new List<int>(cells * cells * 6);
        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                int a = (j * side) + i;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;
                faces.AddRange(new[] { a, b, d, a, d, c });
            }
        }

        return (vertices, side * side, faces.ToArray(), faces.Count / 3);
    }

    private static double[] Rectangle(double minX, double minY, double maxX, double maxY) =>
        new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY };

    private static GradientComplianceAnalyzer.Result Evaluate(
        (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) mesh,
        IReadOnlyList<double[]> areas,
        double limit = OneIn48,
        double measurementLength = 0.0) =>
        GradientComplianceAnalyzer.EvaluateLevelAreas(
            mesh.Vertices,
            mesh.VertexCount,
            mesh.Faces,
            mesh.FaceCount,
            areas,
            new GradientComplianceAnalyzer.Options
            {
                LevelAreaMaxSlopeRatio = limit,
                MeasurementLength = measurementLength,
            });

    [Fact]
    public void EvaluateLevelAreas_PlaneShallowerThanLimit_PassesEveryFaceInside()
    {
        var mesh = Grid(10, (x, y) => x / 50.0);

        var result = Evaluate(mesh, new[] { Rectangle(2, 2, 8, 8) });

        Assert.DoesNotContain(Verdict.Exceeds, result.Verdicts);
        Assert.Contains(Verdict.Pass, result.Verdicts);
        Assert.Equal(36.0, result.CheckedArea, 9);
        Assert.Equal(0.0, result.ExceedingArea);
        Assert.Equal(1.0 / 50.0, result.MaxSlopeRatio!.Value, 9);
    }

    [Fact]
    public void EvaluateLevelAreas_PlaneSteeperThanLimit_FailsEveryFaceInside()
    {
        var mesh = Grid(10, (x, y) => y / 40.0);

        var result = Evaluate(mesh, new[] { Rectangle(2, 2, 8, 8) });

        Assert.DoesNotContain(Verdict.Pass, result.Verdicts);
        Assert.Equal(result.CheckedArea, result.ExceedingArea, 9);
        Assert.Equal(1.0 / 40.0, result.MaxSlopeRatio!.Value, 9);
    }

    [Fact]
    public void EvaluateLevelAreas_DiagonalFall_MeasuresMagnitudeNotEitherAxis()
    {
        // 1:60 along both axes is under 1:48 on either one, but the fall line is 1:60 × √2 ≈ 1:42.4.
        // A level area has no direction of travel, so that is the gradient it has to answer for.
        var mesh = Grid(10, (x, y) => (x + y) / 60.0);

        var result = Evaluate(mesh, new[] { Rectangle(2, 2, 8, 8) });

        Assert.DoesNotContain(Verdict.Pass, result.Verdicts);
        Assert.Equal(Math.Sqrt(2.0) / 60.0, result.MaxSlopeRatio!.Value, 9);
    }

    [Fact]
    public void EvaluateLevelAreas_PlaneExactlyAtLimit_Passes()
    {
        var mesh = Grid(10, (x, y) => x * OneIn48);

        var result = Evaluate(mesh, new[] { Rectangle(2, 2, 8, 8) });

        Assert.DoesNotContain(Verdict.Exceeds, result.Verdicts);
    }

    [Fact]
    public void EvaluateLevelAreas_FacesOutsideEveryArea_AreUncheckedWithNaNSlope()
    {
        var mesh = Grid(10, (x, y) => x / 10.0);

        var result = Evaluate(mesh, new[] { Rectangle(2, 2, 4, 4) });

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            if (result.Verdicts[face] == Verdict.Unchecked)
                Assert.True(double.IsNaN(result.MeasuredSlopeRatios[face]));
            else
                Assert.False(double.IsNaN(result.MeasuredSlopeRatios[face]));
        }

        Assert.Equal(4.0, result.CheckedArea, 9);
    }

    [Fact]
    public void EvaluateLevelAreas_NoAreas_ChecksNothingAndReportsNoMaximum()
    {
        var mesh = Grid(4, (x, y) => x);

        var result = Evaluate(mesh, Array.Empty<double[]>());

        Assert.All(result.Verdicts, verdict => Assert.Equal(Verdict.Unchecked, verdict));
        Assert.Null(result.MaxSlopeRatio);
        Assert.Equal(0.0, result.CheckedArea);
    }

    [Fact]
    public void EvaluateLevelAreas_NestedLoop_IsAHole()
    {
        var mesh = Grid(10, (x, y) => 0.0);

        var result = Evaluate(mesh, new[] { Rectangle(1, 1, 9, 9), Rectangle(4, 4, 6, 6) });

        Assert.Equal(64.0 - 4.0, result.CheckedArea, 9);
    }

    [Fact]
    public void EvaluateLevelAreas_SurveyBumpOnLevelGround_FailsPerFaceButPassesOverAFootprint()
    {
        // A level landing with one vertex 30 mm proud: the faces around it read about 3% face by face,
        // but a level laid across the landing reads flat. The footprint is what makes the check agree
        // with the level rather than with the triangulation.
        var mesh = Grid(10, (x, y) => x == 5 && y == 5 ? 0.03 : 0.0);
        var areas = new[] { Rectangle(0, 0, 10, 10) };

        var perFace = Evaluate(mesh, areas, measurementLength: 0.0);
        var averaged = Evaluate(mesh, areas, measurementLength: 4.0);

        Assert.Contains(Verdict.Exceeds, perFace.Verdicts);
        Assert.DoesNotContain(Verdict.Exceeds, averaged.Verdicts);
    }

    [Fact]
    public void EvaluateLevelAreas_FootprintAtAreaEdge_DoesNotBorrowSlopeFromOutside()
    {
        // Level for x < 5, a 1:12 ramp beyond. The landing runs to x = 5, and a footprint wide enough to
        // reach well onto the ramp must still read the landing as level.
        var mesh = Grid(10, (x, y) => x <= 5 ? 0.0 : (x - 5) / 12.0);

        var result = Evaluate(mesh, new[] { Rectangle(0, 0, 5, 10) }, measurementLength: 6.0);

        Assert.DoesNotContain(Verdict.Exceeds, result.Verdicts);
        Assert.Equal(0.0, result.MaxSlopeRatio!.Value, 12);
    }
}
