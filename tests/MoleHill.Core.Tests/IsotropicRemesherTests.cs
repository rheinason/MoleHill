using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers <see cref="IsotropicRemesher"/>: the split/collapse/flip/relax/back-project loop with pinned
/// feature polylines and frozen retaining-wall faces. The load-bearing invariants: every output vertex
/// sits exactly on the input surface, features survive as chains, walls pass through verbatim, the
/// boundary polygon is never deformed, and the output is always watertight.
/// </summary>
public class IsotropicRemesherTests
{
    private static readonly IReadOnlyList<SurfaceRemesher.ConstraintPolyline> NoConstraints =
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    // === Helpers ======================================================================================

    /// <summary>Regular grid triangulation over explicit row/column coordinates; each cell → 2 triangles.</summary>
    private static (double[] vertices, int[] faces) BuildGrid(double[] xs, double[] ys, Func<double, double, double> z)
    {
        int nx = xs.Length, ny = ys.Length;
        var vertices = new double[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = (j * nx) + i;
                vertices[v * 3] = xs[i];
                vertices[v * 3 + 1] = ys[j];
                vertices[v * 3 + 2] = z(xs[i], ys[j]);
            }
        }

        var faces = new int[(nx - 1) * (ny - 1) * 6];
        int f = 0;
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                int v00 = (j * nx) + i;
                int v10 = v00 + 1;
                int v01 = v00 + nx;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, faces);
    }

    private static double[] Steps(double from, double to, double step)
    {
        var values = new List<double>();
        for (double v = from; v <= to + 1e-9; v += step)
            values.Add(v);
        return values.ToArray();
    }

    /// <summary>Deterministic per-vertex jitter (hash-based) applied to interior vertices only.</summary>
    private static void JitterInterior(double[] vertices, double amount, double minX, double maxX, double minY, double maxY)
    {
        int count = vertices.Length / 3;
        for (int i = 0; i < count; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            if (x <= minX + 1e-9 || x >= maxX - 1e-9 || y <= minY + 1e-9 || y >= maxY - 1e-9)
                continue;
            uint h = (uint)(i * 2654435761u);
            double jx = (((h & 0xFFFF) / 65535.0) - 0.5) * 2.0 * amount;
            double jy = ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 2.0 * amount;
            vertices[i * 3] = x + jx;
            vertices[i * 3 + 1] = y + jy;
        }
    }

    private static IsotropicRemesher.Result RemeshOrThrow(
        double[] vertices, int[] faces, IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints, IsotropicRemesher.Options options)
    {
        var result = IsotropicRemesher.Remesh(vertices, faces, constraints, options);
        Assert.True(result.Success, result.Warning ?? "remesh failed");
        return result;
    }

    private static List<(int a, int b)> CollectEdges(int[] faces)
    {
        var seen = new HashSet<long>();
        var edges = new List<(int a, int b)>();
        for (int t = 0; t < faces.Length / 3; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                int a = faces[t * 3 + c];
                int b = faces[t * 3 + ((c + 1) % 3)];
                long key = IndexedMeshTools.GetEdgeKey(a, b);
                if (seen.Add(key))
                    edges.Add((a, b));
            }
        }

        return edges;
    }

    private static double EdgeLength(double[] v, int a, int b)
    {
        double dx = v[a * 3] - v[b * 3];
        double dy = v[a * 3 + 1] - v[b * 3 + 1];
        double dz = v[a * 3 + 2] - v[b * 3 + 2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static void AssertWatertight(int[] faces)
    {
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faces.Length / 3);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);
    }

    // === Tests ========================================================================================

    [Fact]
    public void Remesh_JitteredDenseGrid_EdgeLengthsApproachTargetAndQualityImproves()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 10, 0.5), Steps(0, 10, 0.5), (x, y) => 0.3 * Math.Sin(x * 0.5) * Math.Cos(y * 0.4));
        JitterInterior(vertices, 0.15, 0, 10, 0, 10);

        var result = RemeshOrThrow(vertices, faces, NoConstraints, new IsotropicRemesher.Options
        {
            TargetEdgeLength = 1.0,
            CreaseAngleDeg = 30,
            Tolerance = 0.01
        });

        Assert.True(result.Collapses > 50, $"expected substantial coarsening, got {result.Collapses} collapses");

        var edges = CollectEdges(result.Faces);
        int within = edges.Count(e =>
        {
            double len = EdgeLength(result.Vertices, e.a, e.b);
            return len >= 0.5 && len <= 1.5;
        });
        Assert.True(within >= edges.Count * 0.8, $"only {within}/{edges.Count} edges within [0.5, 1.5] of target");

        // Mean min-angle must improve versus the jittered input (the whole point of the loop).
        double before = MeanMinAngle(vertices, faces);
        double after = MeanMinAngle(result.Vertices, result.Faces);
        Assert.True(after > before, $"mean min angle regressed: {before:F3} -> {after:F3}");

        AssertWatertight(result.Faces);
    }

    private static double MeanMinAngle(double[] vertices, int[] faces)
    {
        int faceCount = faces.Length / 3;
        double sum = 0;
        for (int t = 0; t < faceCount; t++)
            sum += MeshFlipGeometry.MinTriangleAngle(vertices, faces[t * 3], faces[t * 3 + 1], faces[t * 3 + 2]);
        return sum / faceCount;
    }

    [Fact]
    public void Remesh_CurvedSwale_AllOutputVerticesOnInputSurface()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 10, 1.0), Steps(0, 10, 1.0), (x, y) => 1.2 * Math.Cos((y - 5.0) * 0.5));
        JitterInterior(vertices, 0.25, 0, 10, 0, 10);

        var reference = new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3);
        var result = RemeshOrThrow(vertices, faces, NoConstraints, new IsotropicRemesher.Options
        {
            TargetEdgeLength = 0.8,
            CreaseAngleDeg = 30,
            Tolerance = 0.01
        });

        int outputCount = result.Vertices.Length / 3;
        for (int i = 0; i < outputCount; i++)
        {
            double x = result.Vertices[i * 3], y = result.Vertices[i * 3 + 1], z = result.Vertices[i * 3 + 2];
            Assert.True(reference.TryInterpolateZ(x, y, out double expected), $"vertex {i} left the input XY footprint");
            Assert.True(Math.Abs(z - expected) < 1e-6, $"vertex {i} off surface by {Math.Abs(z - expected):E2}");
        }
    }

    [Fact]
    public void Remesh_RoofRidge_CreaseChainSurvivesAndNoFaceStraddlesIt()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 10, 1.0), Steps(-5, 5, 1.0), (x, y) => 2.0 - (Math.Abs(y) * 0.5));

        var result = RemeshOrThrow(vertices, faces, NoConstraints, new IsotropicRemesher.Options
        {
            TargetEdgeLength = 1.4,
            CreaseAngleDeg = 20,
            Tolerance = 0.01
        });

        // Ridge vertices stay exactly on the ridge (y = 0, z = 2).
        int ridgeVertices = 0;
        int count = result.Vertices.Length / 3;
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(result.Vertices[i * 3 + 1]) < 1e-9)
            {
                ridgeVertices++;
                Assert.True(Math.Abs(result.Vertices[i * 3 + 2] - 2.0) < 1e-9, "ridge vertex dropped below the ridge");
            }
        }

        Assert.True(ridgeVertices >= 2, "ridge chain vanished");

        // No triangle may chord across the ridge: any face with vertices strictly on both sides of
        // y = 0 must touch a ridge vertex (i.e., the ridge is an edge chain, not flipped across).
        for (int t = 0; t < result.Faces.Length / 3; t++)
        {
            bool above = false, below = false, onRidge = false;
            for (int c = 0; c < 3; c++)
            {
                double y = result.Vertices[result.Faces[t * 3 + c] * 3 + 1];
                if (Math.Abs(y) < 1e-9) onRidge = true;
                else if (y > 0) above = true;
                else below = true;
            }

            Assert.False(above && below && !onRidge, $"face {t} chords across the ridge");
        }
    }

    [Fact]
    public void Remesh_LShapedBreakline_CornerVertexStaysExactlyPut()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 10, 1.0), Steps(0, 10, 1.0), (_, _) => 0.0);

        // L-shaped breakline riding grid edges: (2,2) → (2,7) → (7,7).
        var points = new List<double>();
        for (int y = 2; y <= 7; y++) { points.Add(2); points.Add(y); points.Add(0); }
        for (int x = 3; x <= 7; x++) { points.Add(x); points.Add(7); points.Add(0); }
        var constraint = new SurfaceRemesher.ConstraintPolyline(points.ToArray(), points.Count / 3, IsClosed: false);

        var result = RemeshOrThrow(vertices, faces, new[] { constraint }, new IsotropicRemesher.Options
        {
            TargetEdgeLength = 1.6,
            CreaseAngleDeg = 30,
            Tolerance = 0.01
        });

        bool cornerSurvived = false;
        for (int i = 0; i < result.Vertices.Length / 3; i++)
        {
            if (result.Vertices[i * 3] == 2.0 && result.Vertices[i * 3 + 1] == 7.0)
                cornerSurvived = true;
        }

        Assert.True(cornerSurvived, "the L corner (2,7) moved or was collapsed away");

        // Both legs survive as feature chains: at least one more vertex exactly on each leg.
        int onLeg1 = 0, onLeg2 = 0;
        for (int i = 0; i < result.Vertices.Length / 3; i++)
        {
            double x = result.Vertices[i * 3], y = result.Vertices[i * 3 + 1];
            if (x == 2.0 && y >= 2.0 && y < 7.0) onLeg1++;
            if (y == 7.0 && x > 2.0 && x <= 7.0) onLeg2++;
        }

        Assert.True(onLeg1 >= 1, "vertical breakline leg vanished");
        Assert.True(onLeg2 >= 1, "horizontal breakline leg vanished");
        AssertWatertight(result.Faces);
    }

    [Fact]
    public void Remesh_ThinParallelBreaklines_NeverMergeAcrossTheCorridor()
    {
        // Rows at y = 5.0 and y = 5.4 with target 1.0: the 0.4 rungs are collapse candidates and the
        // guards must refuse to merge the two feature lines.
        double[] ys = { 0, 1, 2, 3, 4, 5.0, 5.4, 6.4, 7.4, 8.4, 9.4 };
        var (vertices, faces) = BuildGrid(Steps(0, 10, 1.0), ys, (x, y) => y >= 5.4 ? 1.0 : (y <= 5.0 ? 0.0 : (y - 5.0) * 2.5));

        var line1 = new List<double>();
        var line2 = new List<double>();
        for (int x = 0; x <= 10; x++)
        {
            line1.Add(x); line1.Add(5.0); line1.Add(0.0);
            line2.Add(x); line2.Add(5.4); line2.Add(1.0);
        }

        var constraints = new[]
        {
            new SurfaceRemesher.ConstraintPolyline(line1.ToArray(), line1.Count / 3, IsClosed: false),
            new SurfaceRemesher.ConstraintPolyline(line2.ToArray(), line2.Count / 3, IsClosed: false)
        };

        var result = RemeshOrThrow(vertices, faces, constraints, new IsotropicRemesher.Options
        {
            TargetEdgeLength = 1.0,
            CreaseAngleDeg = 30,
            Tolerance = 0.01
        });

        // A cross-corridor merge would leave vertices at y ≈ 5.2 with z ≈ 0.5 and delete the exact
        // lines; the guards must keep both chains at their exact y AND exact elevation. (Free split
        // midpoints of corridor diagonals may legitimately sit between the lines, ON the slope.)
        int onLine1 = 0, onLine2 = 0;
        for (int i = 0; i < result.Vertices.Length / 3; i++)
        {
            double y = result.Vertices[i * 3 + 1];
            double z = result.Vertices[i * 3 + 2];
            if (Math.Abs(y - 5.0) < 1e-9)
            {
                onLine1++;
                Assert.True(Math.Abs(z) < 1e-9, $"lower breakline vertex {i} left its elevation (z={z})");
            }
            else if (Math.Abs(y - 5.4) < 1e-9)
            {
                onLine2++;
                Assert.True(Math.Abs(z - 1.0) < 1e-9, $"upper breakline vertex {i} left its elevation (z={z})");
            }
        }

        Assert.True(onLine1 >= 2, "lower breakline vanished");
        Assert.True(onLine2 >= 2, "upper breakline vanished");
        AssertWatertight(result.Faces);
    }

    [Fact]
    public void Remesh_SteepWallBand_WallVerticesBitEqualAndMeshWatertight()
    {
        // Two plateaus joined by a near-vertical band (rise 3 over run 0.2 ≈ 86° > 70° threshold).
        double[] xs = { 0, 1, 2, 3, 4, 5, 5.2, 6.2, 7.2, 8.2, 9.2, 10.2 };
        var (vertices, faces) = BuildGrid(xs, Steps(0, 6, 1.0), (x, y) => x <= 5.0 ? 0.0 : (x >= 5.2 ? 3.0 : (x - 5.0) * 15.0));

        var result = RemeshOrThrow(vertices, faces, NoConstraints, new IsotropicRemesher.Options
        {
            TargetEdgeLength = 1.5,
            CreaseAngleDeg = 30,
            Tolerance = 0.01,
            WallFaceMinSlopeDeg = 70
        });

        // Every input wall-band vertex (x = 5 or x = 5.2) must exist bit-identical in the output.
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double x = vertices[i * 3];
            if (x != 5.0 && x != 5.2)
                continue;

            bool found = false;
            for (int o = 0; o < result.Vertices.Length / 3 && !found; o++)
            {
                found = result.Vertices[o * 3] == vertices[i * 3]
                    && result.Vertices[o * 3 + 1] == vertices[i * 3 + 1]
                    && result.Vertices[o * 3 + 2] == vertices[i * 3 + 2];
            }

            Assert.True(found, $"wall vertex ({vertices[i * 3]}, {vertices[i * 3 + 1]}, {vertices[i * 3 + 2]}) was moved or removed");
        }

        Assert.Contains(result.FrozenFaces, frozen => frozen);
        AssertWatertight(result.Faces);
    }

    [Fact]
    public void Remesh_CoarseTarget_BoundaryPolygonAreaAndPerimeterPreserved()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 10, 0.5), Steps(0, 8, 0.5), (x, y) => 0.2 * Math.Sin(x) * Math.Sin(y));

        var result = RemeshOrThrow(vertices, faces, NoConstraints, new IsotropicRemesher.Options
        {
            TargetEdgeLength = 2.0,
            CreaseAngleDeg = 30,
            Tolerance = 0.01
        });

        Assert.True(result.Collapses > 100, "coarse target should trigger heavy collapsing");

        (double areaIn, double perimeterIn) = BoundaryAreaPerimeter(vertices, faces);
        (double areaOut, double perimeterOut) = BoundaryAreaPerimeter(result.Vertices, result.Faces);
        Assert.True(Math.Abs(areaOut - areaIn) < areaIn * 1e-6, $"boundary area changed: {areaIn} -> {areaOut}");
        Assert.True(Math.Abs(perimeterOut - perimeterIn) < perimeterIn * 1e-6, $"boundary perimeter changed: {perimeterIn} -> {perimeterOut}");
    }

    private static (double area, double perimeter) BoundaryAreaPerimeter(double[] vertices, int[] faces)
    {
        // Directed boundary edges (a face's directed edge whose reverse never occurs). Faces are CCW in
        // XY, so the directed boundary edges wind consistently and the shoelace sum is exact.
        var directed = new HashSet<long>();
        for (int t = 0; t < faces.Length / 3; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                int u = faces[t * 3 + c];
                int v = faces[t * 3 + ((c + 1) % 3)];
                directed.Add(((long)u << 32) | (uint)v);
            }
        }

        double perimeter = 0;
        double area2 = 0;
        foreach (long key in directed)
        {
            int u = (int)(key >> 32);
            int v = (int)(key & 0xFFFFFFFFL);
            if (directed.Contains(((long)v << 32) | (uint)u))
                continue; // interior edge

            double ax = vertices[u * 3], ay = vertices[u * 3 + 1];
            double bx = vertices[v * 3], by = vertices[v * 3 + 1];
            perimeter += Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            area2 += (ax * by) - (bx * ay);
        }

        return (Math.Abs(area2) * 0.5, perimeter);
    }

    [Fact]
    public void Remesh_SameInputTwice_IdenticalOutput()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 8, 0.7), Steps(0, 8, 0.7), (x, y) => 0.4 * Math.Sin(x * 0.7) + (0.3 * y * 0.1));
        JitterInterior(vertices, 0.2, 0, 8, 0, 8);

        var options = new IsotropicRemesher.Options { TargetEdgeLength = 1.1, CreaseAngleDeg = 30, Tolerance = 0.01 };
        var first = RemeshOrThrow((double[])vertices.Clone(), (int[])faces.Clone(), NoConstraints, options);
        var second = RemeshOrThrow((double[])vertices.Clone(), (int[])faces.Clone(), NoConstraints, options);

        Assert.Equal(first.Vertices, second.Vertices);
        Assert.Equal(first.Faces, second.Faces);
    }

    [Fact]
    public void Remesh_NoTarget_ReturnsInputWithWarning()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 2, 1.0), Steps(0, 2, 1.0), (_, _) => 0.0);
        var result = IsotropicRemesher.Remesh(vertices, faces, NoConstraints, new IsotropicRemesher.Options { TargetEdgeLength = 0 });
        Assert.False(result.Success);
        Assert.Same(vertices, result.Vertices);
    }
}
