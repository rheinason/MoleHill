using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A remesh coarser than a curved breakline's stations collapses along it, leaving edges that cut across the
/// bends. A later remesh then refused those edges as the breakline and flipped straight across the curve: the
/// tiled remesher's next pass (a curvy Grade Path's rails sagged 0.42 m at a 3 m target, a curved batter toe
/// 0.47 m) and any later remesh of the output (a second Remesh card, Retopo). Two fixes: each tiled pass hands
/// the edges it pinned to the next, and an edge counts as running along a breakline when it is a chord of a
/// gentle bend.
/// </summary>
public class RemeshCurvedBreaklineTests
{
    // A 1:2 batter rising outward from a 15 m circle, embedded at 2 m stations and pinned as a breakline. Its
    // fold (26.6 deg) is under the 30 deg crease angle the options use, so only the breakline keeps it.
    private static double ToeDistance(double x, double y) => Math.Sqrt(((x - 30) * (x - 30)) + ((y - 30) * (y - 30))) - 15;

    [Fact]
    public void TiledRemesh_CurvedBreaklineCoarserThanItsStations_KeepsTheToe()
    {
        (double[] vertices, int[] faces, List<ConstraintPolyline> constraints) = CurvedToe();

        IsotropicRemesher.Result result = TiledIsotropicRemesher.Remesh(vertices, faces, constraints, Options(3.0));

        double worst = WorstToeError(vertices, faces, result.Vertices, result.Faces);
        Assert.True(worst < 0.12, $"Curved toe moved {worst:0.000} m (the whole-mesh remesh: 0.075 m; before the fix: 0.47 m).");
    }

    [Fact]
    public void RemeshTwice_CurvedBreaklineCoarserThanItsStations_KeepsTheToe()
    {
        (double[] vertices, int[] faces, List<ConstraintPolyline> constraints) = CurvedToe();

        IsotropicRemesher.Result once = IsotropicRemesher.Remesh(vertices, faces, constraints, Options(3.0));
        IsotropicRemesher.Result twice = IsotropicRemesher.Remesh(once.Vertices, once.Faces, constraints, Options(3.0));

        double worst = WorstToeError(vertices, faces, twice.Vertices, twice.Faces);
        Assert.True(worst < 0.12, $"Curved toe moved {worst:0.000} m remeshed twice (once: 0.075 m; before the fix: 0.45 m).");
    }

    private static (double[] Vertices, int[] Faces, List<ConstraintPolyline> Constraints) CurvedToe()
    {
        const double k = 0.5;
        static double Z(double x, double y) => k * Math.Max(0, ToeDistance(x, y));

        var rng = new Random(7);
        var xy = new List<double>();
        for (double x = 0; x <= 60.001; x += 2)
        {
            for (double y = 0; y <= 60.001; y += 2)
            {
                bool edge = x < 0.01 || y < 0.01 || x > 59.99 || y > 59.99;
                double px = edge ? x : x + ((rng.NextDouble() - 0.5) * 1.2);
                double py = edge ? y : y + ((rng.NextDouble() - 0.5) * 1.2);
                if (Math.Abs(ToeDistance(px, py)) > 0.5)
                {
                    xy.Add(px);
                    xy.Add(py);
                }
            }
        }

        int n = (int)Math.Round(2 * Math.PI * 15 / 2.0);
        int start = xy.Count / 2;
        var toe = new double[n * 3];
        var segments = new List<(int, int)>();
        for (int i = 0; i < n; i++)
        {
            double x = 30 + (15 * Math.Cos(2 * Math.PI * i / n)), y = 30 + (15 * Math.Sin(2 * Math.PI * i / n));
            xy.Add(x);
            xy.Add(y);
            toe[i * 3] = x;
            toe[i * 3 + 1] = y;
            segments.Add((start + i, start + ((i + 1) % n)));
        }

        (double[] vertices, int[] faces) = Triangulate(xy, segments, Z);
        return (vertices, faces, new List<ConstraintPolyline> { new(toe, n, IsClosed: true) });
    }

    /// <summary>Largest height difference between the remeshed and the input terrain within 1.5 m of the toe.</summary>
    private static double WorstToeError(double[] inputVertices, int[] inputFaces, double[] outputVertices, int[] outputFaces)
    {
        var input = new Locator(inputVertices, inputFaces);
        var output = new Locator(outputVertices, outputFaces);
        double worst = 0;
        for (double x = 3; x <= 57; x += 0.2)
        {
            for (double y = 3; y <= 57; y += 0.2)
            {
                if (Math.Abs(ToeDistance(x, y)) <= 1.5 && input.Z(x, y) is double zi && output.Z(x, y) is double zo)
                    worst = Math.Max(worst, Math.Abs(zo - zi));
            }
        }

        return worst;
    }

    [Fact]
    public void TiledRemesh_CurvyGradePathCoarserThanItsStations_KeepsTheRails()
    {
        (double[] vertices, int[] faces, List<ConstraintPolyline> rails) = CurvyGradePath();

        IsotropicRemesher.Result result = TiledIsotropicRemesher.Remesh(vertices, faces, rails, Options(3.0));

        var input = new Locator(vertices, faces);
        var output = new Locator(result.Vertices, result.Faces);
        double worst = 0;
        foreach (ConstraintPolyline rail in rails)
        {
            for (int s = 0; s + 1 < rail.PointCount; s++)
            {
                double ax = rail.Points[s * 3], ay = rail.Points[s * 3 + 1];
                double bx = rail.Points[s * 3 + 3], by = rail.Points[s * 3 + 4];
                for (double u = 0; u < 1; u += 0.05)
                {
                    double x = ax + ((bx - ax) * u), y = ay + ((by - ay) * u);
                    if (input.Z(x, y) is double zi && output.Z(x, y) is double zo)
                        worst = Math.Max(worst, Math.Abs(zo - zi));
                }
            }
        }

        Assert.True(worst < 0.2, $"Road rail moved {worst:0.000} m (the whole-mesh remesh: 0.09 m; before the fix: 0.42 m).");
    }

    [Fact]
    public void Remesh_GradePathEnds_KeepTheRoadEdgeEndPoints()
    {
        // Where the road ends, a crease of the end cap leaves each road edge's end at 39 degrees, under the
        // 40 degree turn that makes a chain corner, so the two chained and the remesh slid the end point along
        // the crease: the road's end corners rounded off by up to 10 cm. A breakline's end is now a corner.
        (double[] vertices, int[] faces, List<ConstraintPolyline> rails) = CurvyGradePath();

        IsotropicRemesher.Result result = IsotropicRemesher.Remesh(vertices, faces, rails, Options(1.0));

        foreach (ConstraintPolyline rail in rails)
        {
            foreach (int end in new[] { 0, rail.PointCount - 1 })
            {
                double x = rail.Points[end * 3], y = rail.Points[end * 3 + 1], z = rail.Points[end * 3 + 2];
                double nearest = double.MaxValue;
                for (int v = 0; v < result.Vertices.Length / 3; v++)
                {
                    double dx = result.Vertices[v * 3] - x, dy = result.Vertices[v * 3 + 1] - y, dz = result.Vertices[v * 3 + 2] - z;
                    nearest = Math.Min(nearest, Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
                }

                Assert.True(nearest < 1e-3, $"Road edge end ({x:0.###}, {y:0.###}) has no vertex: nearest {nearest:0.000} m.");
            }
        }
    }

    /// <summary>
    /// An S-curve road 4 m wide (Grade Path stations its centerline at half the width) on a survey terrain, its
    /// rails published as the stage publishes them: hard constraints at their own elevation.
    /// </summary>
    private static (double[] Vertices, int[] Faces, List<ConstraintPolyline> Rails) CurvyGradePath()
    {
        static double TerrainZ(double x, double y) => (0.08 * y) + (1.5 * Math.Sin(x / 15) * Math.Cos(y / 20));
        var rng = new Random(11);
        var xy = new List<double>();
        for (double x = 0; x <= 100.001; x += 2)
        {
            for (double y = 0; y <= 100.001; y += 2)
            {
                bool edge = x < 0.01 || y < 0.01 || x > 99.99 || y > 99.99;
                xy.Add(edge ? x : x + ((rng.NextDouble() - 0.5) * 1.2));
                xy.Add(edge ? y : y + ((rng.NextDouble() - 0.5) * 1.2));
            }
        }

        (double[] terrain, int[] terrainFaces) = Triangulate(xy, new List<(int, int)>(), TerrainZ);

        const double width = 4.0;
        var centerline = new List<(double X, double Y)> { (15, 50) };
        double run = 0, px = 15, py = 50;
        for (double x = 15.05; x <= 85.0001; x += 0.05)
        {
            double y = 50 + (20 * Math.Sin((x - 15) / 70 * 2 * Math.PI));
            run += Math.Sqrt(((x - px) * (x - px)) + ((y - py) * (y - py)));
            (px, py) = (x, y);
            if (run >= width * 0.5 || x + 0.05 > 85.0001)
            {
                centerline.Add((x, y));
                run = 0;
            }
        }

        double z0 = TerrainZ(centerline[0].X, centerline[0].Y), z1 = TerrainZ(centerline[^1].X, centerline[^1].Y);
        var pathXy = new double[centerline.Count * 2];
        var pathZ = new double[centerline.Count];
        for (int i = 0; i < centerline.Count; i++)
        {
            pathXy[i * 2] = centerline[i].X;
            pathXy[i * 2 + 1] = centerline[i].Y;
            pathZ[i] = z0 + ((z1 - z0) * i / (centerline.Count - 1));
        }

        GradeOutcome graded = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain, terrain.Length / 3, terrainFaces, terrainFaces.Length / 3),
            Paths = new[] { new PathGrader.PathDefinition(pathXy, pathZ, centerline.Count, width, 33.7, 0.0, 33.7) },
        });
        GradingResult grade = graded.Result ?? throw new InvalidOperationException(graded.ErrorMessage);
        double[] vertices = grade.Vertices[..(grade.VertexCount * 3)];
        int[] faces = grade.Faces[..(grade.FaceCount * 3)];
        List<ConstraintPolyline> rails = grade.OutputPolylines
            .Where(static p => p.VertexCount >= 2)
            .Select(static p => new ConstraintPolyline(p.Vertices, p.VertexCount, p.IsClosed, PreserveInputElevation: true))
            .ToList();
        Assert.Equal(2, rails.Count);
        return (vertices, faces, rails);
    }

    private static IsotropicRemesher.Options Options(double target) => new()
    {
        TargetEdgeLength = target,
        CreaseAngleDeg = 30,
        Tolerance = 0.02,
        WallFaceMinSlopeDeg = 70,
        Iterations = 5
    };

    private static (double[] Vertices, int[] Faces) Triangulate(List<double> xy, List<(int, int)> segments, Func<double, double, double> z)
    {
        TriangulationOutcome outcome = TriangulationHelper.Triangulate(xy, xy.Count / 2, segments, 0, 0, convex: true, 0);
        var extracted = TriangleNetExtractor.Extract(outcome.Mesh!);
        var vertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            vertices[i * 3] = extracted.Xy[i * 2];
            vertices[i * 3 + 1] = extracted.Xy[i * 2 + 1];
            vertices[i * 3 + 2] = z(extracted.Xy[i * 2], extracted.Xy[i * 2 + 1]);
        }

        return (vertices, extracted.Faces[..(extracted.FaceCount * 3)]);
    }

    /// <summary>Height of a mesh at a plan point, by a coarse 1 m bucket grid over face bounding boxes.</summary>
    private sealed class Locator
    {
        private const int N = 100;
        private readonly double[] _v;
        private readonly int[] _f;
        private readonly List<int>?[,] _cells = new List<int>?[N, N];

        public Locator(double[] v, int[] f)
        {
            _v = v;
            _f = f;
            for (int t = 0; t < f.Length / 3; t++)
            {
                double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    double x = v[f[t * 3 + k] * 3], y = v[f[t * 3 + k] * 3 + 1];
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }

                for (int cx = Cell(x0); cx <= Cell(x1); cx++)
                {
                    for (int cy = Cell(y0); cy <= Cell(y1); cy++)
                        (_cells[cx, cy] ??= new List<int>()).Add(t);
                }
            }
        }

        private static int Cell(double c) => Math.Clamp((int)c, 0, N - 1);

        public double? Z(double x, double y)
        {
            List<int>? faces = _cells[Cell(x), Cell(y)];
            if (faces == null)
                return null;
            foreach (int t in faces)
            {
                int a = _f[t * 3], b = _f[t * 3 + 1], c = _f[t * 3 + 2];
                double ax = _v[a * 3], ay = _v[a * 3 + 1], bx = _v[b * 3], by = _v[b * 3 + 1], cx = _v[c * 3], cy = _v[c * 3 + 1];
                double d = ((by - cy) * (ax - cx)) + ((cx - bx) * (ay - cy));
                if (Math.Abs(d) < 1e-15)
                    continue;
                double l1 = (((by - cy) * (x - cx)) + ((cx - bx) * (y - cy))) / d;
                double l2 = (((cy - ay) * (x - cx)) + ((ax - cx) * (y - cy))) / d;
                double l3 = 1 - l1 - l2;
                if (l1 >= -1e-9 && l2 >= -1e-9 && l3 >= -1e-9)
                    return (l1 * _v[a * 3 + 2]) + (l2 * _v[b * 3 + 2]) + (l3 * _v[c * 3 + 2]);
            }

            return null;
        }
    }
}
