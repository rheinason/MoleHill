using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Scattering;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public class LargeTerrainPerformanceBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void GradePath_LargeTerrain20260705Benchmark_ReportsShapeTimingAndTopology()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the large terrain Grade Path benchmark.");
            return;
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var performanceTimings = new PathGrader.PerformanceTimings();
        TerrainGradePathLarge20260705CopiedCaseTests.CopiedCaseRun run =
            TerrainGradePathLarge20260705CopiedCaseTests.RunCase(
                preferSplitKeep: true,
                performanceTimings: performanceTimings);
        stopwatch.Stop();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        output.WriteLine($"Copied-case wrapper: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        output.WriteLine($"PathGrader.Grade only: {run.GradeElapsedMilliseconds:0.0} ms");
        output.WriteLine(
            $"Wrapper current-thread allocation: {Math.Max(0, allocatedAfter - allocatedBefore):N0} bytes");
        output.WriteLine(
            $"PathGrader allocation: current thread={run.GradeThreadAllocatedBytes:N0} bytes; " +
            $"process-wide={run.GradeTotalAllocatedBytes:N0} bytes");
        output.WriteLine(
            $"Phases: input={performanceTimings.InputValidationMilliseconds:0.0}ms; " +
            $"terrain-grid={performanceTimings.TerrainGridMilliseconds:0.0}ms; " +
            $"barriers={performanceTimings.BarrierPreparationMilliseconds:0.0}ms; " +
            $"corridor/daylight={performanceTimings.CorridorDaylightMilliseconds:0.0}ms; " +
            $"loops={performanceTimings.LoopPreparationMilliseconds:0.0}ms; " +
            $"conform-split={performanceTimings.ConformSplitMilliseconds:0.0}ms; " +
            $"topology={performanceTimings.TopologyValidationMilliseconds:0.0}ms; " +
            $"apply-Z={performanceTimings.ApplyGradingZMilliseconds:0.0}ms; " +
            $"result={performanceTimings.ResultAssemblyMilliseconds:0.0}ms; " +
            $"total={performanceTimings.TotalMilliseconds:0.0}ms");
        output.WriteLine(
            $"Phase allocations: input={performanceTimings.InputValidationAllocatedBytes:N0}; " +
            $"terrain-grid={performanceTimings.TerrainGridAllocatedBytes:N0}; " +
            $"barriers={performanceTimings.BarrierPreparationAllocatedBytes:N0}; " +
            $"corridor/daylight={performanceTimings.CorridorDaylightAllocatedBytes:N0}; " +
            $"loops={performanceTimings.LoopPreparationAllocatedBytes:N0}; " +
            $"conform-split={performanceTimings.ConformSplitAllocatedBytes:N0}; " +
            $"topology={performanceTimings.TopologyValidationAllocatedBytes:N0}; " +
            $"apply-Z={performanceTimings.ApplyGradingZAllocatedBytes:N0}; " +
            $"result={performanceTimings.ResultAssemblyAllocatedBytes:N0} bytes");
        MeshAreaTopologySplitter.PerformanceTimings? splitDetails =
            performanceTimings.ConformSplitDetails;
        Assert.NotNull(splitDetails);
        output.WriteLine(
            $"Conform details: faces={splitDetails!.FaceDataMilliseconds:0.0}ms/" +
            $"{splitDetails.FaceDataAllocatedBytes:N0}; " +
            $"boundary={splitDetails.BoundarySegmentsMilliseconds:0.0}ms/" +
            $"{splitDetails.BoundarySegmentsAllocatedBytes:N0}; " +
            $"map={splitDetails.FaceMappingMilliseconds:0.0}ms/" +
            $"{splitDetails.FaceMappingAllocatedBytes:N0}; " +
            $"registry={splitDetails.SharedEdgeRegistryMilliseconds:0.0}ms/" +
            $"{splitDetails.SharedEdgeRegistryAllocatedBytes:N0}; " +
            $"setup={splitDetails.OutputSetupMilliseconds:0.0}ms/" +
            $"{splitDetails.OutputSetupAllocatedBytes:N0}; " +
            $"triangulate={splitDetails.TouchedFaceTriangulationMilliseconds:0.0}ms/" +
            $"{splitDetails.TouchedFaceTriangulationAllocatedBytes:N0}; " +
            $"classify={splitDetails.ClassificationMilliseconds:0.0}ms/" +
            $"{splitDetails.ClassificationAllocatedBytes:N0} bytes");
        output.WriteLine(
            $"Conform touched faces: total={splitDetails.TouchedFaceCount:N0}; " +
            $"registry-only={splitDetails.RegistryOnlyFaceCount:N0}; " +
            $"zero-internal={splitDetails.ZeroInternalSegmentFaceCount:N0}; " +
            $"one-internal={splitDetails.OneInternalSegmentFaceCount:N0}; " +
            $"multiple-internal={splitDetails.MultipleInternalSegmentFaceCount:N0}");
        output.WriteLine($"Shape: {run.InputVertexCount:N0}/{run.InputFaceCount:N0} -> {run.OutputVertexCount:N0}/{run.OutputFaceCount:N0}");
        output.WriteLine($"Mesh fingerprint: {run.MeshFingerprint}");
        output.WriteLine("Bundle-recorded Grade Path output was 8,392 verts / 16,511 faces.");
        foreach (string diagnostic in run.Diagnostics)
            output.WriteLine($"Diagnostic: {diagnostic}");
        output.WriteLine(
            $"Topology: boundaryEdges={run.BoundaryEdgeCount:N0}, boundaryVertices={run.BoundaryVertexCount:N0}, " +
            $"components={run.BoundaryComponentCount:N0}, nonmanifold={run.NonManifoldEdgeCount:N0}, openChains={run.HasOpenBoundaryChains}");

        Assert.Equal(6341, run.InputVertexCount);
        Assert.Equal(12411, run.InputFaceCount);
        Assert.InRange(run.OutputVertexCount, 8350, 8450);
        Assert.InRange(run.OutputFaceCount, 16450, 16650);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MeshHeightProjector_LargeReferenceBenchmark_ReportsLinearLookupTiming()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the MeshHeightProjector benchmark.");
            return;
        }

        const int gridSize = 224;
        BuildRegularGrid(gridSize, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);

        var buildStopwatch = Stopwatch.StartNew();
        var projector = new MeshHeightProjector(vertices, vertexCount, faces, faceCount);
        buildStopwatch.Stop();

        int sampleCount = Math.Min(100_000, faceCount);
        int misses = 0;
        double checksum = 0.0;
        var queryStopwatch = Stopwatch.StartNew();
        for (int faceIndex = 0; faceIndex < sampleCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            double x = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double y = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
            if (projector.TryProjectZ(x, y, 0.0, 1e-6, out double z, out var status) &&
                status == MeshHeightProjector.ProjectionStatus.Projected)
            {
                checksum += z;
            }
            else
            {
                misses++;
            }
        }
        queryStopwatch.Stop();

        output.WriteLine($"Reference mesh: {vertexCount:N0} verts / {faceCount:N0} faces");
        output.WriteLine($"Projector build: {buildStopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        output.WriteLine($"Projector queries: {queryStopwatch.Elapsed.TotalMilliseconds:0.0} ms for {sampleCount:N0} samples");
        output.WriteLine($"Misses: {misses:N0}; checksum={checksum:G17}");

        Assert.Equal(0, misses);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void TerrainFaceGrid_DaylightScaling_ReportsFaceStationCost()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the daylight scaling benchmark.");
            return;
        }

        const int stationCount = 128;
        foreach (int gridSize in new[] { 64, 128, 224 })
        {
            BuildRegularGrid(
                gridSize,
                out double[] vertices,
                out int vertexCount,
                out int[] faces,
                out int faceCount);

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var buildStopwatch = Stopwatch.StartNew();
            var terrain = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
            buildStopwatch.Stop();
            long buildAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            int unexpectedHits = 0;
            double checksum = 0.0;
            var queryStopwatch = Stopwatch.StartNew();
            for (int station = 0; station < stationCount; station++)
            {
                double fraction = (station + 0.5) / stationCount;
                double edgeX = gridSize * 0.1;
                double edgeY = gridSize * fraction;
                if (terrain.TryFindRayDaylightReach(
                    edgeX,
                    edgeY,
                    edgeZ: 1000.0,
                    dirX: 1.0,
                    dirY: 0.0,
                    slopeRatio: 0.5,
                    branchSign: 1.0,
                    maxReach: gridSize * 0.8,
                    out double reach,
                    out double bestApproach))
                {
                    unexpectedHits++;
                    checksum += reach;
                }
                else
                {
                    checksum += bestApproach;
                }
            }
            queryStopwatch.Stop();

            long faceStationTests = (long)faceCount * stationCount;
            double nanosecondsPerFaceStation =
                queryStopwatch.Elapsed.TotalMilliseconds * 1_000_000.0 / faceStationTests;
            output.WriteLine(
                $"Daylight grid {gridSize} ({faceCount:N0} faces): build={buildStopwatch.Elapsed.TotalMilliseconds:0.0}ms, " +
                $"allocated={buildAllocated:N0}; {stationCount} no-hit stations={queryStopwatch.Elapsed.TotalMilliseconds:0.0}ms, " +
                $"{nanosecondsPerFaceStation:0.00}ns/face-station, unexpectedHits={unexpectedHits}, checksum={checksum:G17}");

            Assert.Equal(0, unexpectedHits);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void PathGrader_ClosestSegmentScaling_ReportsSegmentQueryCost()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the closest-path-segment scaling benchmark.");
            return;
        }

        const int queryCount = 10_000;
        var queryXy = new double[queryCount * 2];
        for (int query = 0; query < queryCount; query++)
        {
            double fraction = (query + 0.5) / queryCount;
            queryXy[query * 2] = fraction * 1000.0;
            queryXy[query * 2 + 1] = 5.0 + Math.Sin(fraction * Math.PI * 8.0);
        }

        foreach (int vertexCount in new[] { 32, 128, 463, 1024 })
        {
            var pathXy = new double[vertexCount * 2];
            var pathZ = new double[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                double fraction = vertex / (double)(vertexCount - 1);
                pathXy[vertex * 2] = fraction * 1000.0;
                pathXy[vertex * 2 + 1] = Math.Sin(fraction * Math.PI * 4.0) * 2.0;
                pathZ[vertex] = 100.0 + fraction;
            }

            for (int warmup = 0; warmup < 40; warmup++)
            {
                _ = PathGrader.RunClosestPathQueriesForDiagnostics(
                    pathXy,
                    pathZ,
                    vertexCount,
                    queryXy,
                    queryCount: 1000);
            }
            _ = PathGrader.RunClosestPathQueriesForDiagnostics(
                pathXy,
                pathZ,
                vertexCount,
                queryXy,
                queryCount);
            _ = PathGrader.RunIndexedClosestPathQueriesForDiagnostics(
                pathXy,
                pathZ,
                vertexCount,
                queryXy,
                queryCount,
                maxDistance: 12.0);

            var stopwatch = Stopwatch.StartNew();
            double checksum = PathGrader.RunClosestPathQueriesForDiagnostics(
                pathXy,
                pathZ,
                vertexCount,
                queryXy,
                queryCount);
            stopwatch.Stop();
            var indexedStopwatch = Stopwatch.StartNew();
            double indexedChecksum = PathGrader.RunIndexedClosestPathQueriesForDiagnostics(
                pathXy,
                pathZ,
                vertexCount,
                queryXy,
                queryCount,
                maxDistance: 12.0);
            indexedStopwatch.Stop();

            long segmentQueries = (long)(vertexCount - 1) * queryCount;
            double nanosecondsPerSegmentQuery =
                stopwatch.Elapsed.TotalMilliseconds * 1_000_000.0 / segmentQueries;
            output.WriteLine(
                $"Closest path with {vertexCount - 1:N0} segments × {queryCount:N0} queries: " +
                $"{stopwatch.Elapsed.TotalMilliseconds:0.0}ms, " +
                $"{nanosecondsPerSegmentQuery:0.00}ns/segment-query; " +
                $"indexed={indexedStopwatch.Elapsed.TotalMilliseconds:0.0}ms, checksum={checksum:G17}");
            Assert.Equal(checksum, indexedChecksum);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MeshAreaTopologySplitter_SegmentScaling_ReportsTimeAndAllocation()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the conform-loop segment scaling benchmark.");
            return;
        }

        const int gridSize = 48;
        BuildRegularGrid(
            gridSize,
            out double[] vertices,
            out int vertexCount,
            out int[] faces,
            out int faceCount);

        foreach (int segmentCount in new[] { 32, 128, 384 })
        {
            double[] loop = BuildCircleLoop(
                centerX: gridSize * 0.5,
                centerY: gridSize * 0.5,
                radius: gridSize * 0.3,
                segmentCount: segmentCount);
            var areas = new[]
            {
                new MeshAreaSplitter.AreaBoundary(loop, segmentCount)
            };

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            MeshAreaSplitter.SplitResult? result = MeshAreaTopologySplitter.Split(
                vertices,
                vertexCount,
                faces,
                faceCount,
                areas,
                boundaryTolerance: 1e-6,
                out string? errorMessage);
            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            output.WriteLine(
                $"Conform loop {segmentCount:N0} segments on {faceCount:N0} faces: " +
                $"{stopwatch.Elapsed.TotalMilliseconds:0.0}ms, allocated={allocated:N0}, " +
                $"output={(result == null ? "null" : $"{result.VertexCount:N0}/{result.FaceCount:N0}")}, " +
                $"error={errorMessage ?? "<none>"}");
            Assert.NotNull(result);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MeshTopologyValidator_FlatScaling_ReportsTimeAndAllocation()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the flat-topology scaling benchmark.");
            return;
        }

        foreach (int gridSize in new[] { 72, 224, 708 })
        {
            BuildRegularGrid(
                gridSize,
                out _,
                out _,
                out int[] faces,
                out int faceCount);

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            MeshTopologyValidator.BoundaryGraphAnalysis analysis =
                MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            output.WriteLine(
                $"Flat topology {faceCount:N0} faces: {stopwatch.Elapsed.TotalMilliseconds:0.0}ms, " +
                $"allocated={allocated:N0}, boundary={analysis.BoundaryEdgeCount:N0}/" +
                $"{analysis.BoundaryVertexCount:N0}, components={analysis.BoundaryComponentCount:N0}, " +
                $"open={analysis.HasOpenBoundaryChains}, nonmanifold={analysis.NonManifoldEdgeCount:N0}");

            Assert.Equal(gridSize * 4, analysis.BoundaryEdgeCount);
            Assert.Equal(gridSize * 4, analysis.BoundaryVertexCount);
            Assert.Equal(1, analysis.BoundaryComponentCount);
            Assert.False(analysis.HasOpenBoundaryChains);
            Assert.Equal(0, analysis.NonManifoldEdgeCount);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ScatterSampler_PoissonDomainShape_ReportsHiddenBoundingBoxCost()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the Poisson domain-shape benchmark.");
            return;
        }

        const double spacing = 2.0;
        _ = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { new[] { 0.0, 0.0, 8.0, 0.0, 8.0, 8.0, 0.0, 8.0 } },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 4.0,
            Seed = 1,
            MaxSamples = 1000
        });

        MeasurePoissonCase(
            "compact",
            new[]
            {
                new[] { 0.0, 0.0, 28.284, 0.0, 28.284, 28.284, 0.0, 28.284 }
            });
        MeasurePoissonCase(
            "thin-diagonal",
            new[]
            {
                new[] { 0.0, 0.0, 4.0, 0.0, 200.0, 196.0, 200.0, 200.0 }
            });
        MeasurePoissonCase(
            "disconnected",
            new[]
            {
                new[] { 0.0, 0.0, 20.0, 0.0, 20.0, 20.0, 0.0, 20.0 },
                new[] { 180.0, 180.0, 200.0, 180.0, 200.0, 200.0, 180.0, 200.0 }
            });

        void MeasurePoissonCase(string name, IReadOnlyList<double[]> boundaries)
        {
            long estimatedGridCells = EstimatePoissonGridCells(boundaries, spacing);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            List<(double X, double Y)> points = ScatterSampler.Sample(new ScatterRequest
            {
                Boundaries = boundaries,
                Pattern = ScatterPattern.PoissonDisk,
                DensityMode = ScatterDensityMode.Spacing,
                Spacing = spacing,
                Seed = 17,
                MaxSamples = 50_000
            });
            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            output.WriteLine(
                $"Poisson {name}: {stopwatch.Elapsed.TotalMilliseconds:0.0}ms, allocated={allocated:N0}, " +
                $"visible points={points.Count:N0}, dense grid cells={estimatedGridCells:N0}");
            Assert.NotEmpty(points);
        }
    }

    private static bool IsPerfEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_PERF"), "1", StringComparison.Ordinal);

    private static double[] BuildCircleLoop(
        double centerX,
        double centerY,
        double radius,
        int segmentCount)
    {
        var loop = new double[segmentCount * 2];
        for (int index = 0; index < segmentCount; index++)
        {
            double angle = Math.PI * 2.0 * index / segmentCount;
            loop[index * 2] = centerX + (Math.Cos(angle) * radius);
            loop[index * 2 + 1] = centerY + (Math.Sin(angle) * radius);
        }

        return loop;
    }

    private static long EstimatePoissonGridCells(
        IReadOnlyList<double[]> boundaries,
        double spacing)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (double[] boundary in boundaries)
        {
            for (int index = 0; index < boundary.Length / 2; index++)
            {
                double x = boundary[index * 2];
                double y = boundary[index * 2 + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        double cell = spacing / Math.Sqrt(2.0);
        long width = (long)Math.Ceiling((maxX - minX) / cell);
        long height = (long)Math.Ceiling((maxY - minY) / cell);
        return checked(width * height);
    }

    private static void BuildRegularGrid(int gridSize, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount)
    {
        vertexCount = (gridSize + 1) * (gridSize + 1);
        vertices = new double[vertexCount * 3];
        for (int y = 0; y <= gridSize; y++)
        {
            for (int x = 0; x <= gridSize; x++)
            {
                int index = (y * (gridSize + 1)) + x;
                vertices[index * 3] = x;
                vertices[index * 3 + 1] = y;
                vertices[index * 3 + 2] = Math.Sin(x * 0.03) + Math.Cos(y * 0.04);
            }
        }

        faceCount = gridSize * gridSize * 2;
        faces = new int[faceCount * 3];
        int face = 0;
        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                int v00 = (y * (gridSize + 1)) + x;
                int v10 = v00 + 1;
                int v01 = v00 + gridSize + 1;
                int v11 = v01 + 1;

                faces[face * 3] = v00;
                faces[face * 3 + 1] = v10;
                faces[face * 3 + 2] = v11;
                face++;

                faces[face * 3] = v00;
                faces[face * 3 + 1] = v11;
                faces[face * 3 + 2] = v01;
                face++;
            }
        }
    }
}
