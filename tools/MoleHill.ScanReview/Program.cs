using MoleHill.Core.Scan;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
if (args.Length < 3) throw new ArgumentException("Usage: MoleHill.ScanReview <scan.obj> <texture.rgb> <output-directory> [--no-recovery] [--legacy-recovery] [--max-inputs=N] [--interval=0.5] [--accuracy=0.25]");
double accuracy = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--accuracy="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedAccuracy) ? requestedAccuracy : .25;
double interval = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--interval="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedInterval) ? requestedInterval : .5;
double? neck = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--neck="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedNeck) ? requestedNeck > 0 ? requestedNeck : null : 3;
// The command's guard is a density; --max-inputs=N replaces it with a fixed count, as before.
int inputCap = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--max-inputs="))?.Split('=')[1], out int requestedCap) ? requestedCap : 2000;
double inputsPerHectare = args.Any(a => a.StartsWith("--max-inputs=")) ? 0
    : double.TryParse(args.FirstOrDefault(a => a.StartsWith("--inputs-per-ha="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedDensity) ? requestedDensity : 500;
bool withBuildings = args.Contains("--buildings");
double smoothShare = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--smooth-share="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedSmooth) ? requestedSmooth : .45;
double raisedShare = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--raised-share="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedShare) ? requestedShare : .5;
double planar = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--planar="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedPlanar) ? requestedPlanar : .65;
double turfPlanar = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--turf-planar="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedTurf) ? requestedTurf : .85;
double partDrop = double.TryParse(args.FirstOrDefault(a => a.StartsWith("--part-drop="))?.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedDrop) ? requestedDrop : 1.5;
int roofFaces = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--roof-faces="))?.Split('=')[1], out int requestedFaces) ? requestedFaces : 12;
if (inputCap < 1) throw new ArgumentOutOfRangeException(nameof(args), "Maximum inputs must be positive.");
var folder = args[2]; Directory.CreateDirectory(folder);
var timer = Stopwatch.StartNew();
var vertices = new List<double>(); var faces = new List<int>(); var uv = new List<float>(); var texels = new List<(float U, float V)>();
foreach (string line in File.ReadLines(args[0]))
{
    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); if (p.Length == 0) continue;
    if (p[0] == "v") for (int k = 1; k <= 3; k++) vertices.Add(double.Parse(p[k], CultureInfo.InvariantCulture));
    if (p[0] == "vt") texels.Add((float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture)));
    if (p[0] == "f") for (int k = 2; k < p.Length - 1; k++) foreach (int j in new[] { 1, k, k + 1 }) { var a = p[j].Split('/'); int v = int.Parse(a[0], CultureInfo.InvariantCulture); faces.Add(v > 0 ? v - 1 : vertices.Count / 3 + v); if (a.Length < 2 || a[1].Length == 0) throw new InvalidDataException("Textured OBJ needs face-corner UV coordinates."); int ti = int.Parse(a[1], CultureInfo.InvariantCulture); var t = texels[ti > 0 ? ti - 1 : texels.Count + ti]; uv.Add(t.U); uv.Add(t.V); }
}
Console.WriteLine($"Parsed {vertices.Count / 3} vertices, {faces.Count / 3} triangles");
var mesh = IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());
using var bitmap = new BinaryReader(File.OpenRead(args[1]));
int w = bitmap.ReadInt32(), h = bitmap.ReadInt32(); var colours = new ScanColourSource(new ScanTexture(w, h, bitmap.ReadBytes(w * h * 3)), uv.ToArray());
var result = ScanTerrainPipeline.Build(mesh, colours, new()
{
    Ground = new() { SmallestObject = 2, LargestObject = 48, InitialThreshold = .3, MaximumThreshold = 4, Margin = .5, MinimumObjectArea = 20, PartDrop = partDrop, RaisedPerimeterShare = raisedShare, SmoothRaisedPerimeterShare = smoothShare },
    GroundSpacing = 1,
    PlanTolerance = .5,
    Walls = new() { MinimumHeight = .6, MinimumLength = 3, PlanTolerance = .25, VerticalTolerance = .1, BorderMargin = 2, MaximumWidth = 1 },
    Areas = new() { MaximumPavedRoughness = .1, MinimumArea = 10, PlanTolerance = .5, BuiltEdgePlanTolerance = .25, VerticalTolerance = .25, MaximumFilledObjectHeight = 2.5, SoftNeckWidth = neck },
    Roads = new() { MinimumKerbHeight = .06, MaximumKerbHeight = .3, ProfileReach = 1, MaximumKerbWidth = .6, MinimumLength = 2, PlanTolerance = .25, VerticalTolerance = .015, RoadVerticalTolerance = .2 },
    MaximumBridgeLength = 1,
    NoiseFeatureSize = .5,
    NoiseHeightAllowance = .1,
    RoughGroundHeight = .2,
    StandingObjectWidth = 3,
    StandingObjectHeight = .4,
    StandingObjectMaximumHeight = 2.5,
    // The native command's defaults: 20 m2, 2 m, roof tolerance 0.3 m, 2 m roof detail, 12 roof faces.
    Buildings = withBuildings ? new() { MinimumArea = 20, MinimumHeight = 2, FootprintTolerance = .5, RoofTolerance = .3, MinimumRoofDetail = 2, MaximumRoofFaces = roofFaces, MinimumPlanarShare = planar, MinimumTurfPlanarShare = turfPlanar } : null,
    ReportProgress = stage => Console.WriteLine($"{timer.Elapsed.TotalSeconds:F1}s {stage}")
});
var grid = result.Ground.Ground;
object Curve(ScanCurveGeometry c) => new { c.ControlPointCount, c.PointsXyz, c.PlanTolerance, c.VerticalTolerance, c.ObservedShare, c.InferredIntervals, Pieces = c.Pieces.Select(p => new { p.Degree, p.Knots, p.ControlPoints, p.Weights }) };
File.WriteAllText(Path.Combine(folder, "terrain.json"), JsonSerializer.Serialize(new
{
    grid.OriginX,
    grid.OriginY,
    grid.CellSize,
    grid.Columns,
    grid.Rows,
    result.RejectedFeatureCount,
    result.InferredFeatureLength,
    result.FilteredNodeCount,
    result.FilterMaximumDisplacement,
    Walls = result.Walls.Select(x => new { x.Length, x.MeanHeight, x.MinimumHeight, x.MaximumHeight, Top = Curve(x.TopCurve), Toe = Curve(x.ToeCurve) }),
    Roads = result.Roads.Select(x => new { x.IsKerb, x.IsClosed, x.MeanStep, x.MeasuredShare, Bottom = Curve(x.BottomCurve), Top = x.TopCurve == null ? null : Curve(x.TopCurve) }),
    Areas = result.Areas!.Boundaries.Select(x => new { x.Kind, x.IsSharedDuplicate, x.Points, Curve = Curve(x.Curve) })
}));
using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "ground.bin"))))
{
    // Ground grid for offline review: header, then values (float32), ground flags and domain flags (bytes).
    writer.Write(grid.Columns); writer.Write(grid.Rows); writer.Write(grid.OriginX); writer.Write(grid.OriginY); writer.Write(grid.CellSize);
    foreach (double v in grid.Values) writer.Write((float)v);
    foreach (bool b in result.Ground.IsGround) writer.Write(b);
    foreach (bool b in result.Ground.Domain) writer.Write(b);
}
using (var stream = File.Create(Path.Combine(folder, "orthophoto.ppm"))) { stream.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{grid.Columns} {grid.Rows}\n255\n")); for (int row = grid.Rows - 1; row >= 0; row--) for (int col = 0; col < grid.Columns; col++) { int rgb = result.SurfaceColours![row * grid.Columns + col]; stream.WriteByte(rgb < 0 ? (byte)255 : (byte)(rgb >> 16)); stream.WriteByte(rgb < 0 ? (byte)255 : (byte)(rgb >> 8)); stream.WriteByte(rgb < 0 ? (byte)255 : (byte)rgb); } }
if (result.Buildings is ScanBuildings.Result buildings)
{
    // One record per building, and every solid in one OBJ (a group per building, in the same order).
    File.WriteAllText(Path.Combine(folder, "buildings.json"), JsonSerializer.Serialize(buildings.Buildings.Select((b, i) => new
    {
        Index = i + 1, Kind = b.RoofKind.ToString(), b.IsFallback, b.IsApproximateRoof, b.RoofRms, b.RoofWithinToleranceShare,
        b.RoofSampleCoverage, b.RoofPlaneCount, Faces = b.PlanarFaces.Count, RoofFacets = b.PlanarFaces.Count(face => face.Loops.Sum(loop =>
            Enumerable.Range(0, loop.Length / 3).Sum(k => loop[k * 3] * loop[(k * 3 + 4) % loop.Length] - loop[(k * 3 + 3) % loop.Length] * loop[k * 3 + 1])) > 1e-6), b.FootprintArea, b.GroundHeight, b.EaveHeight, b.RidgeHeight,
        CentreX = b.Footprints[0].Where((_, k) => k % 3 == 0).Average(), CentreY = b.Footprints[0].Where((_, k) => k % 3 == 1).Average(),
        b.Diagnostic
    }), new JsonSerializerOptions { WriteIndented = true }));
    using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "surface.bin"))))
    {
        // The scan's top surface on the ground grid (same header as ground.bin), to compare roofs against.
        ScanGrid top = result.Ground.Surface;
        writer.Write(top.Columns); writer.Write(top.Rows); writer.Write(top.OriginX); writer.Write(top.OriginY); writer.Write(top.CellSize);
        foreach (double v in top.Values) writer.Write((float)v);
    }
    // The planar faces a host joins into each building's closed solid, as flat XYZ loops.
    File.WriteAllText(Path.Combine(folder, "building-faces.json"), JsonSerializer.Serialize(buildings.Buildings.Select(b => b.PlanarFaces.Select(f => f.Loops))));
    using var obj = new StreamWriter(Path.Combine(folder, "buildings.obj"));
    int offset = 1;
    for (int i = 0; i < buildings.Buildings.Count; i++)
    {
        IndexedTriMesh solid = buildings.Buildings[i].Solid;
        obj.WriteLine($"g building{i + 1}");
        for (int v = 0; v < solid.VertexCount; v++)
            obj.WriteLine(string.Create(CultureInfo.InvariantCulture, $"v {solid.Vertices[v * 3]} {solid.Vertices[v * 3 + 1]} {solid.Vertices[v * 3 + 2]}"));
        for (int f = 0; f < solid.FaceCount; f++)
            obj.WriteLine($"f {solid.Faces[f * 3] + offset} {solid.Faces[f * 3 + 1] + offset} {solid.Faces[f * 3 + 2] + offset}");
        offset += solid.VertexCount;
    }
    Console.WriteLine($"{buildings.CandidateCount} raised candidates: {buildings.RejectedVegetation} rejected as vegetation, {buildings.RejectedGeometry} as geometry");
    foreach (var rejected in buildings.Rejections)
        Console.WriteLine(FormattableString.Invariant($"  rejected at ({rejected.X:F1}, {rejected.Y:F1}), {rejected.Area:F0} m2: {rejected.Reason}"));
    Console.WriteLine($"{buildings.Buildings.Count} buildings, {buildings.Buildings.Count(b => b.IsFallback)} fallbacks, median roof RMS {buildings.Buildings.Select(b => b.RoofRms).OrderBy(r => r).ElementAtOrDefault(buildings.Buildings.Count / 2):F3} m");
}
Console.WriteLine($"{result.Walls.Count} wall pairs; {result.Roads.Count(x => x.IsKerb)} kerb pairs; {result.Roads.Count(x => !x.IsKerb)} colour edges; {result.Areas.Boundaries.Count} areas; cell={grid.CellSize}");
if (result.Filled is bool[] filledNodes)
{
    // Where the reconstruction filled gaps rather than measured (fill.pgm, white = filled, grey = measured).
    int covered = result.Ground.Surface.Values.Count(double.IsFinite), fills = filledNodes.Count(f => f);
    Console.WriteLine($"Gap fill: {fills * 100.0 / Math.Max(1, covered):F1}% of covered nodes");
    using var pgm = File.Create(Path.Combine(folder, "fill.pgm"));
    pgm.Write(System.Text.Encoding.ASCII.GetBytes($"P5 {grid.Columns} {grid.Rows} 255 "));
    for (int row = grid.Rows - 1; row >= 0; row--)
        for (int column = 0; column < grid.Columns; column++)
        {
            int node = row * grid.Columns + column;
            pgm.WriteByte(filledNodes[node] ? (byte)255 : double.IsFinite(result.Ground.Surface.Values[node]) ? (byte)90 : (byte)0);
        }
}
// The breaklines the reference was constrained with and the rebuild keeps.
File.WriteAllText(Path.Combine(folder, "fixed.json"), JsonSerializer.Serialize(result.FixedBreaklines));
using (var reference = new StreamWriter(Path.Combine(folder, "reference.obj")))
{
    // The prepared reference the contours are recovered from.
    IndexedTriMesh prepared = result.Reference;
    for (int v = 0; v < prepared.VertexCount; v++)
        reference.WriteLine(string.Create(CultureInfo.InvariantCulture, $"v {prepared.Vertices[v * 3]} {prepared.Vertices[v * 3 + 1]} {prepared.Vertices[v * 3 + 2]}"));
    for (int f = 0; f < prepared.FaceCount; f++)
        reference.WriteLine($"f {prepared.Faces[f * 3] + 1} {prepared.Faces[f * 3 + 1] + 1} {prepared.Faces[f * 3 + 2] + 1}");
}
if (!args.Contains("--no-recovery"))
{
    var recovery = TerrainCurveRecovery.Recover(result.Reference, new()
    {
        Policy = args.Contains("--legacy-recovery") ? null : new() { PreferredContours = 300, MaximumInputs = inputCap, InputsPerArea = inputsPerHectare / 10000, ContourInterval = interval, ReportActivity = message => Console.WriteLine($"{timer.Elapsed.TotalSeconds:F1}s {message}") },
        ReportProgress = (round, miss) => { if (round % 5 == 0) Console.WriteLine($"Recovery {round}: {miss:F4} m"); },
        VerticalTolerance = accuracy,
        PlanTolerance = .5,
        MaximumCurves = 300,
        LandformLines = false,
        FixedBreaklines = result.FixedBreaklines,
        Region = result.Region,
        SecondaryRegion = result.SecondaryRegion,
        Unmeasured = args.Contains("--measure-fills") ? null : result.Unmeasured,
        SecondaryTolerance = .5,
        FitContours = true
    });
    recovery.OriginalMaximumDeviation = result.OriginalReference is IndexedTriMesh original
        ? TerrainCurveRecovery.MeasureMaximumDeviation(recovery, original, new()
        {
            VerticalTolerance = .25,
            PlanTolerance = .5,
            FixedBreaklines = result.FixedBreaklines,
            Region = result.Region
        }) : null;
    File.WriteAllText(Path.Combine(folder, "contours.json"), JsonSerializer.Serialize(new { recovery.MaximumDeviation, recovery.OriginalMaximumDeviation, recovery.AreaOutsideTolerance, recovery.ExcessErrorVolume, recovery.PrunedInputCount, recovery.RejectedCandidateCount, recovery.UncoveredArea, recovery.UnmeasuredArea, recovery.Misses, Termination = recovery.Termination.ToString(), Contours = recovery.Contours, recovery.SpotPoints }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    foreach (var miss in recovery.Misses.Take(12))
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  miss {miss.Deviation:F2} m ({miss.SignedDeviation:+0.00;-0.00}) at {miss.X:F1}, {miss.Y:F1}: {miss.Area:F1} m2, {(miss.IsSecondary ? "secondary" : "primary")}"));
    Console.WriteLine($"{timer.Elapsed.TotalSeconds:F1}s Contours {recovery.Contours.Count}, spots {recovery.SpotPointCount}, residual {recovery.MaximumDeviation}, paved {recovery.SecondaryMaximumDeviation}, unmeasured {recovery.UnmeasuredArea:F0} m2, {recovery.Termination}: {recovery.Diagnostic}");
}

string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
}
File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new {
    CreatedUtc = DateTime.UtcNow, Obj = Path.GetFullPath(args[0]), ObjSha256 = Hash(args[0]),
    Texture = Path.GetFullPath(args[1]), TextureSha256 = Hash(args[1]),
    CoreModule = typeof(ScanTerrainPipeline).Assembly.ManifestModule.ModuleVersionId,
    Arguments = args, PreferredContours = 300, MaximumInputs = inputsPerHectare > 0 ? (int?)null : inputCap, InputsPerHectare = inputsPerHectare,
    VerticalTolerance = .25, PlanTolerance = .5, BuiltEdgeTolerance = .25,
    MaximumBridgeLength = 1, NoiseFeatureSize = .5, NoiseHeightAllowance = .1,
    Buildings = withBuildings, LandformLines = false
}, new JsonSerializerOptions { WriteIndented = true }));
