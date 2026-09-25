using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class DrainageBasinAnalyzerTests
{
    /// <summary>
    /// A regular triangulated grid over [0, nx] x [0, ny] at unit spacing, with elevations from
    /// <paramref name="height"/>. Every scene below is a height function, which keeps the tests about
    /// landform rather than about mesh construction.
    /// </summary>
    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) Grid(
        int nx, int ny, Func<double, double, double> height)
    {
        int columns = nx + 1;
        int rows = ny + 1;
        int vertexCount = columns * rows;
        var vertices = new double[vertexCount * 3];
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                int vertex = (j * columns) + i;
                vertices[vertex * 3] = i;
                vertices[(vertex * 3) + 1] = j;
                vertices[(vertex * 3) + 2] = height(i, j);
            }
        }

        var faces = new int[nx * ny * 6];
        int face = 0;
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int a = (j * columns) + i;
                int b = a + 1;
                int c = a + columns + 1;
                int d = a + columns;
                faces[face++] = a;
                faces[face++] = b;
                faces[face++] = c;
                faces[face++] = a;
                faces[face++] = c;
                faces[face++] = d;
            }
        }

        return (vertices, vertexCount, faces, nx * ny * 2);
    }

    private static BasinGraph Analyze(
        (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) mesh,
        DrainageBasinAnalyzer.Options? options = null)
    {
        return DrainageBasinAnalyzer.Analyze(
            mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount, options);
    }

    private static double SinkArea(BasinGraph graph)
    {
        double area = 0.0;
        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            if (basin.Outlet == BasinGraph.OutletKind.Sink)
                area += basin.PlanArea;
        }

        return area;
    }

    /// <summary>A flat plateau ringed by higher ground, with an optional channel cut through the ring.</summary>
    private static Func<double, double, double> Plateau(bool withNotch)
    {
        return (x, y) =>
        {
            if (x >= 2 && x <= 8 && y >= 2 && y <= 8)
                return 10.0;
            if (withNotch && x >= 4 && x <= 5 && y < 2)
                return 10.0 - ((2.0 - y) * 2.0);
            return 20.0;
        };
    }

    private static bool IsOnPlateau(double[] vertices, int[] faces, int face)
    {
        double x = 0.0;
        double y = 0.0;
        for (int corner = 0; corner < 3; corner++)
        {
            int vertex = faces[(face * 3) + corner];
            x += vertices[vertex * 3] / 3.0;
            y += vertices[(vertex * 3) + 1] / 3.0;
        }

        return x > 2 && x < 8 && y > 2 && y < 8;
    }

    // ---------------------------------------------------------------------------------------------
    // Flat ground. The whole reason this analyzer is not a twenty-line steepest-descent loop.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The confetti test. A perfectly level pad has no gradient to route by, so routed face by face every
    /// face picks a different arbitrary outlet out of rounding noise. Routed as a region it drains through
    /// the one place it can actually spill, and is therefore one catchment.
    /// </summary>
    [Fact]
    public void Analyze_FlatPlateauWithOneOutflow_PutsTheWholePlateauInOneBasin()
    {
        var mesh = Grid(10, 10, Plateau(withNotch: true));

        BasinGraph graph = Analyze(mesh);

        int? plateauBasin = null;
        for (int face = 0; face < graph.FaceCount; face++)
        {
            if (!IsOnPlateau(mesh.Vertices, mesh.Faces, face))
                continue;

            plateauBasin ??= graph.FaceBasin[face];
            Assert.Equal(plateauBasin, graph.FaceBasin[face]);
        }

        Assert.NotNull(plateauBasin);
    }

    /// <summary>A level pad ringed by higher ground with no way out is a depression, not N arbitrary basins.</summary>
    [Fact]
    public void Analyze_FlatPlateauWithNoOutflow_ReportsASingleSink()
    {
        var mesh = Grid(10, 10, Plateau(withNotch: false));

        BasinGraph graph = Analyze(mesh);

        Assert.Equal(1, graph.SinkBasinCount);
        BasinGraph.Basin sink = Assert.Single(
            graph.Basins, basin => basin.Outlet == BasinGraph.OutletKind.Sink);
        Assert.Equal(10.0, sink.LowestZ, 9);
    }

    /// <summary>
    /// The case the whole feature exists for. A graded pad cut into a hillside spills downhill, and a
    /// drainage check that calls it a pond is worse than no check at all — it is the false positive that
    /// makes people switch the analysis off.
    /// </summary>
    [Fact]
    public void Analyze_GradedPadOnAHillside_ReportsNoSink()
    {
        var mesh = Grid(10, 10, (x, y) =>
            x >= 3 && x <= 7 && y >= 3 && y <= 7 ? 100.0 - (0.2 * 5.0) : 100.0 - (0.2 * y));

        BasinGraph graph = Analyze(mesh);

        Assert.Equal(0, graph.SinkBasinCount);
    }

    /// <summary>
    /// A vertical retaining-wall face has no gradient and no plan area. Treated as its own thing it
    /// swallows the water arriving from the terrace above and reads as a depression behind every wall;
    /// routed as flat ground it spills onto the terrace below, which is what water does.
    /// </summary>
    [Fact]
    public void Analyze_TerraceWithAVerticalWall_DrainsOverTheWallRatherThanPondingBehindIt()
    {
        var vertices = new[]
        {
            0.0, 0.0, 10.0,
            10.0, 0.0, 10.0,
            10.0, 5.0, 10.0,
            0.0, 5.0, 10.0,
            0.0, 5.0, 5.0,
            10.0, 5.0, 5.0,
            10.0, 10.0, 5.0,
            0.0, 10.0, 5.0
        };
        var faces = new[]
        {
            0, 1, 2,
            0, 2, 3,
            3, 2, 5,
            3, 5, 4,
            4, 5, 6,
            4, 6, 7
        };

        BasinGraph graph = DrainageBasinAnalyzer.Analyze(vertices, 8, faces, 6);

        Assert.Equal(0, graph.SinkBasinCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Landform.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A bowl has one low point and everything drains to it, so it is one depression.</summary>
    [Fact]
    public void Analyze_Bowl_ReportsOneSinkHoldingAlmostAllOfTheArea()
    {
        var mesh = Grid(10, 10, (x, y) => ((x - 5) * (x - 5)) + ((y - 5) * (y - 5)));

        BasinGraph graph = Analyze(mesh);

        Assert.Equal(1, graph.SinkBasinCount);
        Assert.True(
            SinkArea(graph) > graph.TotalPlanArea * 0.9,
            $"sink held {SinkArea(graph):F1} of {graph.TotalPlanArea:F1}");
    }

    /// <summary>Two bowls behind one ridge are two depressions, and the ridge between them is the divide.</summary>
    [Fact]
    public void Analyze_TwoBowlsBehindARidge_ReportsTwoSubstantialSinks()
    {
        var mesh = Grid(20, 10, (x, y) =>
        {
            double centreX = x < 10 ? 5.0 : 15.0;
            return ((x - centreX) * (x - centreX)) + ((y - 5) * (y - 5));
        });

        BasinGraph graph = Analyze(mesh);

        int substantialSinks = 0;
        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            if (basin.Outlet == BasinGraph.OutletKind.Sink && basin.PlanArea > graph.TotalPlanArea * 0.1)
                substantialSinks++;
        }

        Assert.Equal(2, substantialSinks);
    }

    /// <summary>A uniform slope holds no water anywhere; every basin runs off the terrain edge.</summary>
    [Fact]
    public void Analyze_UniformSlope_ReportsOnlyBoundaryOutlets()
    {
        var mesh = Grid(10, 10, (x, y) => 100.0 - (0.5 * y));

        BasinGraph graph = Analyze(mesh);

        Assert.NotEmpty(graph.Basins);
        Assert.All(graph.Basins, basin => Assert.Equal(BasinGraph.OutletKind.Boundary, basin.Outlet));
    }

    /// <summary>
    /// A shallow dimple is still a depression, and the router says so. Deciding that one 20 mm deep is
    /// not worth drawing is a threshold on the *measurement*, which belongs to the ponding stage — the
    /// router that silently dropped it would leave that stage unable to report what it never saw.
    /// </summary>
    [Fact]
    public void Analyze_ShallowDimpleOnASlope_IsStillReportedAsASink()
    {
        var mesh = Grid(10, 10, (x, y) =>
        {
            // The dimple falls steeper than the ground it sits on, so it genuinely closes — and it is
            // 50 mm deep, which is exactly the "is this really a pond" case.
            double baseZ = 100.0 - (0.01 * y);
            double dx = x - 5.0;
            double dy = y - 5.0;
            double radius = Math.Sqrt((dx * dx) + (dy * dy));
            return radius < 2.0 ? baseZ - (0.025 * (2.0 - radius)) : baseZ;
        });

        BasinGraph graph = Analyze(mesh);

        Assert.True(graph.SinkBasinCount >= 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Invariants that hold for every scene.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every face's pointers must reach an outlet. A cycle left in the graph hangs the caller.</summary>
    [Fact]
    public void Analyze_Bowl_EveryFaceReachesAnOutletInFiniteSteps()
    {
        var mesh = Grid(12, 12, (x, y) => ((x - 6) * (x - 6)) + ((y - 6) * (y - 6)));

        BasinGraph graph = Analyze(mesh);

        for (int face = 0; face < graph.FaceCount; face++)
        {
            int current = face;
            int steps = 0;
            while (graph.FlowsTo[current] >= 0)
            {
                current = graph.FlowsTo[current];
                Assert.True(++steps <= graph.FaceCount, $"face {face} did not reach an outlet");
            }
        }
    }

    /// <summary>The basins partition the terrain: no face is counted twice, and none is left out.</summary>
    [Fact]
    public void Analyze_Bowl_BasinAreasSumToTheTerrainArea()
    {
        var mesh = Grid(10, 10, (x, y) => ((x - 5) * (x - 5)) + ((y - 5) * (y - 5)));

        BasinGraph graph = Analyze(mesh);

        double summed = 0.0;
        int faceCount = 0;
        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            summed += basin.PlanArea;
            faceCount += basin.FaceCount;
        }

        Assert.Equal(graph.TotalPlanArea, summed, 9);
        Assert.Equal(100.0, summed, 9);
        Assert.Equal(graph.FaceCount, faceCount);
    }

    /// <summary>Basins are ordered by descending area, which is what keeps a basin's colour stable.</summary>
    [Fact]
    public void Analyze_UniformSlope_OrdersBasinsByDescendingArea()
    {
        var mesh = Grid(10, 10, (x, y) => 100.0 - (0.5 * y));

        BasinGraph graph = Analyze(mesh);

        for (int index = 1; index < graph.Basins.Count; index++)
            Assert.True(graph.Basins[index - 1].PlanArea >= graph.Basins[index].PlanArea);
    }

    /// <summary>Same mesh, same answer. A basin map that shuffles between rebuilds is unreadable.</summary>
    [Fact]
    public void Analyze_RunTwice_ProducesIdenticalLabels()
    {
        var mesh = Grid(10, 10, (x, y) => ((x - 5) * (x - 5)) + Math.Sin(y) + Plateau(true)(x, y));

        BasinGraph first = Analyze(mesh);
        BasinGraph second = Analyze(mesh);

        Assert.Equal(first.Basins.Count, second.Basins.Count);
        Assert.Equal(first.FaceBasin, second.FaceBasin);
    }

    /// <summary>
    /// Merging absorbs slivers into the basin they spill into. A uniform slope is the worst case for
    /// basin count — every face along the low edge is its own outlet — so it is the honest test of it.
    /// </summary>
    [Fact]
    public void Analyze_UniformSlopeWithMerging_ProducesFewerBasinsAndKeepsEveryFace()
    {
        var mesh = Grid(10, 10, (x, y) => 100.0 - (0.5 * y));

        BasinGraph unmerged = Analyze(mesh);
        BasinGraph merged = Analyze(mesh, new DrainageBasinAnalyzer.Options
        {
            MinimumBasinAreaShare = 0.2
        });

        Assert.True(
            merged.Basins.Count < unmerged.Basins.Count,
            $"merging left {merged.Basins.Count} of {unmerged.Basins.Count} basins");
        Assert.Equal(unmerged.TotalPlanArea, merged.TotalPlanArea, 9);
        Assert.DoesNotContain(-1, merged.FaceBasin);
    }

    /// <summary>
    /// Merging tidies away sliver *catchments*; it must never tidy away a depression. A small pit high on
    /// a slope has a small catchment, so it is exactly the thing an area threshold would absorb — and
    /// absorbed, it vanishes from the graph, leaving the Catchments card reporting no closed depressions
    /// while the Ponding card beside it reports one.
    /// </summary>
    [Fact]
    public void Analyze_SmallPitHighOnASlope_SurvivesAggressiveMerging()
    {
        // The pit sits near the top, so almost nothing drains into it and its basin stays tiny.
        var mesh = Grid(20, 20, (x, y) =>
        {
            double baseZ = 100.0 - (0.5 * y);
            bool inPit = x >= 9 && x <= 11 && y >= 2 && y <= 4;
            return inPit ? baseZ - 1.0 : baseZ;
        });

        BasinGraph unmerged = Analyze(mesh);
        Assert.Equal(1, unmerged.SinkBasinCount);

        // A threshold big enough to swallow most of the terrain's basins.
        BasinGraph merged = Analyze(mesh, new DrainageBasinAnalyzer.Options
        {
            MinimumBasinAreaShare = 0.25
        });

        Assert.Equal(1, merged.SinkBasinCount);
        Assert.True(
            merged.Basins.Count < unmerged.Basins.Count,
            "the merge threshold should still have absorbed ordinary catchments");
    }

    /// <summary>
    /// The other direction: a sliver must not be absorbed *into* a depression either. A pond's spill is
    /// found by flooding until the water reaches another basin, so a depression that has swallowed its
    /// neighbours has its escape pushed outward and is measured too deep.
    /// </summary>
    [Fact]
    public void Analyze_Merging_NeverGrowsADepression()
    {
        var mesh = Grid(20, 20, (x, y) =>
        {
            double baseZ = 100.0 - (0.5 * y);
            bool inPit = x >= 9 && x <= 11 && y >= 2 && y <= 4;
            return inPit ? baseZ - 1.0 : baseZ;
        });

        int SinkFaces(BasinGraph graph)
        {
            int count = 0;
            foreach (BasinGraph.Basin basin in graph.Basins)
            {
                if (basin.Outlet == BasinGraph.OutletKind.Sink)
                    count += basin.FaceCount;
            }

            return count;
        }

        BasinGraph unmerged = Analyze(mesh);
        BasinGraph merged = Analyze(mesh, new DrainageBasinAnalyzer.Options { MinimumBasinAreaShare = 0.25 });

        Assert.Equal(SinkFaces(unmerged), SinkFaces(merged));
    }

    [Fact]
    public void Analyze_EmptyMesh_ReturnsAnEmptyGraph()
    {
        BasinGraph graph = DrainageBasinAnalyzer.Analyze(Array.Empty<double>(), 0, Array.Empty<int>(), 0);

        Assert.Empty(graph.Basins);
        Assert.Equal(0, graph.FaceCount);
        Assert.Equal(0, graph.SinkBasinCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Flow-path heads.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every basin's flow head must actually produce a path.
    ///
    /// Found live, not here: the head was originally the basin's highest *vertex*, and more than half the
    /// catchments on an ordinary hillside drew nothing. A basin's highest vertex is usually a local
    /// maximum, so a trace starting exactly on it lands in one arbitrary face of the several sharing it,
    /// where the descent ray from that corner has no forward exit — the trace stops after one point and
    /// is discarded. Nothing about that is visible in the basin graph itself, which is why no test here
    /// caught it.
    /// </summary>
    [Theory]
    [InlineData(10, 10)]
    [InlineData(21, 21)]
    public void FlowStart_OnAHillsideWithAPad_TracesARealPathForEveryBasin(int nx, int ny)
    {
        var mesh = Grid(nx, ny, (x, y) =>
            x >= nx * 0.3 && x <= nx * 0.7 && y >= ny * 0.3 && y <= ny * 0.7
                ? 100.0 - (0.08 * ny * 0.5)
                : 100.0 - (0.08 * y));
        BasinGraph graph = Analyze(mesh, new DrainageBasinAnalyzer.Options { MinimumBasinAreaShare = 0.01 });
        Assert.NotEmpty(graph.Basins);

        var starts = new double[graph.Basins.Count * 2];
        for (int index = 0; index < graph.Basins.Count; index++)
        {
            starts[index * 2] = graph.Basins[index].FlowStartX;
            starts[(index * 2) + 1] = graph.Basins[index].FlowStartY;
        }

        WaterflowTracer.Result traced = WaterflowTracer.Trace(
            mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount, starts, graph.Basins.Count);

        Assert.Equal(0, traced.RejectedStartCount);
        Assert.Equal(graph.Basins.Count, traced.Paths.Count);

        int stillborn = 0;
        foreach (WaterflowTracer.Path path in traced.Paths)
        {
            if (path.PointCount < 2)
                stillborn++;
        }

        Assert.Equal(0, stillborn);
    }

    /// <summary>
    /// The head is a face centroid, so it is strictly inside the terrain rather than on a vertex — that
    /// interiority is the whole reason an exit is guaranteed to exist.
    /// </summary>
    [Fact]
    public void FlowStart_IsAFaceCentroid_NotAVertex()
    {
        var mesh = Grid(10, 10, (x, y) => 100.0 - (0.5 * y));

        BasinGraph graph = Analyze(mesh);

        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            bool onAVertex = false;
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                if (Math.Abs(mesh.Vertices[vertex * 3] - basin.FlowStartX) < 1e-9 &&
                    Math.Abs(mesh.Vertices[(vertex * 3) + 1] - basin.FlowStartY) < 1e-9)
                    onAVertex = true;
            }

            Assert.False(onAVertex, $"basin {basin.Index} starts on a mesh vertex");
        }
    }

    /// <summary>
    /// A level face has no direction to leave by, so the head skips flat faces even when they are the
    /// basin's highest. Stated as the exact property rather than as a coordinate in a particular scene:
    /// wherever a basin has any falling face at all, its head sits on one. A basin that is flat
    /// throughout has nothing to choose and falls back to its highest face, where a short path is the
    /// honest answer rather than a bug.
    /// </summary>
    [Fact]
    public void FlowStart_SitsOnAFallingFace_WhereverTheBasinHasOne()
    {
        // A level plateau above a slope, so most basins contain both kinds of face.
        var mesh = Grid(10, 10, (x, y) => y <= 4 ? 100.0 : 100.0 - ((y - 4) * 0.5));
        BasinGraph graph = Analyze(mesh);

        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            bool hasFallingFace = false;
            int headFace = -1;
            for (int face = 0; face < graph.FaceCount; face++)
            {
                if (graph.FaceBasin[face] != basin.Index)
                    continue;

                (double cx, double cy, bool falls) = FaceCentroid(mesh, face);
                if (falls)
                    hasFallingFace = true;
                if (Math.Abs(cx - basin.FlowStartX) < 1e-9 && Math.Abs(cy - basin.FlowStartY) < 1e-9)
                    headFace = face;
            }

            Assert.True(headFace >= 0, $"basin {basin.Index}'s head is not on any of its own faces");
            if (!hasFallingFace)
                continue;

            (_, _, bool headFalls) = FaceCentroid(mesh, headFace);
            Assert.True(headFalls, $"basin {basin.Index} has falling faces but its head is on a level one");
        }
    }

    /// <summary>Plan centroid of a face, and whether it falls at all — the two things a head needs.</summary>
    private static (double X, double Y, bool Falls) FaceCentroid(
        (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) mesh, int face)
    {
        double x = 0.0;
        double y = 0.0;
        var z = new double[3];
        for (int corner = 0; corner < 3; corner++)
        {
            int vertex = mesh.Faces[(face * 3) + corner];
            x += mesh.Vertices[vertex * 3] / 3.0;
            y += mesh.Vertices[(vertex * 3) + 1] / 3.0;
            z[corner] = mesh.Vertices[(vertex * 3) + 2];
        }

        bool falls = Math.Abs(z[0] - z[1]) > 1e-9 || Math.Abs(z[1] - z[2]) > 1e-9;
        return (x, y, falls);
    }

    // ---------------------------------------------------------------------------------------------
    // Boundary extraction.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A bowl's single basin is bounded by the terrain edge, so its loop is the terrain perimeter.</summary>
    [Fact]
    public void Extract_BowlBasin_ReturnsOneClosedLoopAroundTheTerrain()
    {
        var mesh = Grid(10, 10, (x, y) => ((x - 5) * (x - 5)) + ((y - 5) * (y - 5)));
        BasinGraph graph = Analyze(mesh);
        int largest = 0;

        List<double[]> loops = BasinBoundaryExtractor.Extract(
            graph, mesh.Vertices, mesh.VertexCount, mesh.Faces, largest);

        double[] loop = Assert.Single(loops);
        int pointCount = loop.Length / 3;
        Assert.Equal(41, pointCount); // 40 boundary edges of a 10 x 10 grid, first point repeated
        Assert.Equal(loop[0], loop[(pointCount - 1) * 3], 9);
        Assert.Equal(loop[1], loop[((pointCount - 1) * 3) + 1], 9);
    }

    /// <summary>
    /// A square split into two faces with the second wound backwards: the boundary edges no longer balance,
    /// so they chain into two open runs. Those must come out open — closing each with a straight segment
    /// back to its start would draw a divide along the square's diagonal.
    /// </summary>
    [Fact]
    public void Extract_InconsistentWinding_ReturnsOpenChainsUnclosed()
    {
        double[] vertices = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
        int[] faces = { 0, 1, 2, 0, 3, 2 };
        var graph = new BasinGraph
        {
            FaceBasin = new[] { 0, 0 },
            FlowsTo = new[] { -1, 0 },
            FaceCount = 2,
            Neighbors = new[] { -1, -1, 1, -1, -1, 0 },
            Basins = Array.Empty<BasinGraph.Basin>(),
            FlatFaceCount = 0,
            TotalPlanArea = 1.0
        };

        List<double[]> chains = BasinBoundaryExtractor.Extract(graph, vertices, 4, faces, 0);

        Assert.Equal(2, chains.Count);
        foreach (double[] chain in chains)
        {
            Assert.Equal(9, chain.Length); // three distinct points, no repeated first point
            int last = (chain.Length / 3) - 1;
            Assert.False(chain[0] == chain[last * 3] && chain[1] == chain[(last * 3) + 1]);
        }
    }

    /// <summary>The one-pass extraction must give every basin exactly what the per-basin scan gives it.</summary>
    [Fact]
    public void ExtractAll_MultiBasinTerrain_MatchesPerBasinExtract()
    {
        var mesh = Grid(12, 12, (x, y) => Math.Sin(x * 0.9) + Math.Cos(y * 0.7) + (0.05 * x));
        BasinGraph graph = Analyze(mesh);
        Assert.True(graph.Basins.Count > 1);

        List<double[]>[] all = BasinBoundaryExtractor.ExtractAll(graph, mesh.Vertices, mesh.VertexCount, mesh.Faces);

        Assert.Equal(graph.Basins.Count, all.Length);
        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            List<double[]> single = BasinBoundaryExtractor.Extract(
                graph, mesh.Vertices, mesh.VertexCount, mesh.Faces, basin.Index);
            Assert.Equal(single.Count, all[basin.Index].Count);
            for (int loop = 0; loop < single.Count; loop++)
                Assert.Equal(single[loop], all[basin.Index][loop]);
        }
    }

    /// <summary>Every basin boundary closes. An open catchment polygon cannot be hatched or measured.</summary>
    [Fact]
    public void Extract_EveryBasinOfASlope_ReturnsClosedLoops()
    {
        var mesh = Grid(8, 8, (x, y) => 100.0 - (0.5 * y));
        BasinGraph graph = Analyze(mesh);

        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            List<double[]> loops = BasinBoundaryExtractor.Extract(
                graph, mesh.Vertices, mesh.VertexCount, mesh.Faces, basin.Index);

            Assert.NotEmpty(loops);
            foreach (double[] loop in loops)
            {
                int pointCount = loop.Length / 3;
                Assert.Equal(loop[0], loop[(pointCount - 1) * 3], 9);
                Assert.Equal(loop[1], loop[((pointCount - 1) * 3) + 1], 9);
            }
        }
    }
}
