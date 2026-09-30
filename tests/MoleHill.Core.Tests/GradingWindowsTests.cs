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
    /// A window follows its item's shape, not its box: a ring road's window is a band, the land inside the ring
    /// stays out, and an edit there reuses the window. (An identity grader stands in for the real one.)
    /// </summary>
    [Fact]
    public void Grade_RingShapedItem_LeavesTheInsideOutAndReusesTheBandAfterAnEditThere()
    {
        Terrain(out double[] v, out int[] f);
        const int segments = 48;
        var ring = new double[segments * 2];
        for (int i = 0; i < segments; i++)
        {
            double a = 2.0 * Math.PI * i / segments;
            ring[i * 2] = 100.0 + (40.0 * Math.Cos(a));
            ring[i * 2 + 1] = 55.0 + (40.0 * Math.Sin(a));
        }

        var reach = new[] { new GradingWindows.Reach(ring, segments, Closed: true, Filled: false, Radius: 5.0) };
        int graded = 0;
        GradingWindows.Outcome RunRing(double[] vertices, GradingWindows.Memo? previous, out GradingWindows.Memo next)
        {
            next = new GradingWindows.Memo();
            return GradingWindows.Grade(
                vertices, vertices.Length / 3, f, f.Length / 3, reach, margin: 2.0,
                (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double, double, double, double) _, out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureDiagnostics) =>
                {
                    graded++;
                    error = null;
                    failurePolylines = Array.Empty<OutputPolyline>();
                    failureDiagnostics = Array.Empty<GradingDiagnostic>();
                    return new GradingResult(wv, wvc, wf, wfc, 0, 0, Array.Empty<double>(), 0);
                },
                (_, _, _, _, _, _, _) => { },
                previous,
                next);
        }

        GradingWindows.Outcome cold = RunRing(v, previous: null, out GradingWindows.Memo memo);
        Assert.Equal(1, cold.WindowCount);
        Assert.False(cold.NeedsWholeMesh);
        (int loops, int nonManifold) = Topology(cold.Result!.Faces, cold.Result.FaceCount);
        Assert.Equal(1, loops);
        Assert.Equal(0, nonManifold);

        // The ring's centre: 40 m from the road, outside its 5 m reach.
        int centre = (55 * 201) + 100;
        var edited = (double[])v.Clone();
        edited[centre * 3 + 2] += 1.0;
        GradingWindows.Outcome incremental = RunRing(edited, memo, out GradingWindows.Memo after);
        output.WriteLine($"{f.Length / 3} faces; graded {graded} time(s); reused {after.ReusedWindows}");
        Assert.Equal(1, after.ReusedWindows);
        Assert.Equal(1, graded);
        Assert.Equal(f.Length / 3, incremental.Result!.FaceCount);
    }

    /// <summary>
    /// A grader expects its input to be a terrain: one boundary loop, no pinched vertex. Faces chosen by distance
    /// can meet at a single rim vertex (read as an open boundary chain), and a long border sliver taken in by its
    /// bounding circle adds a loop of its own; either made the explicit Grade Path batter defer to a softer
    /// tier next to a retaining wall. The road here runs close enough to the border for both.
    /// </summary>
    [Theory]
    [InlineData(1.3)]
    [InlineData(2.2)]
    [InlineData(3.0)]
    [InlineData(4.4)]
    [InlineData(7.5)]
    [InlineData(12.25)]
    public void Grade_DiagonalRoad_WindowIsOneLoopWithNoPinchedVertex(double radius)
    {
        IrregularTerrain(out double[] v, out int[] f);
        var road = new double[] { 10.3, 7.1, 95.2, 88.6, 190.4, 12.9 };
        var reach = new[] { new GradingWindows.Reach(road, 3, Closed: false, Filled: false, Radius: radius) };
        int pinched = -1, loops = -1;
        GradingWindows.Grade(
            v, v.Length / 3, f, f.Length / 3, reach, margin: 0.7,
            (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double, double, double, double) _, out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureDiagnostics) =>
            {
                pinched = PinchedRimVertices(wf, wfc);
                loops = Topology(wf, wfc).Loops;
                error = null;
                failurePolylines = Array.Empty<OutputPolyline>();
                failureDiagnostics = Array.Empty<GradingDiagnostic>();
                return new GradingResult(wv, wvc, wf, wfc, 0, 0, Array.Empty<double>(), 0);
            },
            (_, _, _, _, _, _, _) => { },
            previous: null,
            new GradingWindows.Memo());

        Assert.Equal(0, pinched);
        Assert.Equal(1, loops);
    }

    /// <summary>A grid whose cells split along a hashed diagonal, so faces chosen by distance have ragged rims.</summary>
    private static void IrregularTerrain(out double[] vertices, out int[] faces)
    {
        const int nx = 200, ny = 110;
        vertices = new double[(nx + 1) * (ny + 1) * 3];
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                int k = j * (nx + 1) + i;
                uint h = (uint)k * 2654435761u;
                vertices[k * 3] = i + (i > 0 && i < nx ? (((h & 0xFFFF) / 65535.0) - 0.5) * 0.8 : 0);
                vertices[k * 3 + 1] = j + (j > 0 && j < ny ? ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 0.8 : 0);
                vertices[k * 3 + 2] = Math.Sin(i * 0.04) * 3;
            }
        }

        var f = new List<int>();
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i;
                if ((((uint)(a * 40503) >> 7) & 1) == 0)
                    f.AddRange(new[] { a, a + 1, a + nx + 2, a, a + nx + 2, a + nx + 1 });
                else
                    f.AddRange(new[] { a, a + 1, a + nx + 1, a + 1, a + nx + 2, a + nx + 1 });
            }
        }

        faces = f.ToArray();
    }

    /// <summary>
    /// A long sliver along the border (the kind a triangulated survey's hull leaves) is 19 m from the road, but
    /// its bounding circle reaches within a metre of it. Taken in by the circle, it became a window piece of its
    /// own, a second boundary loop, and the explicit Grade Path batter deferred.
    /// </summary>
    [Fact]
    public void Grade_LongBorderSliverOutOfReach_StaysOutOfTheWindow()
    {
        var v = new List<double>();
        for (int j = 1; j <= 40; j++)
        {
            for (int i = 0; i <= 40; i++)
                v.AddRange(new[] { i, (double)j, 0.0 });
        }

        int corner0 = v.Count / 3;
        v.AddRange(new[] { 0.0, 0.0, 0.0 });
        int corner1 = v.Count / 3;
        v.AddRange(new[] { 40.0, 0.0, 0.0 });
        int Grid(int i, int j) => ((j - 1) * 41) + i;

        var f = new List<int>();
        for (int j = 1; j < 40; j++)
        {
            for (int i = 0; i < 40; i++)
                f.AddRange(new[] { Grid(i, j), Grid(i + 1, j), Grid(i + 1, j + 1), Grid(i, j), Grid(i + 1, j + 1), Grid(i, j + 1) });
        }

        for (int i = 0; i < 20; i++)
            f.AddRange(new[] { corner0, Grid(i + 1, 1), Grid(i, 1) });
        for (int i = 20; i < 40; i++)
            f.AddRange(new[] { corner1, Grid(i + 1, 1), Grid(i, 1) });
        f.AddRange(new[] { corner0, corner1, Grid(20, 1) });

        var reach = new[] { new GradingWindows.Reach(new double[] { 5, 20, 35, 20 }, 2, Closed: false, Filled: false, Radius: 8.0) };
        int windowFaces = -1, loops = -1;
        double[] vertices = v.ToArray();
        int[] faces = f.ToArray();
        GradingWindows.Grade(
            vertices, vertices.Length / 3, faces, faces.Length / 3, reach, margin: 2.0,
            (double[] wv, int wvc, int[] wf, int wfc, int[] items, (double, double, double, double) _, out string? error, out IReadOnlyList<OutputPolyline> failurePolylines, out IReadOnlyList<GradingDiagnostic> failureDiagnostics) =>
            {
                windowFaces = wfc;
                loops = Topology(wf, wfc).Loops;
                error = null;
                failurePolylines = Array.Empty<OutputPolyline>();
                failureDiagnostics = Array.Empty<GradingDiagnostic>();
                return new GradingResult(wv, wvc, wf, wfc, 0, 0, Array.Empty<double>(), 0);
            },
            (_, _, _, _, _, _, _) => { },
            previous: null,
            new GradingWindows.Memo());

        output.WriteLine($"window {windowFaces} faces, {loops} loop(s)");
        Assert.Equal(1, loops);
    }

    /// <summary>
    /// A window's margin is read from the faces within its own item's reach. It was once read from every face in
    /// the item's bounding box, so one long face anywhere in a park-wide road's box widened the band to the whole
    /// park and every edit re-graded it.
    /// </summary>
    [Fact]
    public void WithMargins_LongFaceOutsideTheReach_LeavesTheMarginAlone()
    {
        Terrain(out double[] v, out int[] f);
        var reach = new[] { new GradingWindows.Reach(new double[] { 20, 20, 180, 100 }, 2, Closed: false, Filled: false, Radius: 10.0) };
        double before = GradingWindows.WithMargins(v, f, f.Length / 3, reach, 0.1)[0].Radius;

        // Stretch the faces around (100, 100), inside the road's bounding box but 35 m from the road, into
        // 60 m slivers that stay out of its reach.
        var stretched = (double[])v.Clone();
        int far = (100 * 201) + 100; // (100, 100)
        stretched[far * 3] -= 60.0;
        double after = GradingWindows.WithMargins(stretched, f, f.Length / 3, reach, 0.1)[0].Radius;

        output.WriteLine($"reach with margin: {before:0.###} before, {after:0.###} after");
        Assert.Equal(before, after);
        Assert.InRange(before - 10.0, 0.1, 5.0);
    }

    private static int PinchedRimVertices(int[] faces, int faceCount)
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

        var rimDegree = new Dictionary<int, int>();
        foreach (((int a, int b), int count) in uses)
        {
            if (count != 1)
                continue;
            rimDegree[a] = rimDegree.GetValueOrDefault(a) + 1;
            rimDegree[b] = rimDegree.GetValueOrDefault(b) + 1;
        }

        return rimDegree.Values.Count(d => d > 2);
    }

    [Fact]
    public void GradePathWindowed_EditAwayFromTheRoad_ReusesItsWindowAndEqualsColdRun()
    {
        Terrain(out double[] v, out int[] f);
        var paths = new[]
        {
            new PathGrader.PathDefinition(new double[] { 20, 20, 100, 60, 180, 30 }, new double[] { 4.0, 5.0, 3.0 }, 3, width: 4.0, slopeAngleDeg: 30.0, maxDistance: 12.0)
        };
        GradingResult? Run(double[] vertices, GradingWindows.Memo? previous, out GradingWindows.Memo next, List<string> notes)
        {
            next = new GradingWindows.Memo();
            return PathGrader.GradeWindowed(vertices, vertices.Length / 3, f, f.Length / 3, paths,
                Array.Empty<SurfaceRemesher.ConstraintPolyline>(), 0.001, preferSplitKeep: false, previous, next, notes, out _);
        }

        var notes = new List<string>();
        GradingResult first = Run(v, previous: null, out GradingWindows.Memo memo, notes)!;
        (int loops, int nonManifold) = Topology(first.Faces, first.FaceCount);
        output.WriteLine($"{f.Length / 3} -> {first.FaceCount} faces, {loops} loop(s), {nonManifold} non-manifold; {string.Join(" | ", notes)}");
        Assert.Equal(1, loops);
        Assert.Equal(0, nonManifold);
        Assert.DoesNotContain(notes, n => n.Contains("whole terrain"));

        var edited = (double[])v.Clone();
        int far = (105 * 201) + 20; // (20, 105): 40 m from the road
        edited[far * 3 + 2] += 0.5;
        GradingResult incremental = Run(edited, memo, out GradingWindows.Memo after, new List<string>())!;
        GradingResult cold = Run(edited, previous: null, out _, new List<string>())!;
        Assert.Equal(1, after.ReusedWindows);
        Assert.Equal(cold.Vertices, incremental.Vertices);
        Assert.Equal(cold.Faces, incremental.Faces);
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
            new GradingWindows.Reach(p.XyVertices, p.VertexCount, Closed: true, Filled: true, Radius: p.MaxDistance + p.StitchApronDistance)).ToList();

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
