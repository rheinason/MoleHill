using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The two properties an incremental Remesh rests on (docs/incremental-rebuild-design-2026-09-29.md, D1):
/// the tiled remesh is a pure function of its input, whatever the thread scheduling, and an edit changes its
/// output only near the edit. Together they mean re-running the tiles an edit touches reproduces a cold build.
/// </summary>
public class TiledIsotropicRemesherLocalityTests(ITestOutputHelper output)
{
    private const double Tile = 24.0;

    [Fact]
    public void Remesh_SameInputTwice_IsBitIdentical()
    {
        TiledIsotropicRemesherQualityTests.GradedTerrain(96, out double[] vertices, out int[] faces);
        IsotropicRemesher.Options options = OptionsFor(vertices, faces);

        IsotropicRemesher.Result first = TiledIsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), options, Tile);
        IsotropicRemesher.Result second = TiledIsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), options, Tile);

        Assert.Equal(first.Vertices, second.Vertices);
        Assert.Equal(first.Faces, second.Faces);
    }

    [Fact]
    public void Remesh_OneVertexRaised_ChangesOnlyFacesNearTheEdit()
    {
        TiledIsotropicRemesherQualityTests.GradedTerrain(96, out double[] vertices, out int[] faces);
        IsotropicRemesher.Options options = OptionsFor(vertices, faces);
        IsotropicRemesher.Result before = TiledIsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), options, Tile);

        // Raise the input vertex nearest a point well away from the road.
        const double ex = 80.0, ey = 12.0;
        int edited = 0;
        double best = double.MaxValue;
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double d = Math.Pow(vertices[i * 3] - ex, 2) + Math.Pow(vertices[i * 3 + 1] - ey, 2);
            if (d < best)
            {
                best = d;
                edited = i;
            }
        }

        var raised = (double[])vertices.Clone();
        raised[edited * 3 + 2] += 0.5;
        IsotropicRemesher.Result after = TiledIsotropicRemesher.Remesh(raised, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), options, Tile);

        HashSet<(double, double, double, double, double, double, double, double, double)> beforeFaces = FaceSet(before);
        HashSet<(double, double, double, double, double, double, double, double, double)> afterFaces = FaceSet(after);
        var changed = beforeFaces.Except(afterFaces).Concat(afterFaces.Except(beforeFaces)).ToList();
        double farthest = changed.Count == 0 ? 0 : changed.Max(f => Math.Sqrt(Math.Pow(((f.Item1 + f.Item4 + f.Item7) / 3) - vertices[edited * 3], 2) + Math.Pow(((f.Item2 + f.Item5 + f.Item8) / 3) - vertices[edited * 3 + 1], 2)));
        output.WriteLine($"{changed.Count:N0} of {beforeFaces.Count + afterFaces.Count:N0} faces differ; the farthest is {farthest:0.0} m from the edit (tile {Tile} m)");

        Assert.NotEmpty(changed);
        // Each of the three passes can carry a change at most one tile further: the tile holding it re-runs,
        // and its output reaches that tile's edge.
        Assert.True(farthest < 3.0 * Tile * Math.Sqrt(2.0), $"a face {farthest:0.0} m from the edit changed");
        Assert.True(changed.Count < (beforeFaces.Count + afterFaces.Count) / 2, "most of the terrain changed");
    }

    private static IsotropicRemesher.Options OptionsFor(double[] vertices, int[] faces) => new()
    {
        TargetEdgeLength = IsotropicRemesher.EstimateFaceCountPreservingTarget(vertices, faces),
        CreaseAngleDeg = 30,
        Tolerance = 0.001,
        WallFaceMinSlopeDeg = 70
    };

    private static HashSet<(double, double, double, double, double, double, double, double, double)> FaceSet(IsotropicRemesher.Result result)
    {
        double[] v = result.Vertices;
        int[] f = result.Faces;
        var set = new HashSet<(double, double, double, double, double, double, double, double, double)>();
        for (int t = 0; t < f.Length / 3; t++)
        {
            // Rotated to start at the lexicographically smallest corner, so the key is independent of numbering.
            var corners = new[] { f[t * 3], f[t * 3 + 1], f[t * 3 + 2] }
                .Select(i => (v[i * 3], v[i * 3 + 1], v[i * 3 + 2])).ToArray();
            int start = 0;
            for (int k = 1; k < 3; k++)
            {
                if (corners[k].CompareTo(corners[start]) < 0)
                    start = k;
            }

            var a = corners[start];
            var b = corners[(start + 1) % 3];
            var c = corners[(start + 2) % 3];
            set.Add((a.Item1, a.Item2, a.Item3, b.Item1, b.Item2, b.Item3, c.Item1, c.Item2, c.Item3));
        }

        return set;
    }
}
