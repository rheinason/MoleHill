// The mhSuggestRoute workflow: pick a terrain and points, search, review the alternatives, keep curves.
using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
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
/// Suggests up to three routes between picked points and adds the ones the user keeps as ordinary
/// polylines, ready to edit and then assign to Grade Path. Help with drawing belongs upstream, in the
/// curve: this command produces input and never touches a terrain definition. The search itself is
/// Core's <see cref="RouteSuggester"/>; this file is the picking, the review and the commit.
/// </summary>
internal static class RouteSuggestionCommandService
{
    private static System.Drawing.Color TrackingColor => global::Rhino.ApplicationSettings.AppearanceSettings.TrackingColor;

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext units))
            return Result.Failure;

        Mesh? terrain = RouteTerrainPicker.Pick(doc, "suggesting routes", out Result pickResult);
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
            var options = new RouteOptionSet(doc, settings, maxGrade);

            List<Point3d>? picked = PickPoints(doc, terrain, options, out Result pointResult);
            settings.Save(units);
            if (picked == null)
                return pointResult;

            return Review(doc, mesh, picked, options, units);
        }
    }

    /// <summary>Start, waypoints, end. Enter after the second point finishes; the last point picked is the end.</summary>
    private static List<Point3d>? PickPoints(RhinoDoc doc, Mesh terrain, RouteOptionSet options, out Result result)
    {
        result = Result.Cancel;
        var points = new List<Point3d>();
        while (true)
        {
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt(points.Count switch
            {
                0 => "Start point",
                1 => "End point, or a waypoint",
                _ => "Next waypoint or end point. Press Enter when the last point picked is the end",
            });
            getPoint.Constrain(terrain, false);
            getPoint.AcceptNothing(points.Count >= 2);
            options.AddTo(getPoint);
            int undoOption = points.Count > 0 ? getPoint.AddOption("Undo") : -1;
            getPoint.DynamicDraw += (_, e) =>
            {
                if (points.Count == 0)
                    return;

                var preview = new List<Point3d>(points) { e.CurrentPoint };
                e.Display.DrawPolyline(preview, TrackingColor, 1);
                foreach (Point3d point in points)
                    e.Display.DrawPoint(point, TrackingColor);
            };

            GetResult pick = getPoint.Get();
            options.Commit(getPoint, pick);
            switch (pick)
            {
                case GetResult.Point:
                    points.Add(getPoint.Point());
                    continue;
                case GetResult.Option:
                    if (undoOption >= 0 && getPoint.OptionIndex() == undoOption)
                        points.RemoveAt(points.Count - 1);
                    continue;
                case GetResult.Nothing when points.Count >= 2:
                    doc.Views.Redraw();
                    return points;
                default:
                    result = getPoint.CommandResult();
                    return null;
            }
        }
    }

    private static Result Review(
        RhinoDoc doc,
        IndexedTriMesh mesh,
        List<Point3d> picked,
        RouteOptionSet options,
        ModelUnitContext units)
    {
        RouteSuggestionSettings settings = options.Settings;
        List<(double X, double Y)> plan = picked.Select(point => (point.X, point.Y)).ToList();
        (double minX, double minY, double maxX, double maxY) = RouteSuggester.SearchDomain(plan);
        double span = RouteSuggester.DirectSpan(plan);

        var conduit = new RouteSuggestionConduit();
        RouteHeightGrid? grid = null;
        RouteSuggestionResult? result = null;
        double resultGradeLimit = 0.0;
        double resultRadius = 0.0;
        var routes = new List<Polyline>();
        bool solve = true;

        try
        {
            while (true)
            {
                if (solve)
                {
                    solve = false;
                    routes.Clear();
                    result = null;
                    // A limit has no direction: a typed -8% means the same as 8%.
                    string? invalid = settings.Validate(Math.Abs(options.MaxGrade.Ratio));
                    if (invalid != null)
                    {
                        RhinoApp.WriteLine(invalid);
                    }
                    else
                    {
                        (double cell, bool coarsened) = settings.ResolveCellSize(maxX - minX, maxY - minY, span);
                        if (grid != null && grid.CellSize != cell)
                            grid = null;

                        resultGradeLimit = Math.Abs(options.MaxGrade.Ratio);
                        RouteRequest request = settings.ToRequest(
                            plan, resultGradeLimit, Math.Max(doc.ModelAbsoluteTolerance, units.FromMeters(RouteSuggestionSettings.ProfileToleranceMeters)));
                        var watch = Stopwatch.StartNew();
                        result = Solve(mesh, (minX, minY, maxX, maxY), cell, ref grid, request);
                        if (result == null)
                        {
                            RhinoApp.WriteLine("Route search cancelled.");
                            return Result.Cancel;
                        }

                        resultRadius = request.MinimumRadius;
                        Report(result, grid, cell, coarsened, settings.IsAutomaticCellSize, watch.Elapsed, resultGradeLimit, resultRadius, units, settings.SmoothCurve);
                        routes.AddRange(Previews(result, settings));
                    }

                    conduit.SetRoutes(routes.ToList(), picked);
                    conduit.Enabled = true;
                    doc.Views.Redraw();
                }

                var getOption = new GetOption();
                getOption.SetCommandPrompt(routes.Count switch
                {
                    0 => "No route to keep. Change the options to search again",
                    1 => "Press Enter to keep the route, or change the options to search again",
                    _ => "Keep which route? Press Enter to keep all",
                });
                getOption.AcceptNothing(routes.Count > 0);
                int keepAll = routes.Count > 1 ? getOption.AddOption("KeepAll") : -1;
                var keepOne = new int[routes.Count];
                for (int i = 0; i < routes.Count; i++)
                    keepOne[i] = getOption.AddOption($"Keep{i + 1}");
                options.AddTo(getOption);

                GetResult choice = getOption.Get();
                if (choice == GetResult.Nothing && routes.Count > 0)
                    return Commit(doc, result!, Enumerable.Range(0, routes.Count), resultGradeLimit, resultRadius, options, units);

                if (choice != GetResult.Option)
                    return getOption.CommandResult();

                int chosen = getOption.OptionIndex();
                if (chosen == keepAll)
                    return Commit(doc, result!, Enumerable.Range(0, routes.Count), resultGradeLimit, resultRadius, options, units);

                int single = Array.IndexOf(keepOne, chosen);
                if (single >= 0)
                    return Commit(doc, result!, new[] { single }, resultGradeLimit, resultRadius, options, units);

                bool wasSmooth = settings.SmoothCurve;
                solve = options.Commit(getOption, choice);
                settings.Save(units);
                // Nothing the search reads changed (a new slope unit, the curve style): reprint, don't re-search.
                if (!solve && result is { Succeeded: true })
                {
                    if (wasSmooth != settings.SmoothCurve)
                    {
                        routes.Clear();
                        routes.AddRange(Previews(result, settings));
                        conduit.SetRoutes(routes.ToList(), picked);
                        doc.Views.Redraw();
                    }

                    PrintRoutes(result, resultGradeLimit, resultRadius, units, settings.SmoothCurve);
                }
            }
        }
        finally
        {
            conduit.Enabled = false;
            doc.Views.Redraw();
        }
    }

    /// <summary>
    /// Builds the grid (once per cell size) and searches on a worker, pumping Rhino's message loop so
    /// the viewport stays live and Esc cancels. Null when cancelled.
    /// </summary>
    private static RouteSuggestionResult? Solve(
        IndexedTriMesh mesh,
        (double MinX, double MinY, double MaxX, double MaxY) domain,
        double cell,
        ref RouteHeightGrid? grid,
        RouteRequest request)
    {
        using var cancellation = new CancellationTokenSource();
        void OnEscape(object? sender, EventArgs e) => cancellation.Cancel();
        RhinoApp.EscapeKeyPressed += OnEscape;
        RhinoApp.SetCommandPrompt("Finding routes. Press Esc to cancel");
        RouteHeightGrid? existing = grid;
        try
        {
            CancellationToken token = cancellation.Token;
            Task<(RouteHeightGrid Grid, RouteSuggestionResult Result)> work = Task.Run(() =>
            {
                RouteHeightGrid searched = existing ??
                    RouteHeightGrid.FromMesh(mesh, domain.MinX, domain.MinY, domain.MaxX, domain.MaxY, cell, token);
                return (searched, RouteSuggester.Suggest(searched, request, token));
            });

            while (!work.Wait(30))
                RhinoApp.Wait();

            grid = work.Result.Grid;
            return work.Result.Result;
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

    private static void Report(
        RouteSuggestionResult result,
        RouteHeightGrid? grid,
        double cell,
        bool coarsened,
        bool automatic,
        TimeSpan elapsed,
        double gradeLimit,
        double radius,
        ModelUnitContext units,
        bool smooth)
    {
        string cellNote = coarsened ? " (coarsened: the requested CellSize was too fine for this area)" : automatic ? " (automatic)" : string.Empty;
        if (grid != null)
        {
            RhinoApp.WriteLine(
                $"Searched {grid.Columns} x {grid.Rows} ground samples at {units.FormatLength(cell)}{cellNote} " +
                $"in {elapsed.TotalSeconds:F1} s.");
        }

        if (!result.Succeeded)
        {
            RhinoApp.WriteLine(RouteSuggestionReadout.DescribeFailure(result, gradeLimit, SlopeUnitPreference.Current));
            return;
        }

        PrintRoutes(result, gradeLimit, radius, units, smooth);
        if (result.Alternatives.Count == 1 && result.DroppedNearDuplicates > 0)
            RhinoApp.WriteLine("Every other route found ran along the same corridor, so only one is shown.");
    }

    private static void PrintRoutes(RouteSuggestionResult result, double gradeLimit, double radius, ModelUnitContext units, bool smooth)
    {
        RhinoApp.WriteLine($"Suggested routes within {RouteSuggestionReadout.Slope(gradeLimit, SlopeUnitPreference.Current)}" +
            (smooth ? " (smooth curves; Curve=Exact keeps the exact arcs):" : " (exact arcs):"));
        for (int i = 0; i < result.Alternatives.Count; i++)
        {
            RouteAlternative route = result.Alternatives[i];
            RhinoApp.WriteLine("  " + (smooth
                ? RouteSuggestionReadout.DescribeCurve(i + 1, route.CurveMetrics, route.Curve, radius, units, SlopeUnitPreference.Current)
                : RouteSuggestionReadout.Describe(i + 1, route.Metrics, route.Fillet, radius, units, SlopeUnitPreference.Current, route.Complexity)));
        }
    }

    /// <summary>What the review draws for each route: the curve that will be kept.</summary>
    private static IEnumerable<Polyline> Previews(RouteSuggestionResult result, RouteSuggestionSettings settings) =>
        result.Alternatives.Select(route => RouteCurveBuilder.ToPolyline(settings.SmoothCurve ? route.Curve.PointsXyz : route.PointsXyz));

    private static Result Commit(
        RhinoDoc doc,
        RouteSuggestionResult result,
        IEnumerable<int> keep,
        double gradeLimit,
        double radius,
        RouteOptionSet options,
        ModelUnitContext units)
    {
        bool smooth = options.Settings.SmoothCurve;
        double corridorWidth = options.Settings.CorridorWidth;
        int layerIndex = options.ResolveLayerIndex();
        uint undoRecord = doc.BeginUndoRecord("Suggest Route");
        var added = new List<Guid>();
        try
        {
            foreach (int index in keep)
            {
                RouteAlternative route = result.Alternatives[index];
                RouteMetrics metrics = smooth ? route.CurveMetrics : route.Metrics;
                RouteFilletSummary fillet = smooth ? RouteSuggestionReadout.CurveFillet(route.Curve) : route.Fillet;
                RouteComplexity complexity = smooth ? route.Curve.Complexity : route.Complexity;
                ObjectAttributes attributes = doc.CreateDefaultAttributes();
                attributes.LayerIndex = layerIndex;
                attributes.Name = RouteSuggestionReadout.ObjectName(index + 1, metrics, units, SlopeUnitPreference.Current);
                foreach ((string key, string value) in RouteSuggestionReadout.UserText(
                             index + 1, metrics, gradeLimit, corridorWidth, fillet, radius, units, SlopeUnitPreference.Current, complexity))
                {
                    attributes.SetUserString(key, value);
                }

                using Curve curve = smooth ? RouteCurveBuilder.FromFitted(route.Curve) : RouteCurveBuilder.FromSpans(route);
                Guid id = doc.Objects.AddCurve(curve, attributes);
                if (id == Guid.Empty)
                {
                    foreach (Guid addedId in added)
                        doc.Objects.Delete(addedId, true);
                    RhinoApp.WriteLine("Could not add the route curves; nothing was added.");
                    return Result.Failure;
                }

                added.Add(id);
            }

            doc.Objects.UnselectAll();
            foreach (Guid id in added)
                doc.Objects.Select(id);
            RhinoApp.WriteLine(added.Count == 1
                ? "Added 1 route curve. Edit it freely, then assign it to a Grade Path."
                : $"Added {added.Count} route curves. Edit them freely, then assign one to a Grade Path.");
            return Result.Success;
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }
    }
}
