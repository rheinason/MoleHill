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
    public void SplitConformViaCdt_HardConstraintAlreadyEmbeddedAsVertexChain_EmitsNoSliverFaces()
    {
        // A pad boundary an upstream stage already embedded: the side vertices sit a hair (1e-7) off the
        // corner-to-corner line, exactly as a graded pad side comes out. Forcing each side whole let
        // Triangle.NET close every such vertex against it with a zero-area sliver the length of the side,
        // which a later remesh turned into coincident vertices and scrambled normals.
        var t = FlatGrid(21, 2.0, 10.0);
        for (int i = 0; i < t.vc; i++)
        {
            double x = t.v[i * 3], y = t.v[i * 3 + 1];
            bool onSide = (x > 10.5 && x < 29.5 && (y == 10.0 || y == 30.0)) ||
                          (y > 10.5 && y < 29.5 && (x == 10.0 || x == 30.0));
            if (onSide)
            {
                double offset = (i % 2 == 0 ? 1.0 : -1.0) * 1e-7;
                if (y == 10.0 || y == 30.0)
                    t.v[i * 3 + 1] += offset;
                else
                    t.v[i * 3] += offset;
            }
        }

        var padBoundary = new ConstraintPolyline(
            new double[] { 10, 10, 10, 30, 10, 10, 30, 30, 10, 10, 30, 10 }, 4, true, true);
        double[] outline = { 0, 0, 40, 0, 40, 40, 0, 40 };
        double[] daylight = { 2, 2, 8, 2, 8, 8, 2, 8 };

        MeshAreaSplitter.SplitResult? result = GradedRegionAssembler.SplitConformViaCdt(
            t.v, t.vc, t.f, t.fc, new[] { daylight }, outline, 0.002, new[] { padBoundary });

        Assert.NotNull(result);
        for (int f = 0; f < result!.FaceCount; f++)
        {
            int a = result.Faces[f * 3], b = result.Faces[f * 3 + 1], c = result.Faces[f * 3 + 2];
            double ax = result.Vertices[a * 3], ay = result.Vertices[a * 3 + 1];
            double bx = result.Vertices[b * 3] - ax, by = result.Vertices[b * 3 + 1] - ay;
            double cx = result.Vertices[c * 3] - ax, cy = result.Vertices[c * 3 + 1] - ay;
            double doubleArea = Math.Abs((bx * cy) - (by * cx));
            double longest = Math.Sqrt(Math.Max((bx * bx) + (by * by), Math.Max((cx * cx) + (cy * cy),
                ((cx - bx) * (cx - bx)) + ((cy - by) * (cy - by)))));
            Assert.True(doubleArea / longest > 1e-3, $"Face {f} is a sliver (height {doubleArea / longest:E2}, length {longest:0.###}).");
        }
    }

    [Fact]
    public void AddSegmentsSplitAtOnSegmentPoints_RoutesThroughPointsWithinTolerance_InOrder()
    {
        double[] xy = { 0, 0, 10, 0, 7, 1e-7, 3, -1e-7, 5, 0.5 };
        var output = new List<(int a, int b)>();

        GradedRegionAssembler.AddSegmentsSplitAtOnSegmentPoints(xy, new[] { (0, 1) }, 0.002, output);

        Assert.Equal(new[] { (0, 3), (3, 2), (2, 1) }, output);
    }

    [Fact]
    public void AddSegmentsSplitAtOnSegmentPoints_TwoPointsWithinTolerance_RoutesThroughOnlyOne()
    {
        // Points 2 and 3 are 0.001 apart — inside tolerance, but a rounding weld can leave both.
        double[] xy = { 0, 0, 10, 0, 5, 0, 5.001, 0 };
        var output = new List<(int a, int b)>();

        GradedRegionAssembler.AddSegmentsSplitAtOnSegmentPoints(xy, new[] { (0, 1) }, 0.002, output);

        Assert.Equal(new[] { (0, 2), (2, 1) }, output);
    }

    [Fact]
    public void Grade_PadDaylightReachingTerrainEdge_ClipsAndUsesExplicit()
    {
        // Flat grid terrain at z=10 over [0,40]; a pad at z=0 near the +x edge so the 45Â° batter
        // (reach 10) runs past x=40 and must clip to the terrain boundary instead of deferring.
        var t = FlatGrid(21, 2.0, 10.0);
        double[] padXy = { 32, 16, 38, 16, 38, 24, 32, 24 };
        var pads = new[] { new PadGrader.PadBoundary(padXy, 4, targetZ: 0.0, slopeAngleDeg: 45.0) };

        GradeOutcome gradeOutcome = PadGrader.Grade(new PadGradeRequest
        {
            Terrain = new IndexedTriMesh(t.v, t.vc, t.f, t.fc),
            Pads = pads,
        });
        string? err = gradeOutcome.ErrorMessage;
        GradingResult? result = gradeOutcome.Result;

        Assert.True(result != null, err);
        Assert.Contains("explicit batter", string.Join(" ", result!.Diagnostics), StringComparison.OrdinalIgnoreCase);
        PadInvariantAssert.AssertWatertightManifold(result);
    }
}
