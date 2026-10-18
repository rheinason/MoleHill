using System.Diagnostics;
using System.Globalization;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Runs the scan-to-terrain phase 1 pipeline on a real scan and prints what it found and how long each stage
/// took, for the figures in docs/scan-to-terrain-plan-2026-10-18.md. The scans are not in the repository;
/// opt in with <c>MOLEHILL_SCAN_OBJ</c> set to a Z-up OBJ in metres.
/// </summary>
public class ScanGroundMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Measure_ReferenceScan()
    {
        string? path = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_OBJ");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("Skipped: set MOLEHILL_SCAN_OBJ to a scan OBJ.");
            return;
        }

        var timer = Stopwatch.StartNew();
        IndexedTriMesh scan = ReadObj(path);
        output.WriteLine($"read {scan.VertexCount:N0} vertices, {scan.FaceCount:N0} faces in {timer.Elapsed.TotalSeconds:F1} s");

        timer.Restart();
        double cell = ScanRasterizer.AutomaticCellSize(scan);
        ScanGrid surface = ScanRasterizer.Rasterize(scan, cell).Top;
        output.WriteLine($"raster {surface.Columns} x {surface.Rows} at {cell:F3} in {timer.Elapsed.TotalSeconds:F2} s");

        timer.Restart();
        ScanGround.Result ground = ScanGround.FromSurface(surface, new ScanGround.Options
        {
            SmallestObject = 2.0,
            LargestObject = 48.0,
            InitialThreshold = 0.3,
            MaximumThreshold = 4.0,
            Margin = 0.5,
            MinimumObjectArea = 20.0,
        });
        output.WriteLine(
            $"ground in {timer.Elapsed.TotalSeconds:F2} s: {ground.ObjectCount} objects >= 20 m2, {ground.StepCount} steps kept as ground, " +
            $"domain {ground.DomainArea:N0} m2, filled {ground.FilledShare:P1}");

        string? images = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_IMAGES");
        if (!string.IsNullOrWhiteSpace(images))
            WriteImages(images, ground);

        timer.Restart();
        IndexedTriMesh mesh = ScanGroundMesher.Mesh(ground.Ground, 1.0);
        output.WriteLine($"mesh {mesh.VertexCount:N0} vertices in {timer.Elapsed.TotalSeconds:F2} s");

        timer.Restart();
        int budget = int.TryParse(Environment.GetEnvironmentVariable("MOLEHILL_SCAN_MAX_CONTOURS"), out int requestedBudget)
            ? requestedBudget : 300;
        double planTolerance = double.TryParse(Environment.GetEnvironmentVariable("MOLEHILL_SCAN_PLAN_TOLERANCE"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedPlanTolerance) ? requestedPlanTolerance : 0.5;
        TerrainCurveRecovery.Result curves = TerrainCurveRecovery.Recover(mesh, new TerrainCurveRecovery.Options
        {
            VerticalTolerance = 0.25,
            PlanTolerance = planTolerance,
            LandformLines = false,
            MaximumCurves = budget,
        });
        output.WriteLine(
            $"curves in {timer.Elapsed.TotalSeconds:F1} s: {curves.Contours.Count} contours, {curves.SpotPointCount} spots, " +
            $"{curves.Borders.Count} borders, worst {curves.MaximumDeviation:F3}, {curves.Termination}");
        Assert.False(mesh.IsEmpty);
        Assert.NotEmpty(curves.Borders);
        Assert.NotEqual(TerrainCurveRecovery.Termination.UnsupportedInput, curves.Termination);
        Assert.NotEqual(TerrainCurveRecovery.Termination.TriangulationFailed, curves.Termination);
        Assert.True(double.IsFinite(curves.MaximumDeviation));
    }

    /// <summary>
    /// Hillshades of the surface and the ground, and a class map (ground grey, filled red, outside black), as
    /// binary PPMs, north up, into <c>MOLEHILL_SCAN_IMAGES</c>.
    /// </summary>
    private static void WriteImages(string folder, ScanGround.Result ground)
    {
        Directory.CreateDirectory(folder);
        ScanGrid surface = ground.Surface;
        int columns = surface.Columns, rows = surface.Rows;
        Write("surface.ppm", node => Shade(surface.Values, node));
        Write("ground.ppm", node => Shade(ground.Ground.Values, node));
        Write("classes.ppm", node =>
        {
            if (!ground.Domain[node])
                return (0, 0, 0);
            byte shade = Shade(surface.Values, node).R;
            return ground.IsGround[node] ? (shade, shade, shade) : ((byte)255, (byte)(shade / 3), (byte)(shade / 3));
        });

        (byte R, byte G, byte B) Shade(double[] values, int node)
        {
            int i = node % columns, j = node / columns;
            if (i == 0 || j == 0 || double.IsNaN(values[node]) || double.IsNaN(values[node - 1]) || double.IsNaN(values[node - columns]))
                return (0, 0, 0);
            double dx = (values[node] - values[node - 1]) / surface.CellSize;
            double dy = (values[node] - values[node - columns]) / surface.CellSize;
            double light = Math.Clamp(0.6 + ((-dx + dy) * 0.35), 0.0, 1.0);
            byte b = (byte)(light * 255);
            return (b, b, b);
        }

        void Write(string name, Func<int, (byte R, byte G, byte B)> colour)
        {
            using var stream = File.Create(Path.Combine(folder, name));
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{columns} {rows}\n255\n");
            stream.Write(header);
            var row = new byte[columns * 3];
            for (int j = rows - 1; j >= 0; j--)
            {
                for (int i = 0; i < columns; i++)
                {
                    (byte r, byte g, byte b) = colour((j * columns) + i);
                    row[i * 3] = r;
                    row[(i * 3) + 1] = g;
                    row[(i * 3) + 2] = b;
                }

                stream.Write(row);
            }
        }
    }

    /// <summary>Vertices and faces of an OBJ, polygons fanned into triangles; texture and normals ignored.</summary>
    internal static IndexedTriMesh ReadObj(string path)
    {
        var vertices = new List<double>();
        var faces = new List<int>();
        var corners = new List<int>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int k = 1; k <= 3; k++)
                    vertices.Add(double.Parse(parts[k], CultureInfo.InvariantCulture));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                corners.Clear();
                for (int k = 1; k < parts.Length; k++)
                {
                    int index = int.Parse(parts[k].Split('/')[0], CultureInfo.InvariantCulture);
                    corners.Add(index > 0 ? index - 1 : (vertices.Count / 3) + index);
                }

                for (int k = 1; k + 1 < corners.Count; k++)
                    faces.AddRange(new[] { corners[0], corners[k], corners[k + 1] });
            }
        }

        return IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());
    }
}
