using System.Reflection;
using System.Text.Json;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class TerrainGradePathAfterProtectedPadsCopiedCaseTests
{
    private const string FixtureResourceName =
        "MoleHill.Core.Tests.TestData.TerrainGradePathAfterProtectedPadsCopiedCase.json";

    [Fact]
    public void GradePath_AfterProtectedPadsCopiedCase_DoesNotFailOnHardConstraintTouch()
    {
        CopiedCaseFixture fixture = LoadFixture();
        PathGrader.PathDefinition[] paths = fixture.Paths
            .Select(path => new PathGrader.PathDefinition(
                path.XyVertices,
                path.ZValues,
                path.VertexCount,
                path.Width,
                path.SlopeAngleDeg,
                path.MaxDistance))
            .ToArray();
        ConstraintPolyline[] hardConstraints = fixture.HardConstraints
            .Select(constraint => new ConstraintPolyline(
                constraint.Points,
                constraint.PointCount,
                constraint.IsClosed,
                constraint.PreserveInputElevation))
            .ToArray();

        GradingResult? result = PathGrader.Grade(
            fixture.Vertices,
            fixture.VertexCount,
            fixture.Faces,
            fixture.FaceCount,
            paths,
            hardConstraints,
            out string? errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) ||
            !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);
        Assert.True(result!.VertexCount > 0);
        Assert.True(result.FaceCount > 0);

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        Assert.False(
            topology.HasOpenBoundaryChains,
            $"boundary edges={topology.BoundaryEdgeCount}, boundary vertices={topology.BoundaryVertexCount}, components={topology.BoundaryComponentCount}, nonmanifold={topology.NonManifoldEdgeCount}");
        Assert.Equal(0, topology.NonManifoldEdgeCount);
    }

    private static CopiedCaseFixture LoadFixture()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FixtureResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{FixtureResourceName}'.");
        return JsonSerializer.Deserialize<CopiedCaseFixture>(stream)
            ?? throw new InvalidOperationException($"Could not deserialize embedded resource '{FixtureResourceName}'.");
    }

    private sealed class CopiedCaseFixture
    {
        public double[] Vertices { get; set; } = Array.Empty<double>();
        public int VertexCount { get; set; }
        public int[] Faces { get; set; } = Array.Empty<int>();
        public int FaceCount { get; set; }
        public PathFixture[] Paths { get; set; } = Array.Empty<PathFixture>();
        public ConstraintFixture[] HardConstraints { get; set; } = Array.Empty<ConstraintFixture>();
    }

    private sealed class PathFixture
    {
        public double[] XyVertices { get; set; } = Array.Empty<double>();
        public double[] ZValues { get; set; } = Array.Empty<double>();
        public int VertexCount { get; set; }
        public double Width { get; set; }
        public double SlopeAngleDeg { get; set; }
        public double MaxDistance { get; set; }
    }

    private sealed class ConstraintFixture
    {
        public double[] Points { get; set; } = Array.Empty<double>();
        public int PointCount { get; set; }
        public bool IsClosed { get; set; }
        public bool PreserveInputElevation { get; set; }
    }
}