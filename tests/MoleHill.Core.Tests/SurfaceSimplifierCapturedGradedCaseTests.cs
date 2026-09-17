using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public sealed class SurfaceSimplifierCapturedGradedCaseTests(ITestOutputHelper output)
{
    private const string FixtureResourceName =
        "MoleHill.Core.Tests.TestData.TerrainGradePathAfterProtectedPadsCopiedCase.json";

    [Fact]
    public void Simplify_CapturedGradePathAndProtectedPads_PreservesConstraintsAndCertifiedSurface()
    {
        Fixture fixture = LoadFixture();
        PathGrader.PathDefinition[] paths = fixture.Paths.Select(path => new PathGrader.PathDefinition(
            path.XyVertices, path.ZValues, path.VertexCount, path.Width, path.SlopeAngleDeg, path.MaxDistance)).ToArray();
        SurfaceRemesher.ConstraintPolyline[] hardConstraints = fixture.HardConstraints.Select(constraint =>
            new SurfaceRemesher.ConstraintPolyline(
                constraint.Points, constraint.PointCount, constraint.IsClosed, constraint.PreserveInputElevation)).ToArray();
        GradingResult? graded = PathGrader.Grade(
            fixture.Vertices, fixture.VertexCount, fixture.Faces, fixture.FaceCount,
            paths, hardConstraints, out string? gradingFailure);
        Assert.NotNull(graded);
        Assert.DoesNotContain("failed", gradingFailure ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.False(SurfaceConstraintEdgeResolver.TryResolve(
            graded!.Vertices, graded.Faces, hardConstraints, 1e-3,
            out _, out string? staleConstraintFailure));
        Assert.Contains("not represented", staleConstraintFailure, StringComparison.OrdinalIgnoreCase);

        SurfaceRemesher.ConstraintPolyline[] coherentConstraints = hardConstraints
            .Where(constraint => SurfaceConstraintEdgeResolver.TryResolve(
                graded.Vertices, graded.Faces, [constraint], 1e-3, out _, out _))
            .ToArray();
        Assert.NotEmpty(coherentConstraints);
        Assert.True(SurfaceConstraintEdgeResolver.TryResolve(
            graded.Vertices, graded.Faces, coherentConstraints, 1e-3,
            out int[] requiredSegments, out string? constraintFailure), constraintFailure);

        var stopwatch = Stopwatch.StartNew();
        SurfaceSimplifier.Result simplified = SurfaceSimplifier.Simplify(
            graded.Vertices, graded.Faces, requiredSegments,
            new SurfaceSimplifier.Options { MaximumDeviation = 5.0, NumericalTolerance = 1e-8 });
        stopwatch.Stop();

        output.WriteLine(
            $"Captured graded case: {simplified.InputVertexCount:N0}/{simplified.InputFaceCount:N0} -> " +
            $"{simplified.OutputVertexCount:N0}/{simplified.OutputFaceCount:N0} vertices/faces; " +
            $"required segments={requiredSegments.Length / 2:N0}; error={simplified.MaximumDeviation:G17}; " +
            $"rounds={simplified.Rounds}; termination={simplified.Termination}; " +
            $"diagnostic={simplified.Diagnostic}; time={stopwatch.Elapsed.TotalMilliseconds:0.0}ms");

        Assert.Equal(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, simplified.Termination);
        Assert.True(simplified.Reduced, simplified.Diagnostic);
        Assert.InRange(simplified.MaximumDeviation, 0.0, 5.0);
        SurfaceDeviationEvaluator.Result verification = SurfaceDeviationEvaluator.Evaluate(
            graded.Vertices, graded.Faces, simplified.Vertices, simplified.Faces,
            new SurfaceDeviationEvaluator.Options { NumericalTolerance = 1e-8 });
        Assert.True(verification.HasEqualDomain, verification.FailureReason);
        Assert.Equal(verification.MaximumDeviation, simplified.MaximumDeviation, 8);
        for (int segment = 0; segment < requiredSegments.Length / 2; segment++)
        {
            int a = FindVertex(simplified.Vertices, graded.Vertices, requiredSegments[segment * 2]);
            int b = FindVertex(simplified.Vertices, graded.Vertices, requiredSegments[segment * 2 + 1]);
            Assert.True(a >= 0 && b >= 0 && HasEdge(simplified.Faces, a, b),
                $"Missing captured required segment {segment}.");
        }
    }

    private static int FindVertex(double[] output, double[] source, int sourceIndex)
    {
        for (int vertex = 0; vertex < output.Length / 3; vertex++)
        {
            if (Math.Abs(output[vertex * 3] - source[sourceIndex * 3]) <= 1e-8 &&
                Math.Abs(output[vertex * 3 + 1] - source[sourceIndex * 3 + 1]) <= 1e-8 &&
                Math.Abs(output[vertex * 3 + 2] - source[sourceIndex * 3 + 2]) <= 1e-8)
                return vertex;
        }
        return -1;
    }

    private static bool HasEdge(int[] faces, int a, int b)
    {
        long expected = IndexedMeshTools.GetEdgeKey(a, b);
        for (int face = 0; face < faces.Length / 3; face++)
        {
            int offset = face * 3;
            int x = faces[offset], y = faces[offset + 1], z = faces[offset + 2];
            if (IndexedMeshTools.GetEdgeKey(x, y) == expected ||
                IndexedMeshTools.GetEdgeKey(y, z) == expected ||
                IndexedMeshTools.GetEdgeKey(z, x) == expected)
                return true;
        }
        return false;
    }

    private static Fixture LoadFixture()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FixtureResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{FixtureResourceName}'.");
        return JsonSerializer.Deserialize<Fixture>(stream)
            ?? throw new InvalidOperationException($"Could not deserialize embedded resource '{FixtureResourceName}'.");
    }

    private sealed class Fixture
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
