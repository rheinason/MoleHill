using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PathGraderTests
{
    [Fact]
    public void Grade_InvalidTerrainFace_ReturnsFailure()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0);

        GradingResult? result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            new[] { 0, 1, 99 },
            1,
            new[] { path },
            out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("outside the terrain vertex range", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_InsertsShoulderVertices_WhenExplicitMaxDistanceFitsInsideBoundary()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 33.0,
            maxDistance: 1.5);

        var result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.x - 2.0) < 1e-6 &&
            Math.Abs(vertex.y - 7.5) < 1e-6);
    }

    [Fact]
    public void Grade_InsertsShoulderVertices_WhenAutoTransitionFindsTerrainDifference()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));

        double expectedShoulderY = 5.0 + 1.0 + 1.0 / Math.Tan(33.0 * Math.PI / 180.0);
        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.x - 2.0) < 1e-6 &&
            Math.Abs(vertex.y - expectedShoulderY) < 1e-3);
    }

    [Fact]
    public void Grade_WhenAutoDaylightDoesNotExist_DoesNotProjectShoulderToTerrainBoundary()
    {
        double[] terrain =
        {
            0.0, 0.0, 0.0,
            100.0, 0.0, 0.0,
            100.0, 100.0, 200.0,
            0.0, 100.0, 200.0
        };

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 20.0, 80.0, 20.0 },
            zValues: new[] { 0.0, 0.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 0.0);

        var result = PathGrader.Grade(
            terrain,
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            EnumerateVertices(result!),
            vertex => vertex.x >= 20.0 - 1e-6 &&
                      vertex.x <= 80.0 + 1e-6 &&
                      vertex.y >= 80.0);
    }

    [Fact]
    public void Grade_DensifiesLongPathShoulders_WhenTransitionWidthIsLarge()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 50.0, 80.0, 50.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 15.0);

        var result = PathGrader.Grade(
            BuildLargeSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));

        // Per-vertex shoulder: terrain z=0, path z=1, slope=45° → d=1, shoulder at y=50+1+1=52.
        int shoulderVertexCount = EnumerateVertices(result!)
            .Count(vertex => Math.Abs(vertex.y - 52.0) < 1e-3 && vertex.x >= 20.0 - 1e-6 && vertex.x <= 80.0 + 1e-6);

        Assert.True(shoulderVertexCount >= 5, $"Expected a densified shoulder apron, found {shoulderVertexCount} shoulder vertices.");
    }

    [Fact]
    public void CreateConstraints_RemeshesLongPathIntoApronBand()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 50.0, 80.0, 50.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 15.0);

        var constraints = PathGrader.CreateConstraints(
            BuildLargeSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            tolerance: 1e-3);

        var remesh = SurfaceRemesher.Remesh(
            BuildLargeSquareVertices(),
            BuildSquareFaces(),
            constraints.Constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 1e-3,
                RequestedEdgeLength = constraints.SuggestedEdgeLength,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);

        int shoulderVertexCount = EnumerateVertices(remesh.Vertices)
            .Count(vertex => Math.Abs(vertex.y - 52.0) < 1e-3 && vertex.x >= 20.0 - 1e-6 && vertex.x <= 80.0 + 1e-6);

        Assert.True(shoulderVertexCount >= 5, $"Expected remesh constraints to create an apron band, found {shoulderVertexCount} shoulder vertices.");
    }

    [Fact]
    public void CreateConstraints_WithoutBoundaryLoop_StillBuildsRoadAndShoulderPolylines()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 0.0, 5.0, 10.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 33.0,
            maxDistance: 1.5);

        var constraints = PathGrader.CreateConstraints(
            new[]
            {
                0.0, 0.0, 0.0,
                10.0, 0.0, 0.0
            },
            2,
            Array.Empty<int>(),
            0,
            new[] { path },
            tolerance: 1e-3);

        Assert.True(constraints.Constraints.Length >= 5);
        Assert.True(constraints.SuggestedEdgeLength > 0.0);
        Assert.All(constraints.Constraints, constraint =>
        {
            Assert.True(constraint.PointCount >= 2);
            Assert.Equal(constraint.PointCount * 3, constraint.Points.Length);
        });
    }

    // ── Fix 1 regression tests ───────────────────────────────────────────────

    /// <summary>
    /// On a coarse 4-triangle mesh the vertex-accumulation approach may find no
    /// shoulder-zone vertices at some stations → NaN reference dz → wrong daylight
    /// distance and/or cut/fill inversion → spiky shoulder Z.
    /// Fix 1 replaces that with direct faceGrid sampling. The observable invariant:
    /// every output vertex in the left shoulder zone (Y ∈ [roadEdge, daylight]) must
    /// have Z ∈ [terrainZ, pathZ] (fill scenario: terrain raised toward road level).
    /// Includes both the road-edge vertices (graded to pathZ) and the daylight vertices
    /// (kept at terrainZ by the "past daylight" guard).
    /// </summary>
    [Fact]
    public void Grade_ShoulderZ_IsBoundedBetweenTerrainAndPath_OnSparseTriangleTerrain()
    {
        const double pathZ    = 2.0;
        const double halfWidth = 1.0;
        // Flat terrain Z=0. Fill scenario: road above terrain.
        // Road edge (left) at Y = 5 + halfWidth = 6.0.
        // Explicit maxDistance=2.5 so shoulder IS created regardless of dz estimate.

        var path = new PathGrader.PathDefinition(
            xyVertices: [2.0, 5.0, 8.0, 5.0],
            zValues: [pathZ, pathZ],
            vertexCount: 2,
            width: halfWidth * 2,
            slopeAngleDeg: 45.0,
            maxDistance: 2.5);

        var result = PathGrader.Grade(
            BuildSquareVertices(), 4,
            BuildSquareFaces(), 2,
            new[] { path },
            out string? errorMessage);

        Assert.True(result != null, errorMessage);

        bool anyChecked = false;
        foreach (var (x, y, z) in EnumerateVertices(result!))
        {
            // Left shoulder zone: Y ∈ [6, 8.5], X ∈ [2, 8]
            if (y < 6.0 - 1e-4 || y > 8.5 + 1e-4) continue;
            if (x < 2.0 - 1e-4 || x > 8.0 + 1e-4) continue;

            const double terrainZ = 0.0;
            Assert.True(z >= terrainZ - 0.05,
                $"Shoulder ({x:F2},{y:F2}) Z={z:F4} below terrain {terrainZ} — spike below daylight");
            Assert.True(z <= pathZ + 0.05,
                $"Shoulder ({x:F2},{y:F2}) Z={z:F4} above pathZ {pathZ} — spike above road");
            anyChecked = true;
        }

        Assert.True(anyChecked, "No shoulder-zone vertices found — test setup may be wrong.");
    }

    /// <summary>
    /// On a hillside (terrain: Z = 3 - 0.3·x), a straight path at X=5 (pathZ=1.5)
    /// creates a cut on the left (terrain higher) and fill on the right (terrain lower).
    /// The road-edge vertices on each side must have graded Z ≤ local terrain (cut side)
    /// and graded Z ≥ local terrain (fill side).
    /// Guards against reference-profile sign inversion fixed by Fix 1 and Fix 2.
    /// </summary>
    [Fact]
    public void Grade_RoadEdgeZ_ReflectsCorrectCutFill_OnHillsidePath()
    {
        // Terrain: Z = 3 - 0.3*x. At x=5: Z=1.5 (matches pathZ).
        // At x=4.5 (left road edge): Z=1.65 > 1.5 → cut.
        // At x=5.5 (right road edge): Z=1.35 < 1.5 → fill.
        double[] terrain = [
            0.0,  0.0,  3.0,
            10.0, 0.0,  0.0,
            10.0, 10.0, 0.0,
            0.0,  10.0, 3.0,
        ];

        const double pathZ = 1.5;
        var path = new PathGrader.PathDefinition(
            xyVertices: [5.0, 1.0, 5.0, 9.0],
            zValues: [pathZ, pathZ],
            vertexCount: 2,
            width: 1.0,       // road edges at x=4.5 (left) and x=5.5 (right)
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        var result = PathGrader.Grade(terrain, 4, BuildSquareFaces(), 2, new[] { path }, out _);
        Assert.NotNull(result);

        bool foundLeft = false, foundRight = false;
        foreach (var (x, y, z) in EnumerateVertices(result!))
        {
            if (y < 1.0 - 1e-4 || y > 9.0 + 1e-4) continue;

            if (Math.Abs(x - 4.5) < 0.15) // left road edge: cut side
            {
                double terrainAtX = 3.0 - 0.3 * x; // ≈ 1.65
                Assert.True(z <= terrainAtX + 0.05,
                    $"Left road-edge ({x:F2},{y:F2}): Z={z:F4} should be ≤ terrain {terrainAtX:F4} (cut side)");
                foundLeft = true;
            }
            if (Math.Abs(x - 5.5) < 0.15) // right road edge: fill side
            {
                double terrainAtX = 3.0 - 0.3 * x; // ≈ 1.35
                Assert.True(z >= terrainAtX - 0.05,
                    $"Right road-edge ({x:F2},{y:F2}): Z={z:F4} should be ≥ terrain {terrainAtX:F4} (fill side)");
                foundRight = true;
            }
        }

        Assert.True(foundLeft,  "No left road-edge vertex near x=4.5 — test setup may be wrong.");
        Assert.True(foundRight, "No right road-edge vertex near x=5.5 — test setup may be wrong.");
    }

    /// <summary>
    /// For a path with varying elevation (Z tapers 1.5 → 0 over 8 m), the road-edge
    /// vertices (closest to the path edge, highest Z in shoulder zone) should have Z
    /// that follows pathZ linearly — ΔZ/ΔY ≤ pathZSlope + margin.
    /// A bad reference profile or endpoint tangent kink could cause the road edge to
    /// inherit a wrong Z at the first/last station, which would manifest as a cliff here.
    /// The road-edge line is checked (x ≈ 4.5) rather than the full shoulder zone to
    /// avoid conflating the transverse Z gradient with longitudinal continuity.
    /// </summary>
    [Fact]
    public void Grade_RoadEdgeZ_IsContinuous_WithTaperingPathElevation()
    {
        // Flat terrain Z=0; path Y: 1→9, Z: 1.5→0.
        // Road edge left at x ≈ 5 - 0.5 = 4.5 (path goes north, left normal is -x).
        // pathZ at y: 1.5 - 1.5*(y-1)/8 = 1.5*(9-y)/8.
        // ΔZ/ΔY = 1.5/8 = 0.1875 /m. Allow up to 0.25 with margin.
        var path = new PathGrader.PathDefinition(
            xyVertices: [5.0, 1.0, 5.0, 9.0],
            zValues: [1.5, 0.0],
            vertexCount: 2,
            width: 1.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        var result = PathGrader.Grade(
            BuildSquareVertices(), 4,
            BuildSquareFaces(), 2,
            new[] { path },
            out string? errorMessage);

        Assert.True(result != null, errorMessage);

        // Collect road-edge vertices on the left side (x ≈ 4.5 ± 0.15), sort by Y.
        const double maxZRateAlongY = 0.25; // expected 0.1875 /m + margin

        var edgeVerts = EnumerateVertices(result!)
            .Where(v => Math.Abs(v.x - 4.5) < 0.15
                      && v.y >= 1.0 - 1e-4 && v.y <= 9.0 + 1e-4)
            .OrderBy(v => v.y)
            .ToList();

        Assert.True(edgeVerts.Count >= 2, "Too few road-edge vertices found — test setup may be wrong.");

        for (int i = 1; i < edgeVerts.Count; i++)
        {
            var a = edgeVerts[i - 1];
            var b = edgeVerts[i];
            double dy = b.y - a.y;
            if (dy < 1e-4) continue;
            double dzRate = Math.Abs(b.z - a.z) / dy;
            Assert.True(
                dzRate <= maxZRateAlongY,
                $"Road-edge Z cliff: |ΔZ/ΔY|={dzRate:F4} > {maxZRateAlongY} between y={a.y:F2}(Z={a.z:F3})→y={b.y:F2}(Z={b.z:F3})");
        }
    }

    [Fact]
    public void Grade_WithBarrierBetweenRoadAndHigherFarTerrain_DoesNotSampleAcrossBarrier()
    {
        const int size = 21;
        double[] terrain = BuildGridVertices(size, spacing: 1.0);
        int[] faces = BuildGridFaces(size);
        for (int y = 15; y < size; y++)
        {
            for (int x = 0; x < size; x++)
                terrain[(GetGridVertexIndex(size, x, y) * 3) + 2] = 8.0;
        }

        var path = new PathGrader.PathDefinition(
            xyVertices: [2.0, 10.0, 18.0, 10.0],
            zValues: [0.0, 0.0],
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 5.0);

        var barrier = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                0.0, 14.0, 0.0,
                20.0, 14.0, 0.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        GradingResult? result = PathGrader.Grade(
            terrain,
            terrain.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            new[] { barrier },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);

        int vertexIndex = FindVertexIndex(result!.Vertices, 10.0, 13.0);
        double gradedZ = result.Vertices[(vertexIndex * 3) + 2];
        Assert.InRange(gradedZ, -1e-6, 0.05);
    }

    [Fact]
    public void CreateConstraints_OnInsideSharpBend_SuppressesInsideCornerGuideOnly()
    {
        const int size = 31;
        double[] terrain = BuildGridVertices(size, spacing: 1.0);
        int[] faces = BuildGridFaces(size);

        var path = new PathGrader.PathDefinition(
            xyVertices: [10.0, 10.0, 20.0, 10.0, 20.0, 20.0],
            zValues: [5.0, 5.0, 5.0],
            vertexCount: 3,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 4.0);

        PathGrader.ConstraintSet constraints = PathGrader.CreateConstraints(
            terrain,
            terrain.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            tolerance: 1e-3);

        double insideRoadX = 20.0 - (Math.Sqrt(2.0) * 0.5 * 2.0);
        double insideRoadY = 10.0 + (Math.Sqrt(2.0) * 0.5 * 2.0);
        double insideShoulderX = 20.0 - (Math.Sqrt(2.0) * 0.5 * 6.0);
        double insideShoulderY = 10.0 + (Math.Sqrt(2.0) * 0.5 * 6.0);

        Assert.DoesNotContain(
            constraints.Constraints,
            constraint => ConstraintMatchesSegment(
                constraint,
                insideRoadX,
                insideRoadY,
                insideShoulderX,
                insideShoulderY,
                tolerance: 1e-6));

        int guideCount = constraints.Constraints.Count(constraint => constraint.PointCount == 2);
        Assert.Equal(0, guideCount);
    }

    [Fact]
    public void CreateConstraints_UsesDaylightZForShoulderRuns()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 1.0);

        PathGrader.ConstraintSet constraints = PathGrader.CreateConstraints(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            tolerance: 1e-3);

        Assert.Contains(
            constraints.Constraints,
            constraint => constraint.PointCount >= 2 &&
                          Enumerable.Range(0, constraint.PointCount).All(i =>
                              Math.Abs(constraint.Points[i * 3 + 1] - 7.0) <= 1e-6 &&
                              Math.Abs(constraint.Points[i * 3 + 2]) <= 1e-6));
    }

    [Fact]
    public void ApplyGradingZ_DoesNotAdjustShoulderVertexWithinTolerance()
    {
        const int size = 11;
        double[] vertices = BuildGridVertices(size, spacing: 1.0);
        int[] faces = BuildGridFaces(size);
        int nearGradeIndex = GetGridVertexIndex(size, x: 5, y: 7);
        vertices[(nearGradeIndex * 3) + 2] = 5e-4;

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 1.0);

        double[] graded = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { path }, out _);

        Assert.Equal(5e-4, graded[(nearGradeIndex * 3) + 2], 6);
    }

    [Fact]
    public void Grade_OpenPath_ProducesSingleBoundaryLoop()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        GradingResult? result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);
        Assert.Equal(1, CountBoundaryLoops(result!.Faces, result.FaceCount));
    }

    [Fact]
    public void Grade_OpenPath_AddsFiniteAreaCapsBeyondBothEnds()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        GradingResult? result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);
        Assert.Contains(EnumerateFaceCentroids(result!), face => face.x < 2.0 - 1e-3);
        Assert.Contains(EnumerateFaceCentroids(result!), face => face.x > 8.0 + 1e-3);
    }

    [Fact]
    public void Grade_OpenPath_ReportsSectionStatusDiagnostics()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        GradingResult? result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);
        Assert.Contains(result!.Diagnostics, diagnostic => diagnostic.Contains("left sections:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("right sections:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grade_OpenPath_ReportsTopologyModeDiagnostic()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        GradingResult? result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);
        Assert.Contains(result!.Diagnostics, diagnostic => diagnostic.Contains("topology mode:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("topology summary:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.StructuredDiagnostics, diagnostic => diagnostic.Code == "grade_path.topology_mode.constraint_insertion");
        Assert.Contains(
            result.StructuredDiagnostics,
            diagnostic => diagnostic.Code == "grade_path.topology.summary" &&
                          diagnostic.Operation == "Grade Path");
        Assert.All(result.StructuredDiagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message)));
    }

    [Fact]
    public void Grade_OpenPathOnSlopedTerrain_ProducesSingleBoundaryLoop()
    {
        double[] terrain =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 1.0,
            0.0, 10.0, 1.0
        };

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 0.5, 0.5 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 33.0,
            maxDistance: 2.0);

        GradingResult? result = PathGrader.Grade(
            terrain,
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);
        Assert.True(
            CountBoundaryLoops(result!.Faces, result.FaceCount) == 1,
            string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void IsIsoElevationStrip_WhenRailsMatch_ReturnsTrue()
    {
        bool isPlanar = PathGrader.IsIsoElevationStrip(
            innerZ: new[] { 5.0, 5.0, 4.0 },
            outerZ: new[] { 5.0, 5.0, 4.0 },
            vertexCount: 3,
            tolerance: 1e-3);

        Assert.True(isPlanar);
    }

    [Fact]
    public void IsIsoElevationStrip_WhenRailsDiffer_ReturnsFalse()
    {
        bool isPlanar = PathGrader.IsIsoElevationStrip(
            innerZ: new[] { 5.0, 5.0, 4.0 },
            outerZ: new[] { 5.0, 4.5, 4.0 },
            vertexCount: 3,
            tolerance: 1e-3);

        Assert.False(isPlanar);
    }

    [Fact]
    public void TryBuildLocalizedFallbackBoundary_DiagonalPath_IsTighterThanConservativeBox()
    {
        double[] vertices = BuildGridVertices(11, 10.0);
        int vertexCount = vertices.Length / 3;
        var path = new PathGrader.PathDefinition(
            new[] { 10.0, 10.0, 90.0, 90.0 },
            new[] { 0.0, 0.0 },
            vertexCount: 2,
            width: 10.0,
            slopeAngleDeg: 33.0,
            maxDistance: 20.0);

        bool built = PathGrader.TryBuildLocalizedFallbackBoundary(
            vertices,
            vertexCount,
            path,
            tolerance: 1e-3,
            out double[] boundaryLoopXy);

        Assert.True(built);
        Assert.True(boundaryLoopXy.Length >= 8);

        double localizedArea = Math.Abs(ComputeSignedArea(boundaryLoopXy));
        double conservativeArea = ComputeConservativePathOwnedBoxArea(path);
        Assert.True(localizedArea < conservativeArea, $"localized={localizedArea:0.###}, conservative={conservativeArea:0.###}");
    }

    [Fact]
    public void CreateRoadEdgeRemeshFallbackConstraints_DensePath_ResamplesPrimaryRails()
    {
        const int sampleCount = 81;
        var pathXy = new double[sampleCount * 2];
        var pathZ = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double x = 10.0 + i;
            pathXy[i * 2] = x;
            pathXy[(i * 2) + 1] = 50.0;
            pathZ[i] = 0.0;
        }

        var path = new PathGrader.PathDefinition(
            pathXy,
            pathZ,
            sampleCount,
            width: 10.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        PathGrader.ConstraintSet constraints = PathGrader.CreateRoadEdgeRemeshFallbackConstraints(
            BuildLargeSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            tolerance: 1e-3);

        Assert.NotEmpty(constraints.Constraints);
        Assert.All(
            constraints.Constraints,
            constraint => Assert.True(
                constraint.PointCount < sampleCount,
                $"Expected fallback rail resampling to reduce point count below {sampleCount}, got {constraint.PointCount}."));
    }

    [Fact]
    public void TryValidatePathPatchForStitching_FragmentedSeamBoundary_RejectsPatch()
    {
        var seamPoints = new List<double>();
        for (int i = 0; i < 10; i++)
            seamPoints.AddRange(new[] { i, 0.0 });
        for (int i = 0; i < 10; i++)
            seamPoints.AddRange(new[] { 10.0, i });
        for (int i = 10; i > 0; i--)
            seamPoints.AddRange(new[] { i, 10.0 });
        for (int i = 10; i > 0; i--)
            seamPoints.AddRange(new[] { 0.0, i });
        double[] seamLoopXy = seamPoints.ToArray();

        const int triangleCount = 24;
        var patchVertices = new double[triangleCount * 9];
        var patchFaces = new int[triangleCount * 3];
        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            double startX = 0.15 + (triangleIndex * 0.38);
            int vertexBase = triangleIndex * 3;
            int pointBase = triangleIndex * 9;
            patchVertices[pointBase] = startX;
            patchVertices[pointBase + 1] = 10.0;
            patchVertices[pointBase + 2] = 0.0;
            patchVertices[pointBase + 3] = startX + 0.24;
            patchVertices[pointBase + 4] = 10.0;
            patchVertices[pointBase + 5] = 0.0;
            patchVertices[pointBase + 6] = startX + 0.12;
            patchVertices[pointBase + 7] = 9.998;
            patchVertices[pointBase + 8] = 0.0;

            patchFaces[triangleIndex * 3] = vertexBase;
            patchFaces[(triangleIndex * 3) + 1] = vertexBase + 1;
            patchFaces[(triangleIndex * 3) + 2] = vertexBase + 2;
        }

        bool valid = PathGrader.TryValidatePathPatchForStitching(
            seamLoopXy,
            patchVertices,
            patchFaces,
            patchFaceCount: triangleCount,
            BuildSquareVertices(),
            BuildSquareFaces(),
            outsideFaceCount: 2,
            tolerance: 1e-3,
            out double[] patchBoundaryLoopXy,
            out SeamGraph? seamGraph,
            out string? errorMessage);

        Assert.False(valid);
        Assert.Empty(patchBoundaryLoopXy);
        Assert.NotNull(seamGraph);
        Assert.Contains("fragmentation", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(GradingResult result)
    {
        for (int i = 0; i < result.VertexCount; i++)
            yield return (
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(double[] vertices)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
            yield return (
                vertices[i * 3],
                vertices[i * 3 + 1],
                vertices[i * 3 + 2]);
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateFaceCentroids(GradingResult result)
    {
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            int a = result.Faces[faceIndex * 3];
            int b = result.Faces[faceIndex * 3 + 1];
            int c = result.Faces[faceIndex * 3 + 2];
            yield return (
                (result.Vertices[a * 3] + result.Vertices[b * 3] + result.Vertices[c * 3]) / 3.0,
                (result.Vertices[a * 3 + 1] + result.Vertices[b * 3 + 1] + result.Vertices[c * 3 + 1]) / 3.0,
                (result.Vertices[a * 3 + 2] + result.Vertices[b * 3 + 2] + result.Vertices[c * 3 + 2]) / 3.0);
        }
    }

    private static double[] BuildSquareVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 0.0,
            0.0, 10.0, 0.0
        };
    }

    private static double[] BuildLargeSquareVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            100.0, 0.0, 0.0,
            100.0, 100.0, 0.0,
            0.0, 100.0, 0.0
        };
    }

    private static int[] BuildSquareFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }

    private static double[] BuildGridVertices(int size, double spacing)
    {
        var vertices = new double[size * size * 3];
        int index = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                vertices[index++] = x * spacing;
                vertices[index++] = y * spacing;
                vertices[index++] = 0.0;
            }
        }

        return vertices;
    }

    private static int[] BuildGridFaces(int size)
    {
        var faces = new int[(size - 1) * (size - 1) * 6];
        int index = 0;
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                int v0 = (y * size) + x;
                int v1 = v0 + 1;
                int v2 = v0 + size;
                int v3 = v2 + 1;

                faces[index++] = v0;
                faces[index++] = v1;
                faces[index++] = v3;
                faces[index++] = v0;
                faces[index++] = v3;
                faces[index++] = v2;
            }
        }

        return faces;
    }

    private static double ComputeSignedArea(double[] loopXy)
    {
        double area = 0.0;
        int count = loopXy.Length / 2;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            area += (loopXy[i * 2] * loopXy[next * 2 + 1]) - (loopXy[next * 2] * loopXy[i * 2 + 1]);
        }

        return area * 0.5;
    }

    private static double ComputeConservativePathOwnedBoxArea(PathGrader.PathDefinition path)
    {
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int i = 0; i < path.VertexCount; i++)
        {
            double x = path.XyVertices[i * 2];
            double y = path.XyVertices[i * 2 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double halfWidth = path.Width * 0.5;
        double shoulderAllowance = path.MaxDistance > 0.0
            ? path.MaxDistance
            : Math.Max(path.Width * 2.0, halfWidth);
        double expansion = halfWidth + shoulderAllowance;
        return ((maxX - minX) + (expansion * 2.0)) * ((maxY - minY) + (expansion * 2.0));
    }

    private static int GetGridVertexIndex(int size, int x, int y)
    {
        return (y * size) + x;
    }

    private static int FindVertexIndex(double[] vertices, double x, double y)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= 1e-6 &&
                Math.Abs(vertices[i * 3 + 1] - y) <= 1e-6)
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not find vertex at ({x}, {y}).");
    }

    private static bool ConstraintMatchesSegment(
        SurfaceRemesher.ConstraintPolyline constraint,
        double ax,
        double ay,
        double bx,
        double by,
        double tolerance)
    {
        if (constraint.PointCount != 2)
            return false;

        return (PointMatches(constraint.Points, 0, ax, ay, tolerance) &&
                PointMatches(constraint.Points, 1, bx, by, tolerance)) ||
               (PointMatches(constraint.Points, 0, bx, by, tolerance) &&
                PointMatches(constraint.Points, 1, ax, ay, tolerance));
    }

    private static bool SegmentMatchesWithZ(
        SurfaceRemesher.ConstraintPolyline constraint,
        double ax,
        double ay,
        double az,
        double bx,
        double by,
        double bz,
        double tolerance)
    {
        if (constraint.PointCount != 2)
            return false;

        return (PointMatchesWithZ(constraint.Points, 0, ax, ay, az, tolerance) &&
                PointMatchesWithZ(constraint.Points, 1, bx, by, bz, tolerance)) ||
               (PointMatchesWithZ(constraint.Points, 0, bx, by, bz, tolerance) &&
                PointMatchesWithZ(constraint.Points, 1, ax, ay, az, tolerance));
    }

    private static bool PointMatches(double[] points, int pointIndex, double x, double y, double tolerance)
    {
        return Math.Abs(points[pointIndex * 3] - x) <= tolerance &&
               Math.Abs(points[(pointIndex * 3) + 1] - y) <= tolerance;
    }

    private static bool PointMatchesWithZ(double[] points, int pointIndex, double x, double y, double z, double tolerance)
    {
        return Math.Abs(points[pointIndex * 3] - x) <= tolerance &&
               Math.Abs(points[(pointIndex * 3) + 1] - y) <= tolerance &&
               Math.Abs(points[(pointIndex * 3) + 2] - z) <= tolerance;
    }

    private static int CountBoundaryLoops(int[] faces, int faceCount)
    {
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            IncrementEdge(edgeFaceCount, faces[faceIndex * 3], faces[faceIndex * 3 + 1]);
            IncrementEdge(edgeFaceCount, faces[faceIndex * 3 + 1], faces[faceIndex * 3 + 2]);
            IncrementEdge(edgeFaceCount, faces[faceIndex * 3 + 2], faces[faceIndex * 3]);
        }

        var adjacency = new Dictionary<int, HashSet<int>>();
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
        }

        int loops = 0;
        var visited = new HashSet<int>();
        foreach (int vertex in adjacency.Keys)
        {
            if (!visited.Add(vertex))
                continue;

            loops++;
            var queue = new Queue<int>();
            queue.Enqueue(vertex);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        queue.Enqueue(next);
                }
            }
        }

        return loops;
    }

    private static void IncrementEdge(Dictionary<long, int> edgeFaceCount, int a, int b)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        edgeFaceCount.TryGetValue(key, out int value);
        edgeFaceCount[key] = value + 1;
    }

    private static void AddBoundaryNeighbor(Dictionary<int, HashSet<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out HashSet<int>? neighbors))
        {
            neighbors = new HashSet<int>();
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
    }

}
