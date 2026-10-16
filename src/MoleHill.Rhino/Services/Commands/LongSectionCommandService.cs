// mhExtractProfile and mhApplyProfile: a long section drawn flat, edited with Rhino tools, mapped back by station.
using System.Globalization;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
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
/// The long-section round trip (prototype). mhExtractProfile unrolls a plan curve into a flat drawing —
/// the curve's own elevations as the design line, the active terrain beneath it as the ground line, a datum
/// and station ticks — at true horizontal scale and a chosen vertical exaggeration. The design line is an
/// ordinary polyline: move its points, add some, fillet it, whatever Rhino offers. mhApplyProfile then reads
/// it back by station and re-elevates the plan curve, keeping its XY.
///
/// <para>The design line carries the frame it was drawn in (origin, datum, exaggeration) and the plan curve
/// it came from as user strings, so applying needs no more than picking it. A profile that doubles back
/// names two elevations for one station and is refused.</para>
/// </summary>
internal static class LongSectionCommandService
{
    private const string KeyPrefix = "MoleHill.Profile.";
    private const string SourceKey = KeyPrefix + "Source";
    private const string RoleKey = KeyPrefix + "Role";
    private const string OriginXKey = KeyPrefix + "OriginX";
    private const string OriginYKey = KeyPrefix + "OriginY";
    private const string DatumKey = KeyPrefix + "Datum";
    private const string ExaggerationKey = KeyPrefix + "Exaggeration";
    private const string PlanLengthKey = KeyPrefix + "PlanLength";
    private const string DesignRole = "Design";
    private static readonly string[] GroundLabels = { "ActiveTerrain", "Objects", "None" };
    private static readonly string[] FitLabels = { "EditPoints", "Exact" };

    public static Result RunExtract(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        var getCurve = new GetObject();
        getCurve.SetCommandPrompt("Select a plan curve to draw as a long section");
        getCurve.GeometryFilter = ObjectType.Curve;
        getCurve.EnablePreSelect(true, true);
        if (getCurve.Get() != GetResult.Object)
            return getCurve.CommandResult();

        ObjRef reference = getCurve.Object(0);
        Curve? source = reference.Curve();
        if (source == null)
            return Result.Failure;

        double tolerance = doc.ModelAbsoluteTolerance;
        if (!TryGetPath(source, tolerance, out double[] path))
        {
            RhinoApp.WriteLine("The curve could not be read as a path.");
            return Result.Failure;
        }

        double[] pathStations = LongSectionProfile.PlanStations(path);
        double planLength = pathStations[^1];
        if (planLength <= tolerance)
        {
            RhinoApp.WriteLine("The curve has no length in plan; a long section needs one.");
            return Result.Failure;
        }

        double exaggeration = CommandOptionCache.GetValue("MoleHill.ExtractProfile.Exaggeration", 5.0);
        const string tickKey = "MoleHill.ExtractProfile.TickInterval";
        double tickInterval = CommandOptionCache.GetLength(tickKey, unitContext, 20.0);
        int groundIndex = CommandOptionCache.GetValue("MoleHill.ExtractProfile.Ground", 0);

        // A curve already drawn as a long section can be redrawn where it is, replacing the old drawing,
        // so re-extracting after a plan edit does not leave a stale section beside the new one.
        RhinoObject? existing = FindDrawing(doc, reference.ObjectId);
        Point3d? existingOrigin = null;
        if (existing != null && TryReadFrame(existing.Attributes, out LongSectionFrame oldFrame, out _, out _))
        {
            double z = existing.Geometry is Curve existingCurve ? existingCurve.PointAtStart.Z : 0.0;
            existingOrigin = new Point3d(oldFrame.OriginX, oldFrame.OriginY, z);
            exaggeration = oldFrame.Exaggeration;
        }

        Point3d basePoint;
        bool redrawInPlace = false;
        while (true)
        {
            var getBase = new GetPoint();
            getBase.SetCommandPrompt(existingOrigin != null
                ? "Place the long section, or press Enter to redraw the existing one in place"
                : "Place the long section (station 0 at the datum)");
            getBase.AcceptNothing(existingOrigin != null);
            var exaggerationOption = new OptionDouble(exaggeration, 0.01, 1000.0);
            var tickOption = new OptionDouble(tickInterval, tolerance * 10.0, 1e9);
            getBase.AddOptionDouble("Exaggeration", ref exaggerationOption);
            getBase.AddOptionDouble("TickInterval", ref tickOption);
            int groundOption = getBase.AddOptionList("Ground", GroundLabels, Math.Clamp(groundIndex, 0, GroundLabels.Length - 1));
            GetResult result = getBase.Get();
            exaggeration = exaggerationOption.CurrentValue;
            tickInterval = tickOption.CurrentValue;
            if (result == GetResult.Option)
            {
                if (getBase.OptionIndex() == groundOption)
                    groundIndex = getBase.Option().CurrentListOptionIndex;
                continue;
            }

            if (result == GetResult.Nothing && existingOrigin is { } origin)
            {
                basePoint = origin;
                redrawInPlace = true;
                break;
            }

            if (result != GetResult.Point)
                return getBase.CommandResult();
            basePoint = getBase.Point();
            break;
        }

        CommandOptionCache.SetValue("MoleHill.ExtractProfile.Exaggeration", exaggeration);
        CommandOptionCache.SetLength(tickKey, unitContext, tickInterval);
        CommandOptionCache.SetValue("MoleHill.ExtractProfile.Ground", groundIndex);

        Func<double, double, double?>? groundProbe = null;
        string? groundNote = null;
        switch (groundIndex)
        {
            case 0:
                groundProbe = ActiveTerrainProbe(doc);
                if (groundProbe == null)
                    groundNote = " (no ground line: build the active terrain, or use Ground=Objects)";
                break;
            case 1:
                if (!DrapeCommandService.TrySelectTargets(doc, new HashSet<Guid> { reference.ObjectId },
                        out DrapeTargets? targets, out Result targetResult))
                    return targetResult;
                groundProbe = (x, y) => targets!.HeightAt(x, y, lowest: false);
                break;
        }

        List<double[]> groundRuns = groundProbe == null
            ? new List<double[]>()
            : SampleGround(groundProbe, path, pathStations, Math.Max(tolerance * 10.0, tickInterval / 10.0));
        double lowest = double.PositiveInfinity;
        for (int i = 0; i < path.Length / 3; i++)
            lowest = Math.Min(lowest, path[i * 3 + 2]);
        foreach (double[] run in groundRuns)
            for (int i = 1; i < run.Length; i += 2)
                lowest = Math.Min(lowest, run[i]);

        double datum = LongSectionProfile.SuggestDatum(lowest, unitContext.FromMeters(1.0));
        var frame = new LongSectionFrame(basePoint.X, basePoint.Y, datum, exaggeration);

        var designPoints = new Point3d[path.Length / 3];
        for (int i = 0; i < designPoints.Length; i++)
        {
            (double x, double y) = frame.ToDrawing(pathStations[i], path[i * 3 + 2]);
            designPoints[i] = new Point3d(x, y, basePoint.Z);
        }

        var objects = new List<Guid>();
        uint undoRecord = doc.BeginUndoRecord("Extract Profile");
        try
        {
            if (redrawInPlace && existing != null)
                DeleteDrawing(doc, existing);

            var designAttributes = new ObjectAttributes();
            designAttributes.SetUserString(SourceKey, reference.ObjectId.ToString());
            designAttributes.SetUserString(RoleKey, DesignRole);
            designAttributes.SetUserString(OriginXKey, Format(frame.OriginX));
            designAttributes.SetUserString(OriginYKey, Format(frame.OriginY));
            designAttributes.SetUserString(DatumKey, Format(frame.Datum));
            designAttributes.SetUserString(ExaggerationKey, Format(frame.Exaggeration));
            designAttributes.SetUserString(PlanLengthKey, Format(planLength));
            designAttributes.Name = "Design profile";
            objects.Add(doc.Objects.AddPolyline(designPoints, designAttributes));

            foreach (double[] run in groundRuns)
            {
                var groundPoints = new Point3d[run.Length / 2];
                for (int i = 0; i < groundPoints.Length; i++)
                {
                    (double x, double y) = frame.ToDrawing(run[i * 2], run[i * 2 + 1]);
                    groundPoints[i] = new Point3d(x, y, basePoint.Z);
                }

                var groundAttributes = new ObjectAttributes { Name = "Ground profile" };
                groundAttributes.SetUserString(SourceKey, reference.ObjectId.ToString());
                groundAttributes.SetUserString(RoleKey, "Ground");
                objects.Add(doc.Objects.AddPolyline(groundPoints, groundAttributes));
            }

            AddFrame(doc, frame, planLength, tickInterval, basePoint.Z, objects);

            int group = doc.Groups.Add(objects);
            if (group < 0 || objects.Contains(Guid.Empty))
                RhinoApp.WriteLine("Some parts of the long section could not be added.");
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }

        RhinoApp.WriteLine(
            $"Long section drawn: {ModelUnits.FormatLength(planLength, doc.ModelUnitSystem)} long, datum " +
            $"{datum.ToString("F2", CultureInfo.InvariantCulture)}, vertical x{exaggeration.ToString("G4", CultureInfo.InvariantCulture)}" +
            (redrawInPlace ? ", redrawn in place" : string.Empty) +
            (groundNote ?? (groundIndex != 2 && groundRuns.Count == 0 ? " (the curve does not pass over the ground)" : string.Empty)) + "." +
            " Edit the design line, then run mhApplyProfile.");
        doc.Views.Redraw();
        return Result.Success;
    }

    public static Result RunApply(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return Result.Failure;

        var getProfile = new GetObject();
        getProfile.SetCommandPrompt("Select an edited design profile");
        getProfile.GeometryFilter = ObjectType.Curve;
        getProfile.EnablePreSelect(true, true);
        getProfile.SetCustomGeometryFilter((rhinoObject, _, _) =>
            rhinoObject?.Attributes.GetUserString(RoleKey) == DesignRole);
        if (getProfile.Get() != GetResult.Object)
            return getProfile.CommandResult();

        ObjRef reference = getProfile.Object(0);
        Curve? profile = reference.Curve();
        ObjectAttributes? attributes = reference.Object()?.Attributes;
        if (profile == null || attributes == null || !TryReadFrame(attributes, out LongSectionFrame frame,
                out Guid sourceId, out double extractedLength))
        {
            RhinoApp.WriteLine("That curve does not carry a long-section frame; extract one with mhExtractProfile.");
            return Result.Failure;
        }

        RhinoObject? sourceObject = doc.Objects.FindId(sourceId);
        if (sourceObject?.Geometry is not Curve source)
        {
            RhinoApp.WriteLine("The plan curve this profile was drawn from is no longer in the document.");
            return Result.Failure;
        }

        double tolerance = doc.ModelAbsoluteTolerance;
        if (!TryGetPath(profile, tolerance, out double[] profilePath) ||
            !TryGetPath(source, tolerance, out double[] path))
        {
            RhinoApp.WriteLine("The profile or its plan curve could not be read.");
            return Result.Failure;
        }

        var drawing = new double[profilePath.Length / 3 * 2];
        for (int i = 0; i < drawing.Length / 2; i++)
        {
            drawing[i * 2] = profilePath[i * 3];
            drawing[i * 2 + 1] = profilePath[i * 3 + 1];
        }

        if (!LongSectionProfile.TryRead(drawing, frame, tolerance, out double[] stations, out double[] elevations,
                out string? error))
        {
            RhinoApp.WriteLine(error ?? "The profile could not be read.");
            return Result.Failure;
        }

        double planLength = LongSectionProfile.PlanStations(path)[^1];
        if (Math.Abs(planLength - extractedLength) > Math.Max(tolerance * 10.0, extractedLength * 1e-6))
        {
            RhinoApp.WriteLine(
                $"The plan curve's length changed since the profile was drawn " +
                $"({extractedLength:F3} then, {planLength:F3} now); stations are applied to its current length.");
        }

        // A polyline takes the profile exactly: its own points plus the profile's breaks. A smooth curve keeps
        // its type by default and only its edit points move, at the cost of straying between them.
        bool exact = true;
        if (!source.TryGetPolyline(out _))
        {
            int fitIndex = CommandOptionCache.GetValue("MoleHill.ApplyProfile.Fit", 0);
            while (true)
            {
                var getFit = new GetOption();
                getFit.SetCommandPrompt("The plan curve is smooth: Fit EditPoints keeps it smooth, Exact makes it a polyline. Press Enter to apply");
                getFit.AcceptNothing(true);
                int fitOption = getFit.AddOptionList("Fit", FitLabels, Math.Clamp(fitIndex, 0, 1));
                GetResult fitResult = getFit.Get();
                if (fitResult == GetResult.Option && getFit.OptionIndex() == fitOption)
                {
                    fitIndex = getFit.Option().CurrentListOptionIndex;
                    continue;
                }

                if (fitResult != GetResult.Nothing)
                    return getFit.CommandResult();
                break;
            }

            CommandOptionCache.SetValue("MoleHill.ApplyProfile.Fit", fitIndex);
            exact = fitIndex == 1;
        }

        int uncovered;
        double deviation = 0.0;
        Curve? result;
        if (exact)
        {
            double[] applied = LongSectionProfile.Apply(path, stations, elevations, tolerance, out uncovered);
            var points = new Point3d[applied.Length / 3];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Point3d(applied[i * 3], applied[i * 3 + 1], applied[i * 3 + 2]);
            result = new PolylineCurve(points);
        }
        else
        {
            result = TerrainInputCommandAlgorithms.ApplyProfileToEditPoints(
                source, stations, elevations, tolerance, out uncovered, out deviation);
            if (result == null)
            {
                RhinoApp.WriteLine("The curve's edit points could not be moved.");
                return Result.Failure;
            }
        }

        uint undoRecord = doc.BeginUndoRecord("Apply Profile");
        try
        {
            if (!doc.Objects.Replace(sourceId, result))
            {
                RhinoApp.WriteLine("Could not update the plan curve.");
                return Result.Failure;
            }
        }
        finally
        {
            result.Dispose();
            doc.EndUndoRecord(undoRecord);
        }

        RhinoApp.WriteLine(uncovered > 0
            ? $"Profile applied; {uncovered} point(s) beyond the profile's ends kept their elevation."
            : "Profile applied.");
        if (deviation > tolerance)
        {
            RhinoApp.WriteLine(FormattableString.Invariant(
                $"Between its edit points the curve strays up to {deviation:F3} from the profile; Fit=Exact follows it exactly as a polyline."));
        }
        doc.Views.Redraw();
        return Result.Success;
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

    /// <summary>
    /// The active terrain under the path, every <paramref name="spacing"/> and at every path point, as
    /// (station, elevation) runs; a stretch off the terrain ends one run and starts the next.
    /// </summary>
    private static List<double[]> SampleGround(Func<double, double, double?> probe, double[] path, double[] pathStations, double spacing)
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

    /// <summary>The active terrain's finished surface as a height probe; null when there is none built.</summary>
    private static Func<double, double, double?>? ActiveTerrainProbe(RhinoDoc doc)
    {
        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        Mesh? mesh = terrain == null ? null : TerrainController.Instance.PeekFinalTerrainMesh(doc, terrain.TerrainId);
        if (mesh == null)
            return null;
        DrapeTargets targets = DrapeTargets.Create(new[] { mesh }, doc.ModelAbsoluteTolerance, out _);
        return targets.Count == 0 ? null : (x, y) => targets.HeightAt(x, y, lowest: false);
    }

    /// <summary>The design line drawn for this plan curve, if any.</summary>
    private static RhinoObject? FindDrawing(RhinoDoc doc, Guid sourceId)
    {
        string id = sourceId.ToString();
        return doc.Objects.FirstOrDefault(o =>
            o.Attributes.GetUserString(RoleKey) == DesignRole && o.Attributes.GetUserString(SourceKey) == id);
    }

    /// <summary>Deletes a long-section drawing: every member of the design line's group, or the line alone.</summary>
    private static void DeleteDrawing(RhinoDoc doc, RhinoObject design)
    {
        var ids = new HashSet<Guid> { design.Id };
        foreach (int group in design.Attributes.GetGroupList() ?? Array.Empty<int>())
            foreach (RhinoObject member in doc.Groups.GroupMembers(group) ?? Array.Empty<RhinoObject>())
                ids.Add(member.Id);
        RhinoDocumentHelpers.DeleteObjects(doc, ids.ToList());
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

    /// <summary>The datum line, a tick and a station label every interval, and the datum's elevation.</summary>
    private static void AddFrame(RhinoDoc doc, LongSectionFrame frame, double length, double interval, double z, List<Guid> objects)
    {
        double textHeight = Math.Max(interval * 0.12, doc.ModelAbsoluteTolerance * 10.0);
        var origin = new Point3d(frame.OriginX, frame.OriginY, z);
        objects.Add(doc.Objects.AddLine(origin, origin + new Vector3d(length, 0.0, 0.0)));
        DimensionStyle style = doc.DimStyles.Current;
        for (int tick = 0; tick * interval <= length + doc.ModelAbsoluteTolerance; tick++)
        {
            double station = tick * interval;
            var foot = new Point3d(frame.OriginX + station, frame.OriginY, z);
            objects.Add(doc.Objects.AddLine(foot, foot - new Vector3d(0.0, textHeight, 0.0)));
            objects.Add(AddText(doc, FormatStation(station), foot - new Vector3d(0.0, textHeight * 2.5, 0.0), textHeight, style));
        }

        objects.Add(AddText(doc, $"Datum {frame.Datum.ToString("F2", CultureInfo.InvariantCulture)}",
            origin + new Vector3d(-textHeight * 8.0, 0.0, 0.0), textHeight, style));
    }

    private static Guid AddText(RhinoDoc doc, string text, Point3d location, double height, DimensionStyle style)
    {
        using TextEntity? entity = TextEntity.Create(text, new Plane(location, Vector3d.ZAxis), style, false, 0.0, 0.0);
        if (entity == null)
            return Guid.Empty;
        entity.TextHeight = height;
        return doc.Objects.AddText(entity);
    }

    /// <summary>Chainage style: 120 reads 0+120, 1250.5 reads 1+250.5.</summary>
    internal static string FormatStation(double station)
    {
        long whole = (long)Math.Floor(station / 1000.0);
        double rest = station - whole * 1000.0;
        string restText = Math.Abs(rest - Math.Round(rest)) < 1e-6
            ? Math.Round(rest).ToString("000", CultureInfo.InvariantCulture)
            : rest.ToString("000.0", CultureInfo.InvariantCulture);
        return $"{whole}+{restText}";
    }

    private static bool TryReadFrame(ObjectAttributes attributes, out LongSectionFrame frame, out Guid sourceId, out double planLength)
    {
        frame = default;
        planLength = 0.0;
        if (!Guid.TryParse(attributes.GetUserString(SourceKey), out sourceId) ||
            !TryParse(attributes, OriginXKey, out double originX) ||
            !TryParse(attributes, OriginYKey, out double originY) ||
            !TryParse(attributes, DatumKey, out double datum) ||
            !TryParse(attributes, ExaggerationKey, out double exaggeration) ||
            !TryParse(attributes, PlanLengthKey, out planLength))
            return false;

        frame = new LongSectionFrame(originX, originY, datum, exaggeration);
        return true;
    }

    private static bool TryParse(ObjectAttributes attributes, string key, out double value) =>
        double.TryParse(attributes.GetUserString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
