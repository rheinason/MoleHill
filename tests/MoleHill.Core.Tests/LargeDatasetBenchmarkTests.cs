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
        var mesh = new GenericMesher().Triangulate(polygon, opts, null);
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
        sw.Restart();
        var topology = IndexedMeshTools.BuildEdgeTopology(extracted.Faces, extracted.FaceCount);
        sw.Stop();
        output.WriteLine($"4. BuildEdgeTopology (1):    {sw.ElapsedMilliseconds}ms  ({topology.EdgeCount:N0} edges, {topology.NakedEdgeCount:N0} naked)");

        // TriangleBoundaryCuller.Cull
        sw.Restart();
        var cullResult = TriangleBoundaryCuller.Cull(
            outVerts, extracted.VertexCount,
            extracted.Faces, extracted.FaceCount,
            merged.XyCoords, merged.Segments,
            0);
        sw.Stop();
        output.WriteLine($"5. BoundaryCuller:           {sw.ElapsedMilliseconds}ms  changed={cullResult.Changed}, faces={cullResult.FaceCount:N0}");

        // BuildEdgeTopology (second call, post-cull)
        sw.Restart();
        var topology2 = IndexedMeshTools.BuildEdgeTopology(cullResult.Faces, cullResult.FaceCount);
        sw.Stop();
        output.WriteLine($"6. BuildEdgeTopology (2):    {sw.ElapsedMilliseconds}ms");

        Assert.True(extracted.FaceCount > 0);
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
            var rawMesh = new GenericMesher().Triangulate(poly, opts, null);
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
}
