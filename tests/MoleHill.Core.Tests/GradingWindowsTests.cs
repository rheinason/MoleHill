using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Windowed grading (<see cref="GradingWindows"/>): each group of pads is graded on the faces under its window
/// and stitched back. The result must weld, leave the rest of the terrain alone, and — the point of it — be
/// reproduced exactly by a memoized re-run after an edit, which re-grades only the windows the edit touched.
/// </summary>
public class GradingWindowsTests(ITestOutputHelper output)
{
    private const double MaxDistance = 12.0;

    [Fact]
    public void Grade_TwoSeparatePads_GradesTwoWindowsAndWeldsWatertight()
    {
        Terrain(out double[] v, out int[] f);
        PadGrader.PadBoundary[] pads = Pads(2.0);

        GradingWindows.Outcome outcome = Run(v, f, pads, previous: null, out _);

        Assert.False(outcome.NeedsWholeMesh);
        Assert.Equal(2, outcome.WindowCount);
        GradingResult result = outcome.Result!;
        (int loops, int nonManifold) = Topology(result.Faces, result.FaceCount);
        output.WriteLine($"{f.Length / 3} -> {result.FaceCount} faces, {loops} boundary loop(s), {nonManifold} non-manifold");
        Assert.Equal(1, loops);
        Assert.Equal(0, nonManifold);
        // Far from both pads the terrain is untouched.
        Assert.Contains(FaceSet(result.Vertices, result.Faces, result.FaceCount), key => key.Item1 < 5 && key.Item2 < 5);
    }

    [Fact]
    public void Grade_TerrainEditedAwayFromThePads_MemoizedRunEqualsColdRun()
    {
        Terrain(out double[] v, out int[] f);
        PadGrader.PadBoundary[] pads = Pads(2.0);
        Run(v, f, pads, previous: null, out GradingWindows.Memo memo);

        var edited = (double[])v.Clone();
        edited[2] += 0.5; // the corner vertex, far outside both windows
        GradingWindows.Outcome incremental = Run(edited, f, pads, memo, out GradingWindows.Memo after);
        GradingWindows.Outcome cold = Run(edited, f, pads, previous: null, out _);

        Assert.Equal(2, after.ReusedWindows);
        Assert.Equal(cold.Result!.Vertices, incremental.Result!.Vertices);
        Assert.Equal(cold.Result.Faces, incremental.Result.Faces);
    }

    [Fact]
    public void Grade_OnePadChanged_RegradesOnlyItsWindow()
    {
        Terrain(out double[] v, out int[] f);
        Run(v, f, Pads(2.0), previous: null, out GradingWindows.Memo memo);

        PadGrader.PadBoundary[] changed = Pads(2.0);
        changed[1] = Pad(130, 40, 160, 70, 3.0);
        GradingWindows.Outcome incremental = Run(v, f, changed, memo, out GradingWindows.Memo after);
        GradingWindows.Outcome cold = Run(v, f, changed, previous: null, out _);

        Assert.Equal(1, after.ReusedWindows);
        Assert.Equal(1, after.GradedWindows);
        Assert.Equal(cold.Result!.Faces, incremental.Result!.Faces);
        Assert.Equal(cold.Result.Vertices, incremental.Result.Vertices);
    }

    /// <summary>
    /// A Grade Pad stage input captured from a Retaining Wall stacked before a ring pad: one unowned face sat
    /// in a pocket pinched to the window's rim at a vertex, and grading the window filled that pocket with a
    /// face the rest of the terrain still had — three non-manifold edges. The window must claim the pocket.
    /// </summary>
    [Fact]
    public void GradeWindowed_PocketPinchedToTheWindowRim_WeldsLikeTheWholeMesh()
    {
        ReadPadCase("MoleHill.Core.Tests.TestData.PadWindowPocketCase.bin", out double[] v, out int[] f,
            out PadGrader.PadBoundary[] pads, out List<SurfaceRemesher.ConstraintPolyline> hard, out double tol, out double detail);

        GradingResult whole = PadGrader.Grade(v, v.Length / 3, f, f.Length / 3, pads, null, out _, out _, out _, tol, detail, hard)!;
        var notes = new List<string>();
        GradingResult windowed = PadGrader.GradeWindowed(v, v.Length / 3, f, f.Length / 3, pads, Array.Empty<PadGrader.LockCurve>(),
            hard, tol, detail, null, new GradingWindows.Memo(), notes, out _, out _, out _)!;

        (int loops, int nonManifold) = Topology(windowed.Faces, windowed.FaceCount);
        output.WriteLine($"whole {whole.FaceCount} faces; windowed {windowed.FaceCount} faces, {loops} loop(s), {nonManifold} non-manifold; {string.Join(" | ", notes)}");
        Assert.Equal(0, nonManifold);
        Assert.Equal(1, loops);
        Assert.Equal(whole.FaceCount, windowed.FaceCount);
    }

    private static void ReadPadCase(string resource, out double[] v, out int[] f, out PadGrader.PadBoundary[] pads,
        out List<SurfaceRemesher.ConstraintPolyline> hard, out double tolerance, out double detail)
    {
        using Stream stream = typeof(GradingWindowsTests).Assembly.GetManifestResourceStream(resource)!;
        using var r = new BinaryReader(stream);
        v = new double[r.ReadInt32() * 3];
        for (int i = 0; i < v.Length; i++)
            v[i] = r.ReadDouble();
        f = new int[r.ReadInt32() * 3];
        for (int i = 0; i < f.Length; i++)
            f[i] = r.ReadInt32();

        pads = new PadGrader.PadBoundary[r.ReadInt32()];
        for (int p = 0; p < pads.Length; p++)
        {
            int n = r.ReadInt32();
            var boundary = new double[n * 3];
            for (int i = 0; i < boundary.Length; i++)
                boundary[i] = r.ReadDouble();
            double px = r.ReadDouble(), py = r.ReadDouble(), pc = r.ReadDouble();
            double slope = r.ReadDouble(), fillSlope = r.ReadDouble(), maxDistance = r.ReadDouble();
            int cornerFan = r.ReadInt32();
            double apron = r.ReadDouble();
            pads[p] = PadGrader.PadBoundary.CreatePlanar(boundary, n, px, py, pc, slope, maxDistance, cornerFan, apron, fillSlope);
        }

        hard = new List<SurfaceRemesher.ConstraintPolyline>();
        int constraintCount = r.ReadInt32();
        for (int c = 0; c < constraintCount; c++)
        {
            int n = r.ReadInt32();
            bool closed = r.ReadBoolean(), preserve = r.ReadBoolean();
            var points = new double[n * 3];
            for (int i = 0; i < points.Length; i++)
                points[i] = r.ReadDouble();
            hard.Add(new SurfaceRemesher.ConstraintPolyline(points, n, closed, preserve));
        }

        tolerance = r.ReadDouble();
        detail = r.ReadDouble();
    }

    private static GradingWindows.Outcome Run(double[] v, int[] f, PadGrader.PadBoundary[] pads, GradingWindows.Memo? previous, out GradingWindows.Memo next)
    {
        var reach = pads.Select(p =>
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < p.VertexCount; i++)
            {
                minX = Math.Min(minX, p.XyVertices[i * 2]); maxX = Math.Max(maxX, p.XyVertices[i * 2]);
                minY = Math.Min(minY, p.XyVertices[i * 2 + 1]); maxY = Math.Max(maxY, p.XyVertices[i * 2 + 1]);
            }

            double grow = p.MaxDistance + p.StitchApronDistance;
            return (minX - grow, minY - grow, maxX + grow, maxY + grow);
        }).ToList();

        next = new GradingWindows.Memo();
        return GradingWindows.Grade(
            v, v.Length / 3, f, f.Length / 3, reach, margin: 3.0,
            (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double, double, double, double) _, out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureDiagnostics) =>
            {
                failureDiagnostics = Array.Empty<GradingDiagnostic>();
                return PadGrader.Grade(wv, wvc, wf, wfc, items.Select(i => pads[i]).ToArray(), null, out error, out failurePolylines, 0.001, 1.0);
            },
            (items, _, _, _, _, low, high) =>
            {
                foreach (int i in items)
                {
                    foreach (double d in pads[i].BoundaryVertices.Append(pads[i].SlopeAngleDeg).Append(pads[i].MaxDistance))
                    {
                        low.Add(d);
                        high.Add(d);
                    }
                }
            },
            previous,
            next);
    }

    private static PadGrader.PadBoundary[] Pads(double z) => new[] { Pad(40, 40, 70, 70, z), Pad(130, 40, 160, 70, z) };

    private static PadGrader.PadBoundary Pad(double x0, double y0, double x1, double y1, double z) =>
        new(new[] { x0, y0, x1, y0, x1, y1, x0, y1 }, 4, targetZ: z, slopeAngleDeg: 26.5, maxDistance: MaxDistance);

    private static void Terrain(out double[] vertices, out int[] faces)
    {
        const int nx = 200, ny = 110;
        vertices = new double[(nx + 1) * (ny + 1) * 3];
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                int k = j * (nx + 1) + i;
                uint h = (uint)k * 2654435761u;
                double x = i + (i > 0 && i < nx ? (((h & 0xFFFF) / 65535.0) - 0.5) * 0.4 : 0);
                double y = j + (j > 0 && j < ny ? ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 0.4 : 0);
                vertices[k * 3] = x;
                vertices[k * 3 + 1] = y;
                vertices[k * 3 + 2] = (Math.Sin(x * 0.04) * 3) + (Math.Cos(y * 0.03) * 2);
            }
        }

        var f = new List<int>();
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i;
                f.AddRange(new[] { a, a + 1, a + nx + 2, a, a + nx + 2, a + nx + 1 });
            }
        }

        faces = f.ToArray();
    }

    private static (int Loops, int NonManifold) Topology(int[] faces, int faceCount)
    {
        var uses = new Dictionary<(int, int), int>();
        for (int t = 0; t < faceCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = faces[t * 3 + k], b = faces[t * 3 + ((k + 1) % 3)];
                var key = a < b ? (a, b) : (b, a);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        return (TiledIsotropicRemesherQualityTests.CountLoops(uses.Where(static e => e.Value == 1).Select(static e => e.Key)), uses.Values.Count(static u => u > 2));
    }

    private static HashSet<(double, double)> FaceSet(double[] v, int[] f, int faceCount)
    {
        var set = new HashSet<(double, double)>();
        for (int t = 0; t < faceCount; t++)
        {
            int a = f[t * 3];
            set.Add((v[a * 3], v[a * 3 + 1]));
        }

        return set;
    }
}
