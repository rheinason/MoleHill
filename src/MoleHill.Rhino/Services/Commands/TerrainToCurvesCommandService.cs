// mhTerrainToCurves: recovers a border, spot points and as few contours as rebuild a terrain within tolerance.
using System.Globalization;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Turns a dense terrain — a DEM, a point-cloud mesh, or the active MoleHill terrain — into editable input:
/// border curves, spot points on peaks and pits, and the fewest contours that rebuild it within a vertical
/// tolerance (<see cref="TerrainCurveRecovery"/>). The output is ordinary Rhino geometry, ready to assign as
/// a new terrain's sources and edit from there. Esc stops the search and keeps what it has found.
/// </summary>
internal static class TerrainToCurvesCommandService
{
    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        var getMesh = new GetObject();
        getMesh.SetCommandPrompt("Select a terrain mesh to turn into curves");
        getMesh.GeometryFilter = ObjectType.Mesh;
        getMesh.EnablePreSelect(true, true);
        int activeTerrainOption = getMesh.AddOption("ActiveMoleHillTerrain");
        GetResult picked = getMesh.Get();
        Mesh? mesh;
        string sourceName;
        if (picked == GetResult.Option && getMesh.OptionIndex() == activeTerrainOption)
        {
            TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
            mesh = terrain == null ? null : TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
            if (mesh == null)
            {
                RhinoApp.WriteLine("Select and build an active MoleHill terrain first.");
                return Result.Nothing;
            }

            sourceName = terrain!.Name;
        }
        else if (picked == GetResult.Object && getMesh.Object(0).Mesh() is { } selected)
        {
            mesh = selected.DuplicateMesh();
            string? objectName = getMesh.Object(0).Object()?.Attributes.Name;
            sourceName = string.IsNullOrWhiteSpace(objectName) ? "mesh" : objectName;
        }
        else
        {
            return getMesh.CommandResult();
        }

        using (mesh)
        {
            const string toleranceKey = "MoleHill.TerrainToCurves.Tolerance";
            const string planKey = "MoleHill.TerrainToCurves.PlanTolerance";
            double tolerance = CommandOptionCache.GetLength(toleranceKey, unitContext, 0.1);
            double planTolerance = CommandOptionCache.GetLength(planKey, unitContext, 0.25);
            int maxCurves = CommandOptionCache.GetValue("MoleHill.TerrainToCurves.MaxCurves", 200);
            int linesIndex = CommandOptionCache.GetValue("MoleHill.TerrainToCurves.RidgesValleys", 0);
            while (true)
            {
                var options = new GetOption();
                options.SetCommandPrompt("Terrain to curves options. Press Enter to start");
                options.AcceptNothing(true);
                var toleranceOption = new OptionDouble(tolerance, doc.ModelAbsoluteTolerance, 1e9);
                var planOption = new OptionDouble(planTolerance, doc.ModelAbsoluteTolerance, 1e9);
                var budgetOption = new OptionInteger(maxCurves, 1, 100000);
                options.AddOptionDouble("Tolerance", ref toleranceOption);
                options.AddOptionDouble("PlanTolerance", ref planOption);
                options.AddOptionInteger("MaxContours", ref budgetOption);
                int linesOption = options.AddOptionList("RidgesValleys", LandformLabels, Math.Clamp(linesIndex, 0, 2));
                GetResult result = options.Get();
                tolerance = toleranceOption.CurrentValue;
                planTolerance = planOption.CurrentValue;
                maxCurves = budgetOption.CurrentValue;
                if (result == GetResult.Option)
                {
                    if (options.OptionIndex() == linesOption)
                        linesIndex = options.Option().CurrentListOptionIndex;
                    continue;
                }
                if (result != GetResult.Nothing)
                    return options.CommandResult();
                break;
            }

            CommandOptionCache.SetLength(toleranceKey, unitContext, tolerance);
            CommandOptionCache.SetLength(planKey, unitContext, planTolerance);
            CommandOptionCache.SetValue("MoleHill.TerrainToCurves.MaxCurves", maxCurves);
            CommandOptionCache.SetValue("MoleHill.TerrainToCurves.RidgesValleys", linesIndex);

            if (!RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh terrainMesh, out string? meshError))
            {
                RhinoApp.WriteLine(meshError ?? "The mesh could not be read.");
                return Result.Failure;
            }

            TerrainCurveRecovery.Result recovery = RecoverWithEscape(terrainMesh, tolerance, planTolerance, maxCurves, linesIndex, string.Empty);
            if (recovery.Termination == TerrainCurveRecovery.Termination.UnsupportedInput)
            {
                RhinoApp.WriteLine(recovery.Diagnostic);
                return Result.Failure;
            }

            int added = AddOutput(doc, recovery, $"Terrain to Curves {sourceName}", "Terrain to Curves", null, out string root);
            RhinoApp.WriteLine(
                $"Terrain to curves: {recovery.Borders.Count} border(s), {recovery.LandformLines.Count} ridge/valley line(s), {recovery.Contours.Count} contour(s), " +
                $"{recovery.SpotPointCount} spot point(s) from {terrainMesh.VertexCount:N0} vertices; worst miss " +
                $"{recovery.MaximumDeviation.ToString("G4", CultureInfo.InvariantCulture)} at a terrain vertex. " +
                recovery.Diagnostic);
            if (added > 0)
                RhinoApp.WriteLine($"Added to the layers under '{root}'.");
            doc.Views.Redraw();
            return added > 0 ? Result.Success : Result.Failure;
        }
    }

    internal static readonly string[] LandformLabels = { "Auto", "Yes", "No" };

    /// <summary>
    /// Runs the recovery with Esc wired to stop it (keeping what it found), reporting each round on the
    /// command line. <paramref name="linesIndex"/> picks ridge and valley lines from
    /// <see cref="LandformLabels"/>: Auto runs with and without them and keeps whichever needs fewer curves,
    /// since lines save contours on creased ground and rarely on rough natural ground.
    /// </summary>
    internal static TerrainCurveRecovery.Result RecoverWithEscape(
        IndexedTriMesh terrainMesh, double tolerance, double planTolerance, int maxCurves, int linesIndex, string stage,
        IReadOnlyList<double[]>? fixedBreaklines = null, ITerrainRecoveryRegion? region = null, bool fitContours = false)
    {
        bool cancelled = false;
        void OnEscape(object? sender, EventArgs e) => cancelled = true;
        RhinoApp.EscapeKeyPressed += OnEscape;
        try
        {
            TerrainCurveRecovery.Result Run(bool landformLines, string pass) =>
                TerrainCurveRecovery.Recover(terrainMesh, new TerrainCurveRecovery.Options
                {
                    VerticalTolerance = tolerance,
                    PlanTolerance = planTolerance,
                    MaximumCurves = maxCurves,
                    LandformLines = landformLines,
                    FixedBreaklines = fixedBreaklines ?? Array.Empty<double[]>(),
                    Region = region,
                    FitContours = fitContours,
                    ShouldCancel = () => cancelled,
                    ReportProgress = (round, worst) =>
                    {
                        RhinoApp.SetCommandPrompt(
                            $"{stage}{pass}round {round}: worst miss {worst.ToString("G4", CultureInfo.InvariantCulture)} (Esc stops and keeps the curves so far)");
                        RhinoApp.Wait();
                    }
                });

            if (linesIndex != 0)
                return Run(linesIndex == 1, string.Empty);

            TerrainCurveRecovery.Result withLines = Run(true, "With ridges and valleys, ");
            TerrainCurveRecovery.Result withoutLines = cancelled ? withLines : Run(false, "Contours only, ");
            TerrainCurveRecovery.Result better = Better(withLines, withoutLines);
            if (!cancelled)
            {
                RhinoApp.WriteLine(
                    $"Ridges and valleys: {CurveCount(withLines)} curves with them, {CurveCount(withoutLines)} without; " +
                    (ReferenceEquals(better, withLines) ? "kept them." : "left them out."));
            }

            return better;
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= OnEscape;
        }
    }

    private static int CurveCount(TerrainCurveRecovery.Result result) =>
        result.Contours.Count + result.LandformLines.Count;

    /// <summary>The result within tolerance over one that is not; then fewer curves; then fewer spot points.</summary>
    private static TerrainCurveRecovery.Result Better(TerrainCurveRecovery.Result a, TerrainCurveRecovery.Result b)
    {
        bool aMet = a.Termination == TerrainCurveRecovery.Termination.ToleranceSatisfied;
        bool bMet = b.Termination == TerrainCurveRecovery.Termination.ToleranceSatisfied;
        if (aMet != bMet)
            return aMet ? a : b;
        if (CurveCount(a) != CurveCount(b))
            return CurveCount(a) < CurveCount(b) ? a : b;
        return a.SpotPointCount <= b.SpotPointCount ? a : b;
    }

    /// <summary>
    /// The layers the output goes on, one per kind under a root named after the source: "Terrain to Curves
    /// {source}::Contours" and so on. Each run gets its own root (" 2", " 3" when the name is taken), so a rerun
    /// at another tolerance sits beside the first rather than mixing with it. They went on the current layer,
    /// told apart only by object name, and a user missed the layers (2026-10-18).
    /// </summary>
    internal static readonly (string Name, System.Drawing.Color Color)[] OutputLayers =
    {
        ("Border", System.Drawing.Color.FromArgb(35, 31, 32)),
        ("Ridges and valleys", System.Drawing.Color.FromArgb(178, 34, 34)),
        ("Contours", System.Drawing.Color.FromArgb(150, 100, 40)),
        ("Spot heights", System.Drawing.Color.FromArgb(25, 157, 73)),
    };

    /// <summary>
    /// Adds the curves and points on their layers under a fresh root named <paramref name="rootName"/> (" 2",
    /// " 3" when taken), named by role, grouped, in one undo step. <paramref name="addExtra"/> adds more
    /// objects in the same step: it gets a function from a layer (name and colour) to the attributes for it.
    /// </summary>
    internal static int AddOutput(
        RhinoDoc doc,
        TerrainCurveRecovery.Result recovery,
        string rootName,
        string undoName,
        Func<Func<string, System.Drawing.Color, string, ObjectAttributes>, IEnumerable<Guid>>? addExtra,
        out string root,
        Func<TerrainCurveRecovery.RecoveredContour, Curve>? contourFactory = null)
    {
        root = SurveyLayerNaming.NextFreeRootName(
            rootName.Trim(),
            candidate => doc.Layers.FindByFullPath(candidate, -1) >= 0);
        var ids = new List<Guid>();
        uint undoRecord = doc.BeginUndoRecord(undoName);
        try
        {
            var layers = new Dictionary<string, int>(StringComparer.Ordinal);
            string layerRoot = root;
            ObjectAttributes OnLayer(string kind, System.Drawing.Color color, string name)
            {
                if (!layers.TryGetValue(kind, out int index))
                {
                    index = EnsureLayer(doc, layerRoot, kind, color);
                    layers[kind] = index;
                }

                return new ObjectAttributes { Name = name, LayerIndex = index };
            }

            ObjectAttributes On(string kind, string name) =>
                OnLayer(kind, OutputLayers.First(layer => layer.Name == kind).Color, name);

            foreach (double[] border in recovery.Borders)
                ids.Add(doc.Objects.AddPolyline(ToPoints(border, close: true), On("Border", "Border")));

            foreach (double[] line in recovery.LandformLines)
                ids.Add(doc.Objects.AddPolyline(ToPoints(line, close: false), On("Ridges and valleys", "Ridge or valley")));

            foreach (TerrainCurveRecovery.RecoveredContour contour in recovery.Contours)
            {
                string name = $"Contour {contour.Level.ToString("F3", CultureInfo.InvariantCulture)}";
                if (contourFactory == null)
                    ids.Add(doc.Objects.AddPolyline(ToPoints(contour.PointsXyz, contour.IsClosed), On("Contours", name)));
                else
                {
                    using Curve curve = contourFactory(contour);
                    ids.Add(doc.Objects.AddCurve(curve, On("Contours", name)));
                }
            }

            for (int i = 0; i < recovery.SpotPoints.Length; i += 3)
            {
                var point = new Point3d(recovery.SpotPoints[i], recovery.SpotPoints[i + 1], recovery.SpotPoints[i + 2]);
                ids.Add(doc.Objects.AddPoint(point, On("Spot heights", "Spot")));
            }

            if (addExtra != null)
                ids.AddRange(addExtra(OnLayer));

            ids.RemoveAll(id => id == Guid.Empty);
            if (ids.Count > 0)
                doc.Groups.Add(ids);
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }

        return ids.Count;
    }

    /// <summary>The root layer and the kind's layer under it, created with the kind's colour if missing.</summary>
    private static int EnsureLayer(RhinoDoc doc, string root, string kind, System.Drawing.Color color)
    {
        int parent = doc.Layers.FindByFullPath(root, -1);
        if (parent < 0)
            parent = doc.Layers.Add(new Layer { Name = root });

        string path = $"{root}::{kind}";
        int index = doc.Layers.FindByFullPath(path, -1);
        if (index >= 0)
            return index;

        return doc.Layers.Add(new Layer { Name = kind, ParentLayerId = doc.Layers[parent].Id, Color = color });
    }

    private static List<Point3d> ToPoints(double[] xyz, bool close)
    {
        var points = new List<Point3d>(xyz.Length / 3 + 1);
        for (int i = 0; i < xyz.Length; i += 3)
            points.Add(new Point3d(xyz[i], xyz[i + 1], xyz[i + 2]));
        if (close && points.Count > 2)
            points.Add(points[0]);
        return points;
    }
}
