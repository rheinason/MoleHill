using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Tests that GradeWithEdges clips shoulder/guide constraints at hard-constraint barriers,
/// preventing PSLG crossings that would fail Triangle.NET triangulation.
/// </summary>
public class PathGraderBarrierClippingTests
{
    // 100×100 coarse terrain at z=0
    private static readonly double[] CoarseVertices =
    {
        0.0,   0.0,   0.0,
        100.0, 0.0,   0.0,
        100.0, 100.0, 0.0,
        0.0,   100.0, 0.0
    };

    private static readonly int[] CoarseFaces =
    {
        0, 1, 2,
        0, 2, 3
    };

    private static ConstraintPolyline MakeBarrier(
        double x0, double y0, double x1, double y1)
    {
        return new ConstraintPolyline(
            new[] { x0, y0, 0.0, x1, y1, 0.0 },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);
    }

    private static PathGrader.PathDefinition MakeHorizontalPath(double y, double width, double maxDist, double z = 0.0)
    {
        return new PathGrader.PathDefinition(
            xyVertices: new[] { 10.0, y, 90.0, y },
            zValues: new[] { z, z },
            vertexCount: 2,
            width: width,
            slopeAngleDeg: 45.0,
            maxDistance: maxDist);
    }

    [Fact]
    public void Grade_PathParallelToBarrierBeyondShoulder_TriangulatesSuccessfully()
    {
        // Barrier at y=62, shoulder only extends to y=57 (halfWidth=2, maxDist=5 → edge+5=57). No crossing expected.
        var path = MakeHorizontalPath(y: 50.0, width: 4.0, maxDist: 5.0);
        var barrier = MakeBarrier(0.0, 62.0, 100.0, 62.0);

        GradeOutcome gradeOutcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { barrier },
        });
        string? errorMessage = gradeOutcome.ErrorMessage;
        GradingResult? result = gradeOutcome.Result;

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);
    }

    [Fact]
    public void Grade_BarrierInsideShoulderZone_ShoulderClippedAtBarrier()
    {
        // Barrier at y=57, inside the shoulder zone (road edge at y=52, maxDist=10 → guide reaches y=62).
        // Shoulder segments must be clipped at y=57.
        var path = MakeHorizontalPath(y: 50.0, width: 4.0, maxDist: 10.0);
        var barrier = MakeBarrier(0.0, 57.0, 100.0, 57.0);

        GradeOutcome gradeOutcome2 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { barrier },
        });
        string? errorMessage = gradeOutcome2.ErrorMessage;
        GradingResult? result = gradeOutcome2.Result;

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);

        // No result vertex should lie strictly between the barrier (y=57) and the terrain boundary (y=100),
        // i.e. the shoulder must not have inserted any new vertices north of the barrier.
        const double tol = 0.1;
        int vertCount = result!.VertexCount;
        double[] verts = result.Vertices;
        for (int i = 0; i < vertCount; i++)
        {
            double vy = verts[i * 3 + 1];
            Assert.True(vy <= 57.0 + tol || vy >= 100.0 - tol,
                $"Vertex {i} at y={vy:F3} is north of barrier at y=57 — shoulder was not clipped.");
        }
    }

    [Fact]
    public void Grade_TwoParallelBarriers_ShoulderStopsAtCloserRail()
    {
        // Two barriers simulating a retaining wall: closer rail at y=56, farther at y=60.
        // Shoulder should stop at the closer rail (y=56).
        var path = MakeHorizontalPath(y: 50.0, width: 4.0, maxDist: 20.0);
        var closerRail = MakeBarrier(0.0, 56.0, 100.0, 56.0);
        var fartherRail = MakeBarrier(0.0, 60.0, 100.0, 60.0);

        GradeOutcome gradeOutcome3 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { closerRail, fartherRail },
        });
        string? errorMessage = gradeOutcome3.ErrorMessage;
        GradingResult? result = gradeOutcome3.Result;

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);

        const double tol = 0.1;
        int vertCount = result!.VertexCount;
        double[] verts = result.Vertices;
        for (int i = 0; i < vertCount; i++)
        {
            double vy = verts[i * 3 + 1];
            // Allow vertices at the closer barrier (y=56), the farther barrier (y=60, a hard constraint
            // whose vertices appear in the output), and the terrain boundary (y=100).
            Assert.True(vy <= 56.0 + tol || (vy >= 60.0 - tol && vy <= 60.0 + tol) || vy >= 100.0 - tol,
                $"Vertex {i} at y={vy:F3} is between the barriers — shoulder was not stopped at closer rail.");
        }
    }

    /// <summary>
    /// A road meeting a hard constraint stops at it and is graded on both sides. It used to be refused, and
    /// the refusal took every path in the grade with it.
    /// </summary>
    [Fact]
    public void Grade_RoadCrossesBarrier_StopsAtItAndGradesBothSides()
    {
        // Barrier runs N-S at x=50, crossing the horizontal road.
        var path = MakeHorizontalPath(y: 50.0, width: 4.0, maxDist: 5.0, z: 2.0);
        var barrier = MakeBarrier(50.0, 0.0, 50.0, 100.0);

        GradeOutcome gradeOutcome4 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { barrier },
        });
        string? errorMessage = gradeOutcome4.ErrorMessage;
        GradingResult? result = gradeOutcome4.Result;

        Assert.True(result != null, errorMessage);
        Assert.Contains(result!.Diagnostics, d => d.Contains("stopped at 1", StringComparison.Ordinal));
        var graded = new TerrainFaceGrid(result.Vertices, result.VertexCount, result.Faces, result.FaceCount);
        double roadZ = path.ZValues[0];
        Assert.Equal(roadZ, graded.InterpolateZ(30.0, 50.0), 3);
        Assert.Equal(roadZ, graded.InterpolateZ(70.0, 50.0), 3);
    }

    /// <summary>A road through a graded pad is graded outside it and leaves the pad's interior to the pad.</summary>
    [Fact]
    public void Grade_RoadThroughClosedBarrier_LeavesTheInsideToIt()
    {
        var path = MakeHorizontalPath(y: 50.0, width: 4.0, maxDist: 5.0, z: 2.0);
        var pad = new ConstraintPolyline(
            new[] { 40.0, 40.0, 0.0, 60.0, 40.0, 0.0, 60.0, 60.0, 0.0, 40.0, 60.0, 0.0 },
            PointCount: 4,
            IsClosed: true,
            PreserveInputElevation: true);
        double insideBefore = new TerrainFaceGrid(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3)
            .InterpolateZ(50.0, 50.0);

        GradeOutcome gradeOutcome5 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { pad },
        });
        string? errorMessage = gradeOutcome5.ErrorMessage;
        GradingResult? result = gradeOutcome5.Result;

        Assert.True(result != null, errorMessage);
        Assert.Contains(result!.Diagnostics, d => d.Contains("left to it", StringComparison.Ordinal));
        var graded = new TerrainFaceGrid(result.Vertices, result.VertexCount, result.Faces, result.FaceCount);
        Assert.Equal(insideBefore, graded.InterpolateZ(50.0, 50.0), 6);
        Assert.Equal(path.ZValues[0], graded.InterpolateZ(20.0, 50.0), 3);
        Assert.Equal(path.ZValues[0], graded.InterpolateZ(80.0, 50.0), 3);
    }

    [Fact]
    public void Grade_PathEndsOnBarrier_DoesNotRejectAsCrossing()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 10.0, 50.0, 50.0, 50.0 },
            zValues: new[] { 2.0, 2.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 5.0);
        var barrier = MakeBarrier(50.0, 0.0, 50.0, 100.0);

        GradeOutcome gradeOutcome6 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { barrier },
        });
        string? errorMessage = gradeOutcome6.ErrorMessage;
        GradingResult? result = gradeOutcome6.Result;

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("crosses a hard constraint", StringComparison.OrdinalIgnoreCase),
            errorMessage);
    }

    [Fact]
    public void Grade_PathStartsWithinToleranceOfBarrier_DoesNotRejectAsCrossing()
    {
        const double modelTolerance = 0.01;
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 49.995, 50.0, 90.0, 50.0 },
            zValues: new[] { 2.0, 2.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 5.0);
        var barrier = MakeBarrier(50.0, 0.0, 50.0, 100.0);

        GradeOutcome gradeOutcome7 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { barrier },
            ModelTolerance = modelTolerance,
        });
        string? errorMessage = gradeOutcome7.ErrorMessage;
        GradingResult? result = gradeOutcome7.Result;

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("crosses a hard constraint", StringComparison.OrdinalIgnoreCase),
            errorMessage);
    }

    [Fact]
    public void Grade_OffsetRoadEdgeCrossesBarrierInsidePathStartCap_DoesNotRejectAsCrossing()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 10.0, 50.0, 90.0, 50.0 },
            zValues: new[] { 2.0, 2.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 5.0);
        var barrier = MakeBarrier(10.0, 50.0, 12.0, 60.0);

        GradeOutcome gradeOutcome8 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = new[] { barrier },
        });
        string? errorMessage = gradeOutcome8.ErrorMessage;
        GradingResult? result = gradeOutcome8.Result;

        Assert.NotNull(result);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) || !errorMessage!.Contains("crosses a hard constraint", StringComparison.OrdinalIgnoreCase),
            errorMessage);
    }

    [Fact]
    public void Grade_NoBarriers_BehaviorUnchanged()
    {
        // Regression: no barriers should produce the same result as the existing Grade() overload.
        var path = MakeHorizontalPath(y: 50.0, width: 4.0, maxDist: 10.0, z: 5.0);

        GradeOutcome gradeOutcome9 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
            HardConstraints = Array.Empty<ConstraintPolyline>(),
        });
        string? errorEmpty = gradeOutcome9.ErrorMessage;
        GradingResult? withEmpty = gradeOutcome9.Result;

        GradeOutcome gradeOutcome10 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(CoarseVertices, CoarseVertices.Length / 3, CoarseFaces, CoarseFaces.Length / 3),
            Paths = new[] { path },
        });
        string? errorNoArg = gradeOutcome10.ErrorMessage;
        GradingResult? withNoArg = gradeOutcome10.Result;

        Assert.NotNull(withEmpty);
        Assert.NotNull(withNoArg);
        Assert.Equal(withEmpty!.VertexCount, withNoArg!.VertexCount);
        Assert.Equal(withEmpty.FaceCount, withNoArg.FaceCount);
        Assert.Equal(errorEmpty, errorNoArg);
    }
}
