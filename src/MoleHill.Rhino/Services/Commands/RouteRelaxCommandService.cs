// The mhRelaxRoute workflow: pick a sketched curve and a terrain, relax the sketch live, keep the curve.
using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Relaxes a route the user sketched: the sketch becomes an elastic band that slides over the terrain
/// towards a route the search would price well (<see cref="RouteRelaxer"/>), live, while the user drags it,
/// pins it and turns the compromise on the floating panel. Keep adds it as a curve with only the control
/// points it needs, ready to edit and assign to Grade Path. Like mhSuggestRoute it produces input and never
/// touches a terrain definition; the sketch is left as it was.
/// </summary>
internal static class RouteRelaxCommandService
{
    /// <summary>Most points a sketch is read at: plenty for any drawn line, and the band resamples it anyway.</summary>
    private const int MaximumSketchPoints = 2000;

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext units))
            return Result.Failure;

        Curve? sketch = PickSketch(doc, out Result sketchResult);
        if (sketch == null)
            return sketchResult;

        Mesh? terrain = RouteTerrainPicker.Pick(doc, "relaxing a route", out Result pickResult);
        if (terrain == null)
            return pickResult;

        using (terrain)
        {
            if (!RhinoGeometryConversions.TryExtractMesh(terrain, out IndexedTriMesh mesh, out string? extractError) || mesh.IsEmpty)
            {
                RhinoApp.WriteLine(extractError ?? "The selected terrain has no usable faces.");
                return Result.Failure;
            }

            RouteSuggestionSettings settings = RouteSuggestionSettings.Load(units);
            var maxGrade = new SlopeCommandOption(
                RouteSuggestionSettings.MaxGradeCacheKey,
                RouteSuggestionSettings.DefaultMaxGrade,
                optionName: "MaxGrade");
            var options = new RouteOptionSet(doc, settings, maxGrade, relax: true);
            double tolerance = Math.Max(doc.ModelAbsoluteTolerance, units.FromMeters(RouteSuggestionSettings.ProfileToleranceMeters));

            List<(double X, double Y)> plan = PlanPoints(sketch);
            string? invalid = settings.Validate(Math.Abs(maxGrade.Ratio));
            if (invalid != null)
            {
                RhinoApp.WriteLine(invalid);
                return Result.Failure;
            }

            (double minX, double minY, double maxX, double maxY) = RouteSuggester.SearchDomain(plan);
            (double cell, _) = settings.ResolveCellSize(maxX - minX, maxY - minY, RouteSuggester.DirectSpan(plan));
            RouteHeightGrid? grid = BuildGrid(mesh, (minX, minY, maxX, maxY), cell);
            if (grid == null)
            {
                RhinoApp.WriteLine("Relax route cancelled.");
                return Result.Cancel;
            }

            RouteRelaxSettings relaxSettings = settings.ToRelaxSettings(Math.Abs(maxGrade.Ratio), tolerance, units);
            RouteRelaxer? relaxer = RouteRelaxer.TryCreate(grid, plan, relaxSettings, out RouteRelaxFailure failure, out (double X, double Y) where);
            if (relaxer == null)
            {
                RhinoApp.WriteLine(failure switch
                {
                    RouteRelaxFailure.OffTerrain =>
                        $"The sketch leaves the terrain near ({units.FormatLength(where.X, "F1")}, {units.FormatLength(where.Y, "F1")}). Draw it over the terrain.",
                    RouteRelaxFailure.NoLength => "The sketch has no length in plan.",
                    _ => "The route options cannot be used. Check MaxGrade, the weights and CorridorWidth.",
                });
                return Result.Failure;
            }

            Polyline ghost = Drape(sketch, grid);
            var domain = new BoundingBox(minX, minY, ghost.BoundingBox.Min.Z, maxX, maxY, ghost.BoundingBox.Max.Z);
            RouteRelaxEvaluation? kept;
            using (var session = new RouteRelaxSession(doc, relaxer, grid, terrain, options, units, tolerance, ghost, domain))
                kept = session.Run();

            settings.Save(units);
            if (kept?.Curve == null)
                return Result.Cancel;

            return Commit(doc, kept, options, units);
        }
    }

    private static Curve? PickSketch(RhinoDoc doc, out Result result)
    {
        var getCurve = new GetObject();
        getCurve.SetCommandPrompt("Select the sketched route");
        getCurve.GeometryFilter = ObjectType.Curve;
        getCurve.EnablePreSelect(true, true);
        GetResult pick = getCurve.Get();
        if (pick != GetResult.Object)
        {
            result = getCurve.CommandResult();
            return null;
        }

        // Only the sketch is released: a terrain selected with it is still selected for the terrain prompt.
        ObjRef picked = getCurve.Object(0);
        Curve? curve = picked.Curve();
        result = curve == null ? Result.Failure : Result.Success;
        doc.Objects.Select(picked, false);
        return curve;
    }

    /// <summary>The sketch in plan, read evenly along it, closely enough for any drawn line.</summary>
    private static List<(double X, double Y)> PlanPoints(Curve sketch)
    {
        int count = Math.Clamp((int)Math.Ceiling(sketch.GetLength() / Math.Max(sketch.GetLength() / MaximumSketchPoints, 0.25)), 64, MaximumSketchPoints);
        double[]? parameters = sketch.DivideByCount(count, true);
        var plan = new List<(double X, double Y)>();
        if (parameters == null)
        {
            plan.Add((sketch.PointAtStart.X, sketch.PointAtStart.Y));
            plan.Add((sketch.PointAtEnd.X, sketch.PointAtEnd.Y));
            return plan;
        }

        foreach (double t in parameters)
        {
            Point3d point = sketch.PointAt(t);
            plan.Add((point.X, point.Y));
        }

        if (!sketch.IsClosed)
            plan[^1] = (sketch.PointAtEnd.X, sketch.PointAtEnd.Y);
        return plan;
    }

    /// <summary>The sketch draped on the ground, for the ghost the session draws.</summary>
    private static Polyline Drape(Curve sketch, RouteHeightGrid grid)
    {
        var ghost = new Polyline();
        foreach ((double x, double y) in PlanPoints(sketch))
            ghost.Add(x, y, grid.TrySample(x, y, out double z) ? z : 0.0);
        return ghost;
    }

    /// <summary>Samples the terrain on a worker, pumping Rhino's message loop; null when Esc cancels.</summary>
    private static RouteHeightGrid? BuildGrid(IndexedTriMesh mesh, (double MinX, double MinY, double MaxX, double MaxY) domain, double cell)
    {
        using var cancellation = new CancellationTokenSource();
        void OnEscape(object? sender, EventArgs e) => cancellation.Cancel();
        RhinoApp.EscapeKeyPressed += OnEscape;
        RhinoApp.SetCommandPrompt("Sampling the terrain. Press Esc to cancel");
        try
        {
            CancellationToken token = cancellation.Token;
            Task<RouteHeightGrid> work = Task.Run(() =>
                RouteHeightGrid.FromMesh(mesh, domain.MinX, domain.MinY, domain.MaxX, domain.MaxY, cell, token));
            while (!work.Wait(30))
                RhinoApp.Wait();
            return work.Result;
        }
        catch (AggregateException error) when (error.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            return null;
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= OnEscape;
        }
    }

    private static Result Commit(RhinoDoc doc, RouteRelaxEvaluation kept, RouteOptionSet options, ModelUnitContext units)
    {
        FittedRouteCurve fitted = kept.Curve!;
        RouteMetrics metrics = kept.CurveMetrics ?? kept.Metrics;
        RouteSuggestionSettings settings = options.Settings;
        double gradeLimit = kept.Settings.MaxGrade;
        var unit = SlopeUnitPreference.Current;

        ObjectAttributes attributes = doc.CreateDefaultAttributes();
        attributes.LayerIndex = options.ResolveLayerIndex();
        attributes.Name = $"Relaxed route ({units.FormatLength(metrics.PlanLength, "F0")}, max {RouteSuggestionReadout.Slope(metrics.MaxGrade, unit)})";
        foreach ((string key, string value) in RouteSuggestionReadout.UserText(
                     1, metrics, gradeLimit, settings.CorridorWidth, RouteSuggestionReadout.CurveFillet(fitted),
                     settings.MinimumRadius, units, unit, fitted.Complexity))
        {
            if (key != RouteSuggestionReadout.UserTextPrefix + "Rank")
                attributes.SetUserString(key, value);
        }

        attributes.SetUserString(RouteSuggestionReadout.UserTextPrefix + "Source", "Relaxed sketch");
        attributes.SetUserString(RouteSuggestionReadout.UserTextPrefix + "ControlPoints", fitted.ControlPointCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        uint undoRecord = doc.BeginUndoRecord("Relax Route");
        try
        {
            using Curve curve = RouteCurveBuilder.FromFitted(fitted);
            Guid id = doc.Objects.AddCurve(curve, attributes);
            if (id == Guid.Empty)
            {
                RhinoApp.WriteLine("Could not add the route curve.");
                return Result.Failure;
            }

            doc.Objects.UnselectAll();
            doc.Objects.Select(id);
            RhinoApp.WriteLine(
                $"Added the relaxed route: {RouteRelaxReadout.Score(kept.Improvement)}; {RouteRelaxReadout.Figures(metrics, units, unit)}; " +
                $"{RouteRelaxReadout.Curve(fitted, units)}. Edit it freely, then assign it to a Grade Path.");
            foreach (RouteRelaxIssue issue in kept.Issues)
                RhinoApp.WriteLine("  " + RouteRelaxReadout.IssueSentence(issue, gradeLimit, units, unit));
            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }
    }
}
