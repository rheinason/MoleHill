using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class SurfaceStripGraderTests
{
    [Fact]
    public void Grade_DegenerateTerrainFace_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.4,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        GradingResult? result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, new[] { 0, 1, 1 }, 1), surface, out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("degenerate", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_NonFiniteSurfaceBoundary_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, double.NaN,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        GradingResult? result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("finite", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_NonFiniteBarrierConstraint_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.4,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0);
        var barrier = new ConstraintPolyline(
            new[] { 0.0, 0.5, 0.0, 2.0, double.PositiveInfinity, 0.0 },
            2,
            IsClosed: false,
            PreserveInputElevation: true);

        GradingResult? result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, new[] { barrier }, out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("finite", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_EmptySurfaceList_ReturnsFailure()
    {
        GradingResult? result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), Array.Empty<SurfaceStripGrader.SurfaceDefinition>(), Array.Empty<ConstraintPolyline>(), out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("surface", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_NonFiniteSurfaceParameters_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.4,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: double.NaN,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        GradingResult? result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("finite", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_SurfaceBoundaryArrayTooShort_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[] { 0.0, 0.0, 0.0 },
            boundaryVertexCount: 2,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        GradingResult? result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("boundary", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_SurfaceFootprint_RegradesInteriorToPlane()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.4,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.x - 2.0) < 1e-6 &&
            Math.Abs(vertex.y - 0.5) < 1e-6 &&
            Math.Abs(vertex.z - 0.4) < 1e-6);
    }

    [Fact]
    public void Grade_InvalidBoundary_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 1.0, 0.0 },
            footprintVertexCount: 2,
            boundaryVertices: new[] { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0 },
            boundaryVertexCount: 2,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        var result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("footprint", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_FootprintOutsideTerrainBoundary_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 3.0, 0.0, 4.5, 0.0, 4.5, 1.0, 3.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                3.0, 0.0, 0.0,
                4.5, 0.0, 0.0,
                4.5, 1.0, 0.0,
                3.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 0.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var result = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("terrain boundary", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_HardBarrier_BlocksGradingAcrossRetainingWall()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 1.2, 0.0, 1.2, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 1.0,
                1.2, 0.0, 1.0,
                1.2, 1.0, 1.0,
                0.0, 1.0, 1.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 1.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var withoutBarrier = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, out string? withoutBarrierWarning);

        var barrier = new ConstraintPolyline(
            new[]
            {
                1.4, -1.0, 0.0,
                1.4,  2.0, 0.0
            },
            2,
            IsClosed: false,
            PreserveInputElevation: true);

        var withBarrier = SurfaceStripGrader.Grade(new IndexedTriMesh(BuildGridVertices(), 15, BuildGridFaces(), 16), surface, new[] { barrier }, out string? withBarrierWarning);

        Assert.NotNull(withoutBarrier);
        Assert.NotNull(withBarrier);
        Assert.True(string.IsNullOrWhiteSpace(withoutBarrierWarning) || !withoutBarrierWarning.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.True(string.IsNullOrWhiteSpace(withBarrierWarning) || !withBarrierWarning.Contains("failed", StringComparison.OrdinalIgnoreCase));

        double rightWithoutBarrier = FindVertexZ(withoutBarrier!, 2.0, 0.5);
        double rightWithBarrier = FindVertexZ(withBarrier!, 2.0, 0.5);
        double leftWithBarrier = FindVertexZ(withBarrier, -1.0, 0.5);

        Assert.True(rightWithoutBarrier > 0.2, $"Expected right-side grading without barrier, got {rightWithoutBarrier:G6}.");
        Assert.True(Math.Abs(rightWithBarrier) < 1e-6, $"Expected wall-blocked right side to remain unchanged, got {rightWithBarrier:G6}.");
        Assert.True(leftWithBarrier > 0.2, $"Expected unblocked left side to keep grading, got {leftWithBarrier:G6}.");
    }

    private sealed record CapturedTerrain(string Case, double[] Vertices, int[] Faces);

    /// <summary>
    /// A sparse strip of a real terrain (copied case 2026-10-26): long needles fanning onto a border whose
    /// stationing points sit a float's rounding inside one long border edge, joined to it by zero-area slivers.
    /// The grader used to peel the border again after re-triangulating, which ran up the needles from the
    /// slivers and notched the terrain far from the graded surface.
    /// </summary>
    [Fact]
    public void Grade_SparseBorderStrip_KeepsTheTerrainBorder()
    {
        const string resource = "MoleHill.Core.Tests.TestData.StairBorderNeedleFanCase.json";
        using Stream stream = typeof(SurfaceStripGraderTests).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing {resource}.");
        CapturedTerrain terrain = System.Text.Json.JsonSerializer.Deserialize<CapturedTerrain>(stream)!;
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 50.0, 11.5, 51.0, 11.5, 51.0, 12.5, 50.0, 12.5 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                50.0, 11.5, 29.0,
                51.0, 11.5, 29.0,
                51.0, 12.5, 29.0,
                50.0, 12.5, 29.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 29.0);

        GradingResult? result = SurfaceStripGrader.Grade(
            new IndexedTriMesh(terrain.Vertices, terrain.Vertices.Length / 3, terrain.Faces, terrain.Faces.Length / 3),
            surface,
            out string? errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Equal(
            PlanArea(terrain.Vertices, terrain.Faces, terrain.Faces.Length / 3),
            PlanArea(result!.Vertices, result.Faces, result.FaceCount),
            3);
        Assert.True(
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount).HasSingleClosedBoundaryLoop,
            "Grading changed the terrain's border.");
    }

    /// <summary>
    /// Windowed, a surface re-triangulates only the faces within its reach. A barrier running on past the window
    /// (a wall rail, as every wall persists) must not stop it welding: its points beyond the window, triangulated
    /// with it, used to change the faces the window shares with the rest, so the stair graded the whole terrain.
    /// </summary>
    [Fact]
    public void GradeWindowed_BarrierRunningPastTheWindow_LeavesTheRestUntouched()
    {
        const int cells = 20;
        var vertices = new List<double>();
        for (int j = 0; j <= cells; j++)
            for (int i = 0; i <= cells; i++)
                vertices.AddRange(new[] { (double)i, j, 0.0 });
        var faces = new List<int>();
        int V(int i, int j) => (j * (cells + 1)) + i;
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
                faces.AddRange(new[] { V(i, j), V(i + 1, j), V(i + 1, j + 1), V(i, j), V(i + 1, j + 1), V(i, j + 1) });
        var terrain = new IndexedTriMesh(vertices.ToArray(), vertices.Count / 3, faces.ToArray(), faces.Count / 3);

        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 5.0, 5.0, 7.0, 5.0, 7.0, 7.0, 5.0, 7.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                5.0, 5.0, 0.5,
                7.0, 5.0, 0.5,
                7.0, 7.0, 0.5,
                5.0, 7.0, 0.5
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 0.5);
        // Off the grid, so it crosses the window's edge between vertices, as a real rail does.
        var railPoints = new List<double>();
        for (int i = 0; i < cells; i++)
            railPoints.AddRange(new[] { i + 0.5, 8.3, 0.0 });
        var rail = new ConstraintPolyline(railPoints.ToArray(), cells, IsClosed: false, PreserveInputElevation: true);

        var notes = new List<string>();
        GradingResult? result = SurfaceStripGrader.GradeWindowed(
            terrain, new[] { surface }, new[] { rail }, previous: null, new GradingWindows.Memo(), notes, out string? errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Contains(notes, note => note.Contains("graded 1 window", StringComparison.Ordinal));
        Assert.Equal(0.5, FindVertexZ(result!, 6.0, 6.0), 6);
        Assert.Equal(PlanArea(terrain.Vertices, terrain.Faces, terrain.FaceCount), PlanArea(result.Vertices, result.Faces, result.FaceCount), 6);

        // Every face clear of the surface's reach comes back as it went in.
        HashSet<string> graded = FaceKeys(result.Vertices, result.Faces, result.FaceCount);
        for (int f = 0; f < terrain.FaceCount; f++)
        {
            int a = terrain.Faces[f * 3], b = terrain.Faces[f * 3 + 1], c = terrain.Faces[f * 3 + 2];
            double cx = (terrain.Vertices[a * 3] + terrain.Vertices[b * 3] + terrain.Vertices[c * 3]) / 3.0;
            double cy = (terrain.Vertices[a * 3 + 1] + terrain.Vertices[b * 3 + 1] + terrain.Vertices[c * 3 + 1]) / 3.0;
            if (cx > 2.0 && cx < 10.0 && cy > 2.0 && cy < 10.0)
                continue;
            Assert.Contains(FaceKey(terrain.Vertices, a, b, c), graded);
        }
    }

    private static HashSet<string> FaceKeys(double[] vertices, int[] faces, int faceCount)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int f = 0; f < faceCount; f++)
            keys.Add(FaceKey(vertices, faces[f * 3], faces[f * 3 + 1], faces[f * 3 + 2]));
        return keys;
    }

    private static string FaceKey(double[] vertices, int a, int b, int c) =>
        string.Join(";", new[] { a, b, c }
            .Select(i => FormattableString.Invariant($"{vertices[i * 3]:F6},{vertices[i * 3 + 1]:F6},{vertices[i * 3 + 2]:F6}"))
            .OrderBy(static key => key, StringComparer.Ordinal));

    /// <summary>
    /// A wall rail beside a stair is a barrier to the stair's batter, and a vertex on it keeps its height: the
    /// batter reaching it used to regrade it, and a stringer wall's toe rail rose 0.1 m (2026-10-28 sweep).
    /// </summary>
    [Fact]
    public void Grade_VertexOnBarrierWithinBatterReach_KeepsItsHeight()
    {
        const int cells = 20;
        var vertices = new List<double>();
        for (int j = 0; j <= cells; j++)
            for (int i = 0; i <= cells; i++)
                vertices.AddRange(new[] { (double)i, j, 0.0 });
        var faces = new List<int>();
        int V(int i, int j) => (j * (cells + 1)) + i;
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
                faces.AddRange(new[] { V(i, j), V(i + 1, j), V(i + 1, j + 1), V(i, j), V(i + 1, j + 1), V(i, j + 1) });

        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 5.0, 5.0, 7.0, 5.0, 7.0, 11.0, 5.0, 11.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                5.0, 5.0, 0.5,
                7.0, 5.0, 0.5,
                7.0, 11.0, 0.5,
                5.0, 11.0, 0.5
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 0.5);

        // A wall's toe rail 0.5 m outside the stair, well within its 0.77 m batter.
        var rail = new ConstraintPolyline(
            new[] { 7.5, 6.0, 0.0, 7.5, 8.0, 0.0, 7.5, 10.0, 0.0 },
            3,
            IsClosed: false,
            PreserveInputElevation: true);

        GradingResult? result = SurfaceStripGrader.Grade(
            new IndexedTriMesh(vertices.ToArray(), vertices.Count / 3, faces.ToArray(), faces.Count / 3),
            surface,
            new[] { rail },
            out string? errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Equal(0.0, FindVertexZ(result!, 7.5, 8.0), 9);
        Assert.True(FindVertexZ(result!, 7.0, 8.0) > 0.4, "The stair's edge should still be graded.");
    }

    private static double PlanArea(double[] vertices, int[] faces, int faceCount)
    {
        double area = 0.0;
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3] * 3, b = faces[f * 3 + 1] * 3, c = faces[f * 3 + 2] * 3;
            area += Math.Abs(
                ((vertices[b] - vertices[a]) * (vertices[c + 1] - vertices[a + 1])) -
                ((vertices[b + 1] - vertices[a + 1]) * (vertices[c] - vertices[a]))) * 0.5;
        }

        return area;
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(GradingResult result)
    {
        for (int i = 0; i < result.VertexCount; i++)
            yield return (
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
    }

    private static double FindVertexZ(GradingResult result, double x, double y)
    {
        foreach (var vertex in EnumerateVertices(result))
        {
            if (Math.Abs(vertex.x - x) <= 1e-6 && Math.Abs(vertex.y - y) <= 1e-6)
                return vertex.z;
        }

        throw new Xunit.Sdk.XunitException($"Could not find vertex at ({x}, {y}).");
    }

    private static double[] BuildGridVertices()
    {
        return new[]
        {
            -1.0, -1.0, 0.0,
             0.5, -1.0, 0.0,
             2.0, -1.0, 0.0,
             3.5, -1.0, 0.0,
            -1.0,  0.5, 0.0,
             0.5,  0.5, 0.0,
             2.0,  0.5, 0.0,
             3.5,  0.5, 0.0,
            -1.0,  2.0, 0.0,
             0.5,  2.0, 0.0,
             2.0,  2.0, 0.0,
             3.5,  2.0, 0.0,
            -1.0,  3.5, 0.0,
             0.5,  3.5, 0.0,
             2.0,  3.5, 0.0
        };
    }

    private static int[] BuildGridFaces()
    {
        return new[]
        {
            0, 1, 5,
            0, 5, 4,
            1, 2, 6,
            1, 6, 5,
            2, 3, 7,
            2, 7, 6,
            4, 5, 9,
            4, 9, 8,
            5, 6, 10,
            5, 10, 9,
            6, 7, 11,
            6, 11, 10,
            8, 9, 13,
            8, 13, 12,
            9, 10, 14,
            9, 14, 13
        };
    }
}
