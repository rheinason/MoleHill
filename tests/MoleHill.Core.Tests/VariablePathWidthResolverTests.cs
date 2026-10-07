using MoleHill.Core.Grading;
using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class VariablePathWidthResolverTests
{
    [Fact]
    public void Resolve_TwoFullOpenEdges_AssignsPlanRailsAndKeepsCenterlineElevations()
    {
        PathGrader.PathDefinition path = Path(
            new[] { 0.0, 0.0, 5.0, 0.0, 10.0, 0.0 },
            new[] { 10.0, 11.0, 12.0 });
        VariablePathWidthResolver.EdgeDefinition[] edges =
        {
            Edge(0, false, 0, 3, 5, 4, 10, 5),
            Edge(1, false, 0, -2, 5, -3, 10, -4)
        };

        VariablePathWidthResolver.Result result = VariablePathWidthResolver.Resolve(
            new[] { path },
            edges,
            new VariablePathWidthResolver.Options { MaxEdgeDistance = 10.0 });

        PathGrader.PathDefinition resolved = Assert.Single(result.Paths);
        Assert.True(resolved.HasVariableWidth);
        Assert.Equal(2, result.MatchedEdgeCount);
        Assert.Equal(path.ZValues, resolved.ZValues);
        Assert.Equal(5.0, resolved.LeftEdgeXy![^1], 8);
        Assert.Equal(-4.0, resolved.RightEdgeXy![^1], 8);
    }

    [Fact]
    public void Resolve_PartialEdge_BlendsInsideCoverageAndFallsBackOutside()
    {
        PathGrader.PathDefinition path = Path(
            new[] { 0.0, 0.0, 2.0, 0.0, 4.0, 0.0, 6.0, 0.0, 8.0, 0.0, 10.0, 0.0 },
            new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 });
        VariablePathWidthResolver.EdgeDefinition[] edges =
        {
            Edge(0, false, 2, 3, 4, 3, 6, 3, 8, 3),
            Edge(1, false, 0, -1, 5, -1, 10, -1)
        };

        VariablePathWidthResolver.Result result = VariablePathWidthResolver.Resolve(
            new[] { path },
            edges,
            new VariablePathWidthResolver.Options { MaxEdgeDistance = 5.0 });

        PathGrader.PathDefinition resolved = Assert.Single(result.Paths);
        Assert.Equal(1, result.PartialEdgeCount);
        Assert.Equal(1.0, YAtX(resolved, resolved.LeftEdgeXy!, 0.0), 8);
        Assert.Equal(1.0, YAtX(resolved, resolved.LeftEdgeXy!, 2.0), 8);
        Assert.True(YAtX(resolved, resolved.LeftEdgeXy!, 4.0) > 1.0);
        Assert.True(YAtX(resolved, resolved.LeftEdgeXy!, 6.0) > 1.0);
        Assert.Equal(1.0, YAtX(resolved, resolved.LeftEdgeXy!, 10.0), 8);
    }

    [Fact]
    public void Resolve_AmbiguousEdgeBetweenCenterlines_IsNotClaimed()
    {
        PathGrader.PathDefinition lower = Path(new[] { 0.0, 0.0, 10.0, 0.0 }, new[] { 0.0, 0.0 });
        PathGrader.PathDefinition upper = Path(new[] { 0.0, 2.0, 10.0, 2.0 }, new[] { 0.0, 0.0 });
        VariablePathWidthResolver.EdgeDefinition edge = Edge(7, false, 0, 1, 10, 1);

        VariablePathWidthResolver.Result result = VariablePathWidthResolver.Resolve(
            new[] { lower, upper },
            new[] { edge },
            new VariablePathWidthResolver.Options { MaxEdgeDistance = 4.0 });

        Assert.Equal(0, result.MatchedEdgeCount);
        Assert.All(result.Paths, path => Assert.False(path.HasVariableWidth));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "grade_path.variable_edge.ambiguous_path");
    }

    [Fact]
    public void Resolve_ClosedLoops_AlignsInnerAndOuterEdges()
    {
        PathGrader.PathDefinition center = new(
            Square(5),
            new[] { 2.0, 2.0, 2.0, 2.0, 2.0 },
            5,
            2.0,
            isClosed: true);
        VariablePathWidthResolver.EdgeDefinition[] edges =
        {
            new(Square(7), 5, true, 0),
            new(Square(3), 5, true, 1)
        };

        VariablePathWidthResolver.Result result = VariablePathWidthResolver.Resolve(
            new[] { center },
            edges,
            new VariablePathWidthResolver.Options { MaxEdgeDistance = 4.0 });

        PathGrader.PathDefinition resolved = Assert.Single(result.Paths);
        Assert.True(resolved.HasVariableWidth);
        Assert.True(resolved.IsClosed);
        Assert.Equal(2, result.MatchedEdgeCount);
    }

    [Fact]
    public void Resolve_NoEdges_ReturnsOriginalPathInstance()
    {
        PathGrader.PathDefinition path = Path(new[] { 0.0, 0.0, 10.0, 0.0 }, new[] { 1.0, 2.0 });

        VariablePathWidthResolver.Result result = VariablePathWidthResolver.Resolve(
            new[] { path },
            Array.Empty<VariablePathWidthResolver.EdgeDefinition>());

        Assert.Same(path, Assert.Single(result.Paths));
    }

    [Fact]
    public void Grade_ResolvedVariablePath_ProducesWatertightTerrainAndAuthoredRoadEdges()
    {
        var terrain = FlatGrid(13, 5.0);
        PathGrader.PathDefinition center = Path(
            new[] { 10.0, 30.0, 30.0, 30.0, 50.0, 30.0 },
            new[] { 2.0, 2.5, 3.0 });
        VariablePathWidthResolver.Result width = VariablePathWidthResolver.Resolve(
            new[] { center },
            new[]
            {
                Edge(0, false, 10, 33, 30, 35, 50, 36),
                Edge(1, false, 10, 28, 30, 27, 50, 26)
            },
            new VariablePathWidthResolver.Options { MaxEdgeDistance = 10.0 });

        GradeOutcome gradeOutcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.Vertices, terrain.VertexCount, terrain.Faces, terrain.FaceCount),
            Paths = width.Paths,
        });
        string? error = gradeOutcome.ErrorMessage;
        GradingResult? result = gradeOutcome.Result;

        Assert.NotNull(result);
        Assert.Null(error);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(result!.Faces, result.FaceCount);
        Assert.Equal(1, topology.BoundaryComponentCount);
        Assert.False(topology.HasOpenBoundaryChains);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.True(result.OutputPolylines.Count >= 2);
        Assert.Contains(result.OutputPolylines, polyline =>
            polyline.Vertices.Where((_, index) => index % 3 == 1).Max() >= 5.9 + 30.0);
    }

    private static PathGrader.PathDefinition Path(double[] xy, double[] z) =>
        new(xy, z, z.Length, 2.0);

    private static VariablePathWidthResolver.EdgeDefinition Edge(int sourceIndex, bool closed, params double[] xy) =>
        new(xy, xy.Length / 2, closed, sourceIndex);

    private static double YAtX(PathGrader.PathDefinition path, double[] edge, double x)
    {
        for (int i = 0; i < path.VertexCount; i++)
        {
            if (Math.Abs(path.XyVertices[i * 2] - x) <= 1e-9)
                return edge[(i * 2) + 1];
        }
        throw new InvalidOperationException($"No station at X={x}.");
    }

    private static double[] Square(double halfSize) => new[]
    {
        -halfSize, -halfSize,
        halfSize, -halfSize,
        halfSize, halfSize,
        -halfSize, halfSize,
        -halfSize, -halfSize
    };

    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) FlatGrid(int count, double step)
    {
        var xy = new List<double>();
        for (int y = 0; y < count; y++)
        for (int x = 0; x < count; x++)
        {
            xy.Add(x * step);
            xy.Add(y * step);
        }

        var triangulated = TriangulationHelper.Triangulate(xy, count * count, new List<(int, int)>(), 0, 0, convex: false, 0);
        var extraction = TriangleNetExtractor.Extract(triangulated.Mesh!);
        var vertices = new double[extraction.VertexCount * 3];
        for (int i = 0; i < extraction.VertexCount; i++)
        {
            vertices[i * 3] = extraction.Xy[i * 2];
            vertices[(i * 3) + 1] = extraction.Xy[(i * 2) + 1];
        }
        return (vertices, extraction.VertexCount, extraction.Faces, extraction.FaceCount);
    }
}
