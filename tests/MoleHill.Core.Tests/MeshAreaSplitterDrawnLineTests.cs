using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Zones follow the line as drawn (<see cref="MeshAreaSplitter.BoundarySnap.FollowDrawnLine"/>). Found on a
/// 730k-face terrain remeshed at 0.2 m (case bundle 2026-10-14): the conform snap, 8x the 7.5 mm tolerance, put
/// one zone-seam vertex in five more than 1 cm off its curve and the worst 7.3 cm, so straight zone edges
/// zig-zagged. Snapping within tolerance then exposed faults the wide snap had been hiding: a collinearity test
/// that read steep crossings as overlaps, wall faces cracking under a seam, and vertical faces left whole beside
/// a split edge. Each is tested here.
/// </summary>
public sealed class MeshAreaSplitterDrawnLineTests
{
    private const double Tolerance = 0.0075;

    [Fact]
    public void SplitPreservingTopology_DenseTerrainFollowingDrawnLine_KeepsSeamOnTheLine()
    {
        (double[] vertices, int[] faces) = JitteredGrid(columns: 40, rows: 40, spacing: 0.2, seed: 7);
        double[] polygon = { 1.13, 0.71, 6.42, 1.93, 5.87, 6.38, 0.94, 5.11 };

        MeshAreaSplitter.SplitResult drawn = Split(vertices, faces, new[] { polygon }, MeshAreaSplitter.BoundarySnap.FollowDrawnLine);
        MeshAreaSplitter.SplitResult conform = Split(vertices, faces, new[] { polygon }, MeshAreaSplitter.BoundarySnap.ConformToTerrain);

        Assert.InRange(MaxSeamDistance(drawn, polygon), 0.0, Tolerance * 1.000001);
        // The reason the mode exists: on the same terrain a conform pulls the seam well off the line.
        Assert.True(MaxSeamDistance(conform, polygon) > 3.0 * Tolerance);
        AssertWatertight(vertices, faces, drawn);
    }

    /// <summary>
    /// Two faces from the bundle where a 4.5 cm segment of a curved zone boundary crosses their shared edge at
    /// about 50 degrees, 1 cm from its end. The segment intersection read that as collinear (it compared lengths
    /// squared against a length), so one face's cut ended 8 mm outside it and Triangle.NET split the shared edge
    /// on that side only: a crack.
    /// </summary>
    [Fact]
    public void SplitPreservingTopology_ShortSegmentCrossingEdgeNearItsEnd_StaysWatertight()
    {
        double[] vertices =
        {
            -42.675743, 3.2552135, -1.2410084,
            -42.612095, 3.16238, -1.343831,
            -42.406055, 3.2059202, -1.3382525,
            -42.477043, 3.3065858, -1.2264093,
        };
        int[] faces = { 0, 1, 3, 1, 2, 3 };
        double[] zone =
        {
            -42.371969647, 3.311936186, -42.416130934, 3.305430925, -42.460278094, 3.298830658,
            -42.504410969, 3.292135766, -42.548529476, 3.285346949, -42.592633549, 3.278464977,
            -42.636723123, 3.271490624, -42.680798132, 3.264424662, -42.724858510, 3.257267862,
            -42.72, 2.9, -42.37, 2.9,
        };

        foreach (MeshAreaSplitter.BoundarySnap snap in Enum.GetValues<MeshAreaSplitter.BoundarySnap>())
            AssertWatertight(vertices, faces, Split(vertices, faces, new[] { zone }, snap));
    }

    /// <summary>
    /// A retaining wall from the bundle, 0.6 m across, with an asphalt zone drawn along it. A wall is a band of
    /// faces whose plan is a line, so both of a wall face's long edges lie within reach of the same cut, and
    /// following the drawn line neighbouring wall faces disagreed about where to split them: four cracks here,
    /// and 92 across the bundle's walls, until wall faces stopped taking cuts of their own.
    /// </summary>
    [Fact]
    public void SplitPreservingTopology_DrawnLineAlongRetainingWall_StaysWatertight()
    {
        MeshAreaSplitter.SplitResult result = Split(WallVertices, WallFaces, new[] { WallZone }, MeshAreaSplitter.BoundarySnap.FollowDrawnLine);

        AssertWatertight(WallVertices, WallFaces, result);
        Assert.Contains(result.FaceAreaIndex, area => area == 0);
        Assert.Contains(result.FaceAreaIndex, area => area == -1);
    }

    /// <summary>
    /// A wall from the bundle with the Hella zone running along it. Wall faces stacked on one plan line are left
    /// alone by the cap repair (their long edges carry more than one face across), and with no plan area they
    /// cannot be re-triangulated in plan, so they were emitted whole - beside neighbours that had split their
    /// shared edges, six cracks in either mode. They are fanned over the splits instead; the surface area is
    /// unchanged, so the fans lie in the walls' own planes.
    /// </summary>
    [Theory]
    [InlineData(MeshAreaSplitter.BoundarySnap.ConformToTerrain)]
    [InlineData(MeshAreaSplitter.BoundarySnap.FollowDrawnLine)]
    public void SplitPreservingTopology_ZoneAlongStackedWallFaces_FansThemInTheirPlane(MeshAreaSplitter.BoundarySnap snap)
    {
        MeshAreaSplitter.SplitResult result = Split(StackedWallVertices, StackedWallFaces, new[] { HellaZone }, snap);

        AssertWatertight(StackedWallVertices, StackedWallFaces, result);
        Assert.Equal(
            SurfaceArea(StackedWallVertices, StackedWallFaces, StackedWallFaces.Length / 3),
            SurfaceArea(result.Vertices, result.Faces, result.FaceCount),
            1e-5);
    }

    /// <summary>
    /// A wall from the bundle whose faces lean a few micrometres off vertical: too thick to count as caps, so
    /// they still triangulated in plan, but a face 3 micrometres wide has its opposite edges inside the merge
    /// tolerance, and the piece of it beside a neighbour's split failed the plan-area checks and was dropped -
    /// three cracks where the asphalt zone runs along the wall. A face narrower than the tolerance is now fanned
    /// in 3D like a vertical one.
    /// </summary>
    [Theory]
    [InlineData(MeshAreaSplitter.BoundarySnap.ConformToTerrain)]
    [InlineData(MeshAreaSplitter.BoundarySnap.FollowDrawnLine)]
    public void SplitPreservingTopology_ZoneAlongLeaningWall_KeepsEveryPieceOfTheWall(MeshAreaSplitter.BoundarySnap snap)
    {
        MeshAreaSplitter.SplitResult result = Split(LeaningWallVertices, LeaningWallFaces, new[] { LeaningWallZone }, snap);

        AssertWatertight(LeaningWallVertices, LeaningWallFaces, result);
        Assert.Equal(
            SurfaceArea(LeaningWallVertices, LeaningWallFaces, LeaningWallFaces.Length / 3),
            SurfaceArea(result.Vertices, result.Faces, result.FaceCount),
            1e-5);
    }

    [Fact]
    public void IntersectSegments_ShortSegmentCrossingLongEdgeSteeply_IsAPointNotAnOverlap()
    {
        var edgeStart = new Point2D(0.0, 0.0);
        var edgeEnd = new Point2D(0.2, 0.0);
        // 4.5 cm long, crossing at 50 degrees: |r| |s| sin(50) is under the tolerance, the distance is not.
        var segmentStart = new Point2D(0.1 - (0.0225 * Math.Cos(0.8727)), -0.0225 * Math.Sin(0.8727));
        var segmentEnd = new Point2D(0.1 + (0.0225 * Math.Cos(0.8727)), 0.0225 * Math.Sin(0.8727));

        SegmentIntersection intersection = FaceCutGeometry.IntersectSegments(segmentStart, segmentEnd, edgeStart, edgeEnd, Tolerance);

        Assert.Equal(SegmentIntersectionKind.Point, intersection.Kind);
        Assert.Equal(0.1, intersection.P0.X, 9);
        Assert.Equal(0.0, intersection.P0.Y, 9);
    }

    [Fact]
    public void IntersectSegments_SegmentWithinToleranceAlongEdge_IsAnOverlap()
    {
        SegmentIntersection intersection = FaceCutGeometry.IntersectSegments(
            new Point2D(0.05, 0.004), new Point2D(0.15, -0.004), new Point2D(0.0, 0.0), new Point2D(0.2, 0.0), Tolerance);

        Assert.Equal(SegmentIntersectionKind.Overlap, intersection.Kind);
    }

    [Fact]
    public void IntersectSegments_TwoSegmentsShorterThanToleranceApart_DoNotIntersect()
    {
        SegmentIntersection intersection = FaceCutGeometry.IntersectSegments(
            new Point2D(0.0, 0.0), new Point2D(0.005, 0.0), new Point2D(0.002, 1.0), new Point2D(0.003, 1.0), Tolerance);

        Assert.Equal(SegmentIntersectionKind.None, intersection.Kind);
    }

    private static MeshAreaSplitter.SplitResult Split(
        double[] vertices, int[] faces, double[][] polygons, MeshAreaSplitter.BoundarySnap snap)
    {
        var areas = polygons.Select(xy => new MeshAreaSplitter.AreaBoundary(xy, xy.Length / 2)).ToArray();
        MeshAreaSplitter.SplitResult? result = MeshAreaSplitter.SplitPreservingTopology(
            IndexedTriMesh.FromArrays(vertices, faces), areas, Tolerance, out string? error, snap: snap);
        Assert.True(result != null, error);
        return result!;
    }

    /// <summary>Every edge is shared by exactly two faces except the input's own border, and the plan area is
    /// unchanged: no crack, no fold, no overlap. A border vertex may move by the cap repair's
    /// <see cref="MeshAreaTopologySplitter.ThinCapApexDistance"/>, so the border is matched within that.</summary>
    private static void AssertWatertight(double[] inputVertices, int[] inputFaces, MeshAreaSplitter.SplitResult result)
    {
        Dictionary<long, int> inputUses = EdgeUses(inputFaces, inputFaces.Length / 3);
        var border = inputUses.Where(entry => entry.Value == 1).Select(entry => entry.Key).ToList();
        foreach (KeyValuePair<long, int> entry in EdgeUses(result.Faces, result.FaceCount))
        {
            Assert.True(entry.Value <= 2, "non-manifold edge");
            if (entry.Value == 2)
                continue;

            int a = (int)(entry.Key >> 32), b = (int)(entry.Key & 0xffffffff);
            double mx = (result.Vertices[a * 3] + result.Vertices[b * 3]) * 0.5;
            double my = (result.Vertices[(a * 3) + 1] + result.Vertices[(b * 3) + 1]) * 0.5;
            Assert.True(
                border.Any(key => DistanceToSegment(mx, my, inputVertices, (int)(key >> 32), (int)(key & 0xffffffff)) <= MeshAreaTopologySplitter.ThinCapApexDistance),
                $"open edge inside the terrain at ({mx}, {my})");
        }

        // Cap repair moves the surface by up to ThinCapApexDistance: a few square millimetres on the bundle's walls.
        Assert.Equal(PlanArea(inputVertices, inputFaces, inputFaces.Length / 3), PlanArea(result.Vertices, result.Faces, result.FaceCount), 1e-5);
    }

    private static Dictionary<long, int> EdgeUses(int[] faces, int faceCount)
    {
        Dictionary<long, int> uses = IndexedMeshTools.CreateEdgeKeyMap<int>(faceCount * 3);
        for (int t = 0; t < faceCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                long key = IndexedMeshTools.GetEdgeKey(faces[(t * 3) + k], faces[(t * 3) + ((k + 1) % 3)]);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        return uses;
    }

    /// <summary>The largest plan distance from a vertex on a seam between two areas to the polygon.</summary>
    private static double MaxSeamDistance(MeshAreaSplitter.SplitResult result, double[] polygon)
    {
        var owner = new Dictionary<long, int>();
        double max = 0.0;
        for (int t = 0; t < result.FaceCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = result.Faces[(t * 3) + k], b = result.Faces[(t * 3) + ((k + 1) % 3)];
                long key = IndexedMeshTools.GetEdgeKey(a, b);
                if (!owner.TryAdd(key, t) && result.FaceAreaIndex[owner[key]] != result.FaceAreaIndex[t])
                    max = Math.Max(max, Math.Max(DistanceToPolygon(result.Vertices, a, polygon), DistanceToPolygon(result.Vertices, b, polygon)));
            }
        }

        return max;
    }

    private static double DistanceToPolygon(double[] vertices, int vertex, double[] polygon)
    {
        int count = polygon.Length / 2;
        double best = double.MaxValue;
        var line = new double[6];
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            line[0] = polygon[i * 2];
            line[1] = polygon[(i * 2) + 1];
            line[3] = polygon[j * 2];
            line[4] = polygon[(j * 2) + 1];
            best = Math.Min(best, DistanceToSegment(vertices[vertex * 3], vertices[(vertex * 3) + 1], line, 0, 1));
        }

        return best;
    }

    private static double DistanceToSegment(double x, double y, double[] vertices, int a, int b)
    {
        double ax = vertices[a * 3], ay = vertices[(a * 3) + 1];
        double dx = vertices[b * 3] - ax, dy = vertices[(b * 3) + 1] - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared > 0.0 ? Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared, 0.0, 1.0) : 0.0;
        return Math.Sqrt(Math.Pow(ax + (t * dx) - x, 2) + Math.Pow(ay + (t * dy) - y, 2));
    }

    private static double PlanArea(double[] v, int[] f, int faceCount)
    {
        double area = 0.0;
        for (int t = 0; t < faceCount; t++)
        {
            int a = f[t * 3], b = f[(t * 3) + 1], c = f[(t * 3) + 2];
            area += Math.Abs(((v[b * 3] - v[a * 3]) * (v[(c * 3) + 1] - v[(a * 3) + 1])) - ((v[(b * 3) + 1] - v[(a * 3) + 1]) * (v[c * 3] - v[a * 3]))) * 0.5;
        }

        return area;
    }

    /// <summary>A regular grid whose interior vertices are moved up to a quarter spacing, so no cut lines up
    /// with the terrain's edges by accident.</summary>
    private static (double[] Vertices, int[] Faces) JitteredGrid(int columns, int rows, double spacing, int seed)
    {
        var random = new Random(seed);
        var vertices = new double[(columns + 1) * (rows + 1) * 3];
        for (int j = 0; j <= rows; j++)
        {
            for (int i = 0; i <= columns; i++)
            {
                bool interior = i > 0 && j > 0 && i < columns && j < rows;
                int v = (j * (columns + 1)) + i;
                vertices[v * 3] = (i * spacing) + (interior ? (random.NextDouble() - 0.5) * spacing * 0.5 : 0.0);
                vertices[(v * 3) + 1] = (j * spacing) + (interior ? (random.NextDouble() - 0.5) * spacing * 0.5 : 0.0);
                vertices[(v * 3) + 2] = 0.3 * Math.Sin(i * 0.4) * Math.Cos(j * 0.3);
            }
        }

        var faces = new List<int>(columns * rows * 6);
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                int v00 = (j * (columns + 1)) + i, v10 = v00 + 1, v01 = v00 + columns + 1, v11 = v01 + 1;
                faces.AddRange(new[] { v00, v10, v11, v00, v11, v01 });
            }
        }

        return (vertices, faces.ToArray());
    }

    private static double SurfaceArea(double[] v, int[] f, int faceCount)
    {
        double area = 0.0;
        for (int t = 0; t < faceCount; t++)
        {
            int a = f[t * 3] * 3, b = f[(t * 3) + 1] * 3, c = f[(t * 3) + 2] * 3;
            double ux = v[b] - v[a], uy = v[b + 1] - v[a + 1], uz = v[b + 2] - v[a + 2];
            double wx = v[c] - v[a], wy = v[c + 1] - v[a + 1], wz = v[c + 2] - v[a + 2];
            double nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
            area += Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz)) * 0.5;
        }

        return area;
    }

    private static readonly double[] WallVertices =
    [
        113.22162, 25.408146, 2.8074718,
        113.249344, 25.423506, 2.8082502,
        113.20822, 25.37839, 2.5576625,
        113.21297, 25.386604, 2.6201541,
        113.22247, 25.403036, 2.7451377,
        113.29358, 25.448013, 2.8094919,
        113.22723, 25.411253, 2.8076293,
        113.27046, 25.435207, 2.8088431,
        113.279205, 25.306063, 1.31249,
        113.308105, 25.633863, 2.8230417,
        113.33781, 25.47252, 2.8107336,
        113.29822, 25.338926, 1.5624567,
        113.31722, 25.37179, 1.8124235,
        113.336235, 25.404654, 2.06239,
        113.35524, 25.437517, 2.3123567,
        113.37425, 25.47038, 2.5623236,
        113.3692, 25.2666, 0.31728414,
        113.37871, 25.283031, 0.4422675,
        113.48172, 25.552258, 2.8147736,
        113.388214, 25.299463, 0.56725085,
        113.39772, 25.315895, 0.6922342,
        113.39326, 25.503244, 2.8122902,
        113.40722, 25.332327, 0.8172175,
        113.416725, 25.348759, 0.9422009,
        113.50945, 25.56762, 2.815552,
        113.43749, 25.527752, 2.8135319,
        113.43574, 25.381622, 1.1921676,
        113.45267, 25.536158, 2.813958,
        113.44524, 25.398054, 1.317151,
        113.45474, 25.414486, 1.4421343,
        113.473755, 25.44735, 1.692101,
        113.482895, 25.694033, 2.825238,
        113.47821, 25.26, -0.427955,
        113.4145, 25.442432, 2.002229,
        113.48325, 25.463781, 1.8170843,
        113.49276, 25.480213, 1.9420677,
        113.49722, 25.292864, -0.1779883,
        113.51177, 25.513077, 2.1920345,
        113.51623, 25.325727, 0.0719784,
        113.52127, 25.529509, 2.3170178,
        113.53078, 25.54594, 2.442001,
        113.53524, 25.358591, 0.3219451,
        113.54028, 25.562372, 2.5669844,
        113.54979, 25.578804, 2.6919677,
        113.554245, 25.391455, 0.5719118,
        113.55929, 25.595234, 2.8169513,
        113.581406, 25.607489, 2.817572,
        113.57326, 25.424318, 0.8218785,
        113.59226, 25.457182, 1.0718452,
        113.611275, 25.490046, 1.3218119,
        113.62564, 25.631996, 2.8188138,
        113.634865, 25.637108, 2.8190727,
        113.63028, 25.52291, 1.5717787,
        113.63474, 25.33556, -0.5482774,
        113.64929, 25.555773, 1.8217453,
        113.64424, 25.351992, -0.42329407,
        113.65375, 25.368423, -0.2983107,
        113.6683, 25.588634, 2.071712,
        113.66325, 25.384855, -0.17332736,
        113.67276, 25.401287, -0.048343997,
        113.68731, 25.621498, 2.3216786,
        113.68226, 25.417719, 0.076639354,
        113.691765, 25.43415, 0.20162271,
        113.706314, 25.654362, 2.5716455,
        113.70127, 25.450583, 0.32660607,
        113.71078, 25.467014, 0.4515894,
        113.720276, 25.483446, 0.5765728,
        113.72978, 25.499878, 0.7015561,
        113.73929, 25.51631, 0.82653946,
        113.748795, 25.532742, 0.9515228,
        113.75829, 25.549173, 1.0765061,
    ];

    private static readonly int[] WallFaces =
    [
        2, 15, 3, 3, 15, 4, 0, 5, 18, 0, 18, 10, 4, 15, 21, 4, 21, 6,
        6, 21, 1, 1, 21, 24, 1, 25, 18, 1, 18, 5, 1, 24, 25, 7, 10, 18,
        7, 27, 9, 7, 18, 27, 8, 26, 28, 8, 28, 29, 8, 29, 11, 11, 29, 30,
        11, 30, 12, 9, 27, 31, 12, 33, 13, 12, 30, 34, 12, 34, 35, 12, 35, 33,
        13, 33, 37, 13, 37, 14, 14, 37, 39, 14, 39, 40, 14, 40, 15, 16, 41, 17,
        15, 40, 42, 15, 42, 43, 15, 43, 21, 17, 41, 44, 17, 44, 19, 19, 44, 20,
        21, 43, 45, 21, 45, 46, 21, 46, 24, 20, 44, 47, 20, 47, 22, 22, 47, 23,
        33, 35, 37, 23, 47, 48, 23, 48, 26, 26, 48, 49, 26, 49, 28, 25, 24, 50,
        25, 50, 18, 28, 49, 29, 27, 18, 51, 27, 51, 31, 29, 49, 52, 29, 52, 30,
        30, 52, 54, 30, 54, 34, 32, 53, 55, 32, 55, 56, 32, 56, 36, 18, 50, 51,
        34, 54, 35, 35, 54, 57, 35, 57, 37, 36, 56, 58, 36, 58, 59, 36, 59, 38,
        37, 57, 60, 37, 60, 39, 38, 59, 61, 38, 61, 62, 38, 62, 41, 39, 60, 40,
        40, 60, 63, 40, 63, 42, 41, 62, 64, 41, 64, 65, 41, 65, 44, 42, 63, 43,
        44, 65, 66, 44, 66, 67, 44, 67, 47, 47, 67, 68, 47, 68, 69, 47, 69, 48,
        48, 69, 70,
    ];

    private static readonly double[] WallZone =
    [
        113.893167446, 25.780220704, 113.751012653, 25.701459055, 113.60885786, 25.622697406,
        113.466703066, 25.543935757, 113.324548273, 25.465174108, 113.18239348, 25.38641246,
        113.040238687, 25.307650811, 113.476414662, 24.52040846, 114.329343421, 24.992978353,
    ];

    private static readonly double[] StackedWallVertices =
    [
        -1.5403985, -18.537975, -3.04955,
        -1.5457349, -18.681149, -3.6871972,
        -1.5908378, -18.617025, -3.5277855,
        -1.5235605, -18.584856, -3.208962,
        -1.5686635, -18.520733, -3.04955,
        -1.4645591, -18.84049, -4.2099495,
        -1.4555289, -18.809393, -4.006021,
        -1.5006318, -18.74527, -3.846609,
        -1.4111801, -18.61681, -3.04955,
        -1.4407902, -18.598745, -3.04955,
        -1.4373411, -18.600851, -3.0943062,
        -1.4333545, -18.713102, -3.5277855,
        -1.4784575, -18.64898, -3.3683736,
        -1.3263147, -18.425545, -3.049616,
        -1.3431485, -18.841349, -3.846609,
        -1.3882515, -18.777225, -3.6871972,
        -1.287348, -18.692358, -3.04955,
        -1.2873479, -18.692358, -3.04955,
        -1.3209741, -18.745056, -3.3683736,
        -1.3660772, -18.680933, -3.208962,
        -1.3521787, -18.872446, -4.0505376,
        -1.218643, -18.734274, -3.0793874,
        -1.2759044, -18.69934, -3.04955,
        -1.2307682, -18.873302, -3.6871972,
        -1.2758712, -18.80918, -3.5277855,
        -1.2085938, -18.777012, -3.208962,
        -1.2536968, -18.712889, -3.04955,
        -1.0901283, -18.56895, -3.0496163,
        -1.1634909, -18.841135, -3.3683736,
        -1.1110185, -18.799934, -3.04955,
        -1.0962136, -18.808966, -3.04955,
    ];

    private static readonly int[] StackedWallFaces =
    [
        2, 1, 11, 4, 3, 8, 1, 7, 14, 1, 15, 11, 1, 14, 15, 0, 9, 16,
        0, 16, 17, 0, 17, 13, 3, 12, 18, 3, 19, 8, 3, 18, 19, 7, 6, 14,
        12, 11, 18, 5, 20, 6, 9, 10, 21, 9, 22, 16, 9, 21, 22, 10, 8, 21,
        11, 15, 23, 11, 24, 18, 11, 23, 24, 8, 19, 25, 8, 26, 21, 8, 25, 26,
        15, 14, 23, 19, 18, 25, 13, 17, 27, 18, 28, 25, 22, 21, 29, 26, 30, 21,
    ];

    private static readonly double[] HellaZone =
    [
        -1.170900724, -18.565229983, 8.250326215, -14.008893316, 23.15104573, -13.140446473,
        52.797910687, -15.469260711, 77.753250817, -10.23358809, 93.226882568, -5.371423677,
        106.253585211, -0.48069672, 124.308137997, 5.964093009, 127.908640516, 7.959068876,
        140.225714595, 36.946665632, 156.365328906, 45.972107845, 181.318022084, 10.82597405,
        164.435136061, -20.816164533, 75.488160231, -68.753214625, 27.53626803, -57.96403888,
        -22.014020579, -44.977068075, -50.185757247, -31.79029772, -69.474435538, -23.422663145,
        -76.859009884, -12.27613583, -68.150785419, -5.030893075, -57.004258104, -0.781279536,
        -43.976754305, -1.547603289, -34.711203474, -3.776908752, -25.584984235, -9.768167184,
    ];

    private static readonly double[] LeaningWallVertices =
    [
        123.0898, 30.764004, 1.7518697,
        123.03141, 30.843315, 3.0828586,
        123.02539, 31.052073, 3.0974991,
        123.10372, 30.838713, 2.5518372,
        123.028046, 30.841452, 3.082764,
        123.10427, 30.883682, 3.0849037,
        123.09847, 30.88047, 3.0847409,
        123.162506, 30.714962, 0.6878087,
        123.17642, 30.789669, 1.487776,
        123.096756, 30.80136, 2.1518533,
        123.190346, 30.864378, 2.2877436,
        123.11068, 30.876068, 2.9518209,
        123.20427, 30.939087, 3.0877109,
        123.19552, 30.93424, 3.0874653,
        123.24913, 30.740627, 0.42371505,
        123.169464, 30.752316, 1.0877924,
        123.263054, 30.815334, 1.2236824,
        123.18339, 30.827024, 1.8877598,
        123.27698, 30.890043, 2.02365,
        123.19731, 30.901733, 2.6877272,
        123.2909, 30.964752, 2.8236172,
        123.21996, 30.947783, 3.0881517,
        123.2162, 31.153065, 3.1025255,
        123.28766, 30.985289, 3.090052,
        123.22814, 30.952314, 3.088381,
        123.24217, 30.703272, 0.023731377,
        123.33576, 30.766293, 0.15962145,
        123.256096, 30.77798, 0.82369876,
        123.349686, 30.841, 0.9595888,
        123.27001, 30.85269, 1.6236662,
        123.3636, 30.915709, 1.7595562,
        123.3273, 31.007256, 3.0911648,
        123.283936, 30.927399, 2.4236336,
        123.377525, 30.990417, 2.5595236,
        123.32184, 30.691584, -0.64034593,
        123.3288, 30.728937, -0.24036226,
        123.422386, 30.791958, -0.10447219,
        123.34272, 30.803646, 0.5596051,
        123.35664, 30.878355, 1.3595725,
        123.45023, 30.941374, 1.4954625,
        123.37057, 30.953064, 2.15954,
        123.46416, 31.016083, 2.29543,
        123.38846, 31.04114, 3.0928817,
        123.38449, 31.027773, 2.9595072,
        123.47807, 31.090792, 3.0953975,
        123.3972, 31.045984, 3.0931273,
        123.40014, 31.2531, 3.1075494,
        123.44497, 31.072447, 3.0944679,
        123.40846, 30.717249, -0.90443957,
        123.50902, 30.817623, -0.36856583,
        123.42935, 30.829311, 0.2955115,
        123.52294, 30.892332, 0.43140155,
        123.43631, 30.866667, 0.6954952,
        123.44327, 30.90402, 1.0954789,
        123.53686, 30.96704, 1.2313689,
        123.45719, 30.97873, 1.8954463,
        123.55078, 31.041748, 2.0313363,
        123.47159, 31.087196, 3.095215,
        123.471115, 31.053438, 2.6954136,
        123.50205, 30.780268, -0.7685495,
        123.515976, 30.854977, 0.031417873,
        123.5299, 30.929686, 0.83138525,
        123.54382, 31.004395, 1.6313527,
        123.55775, 31.079103, 2.43132,
    ];

    private static readonly int[] LeaningWallFaces =
    [
        2, 1, 21, 2, 21, 22, 4, 5, 23, 4, 24, 6, 4, 23, 24, 1, 6, 21,
        0, 8, 29, 0, 29, 30, 0, 30, 17, 9, 18, 10, 6, 24, 31, 6, 31, 21,
        3, 10, 32, 3, 32, 33, 3, 33, 19, 5, 13, 23, 11, 20, 12, 7, 14, 37,
        7, 37, 27, 15, 28, 16, 8, 16, 38, 8, 38, 39, 8, 39, 29, 17, 30, 18,
        10, 18, 40, 10, 40, 41, 10, 41, 32, 13, 12, 42, 13, 42, 23, 19, 33, 20,
        12, 20, 43, 12, 43, 44, 12, 44, 42, 22, 21, 45, 22, 45, 46, 21, 31, 45,
        24, 23, 47, 24, 47, 31, 25, 36, 26, 14, 26, 50, 14, 50, 51, 14, 51, 37,
        27, 37, 52, 27, 52, 28, 16, 28, 53, 16, 53, 54, 16, 54, 38, 29, 39, 30,
        18, 30, 55, 18, 55, 56, 18, 56, 40, 32, 41, 33, 23, 42, 57, 23, 57, 47,
        20, 33, 58, 34, 48, 59, 35, 49, 36, 26, 36, 60, 37, 51, 52, 28, 52, 61,
        30, 39, 62, 40, 56, 41, 33, 41, 63,
    ];

    private static readonly double[] LeaningWallZone =
    [
        123.701848169, 31.214774473, 123.559693375, 31.136012824, 123.417538582, 31.057251176,
        123.275383789, 30.978489527, 123.133228996, 30.899727878, 122.991074203, 30.820966229,
        122.84891941, 30.74220458, 123.285095385, 29.954962229, 124.138024144, 30.427532122,
    ];
}
