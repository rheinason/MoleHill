using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Utilities;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public sealed class TerrainPartitionGeometryTests
{
    [Fact]
    public void ClassifyFaceOwners_NestedBoundary_LeavesInnerLoopInRemainder()
    {
        var result = new MeshAreaSplitter.SplitResult(
            new[]
            {
                1.0, 1.0, 0.0, 2.0, 1.0, 0.0, 1.0, 2.0, 0.0,
                4.2, 4.2, 0.0, 5.0, 4.2, 0.0, 4.2, 5.0, 0.0
            },
            6,
            new[] { 0, 1, 2, 3, 4, 5 },
            2,
            new[] { 0, 1 },
            2);
        var outer = Boundary(0.0, 0.0, 10.0, 10.0);
        var hole = Boundary(4.0, 4.0, 6.0, 6.0);

        int[] owners = TerrainPartitionGeometry.ClassifyFaceOwners(
            result,
            new IReadOnlyList<MeshAreaSplitter.AreaBoundary>[] { new[] { outer, hole } });

        Assert.Equal(new[] { 0, -1 }, owners);
    }

    [Fact]
    public void ClassifyFaceOwners_OverlappingRegions_LaterBranchWins()
    {
        var result = new MeshAreaSplitter.SplitResult(
            new[] { 1.0, 1.0, 0.0, 2.0, 1.0, 0.0, 1.0, 2.0, 0.0 },
            3,
            new[] { 0, 1, 2 },
            1,
            new[] { 0, 1 },
            1);

        int[] owners = TerrainPartitionGeometry.ClassifyFaceOwners(
            result,
            new IReadOnlyList<MeshAreaSplitter.AreaBoundary>[]
            {
                new[] { Boundary(0.0, 0.0, 10.0, 10.0) },
                new[] { Boundary(0.0, 0.0, 3.0, 3.0) }
            });

        Assert.Equal(new[] { 1 }, owners);
    }

    [RhinoNativeFact]
    public void ClipBreaklinesToMesh_CrossingLine_ReturnsOnlyCoveredSegment()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(10.0, 0.0, 0.0);
        mesh.Vertices.Add(10.0, 10.0, 0.0);
        mesh.Vertices.Add(0.0, 10.0, 0.0);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Faces.AddFace(0, 2, 3);
        using var line = new LineCurve(new Point3d(-5.0, 5.0, 0.0), new Point3d(15.0, 5.0, 0.0));

        IReadOnlyList<Curve> clipped = TerrainPartitionGeometry.ClipBreaklinesToMesh(
            new[] { line },
            mesh,
            0.001);

        Curve segment = Assert.Single(clipped);
        Assert.InRange(segment.GetLength(), 9.99, 10.01);
        foreach (Curve curve in clipped)
            curve.Dispose();
    }

    private static MeshAreaSplitter.AreaBoundary Boundary(double minX, double minY, double maxX, double maxY)
    {
        return new MeshAreaSplitter.AreaBoundary(
            new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY },
            4);
    }
}
