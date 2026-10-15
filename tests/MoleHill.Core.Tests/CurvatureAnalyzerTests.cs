using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Curvature and landform classes on analytic surfaces whose curvature is known in closed form. All
/// curvatures are convex positive: a dome is positive, a bowl negative.
/// </summary>
public class CurvatureAnalyzerTests
{
    /// <summary>
    /// A square grid from -<paramref name="half"/> to +<paramref name="half"/> at <paramref name="spacing"/>,
    /// two triangles per cell with alternating diagonals, each vertex at <paramref name="height"/>(x, y).
    /// </summary>
    internal static IndexedTriMesh Grid(double half, double spacing, Func<double, double, double> height)
    {
        int cells = (int)Math.Round(2.0 * half / spacing);
        int side = cells + 1;
        var vertices = new double[side * side * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                double x = -half + (i * spacing);
                double y = -half + (j * spacing);
                int v = (j * side) + i;
                vertices[v * 3] = x;
                vertices[(v * 3) + 1] = y;
                vertices[(v * 3) + 2] = height(x, y);
            }
        }

        var faces = new int[cells * cells * 6];
        int f = 0;
        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                int a = (j * side) + i;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;
                if (((i + j) & 1) == 0)
                {
                    faces[f++] = a; faces[f++] = b; faces[f++] = d;
                    faces[f++] = a; faces[f++] = d; faces[f++] = c;
                }
                else
                {
                    faces[f++] = a; faces[f++] = b; faces[f++] = c;
                    faces[f++] = b; faces[f++] = d; faces[f++] = c;
                }
            }
        }

        return IndexedTriMesh.FromArrays(vertices, faces);
    }

    /// <summary>A Delaunay triangulation of the given plan points, each at <paramref name="height"/>(x, y).</summary>
    internal static IndexedTriMesh Triangulate(IReadOnlyList<(double X, double Y)> points, Func<double, double, double> height)
    {
        var xy = new double[points.Count * 2];
        var z = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            xy[i * 2] = points[i].X;
            xy[(i * 2) + 1] = points[i].Y;
            z[i] = height(points[i].X, points[i].Y);
        }

        TinResult? result = new TinEngine().Build(xy, z, Array.Empty<int>(), QualitySettings.None, out string? message);
        Assert.True(result != null, message);
        return new IndexedTriMesh(result!.Vertices, result.VertexCount, result.Faces, result.FaceCount);
    }

    /// <summary>A jittered lattice: irregular triangles of uneven size, fixed seed.</summary>
    private static List<(double X, double Y)> JitteredPoints(double half, double spacing, int seed)
    {
        var random = new Random(seed);
        var points = new List<(double X, double Y)>();
        int cells = (int)Math.Round(2.0 * half / spacing);
        for (int j = 0; j <= cells; j++)
        {
            for (int i = 0; i <= cells; i++)
            {
                bool edge = i == 0 || j == 0 || i == cells || j == cells;
                double jitter = edge ? 0.0 : spacing * 0.4;
                points.Add((
                    -half + (i * spacing) + ((random.NextDouble() - 0.5) * 2.0 * jitter),
                    -half + (j * spacing) + ((random.NextDouble() - 0.5) * 2.0 * jitter)));
            }
        }

        return points;
    }

    private static (double X, double Y) Centroid(IndexedTriMesh mesh, int face)
    {
        double x = 0.0, y = 0.0;
        for (int k = 0; k < 3; k++)
        {
            int v = mesh.Faces[(face * 3) + k];
            x += mesh.Vertices[v * 3];
            y += mesh.Vertices[(v * 3) + 1];
        }

        return (x / 3.0, y / 3.0);
    }

    private static int NearestFace(IndexedTriMesh mesh, double x, double y)
    {
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            (double cx, double cy) = Centroid(mesh, f);
            double d = ((cx - x) * (cx - x)) + ((cy - y) * (cy - y));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = f;
            }
        }

        return best;
    }

    /// <summary>Faces whose centroid satisfies <paramref name="where"/>.</summary>
    private static IEnumerable<int> Faces(IndexedTriMesh mesh, Func<double, double, bool> where)
    {
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            (double x, double y) = Centroid(mesh, f);
            if (where(x, y))
                yield return f;
        }
    }

    [Fact]
    public void Formulas_ProfileAndPlan_AverageToTheMean()
    {
        double p = 0.3, q = -0.2, r = 0.04, s = -0.01, t = 0.07;

        double mean = CurvatureField.MeanCurvature(p, q, r, s, t);
        double profile = CurvatureField.ProfileCurvature(p, q, r, s, t);
        double plan = CurvatureField.PlanCurvature(p, q, r, s, t);
        CurvatureField.PrincipalCurvatures(p, q, r, s, t, out double max, out double min);

        Assert.Equal(mean, (profile + plan) / 2.0, 12);
        Assert.Equal(mean, (max + min) / 2.0, 12);
    }

    [Fact]
    public void Formulas_LevelGround_ProfileAndPlanFallBackToTheMean()
    {
        double mean = CurvatureField.MeanCurvature(0.0, 0.0, -0.1, 0.0, -0.05);

        Assert.Equal(mean, CurvatureField.ProfileCurvature(0.0, 0.0, -0.1, 0.0, -0.05), 12);
        Assert.Equal(mean, CurvatureField.PlanCurvature(0.0, 0.0, -0.1, 0.0, -0.05), 12);
        Assert.True(mean > 0.0, "z = -0.05x² - 0.025y² is a dome, convex, so positive.");
    }

    [Fact]
    public void Measure_SlopingPlane_IsPlanarEverywhere()
    {
        IndexedTriMesh mesh = Grid(20.0, 1.0, (x, y) => (0.1 * x) + (0.05 * y) + 3.0);

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 5.0);
        sbyte[] classes = LandformClassifier.Classify(field, 1000.0);

        for (int f = 0; f < mesh.FaceCount; f++)
        {
            Assert.True(field.IsMeasured(f));
            Assert.Equal(0.0, field.Value(f, CurvatureMeasure.Mean), 9);
            Assert.Equal(0.0, field.Value(f, CurvatureMeasure.Profile), 9);
            Assert.Equal(0.0, field.Value(f, CurvatureMeasure.Plan), 9);
            Assert.Equal((sbyte)LandformClass.Planar, classes[f]);
        }
    }

    /// <summary>A paraboloid dome z = -(x²+y²)/(2ρ) has curvature 1/ρ at its summit in every direction.</summary>
    [Fact]
    public void Measure_Dome_IsConvexPositiveAndPeaks()
    {
        const double rho = 50.0;
        IndexedTriMesh mesh = Grid(15.0, 0.5, (x, y) => -((x * x) + (y * y)) / (2.0 * rho));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 3.0);
        int summit = NearestFace(mesh, 0.0, 0.0);

        Assert.Equal(1.0 / rho, field.Value(summit, CurvatureMeasure.Mean), 4);
        Assert.Equal(1.0 / rho, field.Value(summit, CurvatureMeasure.Profile), 4);
        Assert.Equal(1.0 / rho, field.Value(summit, CurvatureMeasure.Plan), 4);
        sbyte[] classes = LandformClassifier.Classify(field, 200.0);
        Assert.All(Faces(mesh, (x, y) => Math.Abs(x) < 10 && Math.Abs(y) < 10), f => Assert.Equal((sbyte)LandformClass.Peak, classes[f]));
    }

    [Fact]
    public void Measure_Bowl_IsConcaveNegativeAndHollow()
    {
        const double rho = 50.0;
        IndexedTriMesh mesh = Grid(15.0, 0.5, (x, y) => ((x * x) + (y * y)) / (2.0 * rho));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 3.0);
        int floor = NearestFace(mesh, 0.0, 0.0);

        Assert.Equal(-1.0 / rho, field.Value(floor, CurvatureMeasure.Mean), 4);
        sbyte[] classes = LandformClassifier.Classify(field, 200.0);
        Assert.All(Faces(mesh, (x, y) => Math.Abs(x) < 10 && Math.Abs(y) < 10), f => Assert.Equal((sbyte)LandformClass.Hollow, classes[f]));
    }

    /// <summary>
    /// A level, rounded berm z = -x²/(2ρ). Across it the ground is convex along the fall line (profile),
    /// and along it straight (plan zero). Its crest is the ridge; its rounded flanks are shoulders.
    /// </summary>
    [Fact]
    public void Measure_LevelBerm_ProfileCarriesTheBendAndTheCrestIsTheRidge()
    {
        const double rho = 20.0;
        IndexedTriMesh mesh = Grid(15.0, 0.5, (x, y) => -(x * x) / (2.0 * rho));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 4.0);
        int flank = NearestFace(mesh, 10.0, 0.0);
        double expectedProfile = (1.0 / rho) / Math.Pow(1.0 + Math.Pow(10.0 / rho, 2), 1.5);

        Assert.Equal(expectedProfile, field.Value(flank, CurvatureMeasure.Profile), 3);
        Assert.Equal(0.0, field.Value(flank, CurvatureMeasure.Plan), 4);

        sbyte[] classes = LandformClassifier.Classify(field, 100.0);
        Assert.All(Faces(mesh, (x, y) => Math.Abs(x) < 1.0 && Math.Abs(y) < 10), f => Assert.Equal((sbyte)LandformClass.Ridge, classes[f]));
        Assert.All(Faces(mesh, (x, y) => Math.Abs(x) > 4.0 && Math.Abs(x) < 12 && Math.Abs(y) < 10), f => Assert.Equal((sbyte)LandformClass.Shoulder, classes[f]));
    }

    /// <summary>
    /// A spur falling along its length, z = -x²/(2ρ) - 0.2y: the bend is across the fall line, so it is plan
    /// curvature, and the crest is a ridge all the way down.
    /// </summary>
    [Fact]
    public void Measure_DescendingSpur_PlanCarriesTheBendAndReadsAsRidge()
    {
        const double rho = 20.0;
        IndexedTriMesh mesh = Grid(15.0, 0.5, (x, y) => (-(x * x) / (2.0 * rho)) - (0.2 * y));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 4.0);
        int crest = NearestFace(mesh, 0.0, 0.0);

        Assert.Equal((1.0 / rho) / Math.Sqrt(1.04), field.Value(crest, CurvatureMeasure.Plan), 3);
        Assert.Equal(0.0, field.Value(crest, CurvatureMeasure.Profile), 3);
        sbyte[] classes = LandformClassifier.Classify(field, 100.0);
        Assert.All(Faces(mesh, (x, y) => Math.Abs(x) < 3.0 && Math.Abs(y) < 10), f => Assert.Equal((sbyte)LandformClass.Ridge, classes[f]));
    }

    [Fact]
    public void Measure_DescendingSwale_PlanIsConcaveAndReadsAsValley()
    {
        const double rho = 20.0;
        IndexedTriMesh mesh = Grid(15.0, 0.5, (x, y) => ((x * x) / (2.0 * rho)) - (0.2 * y));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 4.0);
        int floor = NearestFace(mesh, 0.0, 0.0);

        Assert.Equal(-(1.0 / rho) / Math.Sqrt(1.04), field.Value(floor, CurvatureMeasure.Plan), 3);
        sbyte[] classes = LandformClassifier.Classify(field, 100.0);
        Assert.All(Faces(mesh, (x, y) => Math.Abs(x) < 3.0 && Math.Abs(y) < 10), f => Assert.Equal((sbyte)LandformClass.Valley, classes[f]));
    }

    /// <summary>
    /// The foot of a slope easing out onto the level: concave along the fall line only, so a toe, while a
    /// slope steepening over an edge is a shoulder.
    /// </summary>
    [Fact]
    public void Measure_SlopeEasingOut_IsAToe_AndSteepening_IsAShoulder()
    {
        // Both fall along +x everywhere in the tested window (the slope never levels out there), so the
        // whole bend runs along the fall line.
        const double rho = 30.0;
        IndexedTriMesh easing = Grid(10.0, 0.5, (x, y) => (-0.5 * x) + ((x * x) / (2.0 * rho)));
        IndexedTriMesh steepening = Grid(10.0, 0.5, (x, y) => (-0.5 * x) - ((x * x) / (2.0 * rho)));

        sbyte[] toe = LandformClassifier.Classify(CurvatureAnalyzer.Measure(easing, 3.0), 100.0);
        sbyte[] shoulder = LandformClassifier.Classify(CurvatureAnalyzer.Measure(steepening, 3.0), 100.0);

        Assert.All(Faces(easing, (x, y) => Math.Abs(x) < 6 && Math.Abs(y) < 6), f => Assert.Equal((sbyte)LandformClass.Toe, toe[f]));
        Assert.All(Faces(steepening, (x, y) => Math.Abs(x) < 6 && Math.Abs(y) < 6), f => Assert.Equal((sbyte)LandformClass.Shoulder, shoulder[f]));
    }

    /// <summary>
    /// z = (x² - y²)/(2ρ) is a saddle everywhere, including along its diagonals, where profile and plan
    /// curvature are both zero. The principal curvatures catch it; profile-by-plan alone would call those
    /// diagonals planar.
    /// </summary>
    [Fact]
    public void Measure_Saddle_IsASaddleEverywhere_WithZeroMean()
    {
        const double rho = 20.0;
        IndexedTriMesh mesh = Grid(15.0, 0.5, (x, y) => ((x * x) - (y * y)) / (2.0 * rho));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 3.0);
        sbyte[] classes = LandformClassifier.Classify(field, 100.0);

        int col = NearestFace(mesh, 0.0, 0.0);
        Assert.Equal(0.0, field.Value(col, CurvatureMeasure.Mean), 4);
        Assert.All(Faces(mesh, (x, y) => Math.Abs(x) < 8 && Math.Abs(y) < 8), f => Assert.Equal((sbyte)LandformClass.Saddle, classes[f]));
    }

    /// <summary>
    /// The landform size is the scale control. A 0.3 m hump on the flank of a 40 m mound dominates the
    /// curvature at a 0.5 m landform size, and has gone at 10 m: the result there matches the mound alone.
    /// </summary>
    [Fact]
    public void Measure_SmallHumpOnBroadMound_ShowsAtSmallSize_AndVanishesAtLargeSize()
    {
        static double Mound(double x, double y) => 4.0 * Math.Exp(-((x * x) + (y * y)) / (2.0 * 10.0 * 10.0));
        static double Hump(double x, double y) =>
            0.05 * Math.Exp(-(((x - 6.0) * (x - 6.0)) + (y * y)) / (2.0 * 0.075 * 0.075));

        // Coarse survey over the mound, dense survey over the hump: a hump this small is only there if
        // it was surveyed, and the uneven density is part of what is being tested.
        var points = JitteredPoints(25.0, 0.5, seed: 17)
            .Where(point => Math.Abs(point.X - 6.0) > 0.5 || Math.Abs(point.Y) > 0.5)
            .ToList();
        for (int j = 0; j <= 32; j++)
        {
            for (int i = 0; i <= 32; i++)
                points.Add((5.6 + (i * 0.025), -0.4 + (j * 0.025)));
        }

        IndexedTriMesh withHump = Triangulate(points, (x, y) => Mound(x, y) + Hump(x, y));
        IndexedTriMesh moundOnly = Triangulate(points, Mound);
        int face = NearestFace(withHump, 6.0, 0.0);
        const double planarRadius = 10.0;

        CurvatureField smallWith = CurvatureAnalyzer.Measure(withHump, 0.5);
        CurvatureField smallWithout = CurvatureAnalyzer.Measure(moundOnly, 0.5);
        Assert.True(smallWith.Value(face, CurvatureMeasure.Mean) > 5.0 * smallWithout.Value(face, CurvatureMeasure.Mean),
            $"At 0.5 m the hump must dominate: {smallWith.Value(face, CurvatureMeasure.Mean)} vs mound {smallWithout.Value(face, CurvatureMeasure.Mean)}.");
        Assert.Equal((sbyte)LandformClass.Peak, LandformClassifier.Classify(smallWith, planarRadius)[face]);
        Assert.Equal((sbyte)LandformClass.Planar, LandformClassifier.Classify(smallWithout, planarRadius)[face]);

        CurvatureField largeWith = CurvatureAnalyzer.Measure(withHump, 10.0);
        CurvatureField largeWithout = CurvatureAnalyzer.Measure(moundOnly, 10.0);
        Assert.Equal(largeWithout.Value(face, CurvatureMeasure.Mean), largeWith.Value(face, CurvatureMeasure.Mean), 3);
        Assert.Equal(
            LandformClassifier.Classify(largeWithout, planarRadius)[face],
            LandformClassifier.Classify(largeWith, planarRadius)[face]);
    }

    /// <summary>
    /// Area weighting makes the result a property of the ground, not of the triangulation: an irregular
    /// triangulation of the same dome measures what the regular grid measures.
    /// </summary>
    [Fact]
    public void Measure_IrregularTriangulation_MatchesTheAnalyticCurvature()
    {
        const double rho = 40.0;
        static double Dome(double x, double y) => -((x * x) + (y * y)) / (2.0 * rho);
        IndexedTriMesh mesh = Triangulate(JitteredPoints(15.0, 0.6, seed: 3), Dome);

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 3.0);

        foreach (int f in Faces(mesh, (x, y) => Math.Abs(x) < 10 && Math.Abs(y) < 10))
        {
            (double x, double y) = Centroid(mesh, f);
            double p = -x / rho, q = -y / rho;
            double expected = CurvatureField.MeanCurvature(p, q, -1.0 / rho, 0.0, -1.0 / rho);
            Assert.InRange(field.Value(f, CurvatureMeasure.Mean), expected * 0.97, expected * 1.03);
        }
    }

    /// <summary>At the border the fit sees half a disc, and still measures the surface, not the edge.</summary>
    [Fact]
    public void Measure_AtTheBorder_StillMeasuresTheSurface()
    {
        const double rho = 40.0;
        IndexedTriMesh mesh = Grid(10.0, 0.5, (x, y) => -((x * x) + (y * y)) / (2.0 * rho));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 3.0);
        sbyte[] classes = LandformClassifier.Classify(field, 200.0);

        foreach (int f in Faces(mesh, (x, y) => Math.Abs(x) > 9.5 || Math.Abs(y) > 9.5))
        {
            Assert.True(field.IsMeasured(f));
            (double x, double y) = Centroid(mesh, f);
            double expected = CurvatureField.MeanCurvature(-x / rho, -y / rho, -1.0 / rho, 0.0, -1.0 / rho);
            Assert.InRange(field.Value(f, CurvatureMeasure.Mean), expected * 0.9, expected * 1.1);
            Assert.Equal((sbyte)LandformClass.Peak, classes[f]);
        }
    }

    /// <summary>
    /// A landform size far below the mesh spacing widens each fit to the nearest samples rather than
    /// failing, so the map measures at the mesh's own resolution.
    /// </summary>
    [Fact]
    public void Measure_SizeBelowTheSpacing_WidensInsteadOfFailing()
    {
        const double rho = 40.0;
        IndexedTriMesh mesh = Grid(10.0, 1.0, (x, y) => -((x * x) + (y * y)) / (2.0 * rho));

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 0.01);

        int summit = NearestFace(mesh, 0.0, 0.0);
        Assert.Equal(1.0 / rho, field.Value(summit, CurvatureMeasure.Mean), 4);
    }

    [Fact]
    public void Measure_EmptyMesh_ReturnsAnEmptyField()
    {
        CurvatureField field = CurvatureAnalyzer.Measure(IndexedTriMesh.Empty, 5.0);

        Assert.Equal(0, field.FaceCount);
        Assert.Empty(LandformClassifier.Classify(field, 100.0));
    }

    /// <summary>One triangle is three samples: no quadric is determined, and it is left unmeasured rather
    /// than called planar.</summary>
    [Fact]
    public void Measure_SingleTriangle_IsUnmeasured()
    {
        var mesh = IndexedTriMesh.FromArrays(new[] { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.5 }, new[] { 0, 1, 2 });

        CurvatureField field = CurvatureAnalyzer.Measure(mesh, 5.0);

        Assert.False(field.IsMeasured(0));
        Assert.True(double.IsNaN(field.Value(0, CurvatureMeasure.Mean)));
        Assert.Equal(LandformClassifier.Unmeasured, LandformClassifier.Classify(field, 100.0)[0]);
    }

    /// <summary>Points on one line (zero-area faces) cannot determine a surface either way.</summary>
    [Fact]
    public void Measure_CollinearStrip_IsUnmeasured()
    {
        var vertices = new List<double>();
        var faces = new List<int>();
        for (int i = 0; i < 30; i++)
            vertices.AddRange(new[] { (double)i, 0.0, i * 0.1 });
        for (int i = 0; i < 28; i++)
            faces.AddRange(new[] { i, i + 1, i + 2 });

        CurvatureField field = CurvatureAnalyzer.Measure(IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray()), 5.0);

        for (int f = 0; f < field.FaceCount; f++)
            Assert.False(field.IsMeasured(f));
    }

    [Fact]
    public void Measure_FaceWithAnOutOfRangeIndex_IsSkipped()
    {
        IndexedTriMesh grid = Grid(5.0, 0.5, (x, y) => 0.0);
        int[] faces = grid.Faces.ToArray();
        faces[0] = grid.VertexCount + 5;

        CurvatureField field = CurvatureAnalyzer.Measure(new IndexedTriMesh(grid.Vertices, grid.VertexCount, faces, grid.FaceCount), 2.0);

        Assert.False(field.IsMeasured(0));
        Assert.True(field.IsMeasured(1));
    }

    /// <summary>Vertices are fitted in parallel; the field must not depend on the scheduling.</summary>
    [Fact]
    public void Measure_IsDeterministic()
    {
        IndexedTriMesh mesh = Triangulate(JitteredPoints(20.0, 0.4, seed: 11), (x, y) => Math.Sin(x * 0.3) * Math.Cos(y * 0.2) * 2.0);

        double[] first = CurvatureAnalyzer.Measure(mesh, 2.5).Values(CurvatureMeasure.Profile);
        double[] second = CurvatureAnalyzer.Measure(mesh, 2.5).Values(CurvatureMeasure.Profile);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Measure_Cancelled_Throws()
    {
        IndexedTriMesh mesh = Grid(20.0, 0.25, (x, y) => 0.0);

        Assert.Throws<OperationCanceledException>(() => CurvatureAnalyzer.Measure(mesh, 2.0, () => true));
    }

    [Fact]
    public void SumPlanAreas_TotalsEachClassAndSkipsUnmeasured()
    {
        sbyte[] classes = { (sbyte)LandformClass.Ridge, (sbyte)LandformClass.Ridge, LandformClassifier.Unmeasured, (sbyte)LandformClass.Hollow };
        double[] areas = { 1.0, 2.0, 5.0, 4.0 };

        double[] sums = LandformClassifier.SumPlanAreas(classes, areas, out double measured);

        Assert.Equal(3.0, sums[(int)LandformClass.Ridge]);
        Assert.Equal(4.0, sums[(int)LandformClass.Hollow]);
        Assert.Equal(7.0, measured);
    }

    [Fact]
    public void Colors_ConvexAndConcaveAreDistinct_AndUnmeasuredIsGrey()
    {
        var colors = LandformClassifier.All.Select(LandformClassifier.ColorOf).ToList();

        Assert.Equal(LandformClassifier.ClassCount, colors.Distinct().Count());
        Assert.DoesNotContain(LandformClassifier.UnmeasuredColor, colors);
        byte[] faceColors = LandformClassifier.BuildFaceColors(new[] { LandformClassifier.Unmeasured });
        Assert.Equal(new byte[] { 130, 130, 130 }, faceColors);
    }
}
