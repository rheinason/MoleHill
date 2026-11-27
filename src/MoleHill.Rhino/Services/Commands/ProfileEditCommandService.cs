// mhEditProfile: edit a curve's long section live beside it, and write the new elevations back on Enter.
using MoleHill.Core.Analysis;
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
/// Picks a curve, opens its long section as a live preview (<see cref="ProfileEditSession"/>), and on Enter
/// replaces the curve with the same plan and the edited elevations, as one undo step. It replaced the
/// mhExtractProfile/mhApplyProfile round trip, whose drawn section carried its frame as user text and so gave
/// wrong elevations once the drawing was moved, and broke under any Rhino edit that made a new object.
/// </summary>
internal static class ProfileEditCommandService
{
    private const string GroundKey = "MoleHill.EditProfile.Ground";
    private const string FitKey = "MoleHill.EditProfile.Fit";

    /// <summary>Most bends the section opens with; a draped curve is simplified until it fits.</summary>
    private const int MaxBends = 40;

    /// <summary>
    /// The opening bends follow the curve within this share of its relief (at least 1 cm). Untouched stretches
    /// keep their own detail anyway, so the bends only need to be the handles a designer would draw: at 1 cm
    /// a smooth curve opened with dozens (found live), far too many to drag.
    /// </summary>
    private const double SimplifyShareOfRelief = 0.03;

    /// <summary>Where each curve's section was last placed, relative to the curve, for this Rhino session.</summary>
    private static readonly Dictionary<Guid, Vector2d> Placements = new();

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext units))
            return Result.Failure;

        var getCurve = new GetObject();
        getCurve.SetCommandPrompt("Select a curve to edit its profile");
        getCurve.GeometryFilter = ObjectType.Curve;
        getCurve.EnablePreSelect(true, true);
        if (getCurve.Get() != GetResult.Object)
            return getCurve.CommandResult();

        ObjRef reference = getCurve.Object(0);
        Curve? source = reference.Curve();
        RhinoObject? sourceObject = reference.Object();
        if (source == null || sourceObject == null)
            return Result.Failure;
        Guid curveId = sourceObject.Id;
        doc.Objects.UnselectAll();

        double tolerance = doc.ModelAbsoluteTolerance;
        if (!TryGetPath(source, tolerance, out double[] path))
        {
            RhinoApp.WriteLine("The curve could not be read as a path.");
            return Result.Failure;
        }

        double[] stations = LongSectionProfile.PlanStations(path);
        double[] elevations = Enumerable.Range(0, path.Length / 3).Select(i => path[i * 3 + 2]).ToArray();
        double relief = elevations.Max() - elevations.Min();
        double simplify = Math.Max(Math.Max(tolerance, units.FromMeters(0.01)), relief * SimplifyShareOfRelief);
        ProfileEditor? editor = ProfileEditor.Create(stations, elevations, tolerance, simplify, MaxBends);
        if (editor == null)
        {
            RhinoApp.WriteLine("The curve has no length in plan; a profile needs one.");
            return Result.Failure;
        }

        string? layerPath = doc.Layers.FindIndex(sourceObject.Attributes.LayerIndex)?.FullPath;
        List<ProfileCurveUser> users = ProfileCurveUsers.Find(TerrainController.Instance.GetTerrains(doc), curveId, layerPath);
        if (ProfileCurveUsers.Describe(users) is { } usedBy)
            RhinoApp.WriteLine(usedBy);
        if (editor.InitialBends.Count < path.Length / 3)
        {
            RhinoApp.WriteLine(
                $"Showing {editor.InitialBends.Count} bends (the curve simplified within {units.FormatLength(editor.SimplifiedWithin, "0.###")}); " +
                "its own detail is kept wherever you do not edit.");
        }

        if (editor.VerticalSteps > 0)
            RhinoApp.WriteLine($"The curve steps vertically {editor.VerticalSteps} time(s); a step is kept unless you edit next to it.");

        int groundIndex = Math.Clamp(CommandOptionCache.GetValue(GroundKey, 0), 0, ProfileEditSession.GroundLabels.Length - 1);
        int fitIndex = Math.Clamp(CommandOptionCache.GetValue(FitKey, 0), 0, ProfileEditSession.FitLabels.Length - 1);
        ProfileGroundLine? LoadGround(int index) => SampleGround(doc, index, curveId, users, path, stations, tolerance);
        ProfileGroundLine ground = LoadGround(groundIndex) ?? ProfileGroundLine.Empty;

        CurveReviewRuleSettings rules = CurveReviewRuleStore.Load(doc);
        double? maxGrade = rules.MaximumGradeMode != CurveReviewRuleMode.Off ? rules.MaximumGradePercent / 100.0 : null;
        double? breakLimit = rules.VerticalBreakMode != CurveReviewRuleMode.Off ? rules.VerticalBreakThresholdPercent / 100.0 : null;
        bool smooth = !source.TryGetPolyline(out _);

        bool accepted;
        int fit;
        using (var session = new ProfileEditSession(
                   doc, editor, path, units, ground, groundIndex, LoadGround, smooth, fitIndex,
                   source.GetBoundingBox(true), Placements.TryGetValue(curveId, out Vector2d placed) ? placed : null,
                   maxGrade, breakLimit))
        {
            accepted = session.Run();
            Placements[curveId] = session.Placement;
            CommandOptionCache.SetValue(GroundKey, session.GroundIndex);
            CommandOptionCache.SetValue(FitKey, session.FitIndex);
            fit = session.FitIndex;
        }

        if (!accepted)
            return Result.Cancel;
        if (!editor.HasChanges)
        {
            RhinoApp.WriteLine("The profile was not changed; the curve is as it was.");
            return Result.Nothing;
        }

        return Commit(doc, curveId, source, editor, path, smooth && fit == 0, maxGrade, tolerance);
    }

    private static Result Commit(
        RhinoDoc doc, Guid curveId, Curve source, ProfileEditor editor, double[] path, bool keepSmooth, double? maxGrade, double tolerance)
    {
        double deviation = 0.0;
        Curve? result;
        if (keepSmooth)
        {
            result = TerrainInputCommandAlgorithms.ApplyProfileToEditPoints(
                source, station => editor.TakesProfile(station) ? editor.ElevationAt(station) : null, out deviation);
            if (result == null)
            {
                RhinoApp.WriteLine("The curve's edit points could not be moved; try Fit=Exact.");
                return Result.Failure;
            }
        }
        else
        {
            double[] applied = editor.ApplyToPath(path);
            var points = new Point3d[applied.Length / 3];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Point3d(applied[i * 3], applied[i * 3 + 1], applied[i * 3 + 2]);
            result = new PolylineCurve(points);
        }

        uint undoRecord = doc.BeginUndoRecord("Edit Profile");
        try
        {
            if (!doc.Objects.Replace(curveId, result))
            {
                RhinoApp.WriteLine("Could not update the curve (is it locked?).");
                return Result.Failure;
            }
        }
        finally
        {
            result.Dispose();
            doc.EndUndoRecord(undoRecord);
        }

        SlopeAnalyzer.SlopeUnit unit = SlopeUnitPreference.Current;
        double[] grades = Enumerable.Range(0, editor.SegmentCount).Select(editor.Grade).ToArray();
        double steepest = grades.Max(Math.Abs);
        string shape = keepSmooth ? "kept smooth, its edit points moved" : source.TryGetPolyline(out _) ? "a polyline" : "now a polyline through the profile's bends";
        RhinoApp.WriteLine(
            $"Profile applied: {editor.Bends.Count} bends, steepest grade {SlopeInput.FormatWithUnit(steepest, unit)}; the curve is {shape}.");
        int tooSteep = maxGrade is { } limit ? grades.Count(grade => Math.Abs(grade) > limit + 1e-9) : 0;
        if (tooSteep > 0)
            RhinoApp.WriteLine($"{tooSteep} segment(s) are steeper than the {SlopeInput.FormatWithUnit(maxGrade!.Value, unit)} limit set in mhInspectCurve.");
        if (deviation > tolerance)
        {
            RhinoApp.WriteLine(FormattableString.Invariant(
                $"Between its edit points the curve strays up to {deviation:0.###} from the profile; Fit=Exact follows it exactly as a polyline."));
        }

        doc.Views.Redraw();
        return Result.Success;
    }

    /// <summary>
    /// The ground line for a choice of Ground. ActiveTerrain is the ground the curve's own card graded when a
    /// card on a terrain reads it (the terrain before the road), else the active terrain as built. Objects asks
    /// for meshes or surfaces. Null when the user cancels the pick, so the previous ground stays.
    /// </summary>
    private static ProfileGroundLine? SampleGround(
        RhinoDoc doc, int index, Guid curveId, IReadOnlyList<ProfileCurveUser> users, double[] path, double[] stations, double tolerance)
    {
        Func<double, double, double?>? probe = null;
        switch (index)
        {
            case 0:
            {
                Mesh? mesh = null;
                bool owned = false;
                string? from = null;
                if (users.FirstOrDefault(user => user.Modifier != null) is { Modifier: { } card } user)
                {
                    mesh = TerrainController.Instance.DuplicateIncomingModifierMesh(doc, user.Terrain, card.Id);
                    owned = mesh != null;
                    from = mesh != null ? $"the ground '{card.Label}' grades" : null;
                }

                if (mesh == null && TerrainController.Instance.GetSelectedTerrain(doc) is { } terrain)
                {
                    mesh = TerrainController.Instance.PeekFinalTerrainMesh(doc, terrain.TerrainId);
                    from = mesh != null ? $"terrain '{terrain.Name}'" : null;
                }

                if (mesh == null)
                {
                    RhinoApp.WriteLine("No ground line: build the active terrain, or use Ground=Objects.");
                    return ProfileGroundLine.Empty;
                }

                DrapeTargets targets = DrapeTargets.Create(new[] { mesh }, tolerance, out _);
                if (owned)
                    mesh.Dispose();
                RhinoApp.WriteLine($"Ground line from {from}.");
                probe = (x, y) => targets.HeightAt(x, y, lowest: false);
                break;
            }

            case 1:
            {
                if (!DrapeCommandService.TrySelectTargets(doc, new HashSet<Guid> { curveId }, out DrapeTargets? picked, out _))
                    return null;
                probe = (x, y) => picked!.HeightAt(x, y, lowest: false);
                break;
            }

            default:
                return ProfileGroundLine.Empty;
        }

        double spacing = Math.Max(tolerance * 10.0, stations[^1] / 500.0);
        return new ProfileGroundLine(SampleRuns(probe, path, stations, spacing));
    }

    /// <summary>The ground under the path every <paramref name="spacing"/> and at every path point, broken where it leaves the ground.</summary>
    private static List<double[]> SampleRuns(Func<double, double, double?> probe, double[] path, double[] pathStations, double spacing)
    {
        var runs = new List<double[]>();
        var samples = new SortedSet<double>(pathStations);
        double length = pathStations[^1];
        for (double s = 0.0; s < length; s += spacing)
            samples.Add(s);

        var current = new List<double>();
        foreach (double station in samples)
        {
            (double x, double y) = PlanPointAt(path, pathStations, station);
            if (probe(x, y) is { } z)
            {
                current.Add(station);
                current.Add(z);
                continue;
            }

            if (current.Count >= 4)
                runs.Add(current.ToArray());
            current.Clear();
        }

        if (current.Count >= 4)
            runs.Add(current.ToArray());
        return runs;
    }

    private static (double X, double Y) PlanPointAt(double[] path, double[] pathStations, double station)
    {
        int index = Array.BinarySearch(pathStations, station);
        if (index >= 0)
            return (path[index * 3], path[index * 3 + 1]);
        int upper = Math.Min(~index, pathStations.Length - 1);
        int lower = Math.Max(upper - 1, 0);
        double span = pathStations[upper] - pathStations[lower];
        double t = span > 0.0 ? (station - pathStations[lower]) / span : 0.0;
        return (path[lower * 3] + (path[upper * 3] - path[lower * 3]) * t,
            path[lower * 3 + 1] + (path[upper * 3 + 1] - path[lower * 3 + 1]) * t);
    }

    /// <summary>The curve as a flat XYZ path: a polyline's own points, otherwise a tolerance-fine tessellation.</summary>
    private static bool TryGetPath(Curve curve, double tolerance, out double[] path)
    {
        path = Array.Empty<double>();
        if (!curve.TryGetPolyline(out Polyline polyline))
        {
            using PolylineCurve? tessellated = curve.ToPolyline(tolerance, RhinoMath.ToRadians(1.0), 0.0, 0.0);
            if (tessellated == null || !tessellated.TryGetPolyline(out polyline))
                return false;
        }

        if (polyline.Count < 2)
            return false;
        path = new double[polyline.Count * 3];
        for (int i = 0; i < polyline.Count; i++)
        {
            path[i * 3] = polyline[i].X;
            path[i * 3 + 1] = polyline[i].Y;
            path[i * 3 + 2] = polyline[i].Z;
        }

        return true;
    }
}
