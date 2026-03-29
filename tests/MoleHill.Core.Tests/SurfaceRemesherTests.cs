using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class SurfaceRemesherTests
{
    [Fact]
    public void Remesh_OpenBoundary_PreSplitsBoundaryEdgesNearTargetLength()
    {
        var result = SurfaceRemesher.Remesh(
            CreateSlopedSquareVertices(),
            CreateSquareFaces(),
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 2.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(result.Success, result.Warning);

        var boundarySegments = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundarySegments, new HashSet<long>(), result.Faces, result.Faces.Length / 3);

        Assert.Equal(20, boundarySegments.Count);

        double minBoundaryLength = boundarySegments
            .Select(segment => GetEdgeLength(result.Vertices, segment.a, segment.b))
            .Min();

        Assert.True(minBoundaryLength >= 1.9, $"Expected boundary edges near 2.0m, got minimum {minBoundaryLength:F3}.");
    }

    [Fact]
    public void Remesh_InternalConstraint_PreSplitsConstraintWithoutTinyEdges()
    {
        var constraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                0.0, 5.0, 5.0,
                10.0, 5.0, 5.0
            },
            PointCount: 2,
            IsClosed: false);

        var result = SurfaceRemesher.Remesh(
            CreateSlopedSquareVertices(),
            CreateSquareFaces(),
            new[] { constraint },
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 2.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(result.Success, result.Warning);

        var constraintEdgeLengths = GetUniqueEdges(result.Faces)
            .Where(edge => IsConstraintEdge(result.Vertices, edge.a, edge.b))
            .Select(edge => GetEdgeLength(result.Vertices, edge.a, edge.b))
            .ToList();

        Assert.Equal(5, constraintEdgeLengths.Count);
        Assert.True(constraintEdgeLengths.Min() >= 1.9, $"Expected constraint edges near 2.0m, got minimum {constraintEdgeLengths.Min():F3}.");
    }

    [Fact]
    public void Remesh_PreserveInputElevation_KeepsConstraintVerticesOnConstraintZ()
    {
        var constraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                0.0, 5.0, 4.0,
                10.0, 5.0, 4.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        var result = SurfaceRemesher.Remesh(
            CreateSlopedSquareVertices(),
            CreateSquareFaces(),
            new[] { constraint },
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 2.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(result.Success, result.Warning);

        var constraintVertices = Enumerable.Range(0, result.Vertices.Length / 3)
            .Where(index => Math.Abs(result.Vertices[index * 3 + 1] - 5.0) < 1e-6)
            .ToList();

        Assert.NotEmpty(constraintVertices);
        Assert.All(constraintVertices, index => Assert.Equal(4.0, result.Vertices[index * 3 + 2], 6));
    }

    [Fact]
    public void Remesh_ClosedInnerConstraintLoop_RefinesInteriorRegion()
    {
        var constraint = new SurfaceRemesher.ConstraintPolyline(
            CreateRegularLoopPoints(centerX: 10.0, centerY: 10.0, radius: 4.0, sides: 12, z: 5.0),
            PointCount: 12,
            IsClosed: true,
            PreserveInputElevation: true);

        var result = SurfaceRemesher.Remesh(
            CreateLargeSquareVertices(),
            CreateSquareFaces(),
            new[] { constraint },
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 1.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(result.Success, result.Warning);

        int interiorVertexCount = Enumerable.Range(0, result.Vertices.Length / 3)
            .Count(index => IsStrictlyInsideRegularLoop(
                result.Vertices[index * 3],
                result.Vertices[index * 3 + 1],
                centerX: 10.0,
                centerY: 10.0,
                radius: 4.0,
                sides: 12,
                margin: 0.15));

        Assert.True(
            interiorVertexCount >= 20,
            $"Expected refined interior vertices inside closed constraint loop, got {interiorVertexCount}. Warning: {result.Warning}");
    }

    [Fact]
    public void Remesh_TwoParallelOpenConstraints_RefinesStripBetweenThem()
    {
        var topConstraint = new SurfaceRemesher.ConstraintPolyline(
            CreateArcPolylinePoints(centerX: 10.0, centerY: 10.0, radius: 4.0, startAngleDeg: 180.0, endAngleDeg: 0.0, divisions: 12, z: 6.0),
            PointCount: 13,
            IsClosed: false,
            PreserveInputElevation: true);

        var bottomConstraint = new SurfaceRemesher.ConstraintPolyline(
            CreateArcPolylinePoints(centerX: 10.0, centerY: 10.0, radius: 2.4, startAngleDeg: 180.0, endAngleDeg: 0.0, divisions: 12, z: 4.0),
            PointCount: 13,
            IsClosed: false,
            PreserveInputElevation: true);

        var result = SurfaceRemesher.Remesh(
            CreateLargeSquareVertices(),
            CreateSquareFaces(),
            new[] { topConstraint, bottomConstraint },
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 1.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(result.Success, result.Warning);

        int stripInteriorVertexCount = Enumerable.Range(0, result.Vertices.Length / 3)
            .Count(index =>
            {
                double x = result.Vertices[index * 3];
                double y = result.Vertices[index * 3 + 1];
                return IsInsideArcStrip(x, y, centerX: 10.0, centerY: 10.0, outerRadius: 4.0, innerRadius: 2.4, margin: 0.15);
            });

        double maxStripTriangleArea = GetMaxTriangleAreaInArcStrip(
            result.Vertices,
            result.Faces,
            centerX: 10.0,
            centerY: 10.0,
            outerRadius: 4.0,
            innerRadius: 2.4,
            margin: 0.15);

        Assert.True(
            stripInteriorVertexCount >= 10,
            $"Expected refined vertices inside strip between open constraints, got {stripInteriorVertexCount}. Max strip triangle area: {maxStripTriangleArea:F3}. Warning: {result.Warning}");

        Assert.True(
            maxStripTriangleArea <= 0.9,
            $"Expected strip triangles near target area, got max {maxStripTriangleArea:F3}. Interior vertices: {stripInteriorVertexCount}. Warning: {result.Warning}");
    }

    [Fact]
    public void Remesh_TwoBreaklinesWithInferredBoundary_DoesNotLeaveCoarseIsland()
    {
        var topConstraint = new SurfaceRemesher.ConstraintPolyline(
            CreateArcPolylinePoints(centerX: 10.0, centerY: 10.0, radius: 4.0, startAngleDeg: 180.0, endAngleDeg: 0.0, divisions: 12, z: 6.0),
            PointCount: 13,
            IsClosed: false,
            PreserveInputElevation: true);

        var bottomConstraint = new SurfaceRemesher.ConstraintPolyline(
            CreateArcPolylinePoints(centerX: 10.0, centerY: 10.0, radius: 2.4, startAngleDeg: 180.0, endAngleDeg: 0.0, divisions: 12, z: 4.0),
            PointCount: 13,
            IsClosed: false,
            PreserveInputElevation: true);

        var (xy, z, segments) = CreateTinInputs(topConstraint, bottomConstraint);
        var prepared = TinBoundaryPreparer.Prepare(
            xy,
            z,
            segments,
            Array.Empty<TinBoundaryPreparer.BoundaryPolyline>(),
            0.001);

        var engine = new TinEngine();
        var tin = engine.Build(
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            QualitySettings.None,
            out string? buildMessage,
            useConvexHull: prepared.UseConvexHull);

        Assert.NotNull(tin);

        var remesh = SurfaceRemesher.Remesh(
            tin!.Vertices,
            tin.Faces,
            new[] { topConstraint, bottomConstraint },
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 1.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, buildMessage ?? remesh.Warning);

        double maxStripTriangleArea = GetMaxTriangleAreaInArcStrip(
            remesh.Vertices,
            remesh.Faces,
            centerX: 10.0,
            centerY: 10.0,
            outerRadius: 4.0,
            innerRadius: 2.4,
            margin: 0.15);

        Assert.True(
            maxStripTriangleArea <= 0.9,
            $"Expected no coarse island between inferred-boundary breaklines, got max strip triangle area {maxStripTriangleArea:F3}. Build: {buildMessage} Remesh: {remesh.Warning}");
    }

    [Fact]
    public void Remesh_UserReportedOpenBreaklines_DoesNotCreateHolesOrCoarseApronTriangles()
    {
        var constraints = CreateUserReportedBreaklines();
        var (xy, z, segments) = CreateTinInputs(constraints);
        var prepared = TinBoundaryPreparer.Prepare(
            xy,
            z,
            segments,
            Array.Empty<TinBoundaryPreparer.BoundaryPolyline>(),
            0.001);

        var engine = new TinEngine();
        var tin = engine.Build(
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            QualitySettings.None,
            out string? buildMessage,
            useConvexHull: prepared.UseConvexHull);

        Assert.NotNull(tin);

        var remesh = SurfaceRemesher.Remesh(
            tin!.Vertices,
            tin.Faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 1.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, buildMessage ?? remesh.Warning);

        int boundaryLoopCount = CountBoundaryLoops(remesh.Faces);
        var maxApronTriangle = GetMaxTriangleInBounds(
            remesh.Vertices,
            remesh.Faces,
            minX: 18.0,
            maxX: 53.5,
            minY: 29.0,
            maxY: 50.5);

        Assert.Equal(
            1,
            boundaryLoopCount);
        Assert.True(
            maxApronTriangle.Area <= 30.0,
            $"Expected no very large apron triangles, got max area {maxApronTriangle.Area:F3} at centroid ({maxApronTriangle.CentroidX:F3}, {maxApronTriangle.CentroidY:F3}) with vertices ({maxApronTriangle.Ax:F3}, {maxApronTriangle.Ay:F3}) / ({maxApronTriangle.Bx:F3}, {maxApronTriangle.By:F3}) / ({maxApronTriangle.Cx:F3}, {maxApronTriangle.Cy:F3}). Build: {buildMessage} Remesh: {remesh.Warning}");
    }

    [Fact]
    public void Remesh_UserReportedOpenBreaklines_OnCoarseEnvelope_DoesNotLeaveVeryLargeApronTriangles()
    {
        var constraints = CreateUserReportedBreaklines();
        var remesh = SurfaceRemesher.Remesh(
            CreateUserReportedEnvelopeVertices(),
            CreateSquareFaces(),
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 4.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);

        var boundary = MeshTopologyValidator.AnalyzeBoundaryGraph(remesh.Faces, remesh.Faces.Length / 3);
        var maxApronTriangle = GetMaxTriangleInBounds(
            remesh.Vertices,
            remesh.Faces,
            minX: 18.0,
            maxX: 53.5,
            minY: 29.0,
            maxY: 50.5);

        Assert.True(boundary.HasSingleClosedBoundaryLoop, remesh.Warning);
        Assert.True(remesh.UsedBoundaryAndGuideSeedFallback, "Expected coarse-envelope remesh to prefer the boundary-and-guide fallback.");
        Assert.True(
            maxApronTriangle.Area <= 30.0,
            $"Expected no very large apron triangles on coarse envelope, got max area {maxApronTriangle.Area:F3} at centroid ({maxApronTriangle.CentroidX:F3}, {maxApronTriangle.CentroidY:F3}) with vertices ({maxApronTriangle.Ax:F3}, {maxApronTriangle.Ay:F3}) / ({maxApronTriangle.Bx:F3}, {maxApronTriangle.By:F3}) / ({maxApronTriangle.Cx:F3}, {maxApronTriangle.Cy:F3}). Remesh: {remesh.Warning}");
    }

    [Fact]
    public void Remesh_DisconnectedSourceMesh_KeepsInputMeshWhenBothPassesFail()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 10.0,
            0.0, 10.0, 10.0,
            20.0, 0.0, 0.0,
            30.0, 0.0, 0.0,
            30.0, 10.0, 10.0,
            20.0, 10.0, 10.0
        };

        int[] faces =
        {
            0, 1, 2,
            0, 2, 3,
            4, 5, 6,
            4, 6, 7
        };

        var remesh = SurfaceRemesher.Remesh(
            vertices,
            faces,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 1.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.False(remesh.Success);
        Assert.True(remesh.ReturnedInputMesh, remesh.Warning);
        Assert.Equal(vertices, remesh.Vertices);
        Assert.Equal(faces, remesh.Faces);
        Assert.Contains("kept the upstream mesh unchanged", remesh.Warning ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remesh_BranchedBoundary_KeepsInputMeshWhenTopologyWouldContainOpenChains()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            8.0, 0.0, 0.0,
            0.0, 8.0, 8.0,
            -8.0, 0.0, 0.0,
            0.0, -8.0, -8.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 3, 4
        };

        var remesh = SurfaceRemesher.Remesh(
            vertices,
            faces,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 1.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.False(remesh.Success);
        Assert.True(remesh.ReturnedInputMesh, remesh.Warning);
        Assert.Contains("open naked-edge chains", remesh.Warning ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remesh_ClosedConstraintBand_OnVeryCoarseEnvelope_PrefersBoundaryAndGuideFallback()
    {
        const double centerX = 100.0;
        const double centerY = 100.0;
        const int sides = 6;
        const double outerRadius = 50.0;
        const double innerRadius = 15.0;
        double[] envelope =
        {
            0.0, 0.0, 0.0,
            200.0, 0.0, 0.0,
            200.0, 200.0, 10.0,
            0.0, 200.0, 10.0
        };

        var outerConstraint = new SurfaceRemesher.ConstraintPolyline(
            CreateRegularLoopPoints(centerX, centerY, outerRadius, sides, z: 6.0),
            PointCount: sides,
            IsClosed: true,
            PreserveInputElevation: true);
        var innerConstraint = new SurfaceRemesher.ConstraintPolyline(
            CreateRegularLoopPoints(centerX, centerY, innerRadius, sides, z: 4.0),
            PointCount: sides,
            IsClosed: true,
            PreserveInputElevation: true);

        var remesh = SurfaceRemesher.Remesh(
            envelope,
            CreateSquareFaces(),
            new[] { outerConstraint, innerConstraint },
            new SurfaceRemesher.Options
            {
                Tolerance = 0.001,
                RequestedEdgeLength = 1.0,
                MinAngle = 20.0,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);
        Assert.True(remesh.UsedBoundaryAndGuideSeedFallback, "Expected the closed-constraint band to prefer the boundary-and-guide fallback.");

        int bandVertexCount = Enumerable.Range(0, remesh.Vertices.Length / 3)
            .Count(index =>
            {
                double x = remesh.Vertices[index * 3];
                double y = remesh.Vertices[index * 3 + 1];
                return IsStrictlyInsideRegularLoop(x, y, centerX, centerY, outerRadius, sides, margin: 0.5) &&
                       !IsStrictlyInsideRegularLoop(x, y, centerX, centerY, innerRadius, sides, margin: 0.5);
            });

        Assert.True(
            bandVertexCount >= 20,
            $"Expected fallback to seed the closed-constraint band, got {bandVertexCount} band vertices. Warning: {remesh.Warning}");
    }

    private static double[] CreateSlopedSquareVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 10.0,
            0.0, 10.0, 10.0
        };
    }

    private static double[] CreateLargeSquareVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            20.0, 0.0, 0.0,
            20.0, 20.0, 10.0,
            0.0, 20.0, 10.0
        };
    }

    private static double[] CreateUserReportedEnvelopeVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            53.30, 0.0, 0.0,
            53.30, 76.79, 10.62,
            0.0, 76.79, 10.62
        };
    }

    private static int[] CreateSquareFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }

    private static IEnumerable<(int a, int b)> GetUniqueEdges(int[] faces)
    {
        var seen = new HashSet<long>();
        for (int i = 0; i < faces.Length / 3; i++)
        {
            AddEdge(seen, faces[i * 3], faces[i * 3 + 1], out var edge0);
            AddEdge(seen, faces[i * 3 + 1], faces[i * 3 + 2], out var edge1);
            AddEdge(seen, faces[i * 3 + 2], faces[i * 3], out var edge2);

            if (edge0.HasValue) yield return edge0.Value;
            if (edge1.HasValue) yield return edge1.Value;
            if (edge2.HasValue) yield return edge2.Value;
        }
    }

    private static void AddEdge(HashSet<long> seen, int a, int b, out (int a, int b)? edge)
    {
        int min = Math.Min(a, b);
        int max = Math.Max(a, b);
        long key = ((long)min << 32) | (uint)max;
        edge = seen.Add(key) ? (min, max) : null;
    }

    private static bool IsConstraintEdge(double[] vertices, int a, int b)
    {
        return Math.Abs(vertices[a * 3 + 1] - 5.0) < 1e-6 &&
               Math.Abs(vertices[b * 3 + 1] - 5.0) < 1e-6;
    }

    private static double GetEdgeLength(double[] vertices, int a, int b)
    {
        double dx = vertices[a * 3] - vertices[b * 3];
        double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double[] CreateRegularLoopPoints(double centerX, double centerY, double radius, int sides, double z)
    {
        var points = new double[sides * 3];
        for (int i = 0; i < sides; i++)
        {
            double angle = (Math.PI * 2.0 * i) / sides;
            points[i * 3] = centerX + radius * Math.Cos(angle);
            points[i * 3 + 1] = centerY + radius * Math.Sin(angle);
            points[i * 3 + 2] = z;
        }

        return points;
    }

    private static (double[] xy, double[] z, int[] segments) CreateTinInputs(params SurfaceRemesher.ConstraintPolyline[] breaklines)
    {
        var xy = new List<double>();
        var z = new List<double>();
        var segments = new List<int>();

        foreach (var breakline in breaklines)
        {
            int startIndex = z.Count;
            for (int i = 0; i < breakline.PointCount; i++)
            {
                xy.Add(breakline.Points[i * 3]);
                xy.Add(breakline.Points[i * 3 + 1]);
                z.Add(breakline.Points[i * 3 + 2]);

                if (i > 0)
                {
                    segments.Add(startIndex + i - 1);
                    segments.Add(startIndex + i);
                }
            }
        }

        return (xy.ToArray(), z.ToArray(), segments.ToArray());
    }

    private static double[] CreateArcPolylinePoints(double centerX, double centerY, double radius, double startAngleDeg, double endAngleDeg, int divisions, double z)
    {
        var points = new double[(divisions + 1) * 3];
        double startAngle = startAngleDeg * Math.PI / 180.0;
        double endAngle = endAngleDeg * Math.PI / 180.0;
        for (int i = 0; i <= divisions; i++)
        {
            double t = i / (double)divisions;
            double angle = startAngle + ((endAngle - startAngle) * t);
            points[i * 3] = centerX + radius * Math.Cos(angle);
            points[i * 3 + 1] = centerY + radius * Math.Sin(angle);
            points[i * 3 + 2] = z;
        }

        return points;
    }

    private static SurfaceRemesher.ConstraintPolyline[] CreateUserReportedBreaklines()
    {
        return new[]
        {
            new SurfaceRemesher.ConstraintPolyline(
                new[]
                {
                    0.0, 0.0, 0.0,
                    53.30, 0.0, 0.0
                },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: true),
            new SurfaceRemesher.ConstraintPolyline(
                new[]
                {
                    0.0, 76.79, 10.62,
                    53.30, 76.79, 10.62
                },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: true),
            new SurfaceRemesher.ConstraintPolyline(
                new[]
                {
                    22.70, 41.88, 2.17,
                    34.55, 29.92, 0.52,
                    44.42, 29.92, 0.52
                },
                PointCount: 3,
                IsClosed: false,
                PreserveInputElevation: true),
            new SurfaceRemesher.ConstraintPolyline(
                new[]
                {
                    0.0, 49.93, 6.90,
                    53.30, 49.93, 6.90
                },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: true),
            new SurfaceRemesher.ConstraintPolyline(
                new[]
                {
                    22.84, 42.02, 2.78,
                    34.63, 30.12, 4.71,
                    44.42, 30.12, 1.40
                },
                PointCount: 3,
                IsClosed: false,
                PreserveInputElevation: true)
        };
    }

    private static bool IsStrictlyInsideRegularLoop(double x, double y, double centerX, double centerY, double radius, int sides, double margin)
    {
        var polygon = Enumerable.Range(0, sides)
            .Select(i =>
            {
                double angle = (Math.PI * 2.0 * i) / sides;
                return (
                    X: centerX + radius * Math.Cos(angle),
                    Y: centerY + radius * Math.Sin(angle));
            })
            .ToArray();

        if (!IsPointInPolygon(x, y, polygon))
            return false;

        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            if (DistanceToSegment(x, y, a.X, a.Y, b.X, b.Y) <= margin)
                return false;
        }

        return true;
    }

    private static bool IsInsideArcStrip(double x, double y, double centerX, double centerY, double outerRadius, double innerRadius, double margin)
    {
        double dx = x - centerX;
        double dy = y - centerY;
        if (x <= centerX - outerRadius + margin || x >= centerX + outerRadius - margin)
            return false;

        double distance = Math.Sqrt(dx * dx + dy * dy);
        return distance < outerRadius - margin &&
               distance > innerRadius + margin &&
               y >= centerY - margin;
    }

    private static bool IsPointInPolygon(double x, double y, IReadOnlyList<(double X, double Y)> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i].X;
            double yi = polygon[i].Y;
            double xj = polygon[j].X;
            double yj = polygon[j].Y;

            bool intersects = ((yi > y) != (yj > y)) &&
                              (x < ((xj - xi) * (y - yi) / ((yj - yi) + 1e-20)) + xi);
            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-20)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
        double cx = ax + t * dx;
        double cy = ay + t * dy;
        double ddx = px - cx;
        double ddy = py - cy;
        return Math.Sqrt(ddx * ddx + ddy * ddy);
    }

    private static double GetMaxTriangleAreaInArcStrip(double[] vertices, int[] faces, double centerX, double centerY, double outerRadius, double innerRadius, double margin)
    {
        double maxArea = 0.0;
        for (int i = 0; i < faces.Length / 3; i++)
        {
            int a = faces[i * 3];
            int b = faces[i * 3 + 1];
            int c = faces[i * 3 + 2];
            double centroidX = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double centroidY = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
            if (!IsInsideArcStrip(centroidX, centroidY, centerX, centerY, outerRadius, innerRadius, margin))
                continue;

            maxArea = Math.Max(maxArea, TriangleArea(vertices, a, b, c));
        }

        return maxArea;
    }

    private readonly record struct TriangleStat(
        double Area,
        double CentroidX,
        double CentroidY,
        double Ax,
        double Ay,
        double Bx,
        double By,
        double Cx,
        double Cy);

    private static TriangleStat GetMaxTriangleInBounds(
        double[] vertices,
        int[] faces,
        double minX,
        double maxX,
        double minY,
        double maxY)
    {
        double maxArea = 0.0;
        double centroidAtMaxX = 0.0;
        double centroidAtMaxY = 0.0;
        double axAtMax = 0.0;
        double ayAtMax = 0.0;
        double bxAtMax = 0.0;
        double byAtMax = 0.0;
        double cxAtMax = 0.0;
        double cyAtMax = 0.0;
        for (int i = 0; i < faces.Length / 3; i++)
        {
            int a = faces[i * 3];
            int b = faces[i * 3 + 1];
            int c = faces[i * 3 + 2];
            double centroidX = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double centroidY = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
            if (centroidX < minX || centroidX > maxX || centroidY < minY || centroidY > maxY)
                continue;

            double area = TriangleArea(vertices, a, b, c);
            if (area > maxArea)
            {
                maxArea = area;
                centroidAtMaxX = centroidX;
                centroidAtMaxY = centroidY;
                axAtMax = vertices[a * 3];
                ayAtMax = vertices[a * 3 + 1];
                bxAtMax = vertices[b * 3];
                byAtMax = vertices[b * 3 + 1];
                cxAtMax = vertices[c * 3];
                cyAtMax = vertices[c * 3 + 1];
            }
        }

        return new TriangleStat(maxArea, centroidAtMaxX, centroidAtMaxY, axAtMax, ayAtMax, bxAtMax, byAtMax, cxAtMax, cyAtMax);
    }

    private static int CountBoundaryLoops(int[] faces)
    {
        var edgeCounts = new Dictionary<long, int>();
        for (int i = 0; i < faces.Length / 3; i++)
        {
            CountEdge(edgeCounts, faces[i * 3], faces[i * 3 + 1]);
            CountEdge(edgeCounts, faces[i * 3 + 1], faces[i * 3 + 2]);
            CountEdge(edgeCounts, faces[i * 3 + 2], faces[i * 3]);
        }

        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (key, count) in edgeCounts)
        {
            if (count != 1)
                continue;

            int a = (int)(key >> 32);
            int b = (int)(key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
        }

        var visited = new HashSet<int>();
        int loops = 0;
        foreach (int start in adjacency.Keys)
        {
            if (!visited.Add(start))
                continue;

            loops++;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        stack.Push(next);
                }
            }
        }

        return loops;
    }

    private static void CountEdge(Dictionary<long, int> edgeCounts, int a, int b)
    {
        int min = Math.Min(a, b);
        int max = Math.Max(a, b);
        long key = ((long)min << 32) | (uint)max;
        edgeCounts[key] = edgeCounts.GetValueOrDefault(key, 0) + 1;
    }

    private static void AddBoundaryNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out var neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
    }

    private static double TriangleArea(double[] vertices, int a, int b, int c)
    {
        double ax = vertices[a * 3];
        double ay = vertices[a * 3 + 1];
        double bx = vertices[b * 3];
        double by = vertices[b * 3 + 1];
        double cx = vertices[c * 3];
        double cy = vertices[c * 3 + 1];
        return Math.Abs(((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax))) * 0.5;
    }
}
