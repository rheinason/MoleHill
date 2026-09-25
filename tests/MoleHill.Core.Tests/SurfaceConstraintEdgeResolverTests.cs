using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class SurfaceConstraintEdgeResolverTests
{
    [Fact]
    public void TryResolve_LongConstraint_MapsEveryCoincidentMeshEdge()
    {
        Grid(out double[] vertices, out int[] faces);
        var constraint = new SurfaceRemesher.ConstraintPolyline(
            new[] { 0.0, 1.0, 1.0, 2.0, 1.0, 3.0 }, 2, IsClosed: false, PreserveInputElevation: true);

        bool success = SurfaceConstraintEdgeResolver.TryResolve(
            vertices, faces, [constraint], 1e-8, out int[] segments, out string? failure);

        Assert.True(success, failure);
        Assert.Equal(
            new[] { IndexedMeshTools.GetEdgeKey(3, 4), IndexedMeshTools.GetEdgeKey(4, 5) }.OrderBy(key => key),
            Enumerable.Range(0, segments.Length / 2)
                .Select(i => IndexedMeshTools.GetEdgeKey(segments[i * 2], segments[i * 2 + 1]))
                .OrderBy(key => key));
    }

    [Fact]
    public void TryResolve_ConflictingElevation_ReturnsFailure()
    {
        Grid(out double[] vertices, out int[] faces);
        var constraint = new SurfaceRemesher.ConstraintPolyline(
            new[] { 0.0, 1.0, 8.0, 2.0, 1.0, 8.0 }, 2, IsClosed: false, PreserveInputElevation: true);

        bool success = SurfaceConstraintEdgeResolver.TryResolve(
            vertices, faces, [constraint], 1e-8, out _, out string? failure);

        Assert.False(success);
        Assert.Contains("conflicts", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_ConstraintMissingFromMesh_ReturnsFailure()
    {
        Grid(out double[] vertices, out int[] faces);
        var constraint = new SurfaceRemesher.ConstraintPolyline(
            new[] { 0.25, 0.25, 0.5, 1.75, 0.25, 2.0 }, 2, IsClosed: false);

        bool success = SurfaceConstraintEdgeResolver.TryResolve(
            vertices, faces, [constraint], 1e-8, out _, out string? failure);

        Assert.False(success);
        Assert.Contains("not represented", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveEach_OneConstraintMissingFromMesh_KeepsTheRepresentedOne()
    {
        Grid(out double[] vertices, out int[] faces);
        var onMesh = new SurfaceRemesher.ConstraintPolyline(
            new[] { 0.0, 1.0, 1.0, 2.0, 1.0, 3.0 }, 2, IsClosed: false, PreserveInputElevation: true);
        var offMesh = new SurfaceRemesher.ConstraintPolyline(
            new[] { 0.25, 0.25, 0.5, 1.75, 0.25, 2.0 }, 2, IsClosed: false);

        int[] segments = SurfaceConstraintEdgeResolver.ResolveEach(
            vertices, faces, [offMesh, onMesh], 1e-8, out bool[] resolved);

        Assert.Equal(new[] { false, true }, resolved);
        Assert.Equal(4, segments.Length);
    }

    private static void Grid(out double[] vertices, out int[] faces)
    {
        vertices =
        [
            0, 0, 0, 1, 0, 1, 2, 0, 2,
            0, 1, 1, 1, 1, 2, 2, 1, 3,
            0, 2, 2, 1, 2, 3, 2, 2, 4
        ];
        faces =
        [
            0, 1, 4, 0, 4, 3,
            1, 2, 5, 1, 5, 4,
            3, 4, 7, 3, 7, 6,
            4, 5, 8, 4, 8, 7
        ];
    }
}
