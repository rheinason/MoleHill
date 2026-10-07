using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The contract of <see cref="MeshConstraintTopologyInserter.TryInsertByLocalTriangulation"/>: the constraint
/// ends up as a chain of mesh edges, the terrain stays one watertight manifold, every face outside the
/// re-triangulated patch comes through untouched, and no existing vertex moves or is renumbered.
/// </summary>
public class MeshConstraintLocalTriangulationTests
{
    private const double Tolerance = 0.001;

    [Fact]
    public void Insert_OpenLineAcrossJitteredTerrain_BecomesAnEdgeChainAndLeavesTheRestAlone()
    {
        (double[] vertices, int[] faces) = JitteredGrid(24, 16, 1.0);
        var line = Line(false, 3.3, 2.7, 9.0, 11.6, 20.4, 12.9);

        AssertInsertedCleanly(vertices, faces, line);
    }

    [Fact]
    public void Insert_ClosedRing_BecomesAClosedEdgeChain()
    {
        (double[] vertices, int[] faces) = JitteredGrid(24, 24, 1.0);
        var points = new List<double>();
        for (int k = 0; k < 32; k++)
        {
            double a = 2 * Math.PI * k / 32;
            points.AddRange(new[] { 12 + (7.3 * Math.Cos(a)), 12 + (7.3 * Math.Sin(a)), 5.0 });
        }

        var ring = new ConstraintPolyline(points.ToArray(), 32, true, PreserveInputElevation: true);

        AssertInsertedCleanly(vertices, faces, ring);
    }

    [Fact]
    public void Insert_LineRunningOffTheTerrain_IsClippedAtTheEdge()
    {
        (double[] vertices, int[] faces) = JitteredGrid(12, 12, 1.0);
        var line = Line(false, -3.0, 4.2, 6.1, 5.3, 17.0, 6.9);

        Assert.True(MeshConstraintTopologyInserter.TryInsertByLocalTriangulation(
            vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { line }, Tolerance,
            out _, out _, out int[] outFaces, out int outFaceCount, out string? error), error);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(outFaces, outFaceCount);
        Assert.Equal(1, topology.BoundaryComponentCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
    }

    private static void AssertInsertedCleanly(double[] vertices, int[] faces, ConstraintPolyline constraint)
    {
        int vertexCount = vertices.Length / 3, faceCount = faces.Length / 3;
        Assert.True(MeshConstraintTopologyInserter.TryInsertByLocalTriangulation(
            vertices, vertexCount, faces, faceCount, new[] { constraint }, Tolerance,
            out double[] outVertices, out int outVertexCount, out int[] outFaces, out int outFaceCount, out string? error), error);

        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(outFaces, outFaceCount);
        Assert.Equal(1, topology.BoundaryComponentCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);

        // Existing vertices keep their index and position.
        for (int i = 0; i < vertexCount * 3; i++)
            Assert.Equal(vertices[i], outVertices[i]);

        // The constraint is in the mesh as one edge chain.
        InsertedConstraintTracer.TraceAll(new[] { constraint }, new IndexedTriMesh(outVertices, outVertexCount, outFaces, outFaceCount), Tolerance, out int traced);
        Assert.Equal(1, traced);

        // Only faces near the constraint were replaced: most of the terrain is carried over as it was.
        var before = new HashSet<(int, int, int)>();
        for (int f = 0; f < faceCount; f++)
            before.Add(Canonical(faces[f * 3], faces[f * 3 + 1], faces[f * 3 + 2]));
        int kept = 0;
        for (int f = 0; f < outFaceCount; f++)
        {
            if (before.Contains(Canonical(outFaces[f * 3], outFaces[f * 3 + 1], outFaces[f * 3 + 2])))
                kept++;
        }

        Assert.True(kept > faceCount / 2, $"only {kept} of {faceCount} faces survived untouched");
    }

    private static (int, int, int) Canonical(int a, int b, int c)
    {
        int[] s = { a, b, c };
        Array.Sort(s);
        return (s[0], s[1], s[2]);
    }

    private static ConstraintPolyline Line(bool closed, params double[] xy)
    {
        var points = new double[xy.Length / 2 * 3];
        for (int i = 0; i < xy.Length / 2; i++)
        {
            points[i * 3] = xy[i * 2];
            points[i * 3 + 1] = xy[i * 2 + 1];
            points[i * 3 + 2] = 5.0;
        }

        return new ConstraintPolyline(points, xy.Length / 2, closed, PreserveInputElevation: true);
    }

    private static (double[] vertices, int[] faces) JitteredGrid(int nx, int ny, double step)
    {
        var v = new List<double>();
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                uint h = unchecked(((uint)i * 374761393u) + ((uint)j * 668265263u));
                h = unchecked((h ^ (h >> 13)) * 1274126177u);
                bool edge = i == 0 || j == 0 || i == nx || j == ny;
                double jx = edge ? 0 : (((h & 0xFFFF) / 65535.0) - 0.5) * step * 0.5;
                double jy = edge ? 0 : ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * step * 0.5;
                v.Add((i * step) + jx);
                v.Add((j * step) + jy);
                v.Add(Math.Sin(i * 0.3) + Math.Cos(j * 0.2));
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
}
