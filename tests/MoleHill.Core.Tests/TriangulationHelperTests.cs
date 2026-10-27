using MoleHill.Core.Engine;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using Xunit;

namespace MoleHill.Core.Tests;

public class TriangulationHelperTests
{
    [Fact]
    public void Triangulate_SimpleSquare_SucceedsWithoutFlags()
    {
        var outcome = TriangulationHelper.Triangulate(
            new List<double>
            {
                0.0, 0.0,
                10.0, 0.0,
                10.0, 10.0,
                0.0, 10.0
            },
            vertexCount: 4,
            new List<(int a, int b)>(),
            maxArea: 0.0,
            minAngle: 0.0,
            convex: true);

        Assert.NotNull(outcome.Mesh);
        Assert.Equal(TriangulationWarningFlags.None, outcome.Flags);
        Assert.Null(outcome.WarningMessage);
        Assert.Empty(outcome.FailureDetails);
    }

    [Fact]
    public void Triangulate_AllAttemptsFail_ReturnsFailureDetails()
    {
        var outcome = TriangulationHelper.Triangulate(
            new List<double>
            {
                0.0, 0.0,
                1.0, 0.0,
                2.0, 0.0
            },
            vertexCount: 3,
            new List<(int a, int b)>(),
            maxArea: 0.0,
            minAngle: 0.0,
            convex: true);

        Assert.Null(outcome.Mesh);
        Assert.Contains("All triangulation attempts failed", outcome.WarningMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEmpty(outcome.FailureDetails);
    }

    [Fact]
    public void Triangulate_TJunction_SplitsConstraintWithoutAddingVertices()
    {
        var xy = new List<double>
        {
            0.0, 0.0,
            10.0, 0.0,
            10.0, 10.0,
            0.0, 10.0,
            5.0, 0.0,
            5.0, 5.0
        };
        var segments = new List<(int a, int b)>
        {
            (0, 1),
            (4, 5)
        };

        var outcome = TriangulationHelper.Triangulate(
            xy, vertexCount: 6, segments, maxArea: 0.0, minAngle: 0.0, convex: true);

        Assert.NotNull(outcome.Mesh);
        Assert.Equal(TriangulationWarningFlags.None, outcome.Flags);
        Assert.Equal(new List<(int a, int b)> { (0, 1), (4, 5) }, segments);
        Assert.Equal(6, outcome.Mesh!.Vertices.Count);
        var edges = outcome.Mesh.Segments
            .Select(segment => (Math.Min(segment.P0, segment.P1), Math.Max(segment.P0, segment.P1)))
            .ToHashSet();
        Assert.Contains((0, 4), edges);
        Assert.Contains((1, 4), edges);
        Assert.Contains((4, 5), edges);
        Assert.DoesNotContain((0, 1), edges);
    }

    /// <summary>
    /// A rail running along an existing line in a stage's mesh, cut from a copied case (2026-10-26): the mesh's
    /// points are float-rounded by design and the rail's in double, so the line's stationing points sit up to a
    /// micrometre off the long segment 0-3 that spans them. Each overlap reached Triangle.NET as a crossing.
    /// </summary>
    private static readonly double[] FloatRoundedOverlapXy =
    {
        27.2388973236084, 9.489117622375488,
        27.03788185119629, 9.738064765930176,
        26.987627029418945, 9.800300598144531,
        26.83686637878418, 9.987010955810547,
        27.138389587402344, 9.613591194152832,
        27.088134765625, 9.675827980041504
    };

    private static readonly (int a, int b)[] FloatRoundedOverlapSegments =
    {
        (0, 3), (1, 2), (1, 3), (4, 5), (5, 1), (4, 3), (4, 3)
    };

    [Fact]
    public void Triangulate_RailOverlappingFloatRoundedLine_KeepsEveryConstraint()
    {
        // The line inside a frame of terrain, as it lies in the mesh.
        var xy = FloatRoundedOverlapXy.ToList();
        xy.AddRange(new[] { 26.5, 9.3, 27.6, 9.3, 27.6, 10.1, 26.5, 10.1 });
        var segments = FloatRoundedOverlapSegments.ToList();
        segments.AddRange(new[] { (6, 7), (7, 8), (8, 9), (9, 6) });

        var outcome = TriangulationHelper.Triangulate(
            xy, xy.Count / 2, segments, maxArea: 0.0, minAngle: 0.0, convex: false, segmentSplitting: 2);

        Assert.NotNull(outcome.Mesh);
        Assert.Equal(TriangulationWarningFlags.None, outcome.Flags);
    }

    /// <summary>
    /// The same input handed to Triangle.NET unsplit used to spin forever in ConstraintMesher.FindDirection,
    /// under the triangulation lock (Remesh Rebuild ran past 20 minutes). It must now fail, and fail fast.
    /// </summary>
    [Fact]
    public void TriangleNet_UnsplitFloatRoundedOverlap_FailsInsteadOfSpinning()
    {
        var polygon = new Polygon(FloatRoundedOverlapXy.Length / 2);
        var vertices = new Vertex[FloatRoundedOverlapXy.Length / 2];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new Vertex(FloatRoundedOverlapXy[i * 2], FloatRoundedOverlapXy[i * 2 + 1]) { ID = i };
            polygon.Add(vertices[i]);
        }

        foreach ((int a, int b) in FloatRoundedOverlapSegments)
            polygon.Add(new Segment(vertices[a], vertices[b], 1), false);

        var run = Task.Run(() => new GenericMesher().Triangulate(
            polygon,
            new ConstraintOptions { Convex = false, SegmentSplitting = 2 },
            new QualityOptions()));

        // Failing (an exception the tiers above fall through on) is the expected outcome; spinning is the bug.
        Assert.True(Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))).Result == run, "Triangle.NET did not return within 30 s.");
    }
}
