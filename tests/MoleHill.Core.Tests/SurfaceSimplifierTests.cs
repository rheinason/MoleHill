using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class SurfaceSimplifierTests
{
    [Fact]
    public void Simplify_PlanarGrid_ReturnsSmallerCertifiedMesh()
    {
        BuildGrid(12, static (_, _) => 3.0, out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 0.0, NumericalTolerance = 1e-8 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, result.Termination);
        Assert.True(result.Reduced, result.Diagnostic);
        Assert.InRange(result.MaximumDeviation, 0.0, 1e-8);
        Assert.True(result.HasEqualDomain);
    }

    [Fact]
    public void Simplify_RollingGrid_ReturnsSmallerMeshWithinBound()
    {
        BuildGrid(16, static (x, y) => Math.Sin(x * 0.22) + Math.Cos(y * 0.17), out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 0.08, NumericalTolerance = 1e-8 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, result.Termination);
        Assert.True(result.Reduced, result.Diagnostic);
        Assert.InRange(result.MaximumDeviation, 0.0, 0.08);
    }

    [Fact]
    public void Simplify_RequiredChain_PreservesEveryVertexAndEdge()
    {
        const int size = 12;
        BuildGrid(size, static (x, y) => x * 0.1 + y * 0.2, out double[] vertices, out int[] faces);
        int row = size + 1;
        var segments = new int[size * 2];
        for (int x = 0; x < size; x++)
        {
            segments[x * 2] = (size / 2 * row) + x;
            segments[x * 2 + 1] = (size / 2 * row) + x + 1;
        }

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, segments,
            new SurfaceSimplifier.Options { MaximumDeviation = 0.0, NumericalTolerance = 1e-8 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, result.Termination);
        Assert.True(result.Reduced, result.Diagnostic);
        for (int segment = 0; segment < segments.Length / 2; segment++)
        {
            int a = FindVertex(result.Vertices, vertices, segments[segment * 2]);
            int b = FindVertex(result.Vertices, vertices, segments[segment * 2 + 1]);
            Assert.True(HasEdge(result.Faces, a, b), $"Missing required segment {segment}.");
        }
    }

    [Fact]
    public void Simplify_ZeroToleranceOnNonPlanarGrid_FallsBackHonestlyWhenNeeded()
    {
        BuildGrid(8, static (x, y) => Math.Sin(x * 0.7) * Math.Cos(y * 0.4), out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 0.0, NumericalTolerance = 1e-10, MaximumRounds = 3 });

        if (result.Termination != SurfaceSimplifier.TerminationReason.ToleranceSatisfied)
        {
            Assert.Same(vertices, result.Vertices);
            Assert.Same(faces, result.Faces);
            Assert.Equal(0.0, result.MaximumDeviation);
            Assert.True(result.HasEqualDomain);
        }
        else
        {
            Assert.Equal(0.0, result.MaximumDeviation, 9);
        }
    }

    [Fact]
    public void Simplify_DegenerateProjectedFace_ReturnsUnsupportedFallback()
    {
        double[] vertices = { 0, 0, 0, 0, 0, 1, 1, 0, 0 };
        int[] faces = { 0, 1, 2 };

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(), new SurfaceSimplifier.Options { MaximumDeviation = 1.0 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.UnsupportedInput, result.Termination);
        Assert.False(result.Reduced);
    }

    [Fact]
    public void Simplify_StackedXyFaces_ReturnsUnsupportedFallback()
    {
        double[] vertices =
        {
            0, 0, 0, 1, 0, 0, 0, 1, 0,
            0, 0, 2, 1, 0, 2, 0, 1, 2
        };
        int[] faces = { 0, 1, 2, 3, 4, 5 };

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(), new SurfaceSimplifier.Options { MaximumDeviation = 1.0 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.UnsupportedInput, result.Termination);
        Assert.False(result.Reduced);
        Assert.Same(vertices, result.Vertices);
        Assert.Same(faces, result.Faces);
    }

    [Fact]
    public void Simplify_InputWithDuplicatedInteriorSeam_NeverReturnsCrackedCandidate()
    {
        double[] vertices =
        {
            0, 0, 0, 1, 0, 0, 1, 1, 0,
            0, 0, 0, 1, 1, 0, 0, 1, 0
        };
        int[] faces = { 0, 1, 2, 3, 4, 5 };

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 0.0, NumericalTolerance = 1e-8 });

        Assert.False(result.Reduced);
        Assert.NotEqual(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, result.Termination);
        Assert.Same(vertices, result.Vertices);
        Assert.Same(faces, result.Faces);
    }

    [Fact]
    public void Simplify_NearVerticalSingleValuedStrip_RemainsVerifiable()
    {
        const double spacing = 1e-4;
        BuildGrid(6, static (x, y) => (x * 20.0) + (y * 0.01), out double[] vertices, out int[] faces);
        for (int vertex = 0; vertex < vertices.Length / 3; vertex++)
            vertices[vertex * 3] *= spacing;

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 1e-6, NumericalTolerance = 1e-10 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, result.Termination);
        Assert.True(result.Reduced, result.Diagnostic);
        SurfaceDeviationEvaluator.Result verification = SurfaceDeviationEvaluator.Evaluate(
            vertices, faces, result.Vertices, result.Faces,
            new SurfaceDeviationEvaluator.Options { NumericalTolerance = 1e-10 });
        Assert.True(verification.HasEqualDomain, verification.FailureReason);
        Assert.InRange(verification.MaximumDeviation, 0.0, 1e-6);
    }

    [Fact]
    public void Simplify_GridWithHole_PreservesDomainAndBoundaryTopology()
    {
        BuildGridWithHole(10, 4, 6, out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 0.03, NumericalTolerance = 1e-8 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, result.Termination);
        Assert.True(result.Reduced, result.Diagnostic);
        SurfaceDeviationEvaluator.Result verification = SurfaceDeviationEvaluator.Evaluate(
            vertices, faces, result.Vertices, result.Faces);
        Assert.True(verification.HasEqualDomain, verification.FailureReason);
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.OutputFaceCount);
        Assert.Equal(2, topology.BoundaryComponentCount);
        Assert.False(topology.HasOpenBoundaryChains);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
    }

    [Fact]
    public void Simplify_DisconnectedPlanarIslands_PreservesBothComponents()
    {
        BuildGrid(5, static (_, _) => 1.0, out double[] firstVertices, out int[] firstFaces);
        BuildGrid(5, static (_, _) => 1.0, out double[] secondVertices, out int[] secondFaces);
        for (int vertex = 0; vertex < secondVertices.Length / 3; vertex++)
            secondVertices[vertex * 3] += 10.0;
        double[] vertices = firstVertices.Concat(secondVertices).ToArray();
        int offset = firstVertices.Length / 3;
        int[] faces = firstFaces.Concat(secondFaces.Select(index => index + offset)).ToArray();

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 0.0, NumericalTolerance = 1e-8 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.ToleranceSatisfied, result.Termination);
        Assert.True(result.Reduced, result.Diagnostic);
        SurfaceDeviationEvaluator.Result verification = SurfaceDeviationEvaluator.Evaluate(
            vertices, faces, result.Vertices, result.Faces);
        Assert.True(verification.HasEqualDomain, verification.FailureReason);
        Assert.Equal(2, MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.OutputFaceCount).BoundaryComponentCount);
    }

    [Fact]
    public void Simplify_AllEdgesRequired_ReturnsInputWithNoReductionReason()
    {
        BuildGrid(5, static (x, y) => Math.Sin(x) + Math.Cos(y), out double[] vertices, out int[] faces);
        int[] allEdges = UniqueEdges(faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, allEdges,
            new SurfaceSimplifier.Options { MaximumDeviation = 1.0, NumericalTolerance = 1e-8 });

        Assert.Equal(SurfaceSimplifier.TerminationReason.NoReductionAchieved, result.Termination);
        Assert.False(result.Reduced);
        Assert.Same(vertices, result.Vertices);
        Assert.Same(faces, result.Faces);
    }

    [Fact]
    public void Simplify_DoesNotMutateInputArrays()
    {
        BuildGrid(10, static (x, y) => Math.Sin(x * 0.2) + y * 0.03, out double[] vertices, out int[] faces);
        double[] originalVertices = (double[])vertices.Clone();
        int[] originalFaces = (int[])faces.Clone();

        _ = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options { MaximumDeviation = 0.05, NumericalTolerance = 1e-8 });

        Assert.Equal(originalVertices, vertices);
        Assert.Equal(originalFaces, faces);
    }

    [Fact]
    public void Simplify_CancelledDuringWork_ThrowsWithoutPartialResult()
    {
        BuildGrid(20, static (x, y) => Math.Sin(x * 0.2) + Math.Cos(y * 0.3), out double[] vertices, out int[] faces);
        int observations = 0;

        Assert.Throws<OperationCanceledException>(() => SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options
            {
                MaximumDeviation = 0.01,
                NumericalTolerance = 1e-8,
                ShouldCancel = () => ++observations > 3
            }));
        Assert.True(observations > 3);
    }

    [Fact]
    public void Simplify_TargetVertexCount_ReturnsCertifiedMeshAtOrBelowCap()
    {
        BuildGrid(16, static (x, y) => Math.Sin(x * 0.22) + Math.Cos(y * 0.17), out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options
            {
                Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
                TargetVertexCount = 120,
                NumericalTolerance = 1e-8
            });

        Assert.Equal(SurfaceSimplifier.TerminationReason.TargetCountSatisfied, result.Termination);
        Assert.True(result.Reduced, result.Diagnostic);
        Assert.InRange(result.OutputVertexCount, 3, 120);
        SurfaceDeviationEvaluator.Result independent = SurfaceDeviationEvaluator.Evaluate(
            vertices, faces, result.Vertices, result.Faces);
        Assert.True(independent.HasEqualDomain, independent.FailureReason);
        Assert.Equal(independent.MaximumDeviation, result.MaximumDeviation, 10);
    }

    [Fact]
    public void Simplify_TargetBelowMandatorySet_ReturnsExplicitUnchangedFailure()
    {
        BuildGrid(8, static (x, y) => x + y, out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options
            {
                Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
                TargetVertexCount = 20
            });

        Assert.Equal(SurfaceSimplifier.TerminationReason.MandatorySetExceedsTarget, result.Termination);
        Assert.False(result.Reduced);
        Assert.Same(vertices, result.Vertices);
        Assert.Same(faces, result.Faces);
    }

    [Fact]
    public void Simplify_TargetAtInputCount_ReturnsInputUnchanged()
    {
        BuildGrid(6, static (x, y) => x - y, out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options
            {
                Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
                TargetVertexCount = vertices.Length / 3
            });

        Assert.Equal(SurfaceSimplifier.TerminationReason.TargetCountSatisfied, result.Termination);
        Assert.False(result.Reduced);
        Assert.Equal(0, result.Rounds);
        Assert.Same(vertices, result.Vertices);
        Assert.Same(faces, result.Faces);
    }

    [Fact]
    public void Simplify_TargetVertexCount_IsDeterministic()
    {
        BuildGrid(14, static (x, y) => Math.Sin(x * 0.3) - Math.Cos(y * 0.2), out double[] vertices, out int[] faces);
        var options = new SurfaceSimplifier.Options
        {
            Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
            TargetVertexCount = 100,
            NumericalTolerance = 1e-8
        };

        SurfaceSimplifier.Result first = SurfaceSimplifier.Simplify(vertices, faces, Array.Empty<int>(), options);
        SurfaceSimplifier.Result second = SurfaceSimplifier.Simplify(vertices, faces, Array.Empty<int>(), options);

        Assert.Equal(first.Termination, second.Termination);
        Assert.Equal(first.MaximumDeviation, second.MaximumDeviation);
        Assert.Equal(first.Vertices, second.Vertices);
        Assert.Equal(first.Faces, second.Faces);
    }

    [Fact]
    public void Simplify_TargetCountAtRoundLimit_ReportsErrorForReturnedCandidate()
    {
        BuildGrid(16, static (x, y) => Math.Sin(x * 0.22) + Math.Cos(y * 0.17), out double[] vertices, out int[] faces);

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options
            {
                Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
                TargetVertexCount = 140,
                MaximumRounds = 1,
                NumericalTolerance = 1e-8
            });
        SurfaceDeviationEvaluator.Result verification = SurfaceDeviationEvaluator.Evaluate(
            vertices, faces, result.Vertices, result.Faces);

        Assert.Equal(SurfaceSimplifier.TerminationReason.TargetCountSatisfied, result.Termination);
        Assert.Equal(1, result.Rounds);
        Assert.True(verification.HasEqualDomain, verification.FailureReason);
        Assert.Equal(verification.MaximumDeviation, result.MaximumDeviation, 10);
    }

    private static void BuildGrid(int size, Func<double, double, double> elevation, out double[] vertices, out int[] faces)
    {
        vertices = TestMeshes.GridVertices(size + 1, size + 1, 1.0, elevation);
        faces = TestMeshes.GridFaces(size + 1, size + 1);
    }

    private static void BuildGridWithHole(int size, int holeMin, int holeMax, out double[] vertices, out int[] faces)
    {
        BuildGrid(size, static (x, y) => (x * 0.01) + (y * 0.02), out vertices, out int[] allFaces);
        var kept = new List<int>(allFaces.Length);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (x >= holeMin && x < holeMax && y >= holeMin && y < holeMax)
                continue;
            int offset = (y * size + x) * 6;
            for (int i = 0; i < 6; i++) kept.Add(allFaces[offset + i]);
        }
        faces = kept.ToArray();
    }

    private static int FindVertex(double[] output, double[] source, int sourceIndex)
    {
        for (int i = 0; i < output.Length / 3; i++)
            if (Math.Abs(output[i * 3] - source[sourceIndex * 3]) < 1e-9 &&
                Math.Abs(output[i * 3 + 1] - source[sourceIndex * 3 + 1]) < 1e-9 &&
                Math.Abs(output[i * 3 + 2] - source[sourceIndex * 3 + 2]) < 1e-9)
                return i;
        return -1;
    }

    private static bool HasEdge(int[] faces, int a, int b)
    {
        Assert.True(a >= 0 && b >= 0);
        long expected = IndexedMeshTools.GetEdgeKey(a, b);
        for (int face = 0; face < faces.Length / 3; face++)
        {
            int x = faces[face * 3], y = faces[face * 3 + 1], z = faces[face * 3 + 2];
            if (IndexedMeshTools.GetEdgeKey(x, y) == expected ||
                IndexedMeshTools.GetEdgeKey(y, z) == expected ||
                IndexedMeshTools.GetEdgeKey(z, x) == expected) return true;
        }
        return false;
    }

    private static int[] UniqueEdges(int[] faces)
    {
        var edges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int face = 0; face < faces.Length / 3; face++)
        {
            int a = faces[face * 3], b = faces[face * 3 + 1], c = faces[face * 3 + 2];
            edges.Add(IndexedMeshTools.GetEdgeKey(a, b));
            edges.Add(IndexedMeshTools.GetEdgeKey(b, c));
            edges.Add(IndexedMeshTools.GetEdgeKey(c, a));
        }
        return edges.OrderBy(key => key)
            .SelectMany(key => new[] { (int)(key >> 32), (int)(key & 0xffffffffL) })
            .ToArray();
    }
}
