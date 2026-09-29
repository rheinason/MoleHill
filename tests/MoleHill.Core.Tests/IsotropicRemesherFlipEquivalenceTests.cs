using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// <see cref="IsotropicRemesher.FlipForQuality(IsotropicRemesher.MeshState)"/> must make exactly the flips
/// the dictionary-driven phase made (<see cref="IsotropicRemesherFlipReference"/>), in the same order. Each
/// case builds two identical states, flips one with each, and compares every face index.
/// </summary>
public class IsotropicRemesherFlipEquivalenceTests
{
    [Fact]
    public void FlipForQuality_JitteredGridWithBadDiagonals_MatchesReference()
    {
        BuildJitteredGrid(60, 1.0, out double[] vertices, out int[] faces);
        AssertFlipsMatch(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), wallSlopeDeg: 0.0);
    }

    [Fact]
    public void FlipForQuality_BreaklineAndSteepWall_MatchesReference()
    {
        BuildJitteredGrid(50, 1.0, out double[] vertices, out int[] faces);

        // A wall: lift one row of the grid by 6 m over a single cell, so the faces across it are steep and
        // frozen. A breakline crosses the grid diagonally, so its edges are pinned features.
        const int n = 50;
        for (int j = 30; j <= n; j++)
            for (int i = 0; i <= n; i++)
                vertices[((j * (n + 1)) + i) * 3 + 2] += 6.0;

        var breakline = new SurfaceRemesher.ConstraintPolyline(
            new[] { 5.0, 5.0, 0.0, 20.0, 12.0, 0.0, 40.0, 25.0, 0.0 },
            PointCount: 3,
            IsClosed: false,
            PreserveInputElevation: false);
        AssertFlipsMatch(vertices, faces, new[] { breakline }, wallSlopeDeg: 70.0);
    }

    [Fact]
    public void FlipForQuality_DeadFacesAndHighDegreeFan_MatchesReference()
    {
        BuildJitteredGrid(40, 1.0, out double[] gridVertices, out int[] gridFaces);

        // A fan of 80 thin triangles around one hub beside the grid: the hub's bucket is far past the
        // index's insertion-sort limit, so the large-bucket sort path is exercised too.
        var vertices = new List<double>(gridVertices);
        var faces = new List<int>(gridFaces);
        int hub = vertices.Count / 3;
        vertices.AddRange(new[] { 60.0, 20.0, 0.0 });
        int ringStart = vertices.Count / 3;
        const int spokes = 80;
        for (int s = 0; s < spokes; s++)
        {
            double angle = 2 * Math.PI * s / spokes;
            vertices.AddRange(new[] { 60.0 + 8 * Math.Cos(angle), 20.0 + 8 * Math.Sin(angle), 0.1 * s % 1.3 });
        }

        for (int s = 0; s < spokes; s++)
            faces.AddRange(new[] { hub, ringStart + s, ringStart + ((s + 1) % spokes) });

        int[] faceArray = faces.ToArray();
        double[] vertexArray = vertices.ToArray();

        // Tombstone every 17th grid face, as a collapse round leaves them before its sweep.
        IsotropicRemesher.MeshState reference = CreateState(vertexArray, faceArray, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 0.0);
        IsotropicRemesher.MeshState indexed = CreateState(vertexArray, faceArray, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 0.0);
        for (int t = 0; t < gridFaces.Length / 3; t += 17)
        {
            reference.Tris[t * 3] = -1;
            indexed.Tris[t * 3] = -1;
        }

        AssertSameFlips(reference, indexed);
    }

    [Fact]
    public void FlipForQuality_FieldAlignedRetopoPath_MatchesReference()
    {
        BuildJitteredGrid(45, 1.0, out double[] vertices, out int[] faces);
        int vertexCount = vertices.Length / 3;
        var theta = new double[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            theta[v] = 0.25 + (vertices[v * 3] * 0.02) - (vertices[v * 3 + 1] * 0.015);

        IsotropicRemesher.MeshState reference = CreateState(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 0.0);
        IsotropicRemesher.MeshState indexed = CreateState(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 0.0);
        reference.Field = CreateField(vertices, faces, theta);
        indexed.Field = CreateField(vertices, faces, theta);

        AssertSameFlips(reference, indexed);
    }

    /// <summary>
    /// The whole operator loop, iteration by iteration: split and collapse leave states the flip phase would
    /// never see on a fresh grid, and the rejection memo is only exercised across sweeps that move nothing.
    /// </summary>
    [Fact]
    public void RemeshLoop_EveryIteration_MatchesReference()
    {
        BuildJitteredGrid(70, 0.5, out double[] vertices, out int[] faces);
        const double target = 1.1;
        var projection = new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3, target * 0.5);
        IsotropicRemesher.MeshState reference = CreateState(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 0.0);
        IsotropicRemesher.MeshState indexed = CreateState(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 0.0);

        int totalFlips = 0;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            foreach (IsotropicRemesher.MeshState state in new[] { reference, indexed })
            {
                IsotropicRemesher.SplitLongEdges(state, target * 8.0 / 5.0, projection);
                IsotropicRemesher.CollapseShortEdges(state, target, projection);
            }

            Assert.Equal(reference.Tris, indexed.Tris);
            int expected = IsotropicRemesherFlipReference.FlipForQuality(reference);
            int actual = IsotropicRemesher.FlipForQuality(indexed);
            Assert.Equal(expected, actual);
            Assert.Equal(reference.Tris, indexed.Tris);
            totalFlips += actual;

            IsotropicRemesher.RelaxAndProject(reference, target, projection);
            IsotropicRemesher.RelaxAndProject(indexed, target, projection);
            Assert.Equal(reference.Verts, indexed.Verts);
        }

        Assert.True(totalFlips > 0, "the loop made no flips, so it compared nothing");
    }

    private static void AssertFlipsMatch(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double wallSlopeDeg)
    {
        AssertSameFlips(
            CreateState(vertices, faces, constraints, wallSlopeDeg),
            CreateState(vertices, faces, constraints, wallSlopeDeg));
    }

    private static void AssertSameFlips(IsotropicRemesher.MeshState reference, IsotropicRemesher.MeshState indexed)
    {
        int expected = IsotropicRemesherFlipReference.FlipForQuality(reference);
        int actual = IsotropicRemesher.FlipForQuality(indexed);

        Assert.True(expected > 0, "the reference made no flips, so the case compares nothing");
        Assert.Equal(expected, actual);
        Assert.Equal(reference.Tris, indexed.Tris);
    }

    private static IsotropicRemesher.MeshState CreateState(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double wallSlopeDeg)
    {
        FeaturePolylineGraph graph = FeaturePolylineGraph.Build(
            vertices, faces, faces.Length / 3, constraints,
            creaseAngleDeg: 30.0, wallFaceMinSlopeDeg: wallSlopeDeg, tolerance: 0.01);
        return new IsotropicRemesher.MeshState(vertices, faces, graph);
    }

    private static IsotropicRemesher.FieldSampler CreateField(double[] vertices, int[] faces, double[] theta) =>
        new(faces, theta, new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3, 0.5));

    /// <summary>
    /// A jittered grid whose cells take a hashed diagonal, so roughly half of them start with the worse
    /// diagonal and Lawson has real work to do.
    /// </summary>
    private static void BuildJitteredGrid(int n, double spacing, out double[] vertices, out int[] faces)
    {
        vertices = new double[(n + 1) * (n + 1) * 3];
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                int v = (j * (n + 1)) + i;
                uint h = (uint)v * 2654435761u;
                double jx = i > 0 && i < n ? (((h & 0xFFFF) / 65535.0) - 0.5) * 0.6 * spacing : 0;
                double jy = j > 0 && j < n ? ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 0.6 * spacing : 0;
                vertices[v * 3] = (i * spacing) + jx;
                vertices[v * 3 + 1] = (j * spacing) + jy;
                vertices[v * 3 + 2] = Math.Sin(i * 0.3) * Math.Cos(j * 0.25);
            }
        }

        faces = new int[n * n * 6];
        int f = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v00 = (j * (n + 1)) + i;
                int v10 = v00 + 1, v01 = v00 + n + 1, v11 = v00 + n + 2;
                if ((((uint)(i * 73856093) ^ (uint)(j * 19349663)) & 1) == 0)
                {
                    faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                    faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
                }
                else
                {
                    faces[f++] = v00; faces[f++] = v10; faces[f++] = v01;
                    faces[f++] = v10; faces[f++] = v11; faces[f++] = v01;
                }
            }
        }
    }
}
