using MoleHill.Rhino.Services;
using MoleHill.Shared;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Hosted probe: builds meshes that exercise every rule of Rhino's normalization (seams of duplicated and
/// float-equal vertices, unused vertices, collinear and repeated-index faces, far-from-origin coordinates)
/// and checks that the managed normalization in <see cref="RhinoGeometryConversions.BuildMesh"/> hands on
/// exactly the arrays Rhino's would. Run inside Rhino: <c>Start(logPath)</c>.
/// </summary>
public static class NormalizeEquivalenceProbe
{
    public static void Start(string logPath)
    {
        Environment.SetEnvironmentVariable("MOLEHILL_VERIFY_NORMALIZE", logPath);
        var random = new Random(20260930);
        foreach (double origin in new[] { 0.0, 1.0, 4000.0, 250000.0 })
        {
            foreach (int size in new[] { 3, 12, 60 })
            {
                for (int trial = 0; trial < 8; trial++)
                {
                    Messy(random, origin, size, out double[] v, out int[] f);
                    RhinoGeometryConversions.BuildMesh(v, v.Length / 3, f, f.Length / 3);
                }
            }
        }

        Environment.SetEnvironmentVariable("MOLEHILL_VERIFY_NORMALIZE", null);
        File.AppendAllText(logPath, "done" + Environment.NewLine);
    }

    /// <summary>
    /// A jittered grid split in two along a column, each half with its own copy of the seam vertices (some
    /// moved by less than a float step, some exactly equal), plus unused vertices, collinear faces and faces
    /// with a repeated index, all consistently wound.
    /// </summary>
    private static void Messy(Random random, double origin, int n, out double[] vertices, out int[] faces)
    {
        var v = new List<double>();
        var index = new int[n + 1, n + 1];
        int seam = n / 2;
        var seamCopy = new Dictionary<int, int>();
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                double x = origin + i + ((random.NextDouble() - 0.5) * 0.3);
                double y = origin + j + ((random.NextDouble() - 0.5) * 0.3);
                double z = random.NextDouble();
                index[i, j] = v.Count / 3;
                v.AddRange(new[] { x, y, z });
                if (i == seam)
                {
                    // The right half's copy: exactly equal, or nudged below float resolution at this magnitude.
                    double nudge = random.Next(3) switch { 0 => 0.0, 1 => Math.Max(Math.Abs(x), 1.0) * 1e-9, _ => Math.Max(Math.Abs(x), 1.0) * 2e-8 };
                    seamCopy[index[i, j]] = v.Count / 3;
                    v.AddRange(new[] { x + nudge, y, z });
                }

                if (random.NextDouble() < 0.05)
                    v.AddRange(new[] { origin + (random.NextDouble() * n), origin + (random.NextDouble() * n), 9.0 });   // unused
            }
        }

        var f = new List<int>();
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int a = index[i, j], b = index[i + 1, j], c = index[i + 1, j + 1], d = index[i, j + 1];
                if (i == seam)
                {
                    a = seamCopy[a];
                    d = seamCopy[d];
                }

                f.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        // A collinear face along the bottom edge and a face with a repeated index.
        int p0 = v.Count / 3;
        v.AddRange(new[] { origin, origin - 1, 0.0, origin + 1, origin - 1, 0.0, origin + 2, origin - 1, 0.0 });
        f.AddRange(new[] { p0, p0 + 1, p0 + 2 });
        f.AddRange(new[] { index[0, 0], index[0, 0], index[1, 1] });

        vertices = v.ToArray();
        faces = f.ToArray();
    }
}
