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
    public void Measure_ReferenceBuildings()
    {
        string? path = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_OBJ");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("Not measured: set MOLEHILL_SCAN_OBJ to the reference OBJ in metres.");
            return;
        }
        IndexedTriMesh scan = ReadObj(path, out float[] uv);
        ScanColourSource? colours = null;
        string? texture = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_TEXTURE_RGB");
        if (!string.IsNullOrWhiteSpace(texture) && File.Exists(texture))
        {
            using var bitmap = new BinaryReader(File.OpenRead(texture));
            int width = bitmap.ReadInt32(), height = bitmap.ReadInt32();
            colours = new ScanColourSource(new ScanTexture(width, height, bitmap.ReadBytes(checked(width * height * 3))), uv);
        }
        var timer = Stopwatch.StartNew();
        ScanTerrainPipeline.Result result = ScanTerrainPipeline.Build(scan, colours, new()
        {
            Ground = new() { SmallestObject = 2, LargestObject = 48, InitialThreshold = .3, MaximumThreshold = 4, Margin = .5, MinimumObjectArea = 20 },
            GroundSpacing = 1, PlanTolerance = .5,
            Buildings = new() { MinimumArea = 20, MinimumHeight = 2, FootprintTolerance = .5, RoofTolerance = .3 },
            ReportProgress = stage => output.WriteLine($"{timer.Elapsed.TotalSeconds:F2}s: {stage}"),
        });
        ScanBuildings.Result buildings = result.Buildings!;
        string measurement = $"buildings {timer.Elapsed.TotalSeconds:F2}s: {buildings.CandidateCount} candidates; {buildings.Buildings.Count} solids; " +
            $"{buildings.RejectedVegetation} vegetation / {buildings.RejectedGeometry} geometry rejected; " +
            string.Join(", ", buildings.Buildings.GroupBy(building => building.RoofKind).Select(group => $"{group.Key}={group.Count()}"));
        output.WriteLine(measurement);
        string? folder = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_IMAGES");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "buildings.txt"), measurement);
            File.WriteAllText(Path.Combine(folder, "buildings.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                result.Ground.Surface.OriginX, result.Ground.Surface.OriginY, result.Ground.Surface.Columns,
                result.Ground.Surface.Rows, result.Ground.Surface.CellSize,
                Buildings = buildings.Buildings.Select(building => new
                {
                    RoofKind = building.RoofKind.ToString(), building.Footprints, building.GroundHeight,
                    building.EaveHeight, building.RidgeHeight, building.RoofRms, building.RoofWithinToleranceShare,
                    building.RoofSampleCoverage,
                    PlanarFaceCount = building.PlanarFaces.Count,
                    building.PlanarShare, building.FootprintArea, building.RoofPlaneCount,
                    building.Solid.Vertices, building.Solid.Faces, building.Diagnostic,
                }),
            }));
        }
        Assert.NotEmpty(buildings.Buildings);
        Assert.All(buildings.Buildings, building => Assert.False(building.Solid.IsEmpty));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Measure_ReferenceSurfaces()
    {
        string? path = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_OBJ");
        string? texture = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_TEXTURE_RGB");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || string.IsNullOrWhiteSpace(texture) || !File.Exists(texture))
        {
            output.WriteLine("Not measured: set MOLEHILL_SCAN_OBJ and MOLEHILL_SCAN_TEXTURE_RGB (width, height, top-down RGB bytes).");
            return;
        }
        IndexedTriMesh scan = ReadObj(path, out float[] uv);
        using var bitmap = new BinaryReader(File.OpenRead(texture));
        int width = bitmap.ReadInt32(), height = bitmap.ReadInt32();
        var colours = new ScanColourSource(new ScanTexture(width, height, bitmap.ReadBytes(checked(width * height * 3))), uv);
        var timer = Stopwatch.StartNew();
        string? progressFolder = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_IMAGES");
        if (!string.IsNullOrWhiteSpace(progressFolder))
            Directory.CreateDirectory(progressFolder);
        ScanTerrainPipeline.Result result = ScanTerrainPipeline.Build(scan, colours, new()
        {
            ReportProgress = stage =>
            {
                output.WriteLine($"{timer.Elapsed.TotalSeconds:F2}s: {stage}");
                if (!string.IsNullOrWhiteSpace(progressFolder))
                    File.AppendAllText(Path.Combine(progressFolder, "stages.txt"), $"{timer.Elapsed.TotalSeconds:F2}s: {stage}{Environment.NewLine}");
            },
            Ground = new() { SmallestObject = 2, LargestObject = 48, InitialThreshold = 0.3, MaximumThreshold = 4, Margin = 0.5, MinimumObjectArea = 20 },
            GroundSpacing = 1,
            PlanTolerance = 0.5,
            Walls = new() { MinimumHeight = 0.6, MinimumLength = 3, PlanTolerance = 0.25, VerticalTolerance = 0.1, BorderMargin = 2, MaximumWidth = 1 },
            Areas = new() { MaximumPavedRoughness = 0.1, MinimumArea = 2, PlanTolerance = 0.5, VerticalTolerance = 0.05 },
            Roads = new() { MinimumKerbHeight = 0.06, MaximumKerbHeight = 0.3, ProfileReach = 1, MaximumKerbWidth = 0.6, MinimumLength = 2, PlanTolerance = 0.5, VerticalTolerance = 0.015 },
        });
        output.WriteLine($"surfaces pipeline {timer.Elapsed.TotalSeconds:F2}s: paved {result.Areas!.PavedArea:F1} m2, soft {result.Areas.SoftArea:F1} m2; {result.Areas.Boundaries.Count} area loops; {result.Walls.Count} walls; {result.Roads.Count(edge => edge.IsKerb)} kerb stretches, {result.Roads.Count(edge => !edge.IsKerb)} road-edge stretches; {result.Reference.VertexCount} reference vertices");
        string? folder = Environment.GetEnvironmentVariable("MOLEHILL_SCAN_IMAGES");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            Write("orthophoto.ppm", node => result.SurfaceColours![node]);
            Write("surfaces.ppm", node => result.Areas.PavedMask[node] ? 0x808080 : result.Areas.SoftMask[node] ? 0x39843C : 0x000000);
            File.WriteAllText(Path.Combine(folder, "surface-lines.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                Boundaries = result.Areas.Boundaries.Select(boundary => new { boundary.Kind, boundary.Points }),
                Roads = result.Roads.Select(edge => new { edge.IsKerb, edge.Bottom, edge.Top, edge.MeanStep, edge.MeasuredShare }),
            }));
        }
        timer.Restart();
        int budget = int.TryParse(Environment.GetEnvironmentVariable("MOLEHILL_SCAN_MAX_CONTOURS"), out int requested) ? requested : 300;
        TerrainCurveRecovery.Result recovery = TerrainCurveRecovery.Recover(result.Reference, new()
        {
            VerticalTolerance = 0.25, PlanTolerance = 0.5, MaximumCurves = budget, LandformLines = false,
            FixedBreaklines = result.FixedBreaklines, Region = result.Region,
            FitContours = true,
        });
        string measurement = $"soft contours {timer.Elapsed.TotalSeconds:F2}s: {recovery.Contours.Count} contours, {recovery.SpotPointCount} spots, {recovery.MaximumDeviation:G6} miss, {recovery.Termination}";
        output.WriteLine(measurement);
        if (!string.IsNullOrWhiteSpace(folder))
            File.WriteAllText(Path.Combine(folder, "recovery.txt"), measurement);
        Assert.NotEqual(TerrainCurveRecovery.Termination.TriangulationFailed, recovery.Termination);
        Assert.NotEqual(TerrainCurveRecovery.Termination.UnsupportedInput, recovery.Termination);
        Assert.NotEmpty(result.Areas.Boundaries);
        void Write(string name, Func<int, int> colour)
        {
            ScanGrid grid = result.Ground.Ground;
            using var stream = File.Create(Path.Combine(folder!, name));
            stream.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{grid.Columns} {grid.Rows}\n255\n"));
            var row = new byte[grid.Columns * 3];
            for (int r = grid.Rows - 1; r >= 0; r--)
            {
                for (int c = 0; c < grid.Columns; c++)
                {
                    int rgb = colour(r * grid.Columns + c);
                    row[c * 3] = rgb < 0 ? (byte)0 : (byte)(rgb >> 16);
                    row[c * 3 + 1] = rgb < 0 ? (byte)0 : (byte)(rgb >> 8);
                    row[c * 3 + 2] = rgb < 0 ? (byte)0 : (byte)rgb;
                }
                stream.Write(row);
            }
        }
    }

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
        double[][] rails = Array.Empty<double[]>();
        if (Environment.GetEnvironmentVariable("MOLEHILL_SCAN_WALLS") == "1")
        {
            timer.Restart();
            IReadOnlyList<ScanWalls.Wall> walls = ScanWalls.Trace(ground.Ground, new()
            {
                MinimumHeight = 0.6,
                MinimumLength = 3,
                PlanTolerance = ground.Ground.CellSize,
                VerticalTolerance = 0.1,
                BorderMargin = 2,
                MaximumWidth = 1,
            }, mesh);
            rails = walls.SelectMany(wall => new[] { wall.Top, wall.Toe }).ToArray();
            output.WriteLine($"walls {walls.Count}, total length {walls.Sum(wall => wall.Length):F1} m in {timer.Elapsed.TotalSeconds:F2} s");
            IndexedTriMesh? constrained = TerrainCurveRecovery.ConstrainReference(mesh, rails, 0.5, out string? error);
            Assert.True(constrained.HasValue, error);
            mesh = constrained.Value;
            output.WriteLine($"constrained reference {mesh.VertexCount:N0} vertices");
        }

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
            FixedBreaklines = rails,
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
    internal static IndexedTriMesh ReadObj(string path) => ReadObj(path, out _);

    internal static IndexedTriMesh ReadObj(string path, out float[] cornerUv)
    {
        var vertices = new List<double>();
        var faces = new List<int>();
        var corners = new List<int>();
        var texels = new List<(float U, float V)>();
        var faceUv = new List<float>();
        var uvCorners = new List<int>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int k = 1; k <= 3; k++)
                    vertices.Add(double.Parse(parts[k], CultureInfo.InvariantCulture));
            }
            else if (line.StartsWith("vt ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                texels.Add((float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture)));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                corners.Clear();
                uvCorners.Clear();
                for (int k = 1; k < parts.Length; k++)
                {
                    string[] item = parts[k].Split('/');
                    int index = int.Parse(item[0], CultureInfo.InvariantCulture);
                    corners.Add(index > 0 ? index - 1 : (vertices.Count / 3) + index);
                    int uvIndex = item.Length > 1 && item[1].Length > 0 ? int.Parse(item[1], CultureInfo.InvariantCulture) : 0;
                    uvCorners.Add(uvIndex > 0 ? uvIndex - 1 : uvIndex < 0 ? texels.Count + uvIndex : -1);
                }

                for (int k = 1; k + 1 < corners.Count; k++)
                {
                    faces.AddRange(new[] { corners[0], corners[k], corners[k + 1] });
                    foreach (int corner in new[] { 0, k, k + 1 })
                    {
                        int uvIndex = uvCorners[corner];
                        (float u, float v) = uvIndex >= 0 && uvIndex < texels.Count ? texels[uvIndex] : (float.NaN, float.NaN);
                        faceUv.Add(u); faceUv.Add(v);
                    }
                }
            }
        }

        cornerUv = faceUv.ToArray();
        return IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());
    }
}
