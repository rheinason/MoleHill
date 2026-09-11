using System.Diagnostics;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Preliminary synthetic baseline for <see cref="MeshAreaTopologySplitter"/>, walking the review's independent
/// scale axes so unrelated growth cannot hide complexity: terrain faces at a fixed boundary, boundary
/// segments at a fixed terrain, zone-piece count at a fixed result, and the spatial distributions that
/// stress cell membership.
/// </summary>
/// <remarks>
/// Opt in with <c>MOLEHILL_PERF=1</c>; otherwise every case returns immediately, as the other
/// performance suites do. Reported per case: per-phase elapsed and process-wide allocation, touched-face
/// ratio, output growth, and the uncollected managed heap delta from a collected starting state.
/// Each case is a single observation, not a warmed median or peak/retained-memory measurement.
/// Process-wide allocation is used rather than current-thread, because the phases parallelise.
/// </remarks>
public class MeshAreaTopologySplitterScalingBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Split_TerrainFaceScaling_AtAFixedBoundary()
    {
        if (!Enabled("terrain face scaling"))
            return;

        foreach (int side in new[] { 224, 448, 708 })
        {
            Run($"faces side={side}", side, BoundaryCount(1, side), boundaryVertices: 64);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Split_BoundarySegmentScaling_AtAFixedTerrain()
    {
        if (!Enabled("boundary segment scaling"))
            return;

        const int side = 448;
        foreach (int boundaryVertices in new[] { 8, 64, 512, 2048 })
        {
            Run($"boundary verts={boundaryVertices}", side, BoundaryCount(1, side), boundaryVertices);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Split_ZonePieceScaling_AtAFixedTerrain()
    {
        if (!Enabled("zone piece scaling"))
            return;

        const int side = 448;
        foreach (int pieces in new[] { 1, 10, 100 })
        {
            Run($"pieces={pieces}", side, BoundaryCount(pieces, side), boundaryVertices: 32);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Split_SpatialDistributions_ThatStressCellMembership()
    {
        if (!Enabled("spatial distribution"))
            return;

        const int side = 448;
        Run("tiny zone in a large terrain", side, new[] { Circle(0.5, 0.5, 0.004, 32) }, boundaryVertices: 32);
        Run("long thin corridor", side, new[] { Rectangle(0.02, 0.45, 0.98, 0.55) }, boundaryVertices: 4);
        Run("diagonal sliver", side, new[] { Diagonal(side) }, boundaryVertices: 3);
        Run("nested rings", side, NestedRings(6), boundaryVertices: 48);
    }

    private void Run(string label, int side, double[][] normalizedLoops, int boundaryVertices)
    {
        BuildGrid(side, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);
        MeshAreaSplitter.AreaBoundary[] areas = ToAreas(normalizedLoops, side, boundaryVertices);

        var timings = new MeshAreaTopologySplitter.PerformanceTimings();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        var stopwatch = Stopwatch.StartNew();
        MeshAreaSplitter.SplitResult? result = MeshAreaSplitter.SplitPreservingTopology(
            vertices, vertexCount, faces, faceCount, areas, 1e-6, out string? warning, timings);
        stopwatch.Stop();

        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        long managedAfter = GC.GetTotalMemory(forceFullCollection: false);

        Assert.NotNull(result);
        output.WriteLine(
            $"[{label}] terrain={vertexCount:N0}v/{faceCount:N0}f  areas={areas.Length}  " +
            $"total={stopwatch.Elapsed.TotalMilliseconds:0.0}ms  " +
            $"allocated={allocatedAfter - allocatedBefore:N0}B  " +
            $"heapDelta={managedAfter - managedBefore:N0}B");
        output.WriteLine(
            $"    phases ms: faceData={timings.FaceDataMilliseconds:0.0} " +
            $"boundary={timings.BoundarySegmentsMilliseconds:0.0} " +
            $"map={timings.FaceMappingMilliseconds:0.0} " +
            $"registry={timings.SharedEdgeRegistryMilliseconds:0.0} " +
            $"setup={timings.OutputSetupMilliseconds:0.0} " +
            $"triangulate={timings.TouchedFaceTriangulationMilliseconds:0.0} " +
            $"classify={timings.ClassificationMilliseconds:0.0}");
        output.WriteLine(
            $"    phases bytes: faceData={timings.FaceDataAllocatedBytes:N0} " +
            $"boundary={timings.BoundarySegmentsAllocatedBytes:N0} " +
            $"map={timings.FaceMappingAllocatedBytes:N0} " +
            $"registry={timings.SharedEdgeRegistryAllocatedBytes:N0} " +
            $"setup={timings.OutputSetupAllocatedBytes:N0} " +
            $"triangulate={timings.TouchedFaceTriangulationAllocatedBytes:N0} " +
            $"classify={timings.ClassificationAllocatedBytes:N0}");
        output.WriteLine(
            $"    touched={timings.TouchedFaceCount:N0} " +
            $"({(faceCount == 0 ? 0.0 : 100.0 * timings.TouchedFaceCount / faceCount):0.00}% of faces) " +
            $"registryOnly={timings.RegistryOnlyFaceCount:N0} " +
            $"internal 0/1/many={timings.ZeroInternalSegmentFaceCount:N0}/" +
            $"{timings.OneInternalSegmentFaceCount:N0}/{timings.MultipleInternalSegmentFaceCount:N0}");
        output.WriteLine(
            $"    output={result!.VertexCount:N0}v/{result.FaceCount:N0}f " +
            $"(growth {(faceCount == 0 ? 0.0 : (double)result.FaceCount / faceCount):0.000}x)" +
            (string.IsNullOrWhiteSpace(warning) ? string.Empty : $"  warning: {warning}"));
    }

    private bool Enabled(string what)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_PERF"), "1", StringComparison.Ordinal))
            return true;

        output.WriteLine($"Set MOLEHILL_PERF=1 to run the zone splitter {what} benchmark.");
        return false;
    }

    /// <summary>Loops in normalized [0,1] terrain coordinates, resampled to the requested vertex count.</summary>
    private static MeshAreaSplitter.AreaBoundary[] ToAreas(double[][] normalizedLoops, int side, int boundaryVertices)
    {
        double extent = side - 1;
        var areas = new MeshAreaSplitter.AreaBoundary[normalizedLoops.Length];
        for (int i = 0; i < normalizedLoops.Length; i++)
        {
            double[] loop = Resample(normalizedLoops[i], boundaryVertices);
            var scaled = new double[loop.Length];
            for (int v = 0; v < loop.Length / 2; v++)
            {
                scaled[v * 2] = loop[v * 2] * extent;
                scaled[(v * 2) + 1] = loop[(v * 2) + 1] * extent;
            }

            areas[i] = new MeshAreaSplitter.AreaBoundary(scaled, scaled.Length / 2);
        }

        return areas;
    }

    /// <summary>Walks the loop's perimeter at even arc length, so segment count is the axis being varied.</summary>
    private static double[] Resample(double[] loop, int targetCount)
    {
        int count = loop.Length / 2;
        if (targetCount <= count)
            return loop;

        var lengths = new double[count];
        double total = 0.0;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            double dx = loop[next * 2] - loop[i * 2];
            double dy = loop[(next * 2) + 1] - loop[(i * 2) + 1];
            lengths[i] = Math.Sqrt((dx * dx) + (dy * dy));
            total += lengths[i];
        }

        if (total <= 0.0)
            return loop;

        var resampled = new List<double>(targetCount * 2);
        double step = total / targetCount;
        double walked = 0.0;
        int segment = 0;
        double consumed = 0.0;
        for (int i = 0; i < targetCount; i++)
        {
            double target = i * step;
            while (segment < count - 1 && consumed + lengths[segment] < target)
            {
                consumed += lengths[segment];
                segment++;
            }

            double t = lengths[segment] > 0.0 ? (target - consumed) / lengths[segment] : 0.0;
            int next = (segment + 1) % count;
            resampled.Add(loop[segment * 2] + ((loop[next * 2] - loop[segment * 2]) * t));
            resampled.Add(loop[(segment * 2) + 1] + ((loop[(next * 2) + 1] - loop[(segment * 2) + 1]) * t));
            walked = target;
        }

        _ = walked;
        return resampled.ToArray();
    }

    private static double[][] BoundaryCount(int pieces, int side)
    {
        _ = side;
        if (pieces == 1)
            return new[] { Rectangle(0.25, 0.25, 0.75, 0.75) };

        int perSide = (int)Math.Ceiling(Math.Sqrt(pieces));
        var loops = new List<double[]>();
        double cell = 1.0 / perSide;
        for (int j = 0; j < perSide && loops.Count < pieces; j++)
        {
            for (int i = 0; i < perSide && loops.Count < pieces; i++)
            {
                double minX = (i + 0.2) * cell;
                double minY = (j + 0.2) * cell;
                loops.Add(Rectangle(minX, minY, minX + (cell * 0.6), minY + (cell * 0.6)));
            }
        }

        return loops.ToArray();
    }

    private static double[] Rectangle(double minX, double minY, double maxX, double maxY) =>
        new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY };

    private static double[] Circle(double cx, double cy, double radius, int count)
    {
        var loop = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            double angle = (i * 2.0 * Math.PI) / count;
            loop[i * 2] = cx + (Math.Cos(angle) * radius);
            loop[(i * 2) + 1] = cy + (Math.Sin(angle) * radius);
        }

        return loop;
    }

    private static double[] Diagonal(int side)
    {
        double thickness = 1.5 / Math.Max(1, side - 1);
        return new[] { 0.02, 0.02, 0.98, 0.98 - thickness, 0.98, 0.98, 0.02, 0.02 + thickness };
    }

    private static double[][] NestedRings(int count)
    {
        var loops = new double[count][];
        for (int i = 0; i < count; i++)
            loops[i] = Circle(0.5, 0.5, 0.45 - (i * 0.06), 48);

        return loops;
    }

    private static void BuildGrid(int side, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount)
    {
        vertexCount = side * side;
        vertices = new double[vertexCount * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                int v = (j * side) + i;
                vertices[v * 3] = i;
                vertices[(v * 3) + 1] = j;
                vertices[(v * 3) + 2] = (Math.Sin(i * 0.05) * 4.0) + (Math.Cos(j * 0.04) * 3.0);
            }
        }

        faceCount = (side - 1) * (side - 1) * 2;
        faces = new int[faceCount * 3];
        int f = 0;
        for (int j = 0; j < side - 1; j++)
        {
            for (int i = 0; i < side - 1; i++)
            {
                int v00 = (j * side) + i;
                int v10 = v00 + 1;
                int v01 = v00 + side;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }
    }
}
