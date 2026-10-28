using System.Text.Json;
using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A stair with a stringer wall each side, as the Remesh stage received it (2026-10-28 sweep). A remesh collapses
/// vertices along a pinned rail, so its output has edges that skip the rail's own stations; the next remesh (the
/// tiled remesher's next pass, or simply remeshing again) did not recognise those edges as the rail, flipped
/// them, and the terrain at one wall's top rail fell 0.34 m below it.
/// </summary>
public class RemeshPinnedRailTests
{
    private const string Resource = "MoleHill.Core.Tests.TestData.StairStringerRemeshCase.json";

    private sealed record CapturedConstraint(double[] Points, int PointCount, bool IsClosed, bool PreserveInputElevation);

    private sealed record CapturedCase(string Case, double[] Vertices, int[] Faces, List<CapturedConstraint> Constraints);

    private static readonly (double X, double Slope, double Side)[] Rails =
    {
        (-0.2, 0.4, -1.0), (2.2, 0.4, 1.0), (0.0, 0.3, 1.0), (2.0, 0.3, -1.0)
    };

    [Fact]
    public void TiledRemesh_StringerRails_StayAtTheirHeight()
    {
        (double[] vertices, int[] faces, List<ConstraintPolyline> constraints) = Load();

        var result = TiledIsotropicRemesher.Remesh(vertices, faces, constraints, Options());

        Assert.True(WorstRailError(result.Vertices, result.Faces) < 0.02);
    }

    [Fact]
    public void RemeshTwice_RailEdgesSkippingStations_StayPinned()
    {
        (double[] vertices, int[] faces, List<ConstraintPolyline> constraints) = Load();

        var once = IsotropicRemesher.Remesh(vertices, faces, constraints, Options());
        var twice = IsotropicRemesher.Remesh(once.Vertices, once.Faces, constraints, Options());

        Assert.True(WorstRailError(twice.Vertices, twice.Faces) < 0.02);
    }

    private static IsotropicRemesher.Options Options() => new()
    {
        TargetEdgeLength = 0.526,
        CreaseAngleDeg = 30,
        WallFaceMinSlopeDeg = 70,
        Tolerance = 0.01,
        Iterations = 3
    };

    private static (double[] Vertices, int[] Faces, List<ConstraintPolyline> Constraints) Load()
    {
        using Stream stream = typeof(RemeshPinnedRailTests).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Missing {Resource}.");
        CapturedCase capture = JsonSerializer.Deserialize<CapturedCase>(stream)!;
        return (capture.Vertices, capture.Faces, capture.Constraints
            .Select(c => new ConstraintPolyline(c.Points, c.PointCount, c.IsClosed, c.PreserveInputElevation))
            .ToList());
    }

    /// <summary>
    /// The terrain's height a centimetre to each rail's own side (the stair side of a toe rail, the ground side of
    /// a top rail), against the rail's height, along the wall.
    /// </summary>
    private static double WorstRailError(double[] vertices, int[] faces)
    {
        double worst = 0.0;
        foreach ((double x, double slope, double side) in Rails)
        {
            for (double y = 1.25; y <= 5.75; y += 0.25)
            {
                double? z = HeightAt(vertices, faces, x + (side * 0.01), y);
                if (z is double height)
                    worst = Math.Max(worst, Math.Abs(height - (slope * y)));
            }
        }

        return worst;
    }

    private static double? HeightAt(double[] v, int[] f, double x, double y)
    {
        for (int i = 0; i < f.Length / 3; i++)
        {
            int a = f[i * 3] * 3, b = f[(i * 3) + 1] * 3, c = f[(i * 3) + 2] * 3;
            double d = ((v[b + 1] - v[c + 1]) * (v[a] - v[c])) + ((v[c] - v[b]) * (v[a + 1] - v[c + 1]));
            if (Math.Abs(d) < 1e-14)
                continue;
            double l1 = (((v[b + 1] - v[c + 1]) * (x - v[c])) + ((v[c] - v[b]) * (y - v[c + 1]))) / d;
            double l2 = (((v[c + 1] - v[a + 1]) * (x - v[c])) + ((v[a] - v[c]) * (y - v[c + 1]))) / d;
            double l3 = 1 - l1 - l2;
            if (l1 >= -1e-9 && l2 >= -1e-9 && l3 >= -1e-9)
                return (l1 * v[a + 2]) + (l2 * v[b + 2]) + (l3 * v[c + 2]);
        }

        return null;
    }
}
