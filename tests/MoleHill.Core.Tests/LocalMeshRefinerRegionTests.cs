using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The <see cref="LocalMeshRefiner.Options.RegionFilter"/> extension (used by the Sculpt modifier's
/// DynTopo): refinement only happens where the filter passes, output stays manifold, midpoint
/// parentage is reported, and a null filter reproduces the previous behavior.
/// </summary>
public class LocalMeshRefinerRegionTests
{
    private static IReadOnlyList<SurfaceRemesher.ConstraintPolyline> NoConstraints =>
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    /// <summary>Flat 10x10 grid (spacing 1) triangulated into two triangles per cell.</summary>
    private static (double[] Verts, int[] Faces) FlatGrid()
    {
        const int n = 11;
        var verts = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = j * n + i;
                verts[v * 3] = i;
                verts[v * 3 + 1] = j;
            }
        }

        var faces = new int[(n - 1) * (n - 1) * 6];
        int f = 0;
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = j * n + i;
                faces[f++] = v00; faces[f++] = v00 + 1; faces[f++] = v00 + n + 1;
                faces[f++] = v00; faces[f++] = v00 + n + 1; faces[f++] = v00 + n;
            }
        }

        return (verts, faces);
    }

    [Fact]
    public void Refine_WithRegionFilter_OnlySubdividesInsideRegion()
    {
        var (verts, faces) = FlatGrid();
        // Region: disk of radius 2 around (5, 5).
        bool InRegion(double x, double y) => (x - 5) * (x - 5) + (y - 5) * (y - 5) <= 4.0;

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.5, Tolerance = 0.001, DoFlips = false, RegionFilter = InRegion });

        Assert.True(r.Success, r.Warning);
        Assert.True(r.AddedVertices > 0, "expected refinement inside the region");

        // Every added vertex must lie inside (or one edge-length outside) the region.
        for (int i = verts.Length / 3; i < r.Vertices.Length / 3; i++)
        {
            double x = r.Vertices[i * 3];
            double y = r.Vertices[i * 3 + 1];
            double distSq = (x - 5) * (x - 5) + (y - 5) * (y - 5);
            Assert.True(distSq <= 3.5 * 3.5, $"vertex added far outside region at ({x}, {y})");
        }
    }

    [Fact]
    public void Refine_WithRegionFilter_TopologyStaysManifold()
    {
        var (verts, faces) = FlatGrid();

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options
            {
                TargetEdgeLength = 0.4,
                Tolerance = 0.001,
                DoFlips = true,
                RegionFilter = (x, y) => x < 4.0,
            });

        Assert.True(r.Success, r.Warning);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(r.Faces, r.Faces.Length / 3);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);
    }

    [Fact]
    public void Refine_NullRegionFilter_MatchesUnfilteredBehavior()
    {
        var (verts, faces) = FlatGrid();
        var options = new LocalMeshRefiner.Options { TargetEdgeLength = 0.6, Tolerance = 0.001, DoFlips = false };

        LocalMeshRefiner.Result unfiltered = LocalMeshRefiner.Refine(verts, faces, NoConstraints, options);
        LocalMeshRefiner.Result passAll = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.6, Tolerance = 0.001, DoFlips = false, RegionFilter = (_, _) => true });

        Assert.Equal(unfiltered.AddedVertices, passAll.AddedVertices);
        Assert.Equal(unfiltered.Faces.Length, passAll.Faces.Length);
    }

    [Fact]
    public void Refine_MidpointParents_ReconstructEveryAddedVertex()
    {
        var (verts, faces) = FlatGrid();
        int inputCount = verts.Length / 3;

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.45, Tolerance = 0.001, DoFlips = false });

        Assert.True(r.Success, r.Warning);
        Assert.Equal(r.AddedVertices * 2, r.MidpointParents.Length);

        for (int k = 0; k < r.AddedVertices; k++)
        {
            int m = inputCount + k;
            int a = r.MidpointParents[k * 2];
            int b = r.MidpointParents[k * 2 + 1];
            Assert.True(a < m && b < m, "parents must precede their midpoint");
            Assert.Equal((r.Vertices[a * 3] + r.Vertices[b * 3]) * 0.5, r.Vertices[m * 3], 9);
            Assert.Equal((r.Vertices[a * 3 + 1] + r.Vertices[b * 3 + 1]) * 0.5, r.Vertices[m * 3 + 1], 9);
            Assert.Equal((r.Vertices[a * 3 + 2] + r.Vertices[b * 3 + 2]) * 0.5, r.Vertices[m * 3 + 2], 9);
        }
    }
}
