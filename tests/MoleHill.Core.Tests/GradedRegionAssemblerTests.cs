using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class GradedRegionAssemblerTests
{
    private static (double[] vertices, int count, int[] faces, int faceCount) FlatTerrain(double z)
    {
        double[] vertices =
        {
            -50.0, -50.0, z,
             50.0, -50.0, z,
             50.0,  50.0, z,
            -50.0,  50.0, z
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        return (vertices, 4, faces, 2);
    }

    private static double[] Square(double half) => new[]
    {
        -half, -half,
         half, -half,
         half,  half,
        -half,  half
    };

    [Fact]
    public void SplitOutside_SquareLoopInFlatTerrain_PreservesOutsideAndTracesBoundary()
    {
        var terrain = FlatTerrain(10.0);
        double[] loopXy = Square(8.0); // closed square hole at [-8,8]

        GradedRegionAssembler.SplitOutsideResult result = GradedRegionAssembler.SplitOutside(
            terrain.vertices, terrain.count, terrain.faces, terrain.faceCount,
            new[] { loopXy }, tolerance: 1e-3);

        Assert.True(result.Success, result.Warning);
        Assert.True(result.OutsideFaceCount > 0);
        Assert.Single(result.HoleBoundaryLoops);

        int[] loop = result.HoleBoundaryLoops[0];
        Assert.True(loop.Length >= 4, "Hole boundary should trace the square.");
        // Every hole-boundary vertex lies on the square's edges (x or y == +/-8), at terrain z=10.
        foreach (int vi in loop)
        {
            double x = result.Vertices[vi * 3];
            double y = result.Vertices[vi * 3 + 1];
            double z = result.Vertices[vi * 3 + 2];
            bool onSquare = Math.Abs(Math.Abs(x) - 8.0) < 1e-6 || Math.Abs(Math.Abs(y) - 8.0) < 1e-6;
            Assert.True(onSquare, $"Boundary vertex ({x:F3},{y:F3}) is not on the square edge.");
            Assert.Equal(10.0, z, 6);
        }

        // Outside faces must not have any vertex strictly inside the square (terrain hole carved).
        for (int f = 0; f < result.OutsideFaceCount; f++)
        {
            int a = result.OutsideFaces[f * 3], b = result.OutsideFaces[f * 3 + 1], c = result.OutsideFaces[f * 3 + 2];
            double cx = (result.Vertices[a * 3] + result.Vertices[b * 3] + result.Vertices[c * 3]) / 3.0;
            double cy = (result.Vertices[a * 3 + 1] + result.Vertices[b * 3 + 1] + result.Vertices[c * 3 + 1]) / 3.0;
            Assert.False(Math.Abs(cx) < 8.0 - 1e-6 && Math.Abs(cy) < 8.0 - 1e-6, "An outside face centroid fell inside the hole.");
        }
    }

    private static (double[] v, int vc, int[] f, int fc) FlatGrid(int n, double cell, double z)
    {
        var v = new double[n * n * 3];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int idx = (j * n) + i;
                v[idx * 3] = i * cell;
                v[idx * 3 + 1] = j * cell;
                v[idx * 3 + 2] = z;
            }

        var f = new List<int>((n - 1) * (n - 1) * 6);
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int a = (j * n) + i, b = (j * n) + i + 1, c = ((j + 1) * n) + i + 1, d = ((j + 1) * n) + i;
                f.Add(a); f.Add(b); f.Add(c);
                f.Add(a); f.Add(c); f.Add(d);
            }

        return (v, n * n, f.ToArray(), f.Count / 3);
    }

    [Fact]
    public void Grade_PadDaylightReachingTerrainEdge_ClipsAndUsesExplicit()
    {
        // Flat grid terrain at z=10 over [0,40]; a pad at z=0 near the +x edge so the 45° batter
        // (reach 10) runs past x=40 and must clip to the terrain boundary instead of deferring.
        var t = FlatGrid(21, 2.0, 10.0);
        double[] padXy = { 32, 16, 38, 16, 38, 24, 32, 24 };
        var pads = new[] { new PadGrader.PadBoundary(padXy, 4, targetZ: 0.0, slopeAngleDeg: 45.0) };

        GradingResult? result = PadGrader.Grade(t.v, t.vc, t.f, t.fc, pads, null, out string? err);

        Assert.True(result != null, err);
        Assert.Contains("explicit batter", string.Join(" ", result!.Diagnostics), StringComparison.OrdinalIgnoreCase);
        PadInvariantAssert.AssertWatertightManifold(result);
    }

    [Fact]
    public void Assemble_PadIntoFlatTerrain_ProducesWatertightManifoldMesh()
    {
        var terrain = FlatTerrain(10.0);

        // Explicit batter from a square pad at z=0 up to flat terrain at z=10.
        var stations = BatterStripBuilder.BuildClosedFootprintStations(Square(5.0), 4, cornerFanSegments: 6);
        BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
            stations.Xy,
            stations.Count,
            isClosed: true,
            outwardNormals: stations.Normals,
            footprintZ: (_, _) => 0.0,
            cutSlopeAngleDeg: 45.0,
            fillSlopeAngleDeg: 45.0,
            maxDistance: 0.0,
            terrain: new TerrainFaceGrid(terrain.vertices, terrain.count, terrain.faces, terrain.faceCount),
            barriers: PreparedBarriers.Empty,
            tolerance: 1e-3);
        BatterStripBuilder.BatterStrip strip = BatterStripBuilder.BuildBatterStrip(loop, edgeLength: 2.0);

        // Pad top: the footprint square at z=0, two triangles.
        var padTop = new GradedRegionAssembler.SubMesh
        {
            Vertices = new[]
            {
                -5.0, -5.0, 0.0,
                 5.0, -5.0, 0.0,
                 5.0,  5.0, 0.0,
                -5.0,  5.0, 0.0
            },
            VertexCount = 4,
            Faces = new[] { 0, 1, 2, 0, 2, 3 },
            FaceCount = 2
        };

        var batter = new GradedRegionAssembler.SubMesh
        {
            Vertices = strip.Vertices,
            VertexCount = strip.VertexCount,
            Faces = strip.Faces,
            FaceCount = strip.FaceCount
        };

        var insert = new GradedRegionAssembler.RegionInsert
        {
            DaylightLoopXyz = loop.DaylightXyz(),
            DaylightLoopCount = loop.Count,
            SubMeshes = new[] { batter, padTop }
        };

        GradedRegionAssembler.AssembledMesh result = GradedRegionAssembler.Assemble(
            terrain.vertices,
            terrain.count,
            terrain.faces,
            terrain.faceCount,
            new[] { insert },
            tolerance: 1e-3);

        Assert.True(result.Success, result.Warning);
        Assert.True(result.FaceCount > strip.FaceCount);

        MeshTopologyValidator.BoundaryGraphAnalysis analysis =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);

        Assert.Equal(0, analysis.NonManifoldEdgeCount);
        Assert.True(analysis.HasSingleClosedBoundaryLoop,
            $"Expected one closed boundary loop; got components={analysis.BoundaryComponentCount}, openChains={analysis.HasOpenBoundaryChains}, nonManifold={analysis.NonManifoldEdgeCount}.");

        // No interior vertex should sit at the original terrain elevation across the pad footprint:
        // the pad interior must have been carved out and replaced by the graded surface.
        Assert.All(EnumerateFaceCentroidZInsidePad(result, 5.0), z => Assert.True(z < 9.0));
    }

    private static IEnumerable<double> EnumerateFaceCentroidZInsidePad(
        GradedRegionAssembler.AssembledMesh mesh,
        double padHalf)
    {
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            int a = mesh.Faces[f * 3], b = mesh.Faces[f * 3 + 1], c = mesh.Faces[f * 3 + 2];
            double cx = (mesh.Vertices[a * 3] + mesh.Vertices[b * 3] + mesh.Vertices[c * 3]) / 3.0;
            double cy = (mesh.Vertices[a * 3 + 1] + mesh.Vertices[b * 3 + 1] + mesh.Vertices[c * 3 + 1]) / 3.0;
            if (Math.Abs(cx) <= padHalf && Math.Abs(cy) <= padHalf)
            {
                double cz = (mesh.Vertices[a * 3 + 2] + mesh.Vertices[b * 3 + 2] + mesh.Vertices[c * 3 + 2]) / 3.0;
                yield return cz;
            }
        }
    }
}
