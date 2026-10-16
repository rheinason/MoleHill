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
        if (picked == GetResult.Option && getMesh.OptionIndex() == activeTerrainOption)
        {
            TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
            mesh = terrain == null ? null : TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
            if (mesh == null)
            {
                RhinoApp.WriteLine("Select and build an active MoleHill terrain first.");
                return Result.Nothing;
            }
        }
        else if (picked == GetResult.Object && getMesh.Object(0).Mesh() is { } selected)
        {
            mesh = selected.DuplicateMesh();
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
                GetResult result = options.Get();
                tolerance = toleranceOption.CurrentValue;
                planTolerance = planOption.CurrentValue;
                maxCurves = budgetOption.CurrentValue;
                if (result == GetResult.Option)
                    continue;
                if (result != GetResult.Nothing)
                    return options.CommandResult();
                break;
            }

            CommandOptionCache.SetLength(toleranceKey, unitContext, tolerance);
            CommandOptionCache.SetLength(planKey, unitContext, planTolerance);
            CommandOptionCache.SetValue("MoleHill.TerrainToCurves.MaxCurves", maxCurves);

            if (!RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh terrainMesh, out string? meshError))
            {
                RhinoApp.WriteLine(meshError ?? "The mesh could not be read.");
                return Result.Failure;
            }

            bool cancelled = false;
            void OnEscape(object? sender, EventArgs e) => cancelled = true;
            RhinoApp.EscapeKeyPressed += OnEscape;
            TerrainCurveRecovery.Result recovery;
            try
            {
                recovery = TerrainCurveRecovery.Recover(terrainMesh, new TerrainCurveRecovery.Options
                {
                    VerticalTolerance = tolerance,
                    PlanTolerance = planTolerance,
                    MaximumCurves = maxCurves,
                    ShouldCancel = () => cancelled,
                    ReportProgress = (round, worst) =>
                    {
                        RhinoApp.SetCommandPrompt(
                            $"Round {round}: worst miss {worst.ToString("G4", CultureInfo.InvariantCulture)} (Esc stops and keeps the curves so far)");
                        RhinoApp.Wait();
                    }
                });
            }
            finally
            {
                RhinoApp.EscapeKeyPressed -= OnEscape;
            }

            if (recovery.Termination == TerrainCurveRecovery.Termination.UnsupportedInput)
            {
                RhinoApp.WriteLine(recovery.Diagnostic);
                return Result.Failure;
            }

            int added = AddOutput(doc, recovery);
            RhinoApp.WriteLine(
                $"Terrain to curves: {recovery.Borders.Count} border(s), {recovery.Contours.Count} contour(s), " +
                $"{recovery.SpotPointCount} spot point(s) from {terrainMesh.VertexCount:N0} vertices; worst miss " +
                $"{recovery.MaximumDeviation.ToString("G4", CultureInfo.InvariantCulture)} at a terrain vertex. " +
                recovery.Diagnostic);
            doc.Views.Redraw();
            return added > 0 ? Result.Success : Result.Failure;
        }
    }

    /// <summary>Adds the curves and points to the current layer, named by role, grouped, in one undo step.</summary>
    private static int AddOutput(RhinoDoc doc, TerrainCurveRecovery.Result recovery)
    {
        var ids = new List<Guid>();
        uint undoRecord = doc.BeginUndoRecord("Terrain to Curves");
        try
        {
            foreach (double[] border in recovery.Borders)
                ids.Add(doc.Objects.AddPolyline(ToPoints(border, close: true), new ObjectAttributes { Name = "Border" }));

            foreach (TerrainCurveRecovery.RecoveredContour contour in recovery.Contours)
            {
                string name = $"Contour {contour.Level.ToString("F3", CultureInfo.InvariantCulture)}";
                ids.Add(doc.Objects.AddPolyline(ToPoints(contour.PointsXyz, contour.IsClosed), new ObjectAttributes { Name = name }));
            }

            for (int i = 0; i < recovery.SpotPoints.Length; i += 3)
            {
                var point = new Point3d(recovery.SpotPoints[i], recovery.SpotPoints[i + 1], recovery.SpotPoints[i + 2]);
                ids.Add(doc.Objects.AddPoint(point, new ObjectAttributes { Name = "Spot" }));
            }

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
