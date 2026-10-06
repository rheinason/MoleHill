using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class InsertedConstraintTracerTests
{
    private const double Tolerance = 0.01;

    [Fact]
    public void TraceAll_LineInsertedIntoGrid_FollowsTheInsertedEdgesEndToEnd()
    {
        (double[] vertices, int[] faces) = Grid(20, 10, 2.0);
        double[] drawn = [0.7, 1.3, 1.0, 37.1, 17.9, 1.0];
        var line = new SurfaceRemesher.ConstraintPolyline(drawn, 2, false, PreserveInputElevation: true);

        Assert.True(TerrainDetailInserter.TryInsert(
            vertices, vertices.Length / 3, faces, faces.Length / 3,
            Array.Empty<double>(),
            new[] { line },
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            Tolerance, Tolerance, 70.0,
            out TerrainDetailInserter.Result? inserted, out string? error), error);

        List<SurfaceRemesher.ConstraintPolyline> traced = InsertedConstraintTracer.TraceAll(
            new[] { line }, inserted!.Vertices, inserted.VertexCount, inserted.Faces, inserted.FaceCount, Tolerance, out int tracedCount);

        Assert.Equal(1, tracedCount);
        SurfaceRemesher.ConstraintPolyline result = traced[0];
        Assert.True(result.PointCount > 10, $"a line crossing ~30 grid edges must be split at them, got {result.PointCount} points");

        // Every traced point is a mesh vertex, and consecutive points are joined by a mesh edge.
        var edges = new HashSet<long>();
        for (int t = 0; t < inserted.FaceCount; t++)
        {
            for (int k = 0; k < 3; k++)
                edges.Add(IndexedMeshTools.GetEdgeKey(inserted.Faces[t * 3 + k], inserted.Faces[t * 3 + ((k + 1) % 3)]));
        }

        int previous = -1;
        for (int i = 0; i < result.PointCount; i++)
        {
            int vertex = FindVertex(inserted.Vertices, inserted.VertexCount, result.Points[i * 3], result.Points[i * 3 + 1]);
            Assert.True(vertex >= 0, $"traced point {i} is not a mesh vertex");
            if (previous >= 0)
                Assert.Contains(IndexedMeshTools.GetEdgeKey(previous, vertex), edges);
            previous = vertex;
        }

        Assert.Equal(drawn[0], result.Points[0], 6);
        Assert.Equal(drawn[3], result.Points[(result.PointCount - 1) * 3], 6);

        // The point of the exercise: a later constrained rebuild with the traced line leaves no caps.
        SurfaceRemesher.Result rebuilt = SurfaceRemesher.Remesh(
            inserted.Vertices[..(inserted.VertexCount * 3)],
            inserted.Faces[..(inserted.FaceCount * 3)],
            new[] { result },
            new SurfaceRemesher.Options
            {
                Tolerance = Tolerance,
                ProtectSharpEdges = true,
                ConstraintInsertionOnly = true,
                AddReducedInteriorGuideSeeds = false,
                AddConstraintCorridorSeeds = false
            });
        Assert.True(rebuilt.Success, rebuilt.Warning);
        Assert.Equal(0, CountCaps(rebuilt.Vertices, rebuilt.Faces));
    }

    /// <summary>
    /// A closed line's inserted start can sit a hair before its drawn start, on the closing segment, where its
    /// arc parameter reads as almost the whole length. Found on graded ring walls, whose conform snapped the
    /// ring's first point a few millimetres back: the trace looked for a start near zero, found none, and the
    /// wall stage re-inserted the ring and tore the terrain.
    /// </summary>
    [Fact]
    public void TraceAll_ClosedLineWhoseInsertedStartLiesBeforeTheSeam_StillTraces()
    {
        (double[] vertices, int[] faces) = Grid(20, 20, 2.0);
        double[] inserted = [20.0, 9.3, 1.0, 31.1, 9.3, 1.0, 31.1, 30.7, 1.0, 9.1, 30.7, 1.0, 9.1, 9.3, 1.0];
        var ring = new SurfaceRemesher.ConstraintPolyline(inserted, 5, true, PreserveInputElevation: true);
        Assert.True(TerrainDetailInserter.TryInsert(
            vertices, vertices.Length / 3, faces, faces.Length / 3,
            Array.Empty<double>(),
            new[] { ring },
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            Tolerance, Tolerance, 70.0,
            out TerrainDetailInserter.Result? mesh, out string? error), error);

        // The same ring, drawn starting 4 mm further along its bottom side.
        double[] drawn = (double[])inserted.Clone();
        drawn[0] += 0.004;
        var drawnRing = new SurfaceRemesher.ConstraintPolyline(drawn, 5, true, PreserveInputElevation: true);

        InsertedConstraintTracer.TraceAll(
            new[] { drawnRing }, mesh!.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount, Tolerance, out int tracedCount);

        Assert.Equal(1, tracedCount);
    }

    [Fact]
    public void TraceAll_LineRunningOffTheTerrain_TracesThePieceInsideIt()
    {
        // Verandi Lendi: seven of seventeen curbs ran past the terrain edge, could not trace whole, and were
        // persisted as drawn - outside part included.
        (double[] vertices, int[] faces) = Grid(20, 10, 2.0);
        var line = new SurfaceRemesher.ConstraintPolyline([-6.0, 7.3, 1.0, 46.0, 13.1, 1.0], 2, false, PreserveInputElevation: true);
        Assert.True(TerrainDetailInserter.TryInsert(
            vertices, vertices.Length / 3, faces, faces.Length / 3,
            Array.Empty<double>(),
            new[] { line },
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            Tolerance, Tolerance, 70.0,
            out TerrainDetailInserter.Result? inserted, out string? error), error);

        List<SurfaceRemesher.ConstraintPolyline> traced = InsertedConstraintTracer.TraceAll(
            new[] { line }, inserted!.Vertices, inserted.VertexCount, inserted.Faces, inserted.FaceCount, Tolerance, out int tracedCount);

        Assert.Equal(1, tracedCount);
        SurfaceRemesher.ConstraintPolyline piece = Assert.Single(traced);
        Assert.Equal(0.0, piece.Points[0], 6);
        Assert.Equal(40.0, piece.Points[(piece.PointCount - 1) * 3], 6);
    }

    [Fact]
    public void TraceAll_LineNotInTheMesh_KeepsTheDrawnLine()
    {
        (double[] vertices, int[] faces) = Grid(10, 10, 2.0);
        double[] drawn = [0.7, 1.3, 1.0, 17.1, 15.9, 1.0];
        var line = new SurfaceRemesher.ConstraintPolyline(drawn, 2, false, PreserveInputElevation: true);

        List<SurfaceRemesher.ConstraintPolyline> traced = InsertedConstraintTracer.TraceAll(
            new[] { line }, vertices, vertices.Length / 3, faces, faces.Length / 3, Tolerance, out int tracedCount);

        Assert.Equal(0, tracedCount);
        Assert.Same(drawn, traced[0].Points);
    }

    private static (double[] vertices, int[] faces) Grid(int nx, int ny, double step)
    {
        var v = new List<double>();
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                v.Add(i * step);
                v.Add(j * step);
                v.Add(1.0);
            }
        }

        var f = new List<int>();
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int a = (j * (nx + 1)) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                f.AddRange(new[] { a, b, d, a, d, c });
            }
        }

        return (v.ToArray(), f.ToArray());
    }

    private static int FindVertex(double[] v, int count, double x, double y)
    {
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(v[i * 3] - x) < 1e-9 && Math.Abs(v[i * 3 + 1] - y) < 1e-9)
                return i;
        }

        return -1;
    }

    private static int CountCaps(double[] v, int[] f)
    {
        int count = 0;
        for (int t = 0; t < f.Length / 3; t++)
        {
            int a = f[t * 3], b = f[t * 3 + 1], c = f[t * 3 + 2];
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            double nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
            double doubleArea = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            double longest = Math.Max(Len(v, a, b), Math.Max(Len(v, b, c), Len(v, c, a)));
            if (doubleArea < Tolerance * longest)
                count++;
        }

        return count;
    }

    private static double Len(double[] v, int a, int b) =>
        Math.Sqrt(Math.Pow(v[a * 3] - v[b * 3], 2) + Math.Pow(v[a * 3 + 1] - v[b * 3 + 1], 2) + Math.Pow(v[a * 3 + 2] - v[b * 3 + 2], 2));
}
