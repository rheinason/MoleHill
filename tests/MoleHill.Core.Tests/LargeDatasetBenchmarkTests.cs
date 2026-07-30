using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using TriangleNet.Meshing.Algorithm;
using TriangleNet.Tools;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Ad-hoc benchmark for large CSV point datasets.
/// Drop a CSV file (x,y,z per row, comma-separated) at the path below and run:
///   dotnet test --filter LargeDatasetBenchmark
/// Results print to the test output window.
/// </summary>
public class LargeDatasetBenchmarkTests(ITestOutputHelper output)
{
    // Change this to wherever you drop the CSV:
    private const string CsvPath = @"C:\Users\hbxma\Dropbox\TopoTest\120K Pointstest.csv";

    [Fact]
    public void LargeDatasetBenchmark_BareDwyer()
    {
        if (!File.Exists(CsvPath))
        {
            output.WriteLine($"CSV not found at {CsvPath} — skipping.");
            return;
        }

        // Load CSV
        var lines = File.ReadAllLines(CsvPath);
        var points = new List<Vertex>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.Trim().TrimStart('{', '(', '[').TrimEnd('}', ')', ']');
            var parts = trimmed.Split(',');
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double y))
                continue;
            points.Add(new Vertex(x, y));
        }
        output.WriteLine($"Loaded {points.Count:N0} points.");

        // Bare Dwyer — exactly like ExamplePar
        var pool = new TrianglePool();
        var predicates = new RobustPredicates();
        var config = new Configuration
        {
            Predicates = () => predicates,
            TrianglePool = () => pool.Restart(),
        };
        var triangulator = new Dwyer();

        var sw = Stopwatch.StartNew();
        var mesh = triangulator.Triangulate(points, config);
        sw.Stop();

        int triCount = mesh.Triangles.Count();
        output.WriteLine($"Bare Dwyer.Triangulate: {sw.ElapsedMilliseconds}ms  ({triCount:N0} triangles)");

        pool.Clear();
        Assert.True(triCount > 0);
    }

    [Fact]
    public void LargeDatasetBenchmark_BuildResultBreakdown()
    {
        if (!File.Exists(CsvPath))
        {
            output.WriteLine($"CSV not found at {CsvPath} — skipping.");
            return;
        }

        // Load + merge (same as full pipeline)
        var lines = File.ReadAllLines(CsvPath);
        var xyzList = new List<double>(lines.Length * 3);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.Trim().TrimStart('{', '(', '[').TrimEnd('}', ')', ']');
            var parts = trimmed.Split(',');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double z))
                continue;
            xyzList.Add(x); xyzList.Add(y); xyzList.Add(z);
        }
        int rawCount = xyzList.Count / 3;
        var spotXyz = xyzList.ToArray();
        var emptyBreaklines = new BreaklineDiscretizer.BreaklineData(Array.Empty<double>(), 0, Array.Empty<int>(), 0);
        var merged = PointCloudProcessor.Merge(spotXyz, rawCount, emptyBreaklines, 0.01);
        output.WriteLine($"Input: {merged.VertexCount:N0} verts after merge");

        // Triangulate — same opts as TinEngine (Convex=true, no segs, no quality)
        var polygon = new Polygon(merged.VertexCount);
        for (int i = 0; i < merged.VertexCount; i++)
            polygon.Add(new Vertex(merged.XyCoords[i * 2], merged.XyCoords[i * 2 + 1]) { ID = i });
        var opts = new ConstraintOptions { ConformingDelaunay = false, Convex = true };

        var sw = Stopwatch.StartNew();
        var mesh = new GenericMesher().Triangulate(polygon, opts);
        sw.Stop();
        output.WriteLine($"1. Triangulate:              {sw.ElapsedMilliseconds}ms  ({mesh.Triangles.Count:N0} triangles)");

        // TriangleNetExtractor.Extract
        sw.Restart();
        var extracted = TriangleNetExtractor.Extract(mesh);
        sw.Stop();
        output.WriteLine($"2. TriangleNetExtractor:     {sw.ElapsedMilliseconds}ms  ({extracted.VertexCount:N0} verts, {extracted.FaceCount:N0} faces)");

        // Z lookup (sourceIds)
        sw.Restart();
        int inputVertexCount = merged.XyCoords.Length / 2;
        var outVerts = new double[extracted.VertexCount * 3];
        var steinerIndices = new List<int>();
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            outVerts[i * 3]     = extracted.Xy[i * 2];
            outVerts[i * 3 + 1] = extracted.Xy[i * 2 + 1];
            int srcId = extracted.SourceIds[i];
            if (srcId >= 0 && srcId < inputVertexCount && !double.IsNaN(merged.ZValues[srcId]))
                outVerts[i * 3 + 2] = merged.ZValues[srcId];
            else
            {
                outVerts[i * 3 + 2] = double.NaN;
                steinerIndices.Add(i);
            }
        }
        sw.Stop();
        output.WriteLine($"3. Z lookup:                 {sw.ElapsedMilliseconds}ms  ({steinerIndices.Count} Steiners)");

        // BuildEdgeTopology (first call)
        long topologyAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        var topology = IndexedMeshTools.BuildEdgeTopology(extracted.Faces, extracted.FaceCount);
        sw.Stop();
        long topologyAllocated =
            GC.GetTotalAllocatedBytes(precise: true) - topologyAllocatedBefore;
        output.WriteLine(
            $"4. BuildEdgeTopology (1):    {sw.ElapsedMilliseconds}ms  " +
            $"({topology.EdgeCount:N0} edges, {topology.NakedEdgeCount:N0} naked, " +
            $"allocated={topologyAllocated:N0})");

        // Exact automatic threshold from the topology already built above. This matches TinEngine's
        // production path and avoids the benchmark-only topology rebuild caused by Cull(..., 0).
        sw.Restart();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        double autoThreshold = TriangleBoundaryCuller.ComputeAutoThreshold(outVerts, topology);
        long autoThresholdAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        sw.Stop();
        output.WriteLine(
            $"5. Exact median threshold:    {sw.ElapsedMilliseconds}ms  " +
            $"threshold={autoThreshold:G6}, allocated={autoThresholdAllocated:N0}");

        var peelSettings = new BoundaryTrianglePeelSettings
        {
            Enabled = true,
            MaxBoundaryEdgeLength = autoThreshold,
            MaxInteriorAngleDegrees = BoundaryTrianglePeelSettings.Default.MaxInteriorAngleDegrees,
            MaxSlopeAngleDegrees = BoundaryTrianglePeelSettings.Default.MaxSlopeAngleDegrees
        };
        var cullTimings = new TriangleBoundaryCuller.PerformanceTimings();
        allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        var cullResult = TriangleBoundaryCuller.Cull(
            outVerts, extracted.VertexCount,
            extracted.Faces, extracted.FaceCount,
            merged.XyCoords, merged.Segments,
            peelSettings,
            cullTimings);
        sw.Stop();
        long cullAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        output.WriteLine(
            $"6. BoundaryCuller (production-shaped): {sw.ElapsedMilliseconds}ms  " +
            $"changed={cullResult.Changed}, faces={cullResult.FaceCount:N0}, allocated={cullAllocated:N0}");
        output.WriteLine(
            $"   constraint index={cullTimings.ConstraintIndexMilliseconds:0.0}ms; " +
            $"edge maps={cullTimings.EdgeMapsMilliseconds:0.0}ms; " +
            $"seed={cullTimings.SeedQueueMilliseconds:0.0}ms ({cullTimings.InitialQueuedFaceCount:N0} queued); " +
            $"peel={cullTimings.PeelMilliseconds:0.0}ms ({cullTimings.RemovedFaceCount:N0} removed); " +
            $"compaction={cullTimings.CompactionMilliseconds:0.0}ms");
        output.WriteLine(
            $"   phase allocations: threshold={cullTimings.AutoThresholdAllocatedBytes:N0}; " +
            $"constraint index={cullTimings.ConstraintIndexAllocatedBytes:N0}; " +
            $"edge maps={cullTimings.EdgeMapsAllocatedBytes:N0}; " +
            $"seed={cullTimings.SeedQueueAllocatedBytes:N0}; " +
            $"peel={cullTimings.PeelAllocatedBytes:N0}; " +
            $"compaction={cullTimings.CompactionAllocatedBytes:N0} bytes");
        output.WriteLine("   includeEdgeTopology=false: no post-cull topology materialization.");

        Assert.True(extracted.FaceCount > 0);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void LargeDatasetBenchmark_TopologyRepresentationComparison()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the TIN topology representation comparison.");
            return;
        }

        if (!File.Exists(CsvPath))
        {
            output.WriteLine($"CSV not found at {CsvPath} — skipping.");
            return;
        }

        PointCloudProcessor.MergedData merged = LoadMergedCsv();
        IMesh mesh = TriangulateMerged(merged);
        TriangleNetExtractor.Result extracted = TriangleNetExtractor.Extract(mesh);
        output.WriteLine(
            $"Comparison input: {extracted.VertexCount:N0} vertices / {extracted.FaceCount:N0} faces.");

        ForceFullCollection();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long privateBefore = CurrentPrivateBytes();
        var stopwatch = Stopwatch.StartNew();
        NativeAdjacencySummary native = InspectNativeAdjacency(mesh);
        stopwatch.Stop();
        long nativeAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        long nativePrivateDelta = CurrentPrivateBytes() - privateBefore;
        output.WriteLine(
            $"Native adjacency index/validation: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms; " +
            $"allocated={nativeAllocated:N0}; private delta={nativePrivateDelta / (1024.0 * 1024.0):0.0} MB.");
        output.WriteLine(
            $"   materialize={native.MaterializeMilliseconds:0.0}ms; map={native.MapMilliseconds:0.0}ms; " +
            $"scan={native.ScanMilliseconds:0.0}ms; boundary refs={native.BoundaryReferences:N0}; " +
            $"resolved refs={native.ResolvedNeighborReferences:N0}; invalid={native.InvalidNeighborReferences:N0}; " +
            $"non-reciprocal={native.NonReciprocalReferences:N0}; max triangle id={native.MaxTriangleId:N0}.");

        ForceFullCollection();
        allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        privateBefore = CurrentPrivateBytes();
        stopwatch.Restart();
        PackedEdgeSummary packed = BuildAndSortPackedEdgeArrays(extracted.Faces, extracted.FaceCount);
        stopwatch.Stop();
        long packedAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        long packedPrivateDelta = CurrentPrivateBytes() - privateBefore;
        output.WriteLine(
            $"Sorted key/face arrays: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms; " +
            $"allocated={packedAllocated:N0}; private delta={packedPrivateDelta / (1024.0 * 1024.0):0.0} MB.");
        output.WriteLine(
            $"   fill={packed.FillMilliseconds:0.0}ms; sort={packed.SortMilliseconds:0.0}ms; " +
            $"scan={packed.ScanMilliseconds:0.0}ms; unique={packed.UniqueEdgeCount:N0}; " +
            $"naked={packed.NakedEdgeCount:N0}; non-manifold={packed.NonManifoldEdgeCount:N0}.");

        Assert.Equal(extracted.FaceCount, native.FaceCount);
        Assert.Equal(0, native.InvalidNeighborReferences);
        Assert.Equal(0, native.NonReciprocalReferences);
        Assert.True(packed.UniqueEdgeCount > 0);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void LargeDatasetBenchmark_ExplicitBoundaryPreparation_ReportsTimeAndAllocation()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the explicit-boundary preparation benchmark.");
            return;
        }

        if (!File.Exists(CsvPath))
        {
            output.WriteLine($"CSV not found at {CsvPath} — skipping.");
            return;
        }

        PointCloudProcessor.MergedData merged = LoadMergedCsv();
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        for (int index = 0; index < merged.VertexCount; index++)
        {
            double x = merged.XyCoords[index * 2];
            double y = merged.XyCoords[index * 2 + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        double padding = Math.Max(maxX - minX, maxY - minY) * 0.001;
        double[] boundaryPoints =
        {
            minX - padding, minY - padding, 0.0,
            maxX + padding, minY - padding, 0.0,
            maxX + padding, maxY + padding, 0.0,
            minX - padding, maxY + padding, 0.0,
            minX - padding, minY - padding, 0.0
        };
        var boundaries = new[]
        {
            new TinBoundaryPreparer.BoundaryPolyline(
                boundaryPoints,
                PointCount: 5,
                IsClosed: true)
        };

        ForceFullCollection();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long privateBefore = CurrentPrivateBytes();
        var stopwatch = Stopwatch.StartNew();
        TinBoundaryPreparer.PreparedTinInput prepared = TinBoundaryPreparer.Prepare(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            boundaries,
            tolerance: 0.01);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        long privateDelta = CurrentPrivateBytes() - privateBefore;

        output.WriteLine(
            $"Explicit-boundary preparation for {merged.VertexCount:N0} input vertices: " +
            $"{stopwatch.Elapsed.TotalMilliseconds:0.0}ms; allocated={allocated:N0}; " +
            $"private delta={privateDelta / (1024.0 * 1024.0):0.0} MB; " +
            $"output={prepared.XyCoords.Length / 2:N0} vertices / {prepared.Segments.Length / 2:N0} segments; " +
            $"mode={prepared.Mode}.");

        Assert.Equal(TinBoundaryPreparer.BoundaryMode.Explicit, prepared.Mode);
        Assert.True(prepared.XyCoords.Length / 2 >= merged.VertexCount);
    }

    [Fact]
    public void LargeDatasetBenchmark_ReportsPerStageTiming()
    {
        if (!File.Exists(CsvPath))
        {
            output.WriteLine($"CSV not found at {CsvPath} — skipping.");
            return;
        }

        // --- Load CSV ---
        var sw = Stopwatch.StartNew();
        var lines = File.ReadAllLines(CsvPath);
        var xyzList = new List<double>(lines.Length * 3);
        int skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Strip surrounding braces/brackets if present: {x, y, z} or (x, y, z)
            var trimmed = line.Trim().TrimStart('{', '(', '[').TrimEnd('}', ')', ']');
            var parts = trimmed.Split(',');
            if (parts.Length < 3) { skipped++; continue; }
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double z))
            { skipped++; continue; }
            xyzList.Add(x); xyzList.Add(y); xyzList.Add(z);
        }
        sw.Stop();
        int rawCount = xyzList.Count / 3;
        output.WriteLine($"Load CSV:         {sw.ElapsedMilliseconds}ms  ({rawCount:N0} points, {skipped} skipped)");
        output.WriteLine($"  X range: {xyzList.Where((_, i) => i % 3 == 0).Min():G6} .. {xyzList.Where((_, i) => i % 3 == 0).Max():G6}");
        output.WriteLine($"  Y range: {xyzList.Where((_, i) => i % 3 == 1).Min():G6} .. {xyzList.Where((_, i) => i % 3 == 1).Max():G6}");
        output.WriteLine($"  Z range: {xyzList.Where((_, i) => i % 3 == 2).Min():G6} .. {xyzList.Where((_, i) => i % 3 == 2).Max():G6}");

        if (rawCount < 3)
        {
            output.WriteLine("Not enough valid points.");
            return;
        }

        var spotXyz = xyzList.ToArray();

        // --- PointCloudProcessor.Merge (spot points only, no breaklines) ---
        sw.Restart();
        var emptyBreaklines = new BreaklineDiscretizer.BreaklineData(
            Array.Empty<double>(), 0, Array.Empty<int>(), 0);
        const double tolerance = 0.01;
        var merged = PointCloudProcessor.Merge(spotXyz, rawCount, emptyBreaklines, tolerance);
        sw.Stop();
        output.WriteLine($"PointCloudProcessor.Merge: {sw.ElapsedMilliseconds}ms  ({merged.VertexCount:N0} unique, {merged.DuplicatesRemoved:N0} dupes removed)");

        // --- Raw Triangle.NET (Example2-style, no wrappers) ---
        sw.Restart();
        {
            var poly = new Polygon(merged.VertexCount);
            for (int i = 0; i < merged.VertexCount; i++)
                poly.Add(new Vertex(merged.XyCoords[i * 2], merged.XyCoords[i * 2 + 1]) { ID = i });
            var opts = new ConstraintOptions { ConformingDelaunay = false, Convex = true };
            var rawMesh = new GenericMesher().Triangulate(poly, opts);
            output.WriteLine($"Raw Triangle.NET (Convex=true, no segs): {sw.ElapsedMilliseconds}ms  ({rawMesh.Triangles.Count:N0} triangles)");
        }
        sw.Stop();

        // --- TinBoundaryPreparer.Prepare (no explicit boundary → convex hull mode) ---
        sw.Restart();
        var prepared = TinBoundaryPreparer.Prepare(
            merged.XyCoords, merged.ZValues, merged.Segments,
            Array.Empty<TinBoundaryPreparer.BoundaryPolyline>(), tolerance);
        sw.Stop();
        output.WriteLine($"TinBoundaryPreparer.Prepare: {sw.ElapsedMilliseconds}ms  mode={prepared.Mode}  segs={prepared.Segments.Length / 2:N0}  verts={prepared.XyCoords.Length / 2:N0}");
        if (!string.IsNullOrWhiteSpace(prepared.InfoMessage))
            output.WriteLine($"  Info: {prepared.InfoMessage}");
        if (!string.IsNullOrWhiteSpace(prepared.WarningMessage))
            output.WriteLine($"  Warning: {prepared.WarningMessage}");

        // --- TinEngine.Build ---
        sw.Restart();
        var engine = new TinEngine();
        var result = engine.Build(
            prepared.XyCoords, prepared.ZValues, prepared.Segments,
            QualitySettings.None,
            out string? message,
            useConvexHull: prepared.UseConvexHull,
            maxBoundaryEdgeLength: 0);
        sw.Stop();
        output.WriteLine($"TinEngine.Build:   {sw.ElapsedMilliseconds}ms");
        if (!string.IsNullOrWhiteSpace(message))
            output.WriteLine($"  Message: {message}");
        if (result != null)
            output.WriteLine($"  Output: {result.VertexCount:N0} verts, {result.FaceCount:N0} faces, {result.NakedEdgeCount:N0} naked edges");
        else
            output.WriteLine("  Result: null (triangulation failed)");

        Assert.NotNull(result);
    }

    private static bool IsPerfEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_PERF"), "1", StringComparison.Ordinal);

    private static PointCloudProcessor.MergedData LoadMergedCsv()
    {
        var xyz = new List<double>();
        foreach (string line in File.ReadLines(CsvPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string trimmed = line.Trim().TrimStart('{', '(', '[').TrimEnd('}', ')', ']');
            string[] parts = trimmed.Split(',');
            if (parts.Length < 3 ||
                !double.TryParse(
                    parts[0].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double x) ||
                !double.TryParse(
                    parts[1].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double y) ||
                !double.TryParse(
                    parts[2].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double z))
            {
                continue;
            }

            xyz.Add(x);
            xyz.Add(y);
            xyz.Add(z);
        }

        double[] spotXyz = xyz.ToArray();
        var emptyBreaklines = new BreaklineDiscretizer.BreaklineData(
            Array.Empty<double>(),
            0,
            Array.Empty<int>(),
            0);
        return PointCloudProcessor.Merge(spotXyz, spotXyz.Length / 3, emptyBreaklines, 0.01);
    }

    private static IMesh TriangulateMerged(PointCloudProcessor.MergedData merged)
    {
        var polygon = new Polygon(merged.VertexCount);
        for (int i = 0; i < merged.VertexCount; i++)
        {
            polygon.Add(new Vertex(merged.XyCoords[i * 2], merged.XyCoords[i * 2 + 1])
            {
                ID = i
            });
        }

        var options = new ConstraintOptions
        {
            ConformingDelaunay = false,
            Convex = true
        };
        return new GenericMesher().Triangulate(polygon, options);
    }

    private static NativeAdjacencySummary InspectNativeAdjacency(IMesh mesh)
    {
        var phase = Stopwatch.StartNew();
        TriangleNet.Topology.Triangle[] triangles = mesh.Triangles.ToArray();
        double materializeMilliseconds = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        int maxTriangleId = -1;
        for (int face = 0; face < triangles.Length; face++)
            maxTriangleId = Math.Max(maxTriangleId, triangles[face].ID);

        int[]? denseIdToFace = null;
        Dictionary<int, int>? sparseIdToFace = null;
        if (maxTriangleId >= 0 && (long)maxTriangleId <= Math.Max((long)triangles.Length * 4, 1024))
        {
            denseIdToFace = new int[maxTriangleId + 1];
            Array.Fill(denseIdToFace, -1);
            for (int face = 0; face < triangles.Length; face++)
                denseIdToFace[triangles[face].ID] = face;
        }
        else
        {
            sparseIdToFace = new Dictionary<int, int>(triangles.Length);
            for (int face = 0; face < triangles.Length; face++)
                sparseIdToFace[triangles[face].ID] = face;
        }
        double mapMilliseconds = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        int boundaryReferences = 0;
        int resolvedReferences = 0;
        int invalidReferences = 0;
        int nonReciprocalReferences = 0;
        long checksum = 0;
        for (int face = 0; face < triangles.Length; face++)
        {
            TriangleNet.Topology.Triangle triangle = triangles[face];
            for (int localVertex = 0; localVertex < 3; localVertex++)
            {
                TriangleNet.Geometry.ITriangle? neighbor = triangle.GetNeighbor(localVertex);
                if (neighbor == null)
                {
                    boundaryReferences++;
                    continue;
                }

                int neighborFace = denseIdToFace != null
                    ? neighbor.ID >= 0 && neighbor.ID < denseIdToFace.Length
                        ? denseIdToFace[neighbor.ID]
                        : -1
                    : sparseIdToFace!.GetValueOrDefault(neighbor.ID, -1);
                if (neighborFace < 0)
                {
                    invalidReferences++;
                    continue;
                }

                resolvedReferences++;
                checksum = unchecked((checksum * 397) ^ neighborFace);

                bool reciprocal = false;
                for (int neighborVertex = 0; neighborVertex < 3; neighborVertex++)
                {
                    if (ReferenceEquals(neighbor.GetNeighbor(neighborVertex), triangle))
                    {
                        reciprocal = true;
                        break;
                    }
                }

                if (!reciprocal)
                    nonReciprocalReferences++;
            }
        }
        double scanMilliseconds = phase.Elapsed.TotalMilliseconds;

        GC.KeepAlive(triangles);
        GC.KeepAlive(denseIdToFace);
        GC.KeepAlive(sparseIdToFace);
        GC.KeepAlive(checksum);
        return new NativeAdjacencySummary(
            triangles.Length,
            maxTriangleId,
            boundaryReferences,
            resolvedReferences,
            invalidReferences,
            nonReciprocalReferences,
            materializeMilliseconds,
            mapMilliseconds,
            scanMilliseconds);
    }

    private static PackedEdgeSummary BuildAndSortPackedEdgeArrays(int[] faces, int faceCount)
    {
        var phase = Stopwatch.StartNew();
        int edgeReferenceCount = checked(faceCount * 3);
        var keys = new long[edgeReferenceCount];
        var incidentFaces = new int[edgeReferenceCount];
        for (int face = 0; face < faceCount; face++)
        {
            int a = faces[face * 3];
            int b = faces[face * 3 + 1];
            int c = faces[face * 3 + 2];
            keys[face * 3] = IndexedMeshTools.GetEdgeKey(a, b);
            keys[face * 3 + 1] = IndexedMeshTools.GetEdgeKey(b, c);
            keys[face * 3 + 2] = IndexedMeshTools.GetEdgeKey(c, a);
            incidentFaces[face * 3] = face;
            incidentFaces[face * 3 + 1] = face;
            incidentFaces[face * 3 + 2] = face;
        }
        double fillMilliseconds = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        Array.Sort(keys, incidentFaces);
        double sortMilliseconds = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        int uniqueEdges = 0;
        int nakedEdges = 0;
        int nonManifoldEdges = 0;
        long checksum = 0;
        int index = 0;
        while (index < edgeReferenceCount)
        {
            long key = keys[index];
            int runLength = 1;
            while (index + runLength < edgeReferenceCount && keys[index + runLength] == key)
                runLength++;

            uniqueEdges++;
            if (runLength == 1)
                nakedEdges++;
            else if (runLength > 2)
                nonManifoldEdges++;
            checksum = unchecked((checksum * 397) ^ incidentFaces[index]);
            index += runLength;
        }
        double scanMilliseconds = phase.Elapsed.TotalMilliseconds;

        GC.KeepAlive(keys);
        GC.KeepAlive(incidentFaces);
        GC.KeepAlive(checksum);
        return new PackedEdgeSummary(
            uniqueEdges,
            nakedEdges,
            nonManifoldEdges,
            fillMilliseconds,
            sortMilliseconds,
            scanMilliseconds);
    }

    private static void ForceFullCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static long CurrentPrivateBytes()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private readonly record struct NativeAdjacencySummary(
        int FaceCount,
        int MaxTriangleId,
        int BoundaryReferences,
        int ResolvedNeighborReferences,
        int InvalidNeighborReferences,
        int NonReciprocalReferences,
        double MaterializeMilliseconds,
        double MapMilliseconds,
        double ScanMilliseconds);

    private readonly record struct PackedEdgeSummary(
        int UniqueEdgeCount,
        int NakedEdgeCount,
        int NonManifoldEdgeCount,
        double FillMilliseconds,
        double SortMilliseconds,
        double ScanMilliseconds);
}
