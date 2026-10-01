using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// <see cref="GradingWindows.Stitch"/> welds only window vertices through coordinates and maps the rest by
/// index. On a normalized mesh that must give exactly the arrays the original weld-everything stitch gave,
/// kept here as the reference.
/// </summary>
public class GradingWindowsStitchTests
{
    [Fact]
    public void Stitch_GradedAndFailedWindows_EqualsWeldingEveryVertexByCoordinates()
    {
        const int side = 12;
        var vertices = new List<double>();
        for (int y = 0; y <= side; y++)
        {
            for (int x = 0; x <= side; x++)
            {
                vertices.Add(x);
                vertices.Add(y);
                vertices.Add(Math.Round(Math.Sin(x * 0.7) + Math.Cos(y * 0.4), 3));
            }
        }

        var faces = new List<int>();
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int a = (y * (side + 1)) + x;
                faces.AddRange(new[] { a, a + 1, a + side + 2, a, a + side + 2, a + side + 1 });
            }
        }

        double[] v = vertices.ToArray();
        int[] f = faces.ToArray();
        int faceCount = f.Length / 3;
        int vertexCount = v.Length / 3;

        // Window 0: a 3x3 block re-triangulated with a raised centre vertex; window 1: a 2x2 block that failed.
        var owner = Enumerable.Repeat(-1, faceCount).ToArray();
        var window0 = new List<int>();
        var window1 = new List<int>();
        for (int t = 0; t < faceCount; t++)
        {
            int cell = t / 2, x = cell % side, y = cell / side;
            if (x is >= 2 and < 5 && y is >= 2 and < 5)
                window0.Add(t);
            else if (x is >= 7 and < 9 && y is >= 6 and < 8)
                window1.Add(t);
        }

        foreach (int t in window0)
            owner[t] = 0;
        foreach (int t in window1)
            owner[t] = 1;

        GradingResult patch = Retriangulate(v, f, window0, (3.5, 3.5));
        var outputs = new (GradingResult?, int[], GradingWindows.WindowResult)[]
        {
            (patch, window0.ToArray(), new GradingWindows.WindowResult(patch, null, Array.Empty<OutputPolyline>(), Array.Empty<GradingDiagnostic>(), true)),
            (null, window1.ToArray(), new GradingWindows.WindowResult(null, "declined", Array.Empty<OutputPolyline>(), Array.Empty<GradingDiagnostic>(), true))
        };

        GradingWindows.Outcome actual = GradingWindows.Stitch(v, vertexCount, f, faceCount, owner, outputs, 0);
        (double[] expectedV, int[] expectedF) = ReferenceStitch(v, f, faceCount, owner, outputs);

        Assert.NotNull(actual.Result);
        Assert.Equal(expectedV, actual.Result!.Vertices);
        Assert.Equal(expectedF, actual.Result.Faces);
        Assert.Equal(new[] { "declined" }, actual.Errors);
    }

    /// <summary>The window's faces fanned from a new vertex at <paramref name="centre"/>, boundary kept.</summary>
    private static GradingResult Retriangulate(double[] v, int[] f, List<int> windowFaces, (double X, double Y) centre)
    {
        var uses = new Dictionary<(int, int), int>();
        foreach (int t in windowFaces)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = f[t * 3 + k], b = f[t * 3 + ((k + 1) % 3)];
                var key = a < b ? (a, b) : (b, a);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        var local = new List<double> { centre.X, centre.Y, 9.0 };
        var map = new Dictionary<int, int>();
        int Local(int g)
        {
            if (!map.TryGetValue(g, out int i))
            {
                i = local.Count / 3;
                map[g] = i;
                local.Add(v[g * 3]);
                local.Add(v[g * 3 + 1]);
                local.Add(v[g * 3 + 2]);
            }

            return i;
        }

        var outF = new List<int>();
        foreach (int t in windowFaces)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = f[t * 3 + k], b = f[t * 3 + ((k + 1) % 3)];
                if (uses[a < b ? (a, b) : (b, a)] == 1)
                    outF.AddRange(new[] { Local(a), Local(b), 0 });
            }
        }

        return new GradingResult(local.ToArray(), local.Count / 3, outF.ToArray(), outF.Count / 3, 0, 0, Array.Empty<double>(), 0);
    }

    /// <summary>The stitch as it was: every corner welded through a coordinate dictionary.</summary>
    private static (double[] V, int[] F) ReferenceStitch(
        double[] vertices, int[] faces, int faceCount, int[] owner, (GradingResult? Result, int[] Faces, GradingWindows.WindowResult Graded)[] outputs)
    {
        var outV = new List<double>();
        var outF = new List<int>();
        var index = new Dictionary<(double, double, double), int>();
        int Vertex(double x, double y, double z)
        {
            if (!index.TryGetValue((x, y, z), out int i))
            {
                i = outV.Count / 3;
                index[(x, y, z)] = i;
                outV.Add(x);
                outV.Add(y);
                outV.Add(z);
            }

            return i;
        }

        for (int t = 0; t < faceCount; t++)
        {
            if (owner[t] >= 0)
                continue;
            for (int k = 0; k < 3; k++)
            {
                int g = faces[t * 3 + k];
                outF.Add(Vertex(vertices[g * 3], vertices[g * 3 + 1], vertices[g * 3 + 2]));
            }
        }

        foreach ((GradingResult? result, int[] windowFaces, _) in outputs)
        {
            if (result == null)
            {
                foreach (int t in windowFaces)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int g = faces[t * 3 + k];
                        outF.Add(Vertex(vertices[g * 3], vertices[g * 3 + 1], vertices[g * 3 + 2]));
                    }
                }

                continue;
            }

            for (int t = 0; t < result.FaceCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int g = result.Faces[t * 3 + k];
                    outF.Add(Vertex(result.Vertices[g * 3], result.Vertices[g * 3 + 1], result.Vertices[g * 3 + 2]));
                }
            }
        }

        return (outV.ToArray(), outF.ToArray());
    }
}
