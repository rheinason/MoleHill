// mhScanToTerrain: the ground under a drone scan, as contours and spot heights with the houses taken out.
using System.Diagnostics;
using System.Globalization;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Phase 1 of scan to terrain (docs/scan-to-terrain-plan-2026-10-18.md): takes a photogrammetry mesh with
/// its houses, cars and trees in, finds the bare ground under it (<see cref="ScanGround"/>), meshes that at
/// a contouring spacing (<see cref="ScanGroundMesher"/>) and recovers the fewest contours and spot heights
/// that rebuild it within a tolerance, exactly as <c>mhTerrainToCurves</c> does for a clean terrain. The
/// output is curves on layers under "Scan {source}"; the ground mesh itself is only added when asked for,
/// to check the filter by.
/// </summary>
internal static class ScanToTerrainCommandService
{
    private const string Prefix = "MoleHill.ScanToTerrain.";

    private static readonly string[] YesNo = { "No", "Yes" };

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        var getMesh = new GetObject();
        getMesh.SetCommandPrompt("Select a scan mesh (a drone or photogrammetry mesh, Z up)");
        getMesh.GeometryFilter = ObjectType.Mesh;
        getMesh.EnablePreSelect(true, true);
        if (getMesh.Get() != GetResult.Object || getMesh.Object(0).Mesh() is not { } picked)
            return getMesh.CommandResult();
        string? objectName = getMesh.Object(0).Object()?.Attributes.Name;
        string sourceName = string.IsNullOrWhiteSpace(objectName) ? "mesh" : objectName;

        double tolerance = CommandOptionCache.GetLength(Prefix + "Tolerance", unitContext, 0.25);
        double planTolerance = CommandOptionCache.GetLength(Prefix + "PlanTolerance", unitContext, 0.5);
        double largestObject = CommandOptionCache.GetLength(Prefix + "LargestObject", unitContext, 48.0);
        double spacing = CommandOptionCache.GetLength(Prefix + "GroundSpacing", unitContext, 1.0);
        double cellSize = CommandOptionCache.GetLength(Prefix + "RasterCell", unitContext, 0.0);
        int maxCurves = CommandOptionCache.GetValue(Prefix + "MaxCurves", 300);
        int linesIndex = CommandOptionCache.GetValue(Prefix + "RidgesValleys", 2);
        int showGround = CommandOptionCache.GetValue(Prefix + "AddGroundMesh", 0);
        int traceWalls = CommandOptionCache.GetValue(Prefix + "Walls", 1);
        double wallHeight = CommandOptionCache.GetLength(Prefix + "WallHeight", unitContext, 0.6);
        double wallLength = CommandOptionCache.GetLength(Prefix + "WallLength", unitContext, 3.0);
        while (true)
        {
            var options = new GetOption();
            options.SetCommandPrompt("Scan to terrain options. Press Enter to start");
            options.AcceptNothing(true);
            var toleranceOption = new OptionDouble(tolerance, doc.ModelAbsoluteTolerance, 1e9);
            var planOption = new OptionDouble(planTolerance, doc.ModelAbsoluteTolerance, 1e9);
            var largestOption = new OptionDouble(largestObject, unitContext.FromMeters(2.0), 1e9);
            var spacingOption = new OptionDouble(spacing, doc.ModelAbsoluteTolerance, 1e9);
            var cellOption = new OptionDouble(cellSize, 0.0, 1e9);
            var budgetOption = new OptionInteger(maxCurves, 1, 100000);
            var wallHeightOption = new OptionDouble(wallHeight, doc.ModelAbsoluteTolerance, 1e9);
            var wallLengthOption = new OptionDouble(wallLength, doc.ModelAbsoluteTolerance, 1e9);
            options.AddOptionDouble("Tolerance", ref toleranceOption, "Largest vertical miss of the contours");
            options.AddOptionDouble("PlanTolerance", ref planOption);
            options.AddOptionDouble("LargestObject", ref largestOption, "Widest thing standing on the ground, wider than the largest building");
            options.AddOptionDouble("GroundSpacing", ref spacingOption, "Spacing of the ground the contours are traced on");
            options.AddOptionDouble("RasterCell", ref cellOption, "0 picks one from the scan's density");
            options.AddOptionInteger("MaxContours", ref budgetOption);
            options.AddOptionDouble("WallHeight", ref wallHeightOption, "Smallest step above the local slope to trace");
            options.AddOptionDouble("WallLength", ref wallLengthOption, "Shortest continuous wall to keep");
            int wallsOption = options.AddOptionList("Walls", YesNo, Math.Clamp(traceWalls, 0, 1));
            int linesOption = options.AddOptionList("RidgesValleys", TerrainToCurvesCommandService.LandformLabels, Math.Clamp(linesIndex, 0, 2));
            int groundOption = options.AddOptionList("AddGroundMesh", YesNo, Math.Clamp(showGround, 0, 1));
            GetResult result = options.Get();
            tolerance = toleranceOption.CurrentValue;
            planTolerance = planOption.CurrentValue;
            largestObject = largestOption.CurrentValue;
            spacing = spacingOption.CurrentValue;
            cellSize = cellOption.CurrentValue;
            maxCurves = budgetOption.CurrentValue;
            wallHeight = wallHeightOption.CurrentValue;
            wallLength = wallLengthOption.CurrentValue;
            if (result == GetResult.Option)
            {
                if (options.OptionIndex() == linesOption)
                    linesIndex = options.Option().CurrentListOptionIndex;
                else if (options.OptionIndex() == groundOption)
                    showGround = options.Option().CurrentListOptionIndex;
                else if (options.OptionIndex() == wallsOption)
                    traceWalls = options.Option().CurrentListOptionIndex;
                continue;
            }

            if (result != GetResult.Nothing)
                return options.CommandResult();
            break;
        }

        CommandOptionCache.SetLength(Prefix + "Tolerance", unitContext, tolerance);
        CommandOptionCache.SetLength(Prefix + "PlanTolerance", unitContext, planTolerance);
        CommandOptionCache.SetLength(Prefix + "LargestObject", unitContext, largestObject);
        CommandOptionCache.SetLength(Prefix + "GroundSpacing", unitContext, spacing);
        CommandOptionCache.SetLength(Prefix + "RasterCell", unitContext, cellSize);
        CommandOptionCache.SetValue(Prefix + "MaxCurves", maxCurves);
        CommandOptionCache.SetValue(Prefix + "RidgesValleys", linesIndex);
        CommandOptionCache.SetValue(Prefix + "AddGroundMesh", showGround);
        CommandOptionCache.SetValue(Prefix + "Walls", traceWalls);
        CommandOptionCache.SetLength(Prefix + "WallHeight", unitContext, wallHeight);
        CommandOptionCache.SetLength(Prefix + "WallLength", unitContext, wallLength);

        RhinoApp.SetCommandPrompt("Reading the scan…");
        RhinoApp.Wait();
        IndexedTriMesh scan;
        using (Mesh copy = picked.DuplicateMesh())
        {
            if (!RhinoGeometryConversions.TryExtractMesh(copy, out scan, out string? meshError))
            {
                RhinoApp.WriteLine(meshError ?? "The mesh could not be read.");
                return Result.Failure;
            }
        }

        RhinoApp.SetCommandPrompt("Finding the ground under the scan. Press Esc to cancel");
        RhinoApp.Wait();
        var timer = Stopwatch.StartNew();
        ScanGround.Result ground;
        IndexedTriMesh groundMesh;
        IReadOnlyList<ScanWalls.Wall> walls;
        using var cancellation = new CancellationTokenSource();
        void OnEscape(object? sender, EventArgs e) => cancellation.Cancel();
        RhinoApp.EscapeKeyPressed += OnEscape;
        try
        {
            var groundOptions = new ScanGround.Options
            {
                CellSize = cellSize,
                SmallestObject = unitContext.FromMeters(2.0),
                LargestObject = largestObject,
                InitialThreshold = unitContext.FromMeters(0.3),
                MaximumThreshold = unitContext.FromMeters(4.0),
                Margin = unitContext.FromMeters(0.5),
                MinimumObjectArea = unitContext.FromSquareMeters(20.0),
                CancellationToken = cancellation.Token,
            };
            var work = Task.Run(() =>
            {
                ScanGround.Result found = ScanGround.Build(scan, groundOptions);
                IndexedTriMesh mesh = ScanGroundMesher.Mesh(found.Ground, spacing);
                IReadOnlyList<ScanWalls.Wall> measured = traceWalls == 1 && !mesh.IsEmpty
                    ? ScanWalls.Trace(found.Ground, new ScanWalls.Options
                    {
                        MinimumHeight = wallHeight,
                        MinimumLength = wallLength,
                        PlanTolerance = Math.Min(planTolerance, found.Ground.CellSize),
                        VerticalTolerance = Math.Min(tolerance, unitContext.FromMeters(0.1)),
                        BorderMargin = spacing * 2,
                        MaximumWidth = unitContext.FromMeters(1.0),
                        CancellationToken = cancellation.Token,
                    }, mesh)
                    : Array.Empty<ScanWalls.Wall>();
                if (measured.Count > 0)
                {
                    double[][] rails = measured.SelectMany(wall => new[] { wall.Top, wall.Toe }).ToArray();
                    IndexedTriMesh? constrained = TerrainCurveRecovery.ConstrainReference(mesh, rails, planTolerance, out string? error);
                    if (constrained == null)
                        throw new InvalidOperationException(error ?? "The measured wall lines could not be triangulated.");
                    mesh = constrained.Value;
                }
                return (found, mesh, measured);
            });
            while (!work.IsCompleted)
            {
                RhinoApp.Wait();
                System.Threading.Thread.Sleep(15);
            }
            (ground, groundMesh, walls) = work.GetAwaiter().GetResult();
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            RhinoApp.WriteLine("Scan to terrain cancelled before any output was added.");
            return Result.Cancel;
        }
        catch (ArgumentOutOfRangeException e)
        {
            RhinoApp.WriteLine($"Scan to terrain: {e.Message} Set a larger RasterCell.");
            return Result.Failure;
        }
        catch (InvalidOperationException e)
        {
            RhinoApp.WriteLine($"Scan to terrain: {e.Message}");
            return Result.Failure;
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= OnEscape;
        }

        if (groundMesh.IsEmpty)
        {
            RhinoApp.WriteLine("Scan to terrain: no ground was found under the scan.");
            return Result.Failure;
        }

        RhinoApp.WriteLine(
            $"Ground found in {timer.Elapsed.TotalSeconds:F1} s at a {Format(ground.Surface.CellSize)} cell: " +
            $"{ground.ObjectCount} objects of 20 m² or more taken out, {ground.FilledShare:P0} of the site filled beneath them; " +
            $"contours traced on {groundMesh.VertexCount:N0} points at about {Format(Math.Max(1, Math.Round(spacing / ground.Surface.CellSize)) * ground.Surface.CellSize)} {unitContext.Abbreviation} spacing.");
        RhinoApp.WriteLine("Contour tolerance is measured against the inferred ground, not surveyed ground beneath removed objects. Ground near terrace steps may need correction.");
        RhinoApp.WriteLine($"Traced {walls.Count} wall/terrace edge(s), each with a measured top and toe.");

        TerrainCurveRecovery.Result recovery = TerrainToCurvesCommandService.RecoverWithEscape(
            groundMesh, tolerance, planTolerance, maxCurves, linesIndex, "Contours, ",
            walls.SelectMany(wall => new[] { wall.Top, wall.Toe }).ToArray());
        if (recovery.Termination == TerrainCurveRecovery.Termination.UnsupportedInput)
        {
            RhinoApp.WriteLine(recovery.Diagnostic);
            return Result.Failure;
        }

        int added = TerrainToCurvesCommandService.AddOutput(
            doc,
            recovery,
            $"Scan {sourceName}",
            "Scan to Terrain",
            onLayer => AddExtras(doc, groundMesh, walls, showGround == 1, onLayer),
            out string root);
        RhinoApp.WriteLine(
            $"Scan to terrain: {recovery.Contours.Count} contour(s), {recovery.LandformLines.Count} ridge/valley line(s), " +
            $"{recovery.SpotPointCount} spot point(s); worst miss {Format(recovery.MaximumDeviation)} against the ground. " +
            recovery.Diagnostic);
        if (added > 0)
            RhinoApp.WriteLine($"Added to the layers under '{root}'.");
        doc.Views.Redraw();
        return added > 0 ? Result.Success : Result.Failure;
    }

    private static Guid AddGroundMesh(
        RhinoDoc doc, IndexedTriMesh groundMesh, Func<string, System.Drawing.Color, string, ObjectAttributes> onLayer)
    {
        using var mesh = new Mesh();
        double[] vertices = groundMesh.Vertices;
        for (int v = 0; v < groundMesh.VertexCount; v++)
            mesh.Vertices.Add(vertices[v * 3], vertices[(v * 3) + 1], vertices[(v * 3) + 2]);
        int[] faces = groundMesh.Faces;
        for (int f = 0; f < groundMesh.FaceCount; f++)
            mesh.Faces.AddFace(faces[f * 3], faces[(f * 3) + 1], faces[(f * 3) + 2]);
        MeshNormalOrientation.UnifyAndComputeNormalsWelded(mesh);
        return doc.Objects.AddMesh(mesh, onLayer("Ground (check)", System.Drawing.Color.FromArgb(160, 160, 160), "Scan ground"));
    }

    private static IEnumerable<Guid> AddExtras(RhinoDoc doc, IndexedTriMesh mesh, IReadOnlyList<ScanWalls.Wall> walls,
        bool showGround, Func<string, System.Drawing.Color, string, ObjectAttributes> onLayer)
    {
        foreach (ScanWalls.Wall wall in walls)
        {
            using Curve top = RouteCurveBuilder.FromFitted(wall.TopCurve);
            using Curve toe = RouteCurveBuilder.FromFitted(wall.ToeCurve);
            ObjectAttributes Attributes(string side)
            {
                ObjectAttributes attributes = onLayer("Walls and edges", System.Drawing.Color.FromArgb(110, 85, 65), $"Wall {side}");
                attributes.SetUserString("MoleHill.Scan.MeanStep", Format(wall.MeanHeight));
                attributes.SetUserString("MoleHill.Scan.MeasuredLength", Format(wall.Length));
                return attributes;
            }
            yield return doc.Objects.AddCurve(top, Attributes("top"));
            yield return doc.Objects.AddCurve(toe, Attributes("toe"));
        }
        if (showGround)
            yield return AddGroundMesh(doc, mesh, onLayer);
    }

    private static string Format(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
}
