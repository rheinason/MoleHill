using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Runs a deterministic large-terrain benchmark through the TIN and Rhino wrapper stages.</summary>
internal static class LargeTinDiagnostic
{
    public const int DefaultPointCount = 247_000;
    public const double DefaultWidth = 40_000.0;
    public const double DefaultHeight = 15_000.0;

    public static void Run(Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var total = Stopwatch.StartNew();
        Mesh? mesh = null;
        Mesh? cacheClone = null;
        Mesh? baseClone = null;

        try
        {
            double[] xyz = Measure(
                "Generate deterministic input",
                report,
                () => CreatePoints(DefaultPointCount, DefaultWidth, DefaultHeight));

            var emptyBreaklines = new BreaklineDiscretizer.BreaklineData(
                Array.Empty<double>(),
                0,
                Array.Empty<int>(),
                0);
            PointCloudProcessor.MergedData merged = Measure(
                "Point deduplication",
                report,
                () => PointCloudProcessor.Merge(xyz, DefaultPointCount, emptyBreaklines, 0.0125));

            var engine = new TinEngine();
            TinResult? result = Measure(
                "TIN engine (including default boundary peel)",
                report,
                () => engine.Build(
                    merged.XyCoords,
                    merged.ZValues,
                    merged.Segments,
                    QualitySettings.None,
                    out _,
                    useConvexHull: true,
                    boundaryPeelSettings: BoundaryTrianglePeelSettings.Default,
                    includeEdgeTopology: false));
            if (result == null)
                throw new InvalidOperationException("The synthetic TIN build returned no result.");

            mesh = Measure(
                "Rhino mesh conversion and first normalization",
                report,
                () => RhinoGeometryConversions.ToRhinoMesh(result));

            Measure(
                "Legacy redundant second normalization",
                report,
                () => RhinoGeometryConversions.NormalizeMeshInPlace(mesh));

            _ = TerrainBuildService.ComputeMeshFingerprintForDiagnostics(mesh);

            cacheClone = Measure(
                "Stage-cache mesh duplicate",
                report,
                mesh.DuplicateMesh);

            baseClone = Measure(
                "Base-mesh duplicate",
                report,
                mesh.DuplicateMesh);
            _ = TerrainBuildService.ComputeMeshFingerprintForDiagnostics(baseClone);

            ProcessMemorySnapshot finalMemory = CaptureMemory();
            report($"Diagnostic complete in {total.Elapsed.TotalSeconds:0.###} s; " +
                   $"{mesh.Vertices.Count:N0} vertices, {mesh.Faces.Count:N0} faces; " +
                   $"managed {ToMegabytes(finalMemory.ManagedBytes):0.0} MB; " +
                   $"private {ToMegabytes(finalMemory.PrivateBytes):0.0} MB; " +
                   $"working set {ToMegabytes(finalMemory.WorkingSetBytes):0.0} MB.");
        }
        catch (Exception ex)
        {
            report($"Diagnostic failed after {total.Elapsed.TotalSeconds:0.###} s: {ex}");
        }
        finally
        {
            baseClone?.Dispose();
            cacheClone?.Dispose();
            mesh?.Dispose();
        }
    }

    private static double[] CreatePoints(int pointCount, double width, double height)
    {
        int columns = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(pointCount * width / height)));
        int rows = Math.Max(2, (int)Math.Ceiling(pointCount / (double)columns));
        double dx = width / (columns - 1);
        double dy = height / (rows - 1);
        var random = new Random(0x4D484C);
        var xyz = new double[pointCount * 3];

        for (int index = 0; index < pointCount; index++)
        {
            int column = index % columns;
            int row = index / columns;
            double jitterX = column is 0 || column == columns - 1 ? 0.0 : (random.NextDouble() - 0.5) * dx * 0.35;
            double jitterY = row is 0 || row == rows - 1 ? 0.0 : (random.NextDouble() - 0.5) * dy * 0.35;
            double x = Math.Min(width, column * dx) + jitterX;
            double y = Math.Min(height, row * dy) + jitterY;
            double z = 120.0 * Math.Sin(x / 3500.0) + 75.0 * Math.Cos(y / 2200.0) + random.NextDouble() * 2.0;
            xyz[index * 3] = x;
            xyz[index * 3 + 1] = y;
            xyz[index * 3 + 2] = z;
        }

        return xyz;
    }

    private static T Measure<T>(string name, Action<string> report, Func<T> action)
    {
        ProcessMemorySnapshot before = CaptureMemory();
        report(
            $"{name}: starting; managed {ToMegabytes(before.ManagedBytes):0.0} MB; " +
            $"private {ToMegabytes(before.PrivateBytes):0.0} MB; " +
            $"working set {ToMegabytes(before.WorkingSetBytes):0.0} MB.");
        var timer = Stopwatch.StartNew();
        T result = action();
        timer.Stop();
        ProcessMemorySnapshot after = CaptureMemory();
        report(
            $"{name}: {timer.Elapsed.TotalSeconds:0.###} s; " +
            $"managed {ToMegabytes(after.ManagedBytes):0.0} MB " +
            $"(Δ {ToSignedMegabytes(after.ManagedBytes - before.ManagedBytes)}); " +
            $"private {ToMegabytes(after.PrivateBytes):0.0} MB " +
            $"(Δ {ToSignedMegabytes(after.PrivateBytes - before.PrivateBytes)}); " +
            $"working set {ToMegabytes(after.WorkingSetBytes):0.0} MB " +
            $"(Δ {ToSignedMegabytes(after.WorkingSetBytes - before.WorkingSetBytes)}); " +
            $"allocated Δ {ToMegabytes(after.TotalAllocatedBytes - before.TotalAllocatedBytes):0.0} MB.");
        return result;
    }

    private static void Measure(string name, Action<string> report, Action action)
    {
        Measure(name, report, () =>
        {
            action();
            return true;
        });
    }

    private static ProcessMemorySnapshot CaptureMemory()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessMemorySnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: true),
            process.PrivateMemorySize64,
            process.WorkingSet64);
    }

    private static double ToMegabytes(long bytes) => bytes / (1024.0 * 1024.0);

    private static string ToSignedMegabytes(long bytes) =>
        $"{(bytes >= 0 ? "+" : string.Empty)}{ToMegabytes(bytes):0.0} MB";

    private readonly record struct ProcessMemorySnapshot(
        long ManagedBytes,
        long TotalAllocatedBytes,
        long PrivateBytes,
        long WorkingSetBytes);
}
