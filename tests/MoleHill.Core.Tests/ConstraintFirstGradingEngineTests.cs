using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class ConstraintFirstGradingEngineTests
{
    [Fact]
    public void TryBuild_PreservedConstraintElevations_UsesLaterOverlappingConstraint()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 0.0,
            0.0, 10.0, 0.0,
            5.0, 5.0, 0.0
        };
        int[] faces =
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };

        var constraints = new[]
        {
            CreatePreservedLine(0.0, 5.0, 10.0, 10.0, 5.0, 20.0),
            CreatePreservedLine(0.0, 5.0, 100.0, 10.0, 5.0, 120.0)
        };

        GradingResult? result = ConstraintFirstGradingEngine.TryBuild(
            "Grade Path",
            vertices,
            vertexCount: 5,
            faces,
            faceCount: 4,
            constraints,
            requestedEdgeLength: 10.0,
            tolerance: 0.001,
            applyGrading: FlattenToZero,
            outputPolylines: null,
            patchSummaries: null,
            preDiagnostics: null,
            preStructuredDiagnostics: null,
            appendOutputDiagnostics: null,
            out IReadOnlyList<GradingDiagnostic> failureDiagnostics,
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.Null(errorMessage);
        Assert.Empty(failureDiagnostics);
        int startIndex = FindVertex(result.Vertices, result.VertexCount, 0.0, 5.0);
        int endIndex = FindVertex(result.Vertices, result.VertexCount, 10.0, 5.0);
        Assert.NotEqual(-1, startIndex);
        Assert.NotEqual(-1, endIndex);
        Assert.Equal(100.0, result.Vertices[(startIndex * 3) + 2], 6);
        Assert.Equal(120.0, result.Vertices[(endIndex * 3) + 2], 6);
        GradingDiagnostic snapDiagnostic = Assert.Single(
            result.StructuredDiagnostics,
            diagnostic => diagnostic.Code == "grade_path.preserved_elevation.snap");
        Assert.Equal(GradingDiagnosticSeverity.Information, snapDiagnostic.Severity);
        Assert.Contains("snapped", snapDiagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 constraint", snapDiagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuild_PreservedConstraintElevations_FindsConstraintAmongManyFarSegments()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 0.0,
            0.0, 10.0, 0.0,
            5.0, 5.0, 0.0
        };
        int[] faces =
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };

        var constraints = new List<SurfaceRemesher.ConstraintPolyline>();
        for (int i = 0; i < 200; i++)
        {
            double offset = 1000.0 + (i * 10.0);
            constraints.Add(CreatePreservedLine(offset, offset, 1.0, offset + 5.0, offset, 2.0));
        }

        constraints.Add(CreatePreservedLine(0.0, 5.0, 30.0, 10.0, 5.0, 40.0));

        GradingResult? result = ConstraintFirstGradingEngine.TryBuild(
            "Grade Path",
            vertices,
            vertexCount: 5,
            faces,
            faceCount: 4,
            constraints,
            requestedEdgeLength: 10.0,
            tolerance: 0.001,
            applyGrading: FlattenToZero,
            outputPolylines: null,
            patchSummaries: null,
            preDiagnostics: null,
            preStructuredDiagnostics: null,
            appendOutputDiagnostics: null,
            out IReadOnlyList<GradingDiagnostic> failureDiagnostics,
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.Null(errorMessage);
        Assert.Empty(failureDiagnostics);
        int startIndex = FindVertex(result.Vertices, result.VertexCount, 0.0, 5.0);
        int endIndex = FindVertex(result.Vertices, result.VertexCount, 10.0, 5.0);
        Assert.NotEqual(-1, startIndex);
        Assert.NotEqual(-1, endIndex);
        Assert.Equal(30.0, result.Vertices[(startIndex * 3) + 2], 6);
        Assert.Equal(40.0, result.Vertices[(endIndex * 3) + 2], 6);
    }

    [Fact]
    public void TryBuild_ZEvaluationThrows_ReturnsStructuredFailureDiagnostics()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            0.0, 10.0, 0.0
        };
        int[] faces = { 0, 1, 2 };

        GradingResult? result = ConstraintFirstGradingEngine.TryBuild(
            "Grade Path",
            vertices,
            vertexCount: 3,
            faces,
            faceCount: 1,
            constraints: Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            requestedEdgeLength: 10.0,
            tolerance: 0.001,
            applyGrading: static (_, _, _, _) => throw new InvalidOperationException("synthetic failure"),
            outputPolylines: null,
            patchSummaries: null,
            preDiagnostics: null,
            preStructuredDiagnostics: null,
            appendOutputDiagnostics: null,
            out IReadOnlyList<GradingDiagnostic> failureDiagnostics,
            out string? errorMessage);

        Assert.Null(result);
        Assert.Equal("Grade Path Z evaluation failed: synthetic failure", errorMessage);
        GradingDiagnostic diagnostic = Assert.Single(failureDiagnostics);
        Assert.Equal("grade_path.constraint_first.failed", diagnostic.Code);
        Assert.Equal(GradingDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(errorMessage, diagnostic.Message);
        Assert.Equal("grade_path", diagnostic.Operation);
    }

    [Fact]
    public void TryBuild_PadFailureDiagnostic_UsesStableOperationId()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            0.0, 10.0, 0.0
        };
        int[] faces = { 0, 1, 2 };

        GradingResult? result = ConstraintFirstGradingEngine.TryBuild(
            "Grade Pad",
            vertices,
            vertexCount: 3,
            faces,
            faceCount: 1,
            constraints: Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            requestedEdgeLength: 10.0,
            tolerance: 0.001,
            applyGrading: static (_, _, _, _) => throw new InvalidOperationException("synthetic pad failure"),
            outputPolylines: null,
            patchSummaries: null,
            preDiagnostics: null,
            preStructuredDiagnostics: null,
            appendOutputDiagnostics: null,
            out IReadOnlyList<GradingDiagnostic> failureDiagnostics,
            out string? errorMessage);

        Assert.Null(result);
        Assert.Equal("Grade Pad Z evaluation failed: synthetic pad failure", errorMessage);
        GradingDiagnostic diagnostic = Assert.Single(failureDiagnostics);
        Assert.Equal("grade_pad.constraint_first.failed", diagnostic.Code);
        Assert.Equal("grade_pad", diagnostic.Operation);
    }

    [Fact]
    public void TryBuild_CrossingConstraints_EmitsStructuredNormalizationDiagnostic()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 0.0,
            0.0, 10.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3
        };
        var constraints = new[]
        {
            new SurfaceRemesher.ConstraintPolyline(
                new[] { 0.0, 5.0, 0.0, 10.0, 5.0, 0.0 },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: false),
            new SurfaceRemesher.ConstraintPolyline(
                new[] { 5.0, 0.0, 0.0, 5.0, 10.0, 0.0 },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: false)
        };

        GradingResult? result = ConstraintFirstGradingEngine.TryBuild(
            "Grade Path",
            vertices,
            vertexCount: 4,
            faces,
            faceCount: 2,
            constraints,
            requestedEdgeLength: 10.0,
            tolerance: 0.001,
            applyGrading: FlattenToZero,
            outputPolylines: null,
            patchSummaries: null,
            preDiagnostics: null,
            preStructuredDiagnostics: null,
            appendOutputDiagnostics: null,
            out IReadOnlyList<GradingDiagnostic> failureDiagnostics,
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.Null(errorMessage);
        Assert.Empty(failureDiagnostics);
        GradingDiagnostic diagnostic = Assert.Single(
            result.StructuredDiagnostics,
            diagnostic => diagnostic.Code == "grade_path.constraint_network.normalized");
        Assert.Equal(GradingDiagnosticSeverity.Information, diagnostic.Severity);
        Assert.Contains("2 intersection split", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuild_DenseConstraintOutput_EmitsStructuredDensityDiagnostic()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 0.0,
            0.0, 10.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3
        };

        var constraints = new List<SurfaceRemesher.ConstraintPolyline>
        {
            new(
                new[]
                {
                    0.0, 0.0, 0.0,
                    10.0, 0.0, 0.0,
                    10.0, 10.0, 0.0,
                    0.0, 10.0, 0.0
                },
                PointCount: 4,
                IsClosed: true,
                PreserveInputElevation: false)
        };
        for (int i = 1; i < 10; i++)
        {
            double x = i;
            constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                new[] { x, 0.0, 0.0, x, 10.0, 0.0 },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: false));
        }

        GradingResult? result = ConstraintFirstGradingEngine.TryBuild(
            "Grade Path",
            vertices,
            vertexCount: 4,
            faces,
            faceCount: 2,
            constraints,
            requestedEdgeLength: 10.0,
            tolerance: 0.001,
            applyGrading: FlattenToZero,
            outputPolylines: null,
            patchSummaries: null,
            preDiagnostics: null,
            preStructuredDiagnostics: null,
            appendOutputDiagnostics: null,
            out IReadOnlyList<GradingDiagnostic> failureDiagnostics,
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.Null(errorMessage);
        Assert.Empty(failureDiagnostics);
        Assert.Contains(
            result.StructuredDiagnostics,
            diagnostic =>
                diagnostic.Severity is GradingDiagnosticSeverity.Information or GradingDiagnosticSeverity.Warning &&
                (diagnostic.Code == "grade_path.density.note" || diagnostic.Code == "grade_path.density.high"));
    }

    private static SurfaceRemesher.ConstraintPolyline CreatePreservedLine(
        double ax,
        double ay,
        double az,
        double bx,
        double by,
        double bz)
    {
        return new SurfaceRemesher.ConstraintPolyline(
            new[] { ax, ay, az, bx, by, bz },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);
    }

    private static double[] FlattenToZero(double[] topologyVertices, int vertexCount, int[] faces, int faceCount)
    {
        var graded = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            graded[i * 3] = topologyVertices[i * 3];
            graded[(i * 3) + 1] = topologyVertices[(i * 3) + 1];
        }

        return graded;
    }

    private static int FindVertex(double[] vertices, int vertexCount, double x, double y)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = vertices[i * 3] - x;
            double dy = vertices[(i * 3) + 1] - y;
            if ((dx * dx) + (dy * dy) <= 1e-12)
                return i;
        }

        return -1;
    }
}
