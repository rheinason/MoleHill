using MoleHill.Core.Grading;
using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class VariablePathWidthQualityRegressionTests
{
    [Fact]
    public void Grade_BendingSlopedPathWithAsymmetricPartialRails_RemainsExplicitAndWatertight()
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = FlatGridTerrain(23, 5.0);
        double[] centerXy =
        {
            12.0, 24.0,
            23.0, 22.0,
            34.0, 28.0,
            40.0, 39.0,
            49.0, 49.0,
            62.0, 52.0,
            74.0, 46.0,
            82.0, 35.0,
            94.0, 31.0
        };
        double[] centerZ = { 1.0, 1.35, 2.15, 3.0, 2.55, 3.25, 4.1, 3.35, 2.8 };
        double[] leftHalfWidths = { 2.0, 3.2, 5.4, 2.4, 6.0, 3.1, 4.8, 2.2, 3.0 };
        double[] rightHalfWidths = { 2.0, 4.7, 2.3, 5.6, 2.1, 4.4, 3.0, 5.8, 2.0 };

        var constantPath = new PathGrader.PathDefinition(
            centerXy,
            centerZ,
            centerZ.Length,
            4.0,
            33.0,
            12.0,
            33.0);
        double[] leftRail = OffsetRail(centerXy, leftHalfWidths, 0, centerZ.Length);
        double[] partialRightRail = OffsetRail(centerXy, rightHalfWidths, 1, centerZ.Length - 1, rightSide: true);
        VariablePathWidthResolver.Result resolution = VariablePathWidthResolver.Resolve(
            new[] { constantPath },
            new[]
            {
                new VariablePathWidthResolver.EdgeDefinition(leftRail, leftRail.Length / 2, false, 0),
                new VariablePathWidthResolver.EdgeDefinition(partialRightRail, partialRightRail.Length / 2, false, 1)
            },
            new VariablePathWidthResolver.Options { MaxEdgeDistance = 9.0 });

        PathGrader.PathDefinition variablePath = Assert.Single(resolution.Paths);
        Assert.True(variablePath.HasVariableWidth);
        Assert.Equal(2, resolution.MatchedEdgeCount);
        Assert.Equal(1, resolution.PartialEdgeCount);

        GradeOutcome gradeOutcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            Paths = new[] { constantPath },
        });
        string? constantError = gradeOutcome.ErrorMessage;
        GradingResult? constant = gradeOutcome.Result;
        GradeOutcome gradeOutcome2 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            Paths = new[] { variablePath },
        });
        string? variableError = gradeOutcome2.ErrorMessage;
        GradingResult? variable = gradeOutcome2.Result;

        Assert.NotNull(constant);
        Assert.Null(constantError);
        Assert.NotNull(variable);
        Assert.Null(variableError);
        Assert.DoesNotContain(variable!.StructuredDiagnostics, diagnostic =>
            diagnostic.Code == "grade_path.explicit.fallback");
        Assert.True(
            variable.OutputPolylines.Max(static polyline => polyline.VertexCount) >=
            constant!.OutputPolylines.Max(static polyline => polyline.VertexCount));
        Assert.True(variable.FaceCount >= constant.FaceCount * 0.75);

        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(variable.Faces, variable.FaceCount);
        Assert.Equal(1, topology.BoundaryComponentCount);
        Assert.False(topology.HasOpenBoundaryChains);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
    }

    [Fact]
    public void Grade_PartialVariableRails_PreservesExplicitCorridorAndLongitudinalDensity()
    {
        (double[] vertices, int[] faces) = SparseLiveTerrain();
        double[] centerXy = { -5.7108562800071185, 0.0, 2.0, 0.0, 10.000000000000002, 0.0, 18.000000000000004, 0.0, 25.710856280007114, 0.0 };
        double[] centerZ = { 2.0, 2.0, 2.0, 2.0, 2.0 };
        var constantPath = new PathGrader.PathDefinition(centerXy, centerZ, 5, 2.0, 33.0, 12.0, 33.0);
        var variablePath = new PathGrader.PathDefinition(
            centerXy,
            centerZ,
            5,
            2.0,
            33.0,
            12.0,
            33.0,
            new[] { -5.7108562800071185, 1.0, 2.0, 1.0, 10.0, 5.564493443661487, 18.000000000000004, 1.0, 25.710856280007114, 1.0 },
            new[] { -5.7108562800071185, -1.0, 2.0, -1.0, 10.0, -2.0, 18.000000000000004, -1.0, 25.710856280007114, -1.0 },
            false);

        GradeOutcome gradeOutcome3 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertices.Length / 3, faces, faces.Length / 3),
            Paths = new[] { constantPath },
        });
        string? constantError = gradeOutcome3.ErrorMessage;
        GradingResult? constant = gradeOutcome3.Result;
        GradeOutcome gradeOutcome4 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertices.Length / 3, faces, faces.Length / 3),
            Paths = new[] { variablePath },
        });
        string? variableError = gradeOutcome4.ErrorMessage;
        GradingResult? variable = gradeOutcome4.Result;

        Assert.NotNull(constant);
        Assert.Null(constantError);
        Assert.NotNull(variable);
        Assert.Null(variableError);
        Assert.Contains(constant!.StructuredDiagnostics, diagnostic =>
            diagnostic.Code == "grade_path.topology.mode");
        Assert.Contains(variable!.StructuredDiagnostics, diagnostic =>
            diagnostic.Code == "grade_path.topology.mode");
        Assert.DoesNotContain(variable.StructuredDiagnostics, diagnostic =>
            diagnostic.Code == "grade_path.explicit.fallback");
        Assert.Equal(
            constant.OutputPolylines.Max(static polyline => polyline.VertexCount),
            variable.OutputPolylines.Max(static polyline => polyline.VertexCount));
        Assert.True(variable.FaceCount >= constant.FaceCount * 0.75);
    }

    private static (double[] Vertices, int[] Faces) SparseLiveTerrain()
    {
        double[] vertices =
        {
            35.61647415161133, -35.98030471801758, 6.846324920654297,
            43.54726791381836, -26.857555389404297, 6.846324920654297,
            44.61046600341797, -23.148178100585938, 6.846324920654297,
            48.914424896240234, 0.7440902590751648, 6.846324920654297,
            47.231956481933594, 7.511927604675293, 6.846324920654297,
            42.12649917602539, 13.197555541992188, 6.846324920654297,
            -49.50139617919922, -75.93618774414062, 0.0,
            -38.293128967285156, -75.93618774414062, 0.0,
            -27.084857940673828, -75.93618774414062, 0.0,
            -15.876587867736816, -75.93618774414062, 0.0,
            -4.668317794799805, -75.93618774414062, 0.0,
            6.539952278137207, -75.93618774414062, 0.0,
            17.74822235107422, -75.93618774414062, 0.0,
            28.956491470336914, -75.93618774414062, 0.0,
            40.16476058959961, -75.93618774414062, 0.0,
            51.37303161621094, -75.93618774414062, 0.0,
            62.581302642822266, -75.93618774414062, 0.0,
            73.7895736694336, -75.93618774414062, 0.0,
            84.99784088134766, -75.93618774414062, 0.0,
            -49.50139617919922, 29.72606086730957, 0.0,
            -39.494537353515625, 31.755517959594727, 0.0,
            -29.48767852783203, 33.78497314453125, 0.0,
            -19.480819702148438, 35.814430236816406, 0.0,
            -9.473959922790527, 37.84388732910156, 0.0,
            0.5328999161720276, 39.87334442138672, 0.0,
            10.539759635925293, 41.902801513671875, 0.0,
            20.698503494262695, 39.55194091796875, 0.0,
            30.85724639892578, 37.201080322265625, 0.0,
            41.015987396240234, 34.8502197265625, 0.0,
            51.17473220825195, 32.499359130859375, 0.0,
            59.63050842285156, 25.87958335876465, 0.0,
            68.08628845214844, 19.259809494018555, 0.0,
            76.54206848144531, 12.640035629272461, 0.0,
            84.99784088134766, 6.020261764526367, 0.0,
            -26.618270874023438, 2.3736886978149414, -2.297713279724121,
            -35.025943756103516, -5.705641269683838, -2.297713279724121,
            -35.898860931396484, -14.740419387817383, -2.297713279724121,
            -34.1945686340332, -21.083629608154297, -2.297713279724121,
            -32.87633514404297, -31.251293182373047, -2.297713279724121
        };
        int[] faces =
        {
            38, 6, 7, 37, 0, 34, 6, 38, 37, 38, 7, 8, 36, 6, 37, 38, 8, 9,
            0, 38, 10, 11, 0, 10, 38, 9, 10, 37, 38, 0, 19, 36, 35, 19, 35, 34,
            19, 34, 20, 20, 34, 21, 34, 35, 36, 6, 36, 19, 21, 34, 22, 23, 22, 34,
            21, 23, 24, 34, 24, 23, 24, 26, 25, 21, 24, 25, 34, 36, 37, 21, 22, 23,
            5, 24, 34, 11, 12, 0, 0, 13, 14, 2, 0, 1, 0, 16, 1, 1, 18, 2,
            5, 34, 2, 14, 15, 0, 2, 34, 0, 16, 17, 1, 33, 3, 2, 18, 1, 17,
            18, 33, 2, 15, 16, 0, 3, 5, 2, 29, 28, 5, 3, 4, 5, 25, 27, 28,
            5, 27, 26, 5, 28, 27, 26, 27, 25, 30, 5, 4, 5, 26, 24, 31, 32, 33,
            32, 31, 3, 30, 29, 5, 31, 30, 4, 4, 3, 31, 32, 3, 33, 13, 0, 12
        };
        return (vertices, faces);
    }

    private static double[] OffsetRail(
        double[] centerXy,
        double[] halfWidths,
        int startIndex,
        int endIndex,
        bool rightSide = false)
    {
        var rail = new double[(endIndex - startIndex) * 2];
        int vertexCount = centerXy.Length / 2;
        double side = rightSide ? -1.0 : 1.0;
        for (int index = startIndex; index < endIndex; index++)
        {
            int previous = Math.Max(0, index - 1);
            int next = Math.Min(vertexCount - 1, index + 1);
            double dx = centerXy[next * 2] - centerXy[previous * 2];
            double dy = centerXy[(next * 2) + 1] - centerXy[(previous * 2) + 1];
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            int destination = (index - startIndex) * 2;
            rail[destination] = centerXy[index * 2] + (side * -dy / length * halfWidths[index]);
            rail[destination + 1] = centerXy[(index * 2) + 1] + (side * dx / length * halfWidths[index]);
        }

        return rail;
    }

    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) FlatGridTerrain(
        int count,
        double step)
    {
        var xy = new List<double>(count * count * 2);
        for (int y = 0; y < count; y++)
        for (int x = 0; x < count; x++)
        {
            xy.Add(x * step);
            xy.Add(y * step);
        }

        var triangulated = TriangulationHelper.Triangulate(
            xy,
            count * count,
            new List<(int, int)>(),
            0,
            0,
            convex: false,
            0);
        var extraction = TriangleNetExtractor.Extract(triangulated.Mesh!);
        var vertices = new double[extraction.VertexCount * 3];
        for (int index = 0; index < extraction.VertexCount; index++)
        {
            vertices[index * 3] = extraction.Xy[index * 2];
            vertices[(index * 3) + 1] = extraction.Xy[(index * 2) + 1];
        }

        return (vertices, extraction.VertexCount, extraction.Faces, extraction.FaceCount);
    }
}
