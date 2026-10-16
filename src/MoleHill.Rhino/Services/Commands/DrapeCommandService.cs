// mhDrapeCurve and mhDrapeCurveSection: drape curves onto meshes, surfaces or the active terrain.
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using DrawingColor = System.Drawing.Color;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Draping curves onto terrain. Both commands read the same targets (any number of meshes, surfaces and
/// polysurfaces, or the active MoleHill terrain), take the top or the lowest surface, and share one miss
/// policy for stations with no surface under them.
///
/// <para>Each curve is reported on its own and a curve that fails no longer fails the command: the ones
/// that drape still come out. mhDrapeCurveSection drapes only a picked stretch and ties both ends back
/// into the curve, ramping in over a transition, for tying a new line into existing ground.</para>
/// </summary>
internal static class DrapeCommandService
{
    private static readonly string[] MissLabels = { "Split", "Bridge", "KeepZ" };
    private static readonly string[] SectionMissLabels = { "KeepZ", "Bridge" };

    private static DrawingColor TrackingColor => global::Rhino.ApplicationSettings.AppearanceSettings.TrackingColor;

    public static Result RunDrapeCurve(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        var getCurves = new GetObject();
        getCurves.SetCommandPrompt("Select curves to drape");
        getCurves.GeometryFilter = ObjectType.Curve;
        getCurves.EnablePreSelect(true, true);
        getCurves.GroupSelect = true;
        getCurves.GetMultiple(1, 0);
        if (getCurves.CommandResult() != Result.Success)
            return getCurves.CommandResult();

        var sources = new List<(Guid ObjectId, Curve Curve, ObjectAttributes Attributes)>();
        for (int i = 0; i < getCurves.ObjectCount; i++)
        {
            ObjRef? reference = getCurves.Object(i);
            RhinoObject? rhinoObject = reference?.Object();
            Curve? curve = reference?.Curve();
            if (reference == null || rhinoObject == null || curve == null)
                continue;
            sources.Add((reference.ObjectId, curve.DuplicateCurve(), rhinoObject.Attributes.Duplicate()));
        }

        if (sources.Count == 0)
        {
            RhinoApp.WriteLine("No curves were selected for draping.");
            return Result.Nothing;
        }

        var excluded = sources.Select(source => source.ObjectId).ToHashSet();
        doc.Objects.UnselectAll();
        if (!TrySelectTargets(doc, excluded, out DrapeTargets? targets, out Result targetResult))
        {
            Dispose(sources.Select(source => source.Curve));
            return targetResult;
        }

        const string spacingKey = "MoleHill.DrapeCurve.Spacing";
        double automaticSpacing = Math.Max(
            doc.ModelAbsoluteTolerance * 10.0,
            sources.Max(item => item.Curve.GetLength()) / 100.0);
        double spacing = CommandOptionCache.GetLengthFromModelDefault(spacingKey, unitContext, automaticSpacing);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.DrapeCurve.ReplaceInput", false);
        bool lowest = CommandOptionCache.GetValue("MoleHill.DrapeCurve.Lowest", false);
        int missIndex = CommandOptionCache.GetValue("MoleHill.DrapeCurve.Miss", 0);

        while (true)
        {
            var getOptions = new GetOption();
            getOptions.SetCommandPrompt("Adjust drape options or press Enter to accept");
            getOptions.AcceptNothing(true);
            var spacingOption = new OptionDouble(spacing, doc.ModelAbsoluteTolerance, 1000000000.0);
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            var hitOption = new OptionToggle(lowest, "Top", "Lowest");
            getOptions.AddOptionDouble("Spacing", ref spacingOption);
            getOptions.AddOptionToggle("Hit", ref hitOption);
            int missOption = getOptions.AddOptionList("Miss", MissLabels, Math.Clamp(missIndex, 0, MissLabels.Length - 1));
            getOptions.AddOptionToggle("Output", ref replaceOption);

            GetResult optionResult = getOptions.Get();
            spacing = spacingOption.CurrentValue;
            replaceInput = replaceOption.CurrentValue;
            lowest = hitOption.CurrentValue;
            if (optionResult == GetResult.Option)
            {
                if (getOptions.OptionIndex() == missOption)
                    missIndex = getOptions.Option().CurrentListOptionIndex;
                continue;
            }

            if (optionResult != GetResult.Nothing)
            {
                Dispose(sources.Select(source => source.Curve));
                return getOptions.CommandResult();
            }

            break;
        }

        CommandOptionCache.SetLength(spacingKey, unitContext, spacing);
        CommandOptionCache.SetValue("MoleHill.DrapeCurve.ReplaceInput", replaceInput);
        CommandOptionCache.SetValue("MoleHill.DrapeCurve.Lowest", lowest);
        CommandOptionCache.SetValue("MoleHill.DrapeCurve.Miss", missIndex);

        var policy = (DrapeMissPolicy)Math.Clamp(missIndex, 0, 2);
        var options = new CurveDrapeOptions
        {
            Spacing = spacing,
            Tolerance = doc.ModelAbsoluteTolerance,
            MissPolicy = policy
        };

        var outputs = new List<(Guid SourceId, ObjectAttributes Attributes, List<PolylineCurve> Pieces)>();
        int failed = 0;
        for (int index = 0; index < sources.Count; index++)
        {
            CurveDrapeResult drape = TerrainInputCommandAlgorithms.DrapeCurve(sources[index].Curve, targets!, lowest, options);
            string label = sources.Count == 1 ? "Curve" : $"Curve {index + 1}";
            if (!drape.Succeeded)
            {
                failed++;
                RhinoApp.WriteLine($"{label}: not draped. {drape.Failure}");
                continue;
            }

            RhinoApp.WriteLine($"{label}: {DescribeDrape(drape, policy)}");
            outputs.Add((sources[index].ObjectId, sources[index].Attributes,
                drape.Runs.Select(TerrainInputCommandAlgorithms.ToPolylineCurve).ToList()));
        }

        try
        {
            if (outputs.Count == 0)
                return Result.Failure;

            if (!Commit(doc, sources, outputs, replaceInput))
            {
                RhinoApp.WriteLine("Could not commit the draped curves; the original document state was restored.");
                return Result.Failure;
            }

            if (failed > 0)
                RhinoApp.WriteLine($"{outputs.Count} of {sources.Count} curves draped; the rest are unchanged.");
            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            Dispose(outputs.SelectMany(output => output.Pieces));
            Dispose(sources.Select(source => source.Curve));
        }
    }

    public static Result RunDrapeCurveSection(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select curve to drape a section of");
        getObject.GeometryFilter = ObjectType.Curve;
        if (getObject.Get() != GetResult.Object)
            return getObject.CommandResult();

        ObjRef reference = getObject.Object(0);
        Curve? source = reference.Curve();
        if (source == null)
            return Result.Failure;

        var firstPick = new GetPoint();
        firstPick.SetCommandPrompt("Pick where the section starts");
        firstPick.Constrain(source, false);
        if (firstPick.Get() != GetResult.Point)
            return firstPick.CommandResult();
        Point3d firstPoint = firstPick.Point();

        var secondPick = new GetPoint();
        secondPick.SetCommandPrompt("Pick where the section ends");
        secondPick.Constrain(source, false);
        secondPick.DynamicDraw += (_, e) => e.Display.DrawPoint(firstPoint, TrackingColor);
        if (secondPick.Get() != GetResult.Point)
            return secondPick.CommandResult();
        Point3d secondPoint = secondPick.Point();

        if (!source.ClosestPoint(firstPoint, out double firstParameter) ||
            !source.ClosestPoint(secondPoint, out double secondParameter) ||
            Math.Abs(firstParameter - secondParameter) <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("Pick two different points on the curve.");
            return Result.Failure;
        }

        double low = Math.Min(firstParameter, secondParameter);
        double high = Math.Max(firstParameter, secondParameter);

        doc.Objects.UnselectAll();
        if (!TrySelectTargets(doc, new HashSet<Guid> { reference.ObjectId }, out DrapeTargets? targets, out Result targetResult))
            return targetResult;

        const string spacingKey = "MoleHill.DrapeCurveSection.Spacing";
        const string transitionKey = "MoleHill.DrapeCurveSection.Transition";
        double sectionLength = source.GetLength(new Interval(low, high));
        double spacing = CommandOptionCache.GetLengthFromModelDefault(
            spacingKey, unitContext, Math.Max(doc.ModelAbsoluteTolerance * 10.0, sectionLength / 50.0));
        double transition = CommandOptionCache.GetLength(transitionKey, unitContext, 5.0);
        bool smooth = CommandOptionCache.GetValue("MoleHill.DrapeCurveSection.SmoothFalloff", true);
        bool lowest = CommandOptionCache.GetValue("MoleHill.DrapeCurve.Lowest", false);
        int missIndex = CommandOptionCache.GetValue("MoleHill.DrapeCurveSection.Miss", 0);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.DrapeCurveSection.ReplaceInput", true);

        Curve? Build(out string? error, out CurveDrapeResult? drape)
        {
            return TerrainInputCommandAlgorithms.TryBuildDrapedSection(
                source, low, high, targets!, lowest, new CurveDrapeOptions
                {
                    Spacing = spacing,
                    Tolerance = doc.ModelAbsoluteTolerance,
                    MissPolicy = missIndex == 1 ? DrapeMissPolicy.Bridge : DrapeMissPolicy.KeepOriginal,
                    TieInLength = transition,
                    SmoothTieIn = smooth
                }, out drape, out error);
        }

        while (true)
        {
            var options = new GetPoint();
            Curve? previewCurve = Build(out string? previewError, out _);
            options.SetCommandPrompt(previewCurve == null
                ? $"{previewError} Adjust options"
                : "Drape section options. Press Enter to accept");
            previewCurve?.Dispose();
            options.AcceptNothing(true);
            options.AcceptPoint(false);
            var spacingOption = new OptionDouble(spacing, doc.ModelAbsoluteTolerance, 1000000000.0);
            var transitionOption = new OptionDouble(transition, true, 0.0);
            var falloffOption = new OptionToggle(smooth, "Linear", "Smooth");
            var hitOption = new OptionToggle(lowest, "Top", "Lowest");
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            options.AddOptionDouble("Spacing", ref spacingOption);
            options.AddOptionDouble("Transition", ref transitionOption);
            options.AddOptionToggle("Falloff", ref falloffOption);
            options.AddOptionToggle("Hit", ref hitOption);
            int missOption = options.AddOptionList("Miss", SectionMissLabels, Math.Clamp(missIndex, 0, 1));
            options.AddOptionToggle("Output", ref replaceOption);
            options.DynamicDraw += (_, e) =>
            {
                e.Display.DrawPoint(firstPoint, TrackingColor);
                e.Display.DrawPoint(secondPoint, TrackingColor);
                Curve? candidate = Build(out _, out _);
                if (candidate == null)
                    return;
                e.Display.DrawCurve(candidate, TrackingColor, 2);
                candidate.Dispose();
            };

            GetResult result = options.Get();
            spacing = spacingOption.CurrentValue;
            transition = Math.Max(0.0, transitionOption.CurrentValue);
            smooth = falloffOption.CurrentValue;
            lowest = hitOption.CurrentValue;
            replaceInput = replaceOption.CurrentValue;
            if (result == GetResult.Option)
            {
                if (options.OptionIndex() == missOption)
                    missIndex = options.Option().CurrentListOptionIndex;
                continue;
            }

            if (result != GetResult.Nothing)
                return options.CommandResult();
            break;
        }

        CommandOptionCache.SetLength(spacingKey, unitContext, spacing);
        CommandOptionCache.SetLength(transitionKey, unitContext, transition);
        CommandOptionCache.SetValue("MoleHill.DrapeCurveSection.SmoothFalloff", smooth);
        CommandOptionCache.SetValue("MoleHill.DrapeCurve.Lowest", lowest);
        CommandOptionCache.SetValue("MoleHill.DrapeCurveSection.Miss", missIndex);
        CommandOptionCache.SetValue("MoleHill.DrapeCurveSection.ReplaceInput", replaceInput);

        Curve? resultCurve = Build(out string? error, out CurveDrapeResult? report);
        if (resultCurve == null)
        {
            RhinoApp.WriteLine(error ?? "The section could not be draped.");
            return Result.Failure;
        }

        uint undoRecord = doc.BeginUndoRecord("Drape Curve Section");
        try
        {
            bool committed = replaceInput
                ? doc.Objects.Replace(reference.ObjectId, resultCurve)
                : doc.Objects.AddCurve(resultCurve, reference.Object()?.Attributes.Duplicate()) != Guid.Empty;
            if (!committed)
                return Result.Failure;
        }
        finally
        {
            resultCurve.Dispose();
            doc.EndUndoRecord(undoRecord);
        }

        RhinoApp.WriteLine($"Section draped: {DescribeDrape(report!, missIndex == 1 ? DrapeMissPolicy.Bridge : DrapeMissPolicy.KeepOriginal)}");
        doc.Views.Redraw();
        return Result.Success;
    }

    private static string DescribeDrape(CurveDrapeResult drape, DrapeMissPolicy policy)
    {
        string pieces = drape.Runs.Count == 1 ? "1 piece" : $"{drape.Runs.Count} pieces";
        if (drape.MissedStations == 0)
            return $"{pieces}, every one of {drape.StationCount} stations on a surface.";

        string handled = policy switch
        {
            DrapeMissPolicy.Bridge => $"{drape.BridgedStations} bridged",
            DrapeMissPolicy.KeepOriginal => $"{drape.KeptStations} kept at their own Z",
            _ => "split there"
        };
        string dropped = drape.DroppedStations > 0 ? $", {drape.DroppedStations} left out" : string.Empty;
        return $"{pieces}; {drape.MissedStations} of {drape.StationCount} stations off the surface ({handled}{dropped}).";
    }

    /// <summary>
    /// Asks for the surfaces to drape onto: any number of meshes, surfaces and polysurfaces, or the active
    /// MoleHill terrain's final build. The curves being draped are refused as targets.
    /// </summary>
    private static bool TrySelectTargets(RhinoDoc doc, HashSet<Guid> excluded, out DrapeTargets? targets, out Result result)
    {
        targets = null;
        result = Result.Cancel;
        var getTarget = new GetObject();
        getTarget.SetCommandPrompt("Select meshes or surfaces to drape onto");
        getTarget.GeometryFilter = ObjectType.Mesh | ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
        getTarget.EnablePreSelect(false, true);
        getTarget.SetCustomGeometryFilter((rhinoObject, _, _) => rhinoObject == null || !excluded.Contains(rhinoObject.Id));
        int activeTerrainOption = getTarget.AddOption("ActiveMoleHillTerrain");

        var meshes = new List<Mesh>();
        GetResult targetResult = getTarget.GetMultiple(1, 0);
        if (targetResult == GetResult.Option && getTarget.OptionIndex() == activeTerrainOption)
        {
            TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
            if (terrain == null)
            {
                RhinoApp.WriteLine("Select an active MoleHill terrain before draping.");
                result = Result.Nothing;
                return false;
            }

            Mesh? finalMesh = TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
            if (finalMesh == null)
            {
                RhinoApp.WriteLine($"Active terrain '{terrain.Name}' has no current final build. Rebuild it before draping.");
                result = Result.Nothing;
                return false;
            }

            meshes.Add(finalMesh);
        }
        else if (targetResult == GetResult.Object)
        {
            for (int i = 0; i < getTarget.ObjectCount; i++)
            {
                if (getTarget.Object(i).Object() is { } targetObject)
                    meshes.AddRange(TerrainInputCommandService.CreateProjectionMeshes(targetObject.Geometry));
            }
        }
        else
        {
            result = getTarget.CommandResult();
            return false;
        }

        targets = DrapeTargets.Create(meshes, doc.ModelAbsoluteTolerance, out int unreadable);
        Dispose(meshes);
        if (unreadable > 0)
            RhinoApp.WriteLine($"{unreadable} target mesh(es) could not be read and were left out.");
        if (targets.Count == 0)
        {
            RhinoApp.WriteLine("None of the selected objects could be converted into a usable mesh.");
            result = Result.Failure;
            return false;
        }

        result = Result.Success;
        return true;
    }

    /// <summary>
    /// Writes every draped curve in one go, restoring the document if any write fails. Replacing a curve
    /// that split into pieces replaces it with the first and adds the rest beside it on its layer.
    /// </summary>
    private static bool Commit(
        RhinoDoc doc,
        IReadOnlyList<(Guid ObjectId, Curve Curve, ObjectAttributes Attributes)> sources,
        IReadOnlyList<(Guid SourceId, ObjectAttributes Attributes, List<PolylineCurve> Pieces)> outputs,
        bool replaceInput)
    {
        uint undoRecord = doc.BeginUndoRecord("Drape Curves");
        var replacedIds = new List<Guid>();
        var addedIds = new List<Guid>();
        try
        {
            foreach (var output in outputs)
            {
                for (int piece = 0; piece < output.Pieces.Count; piece++)
                {
                    if (replaceInput && piece == 0)
                    {
                        if (!doc.Objects.Replace(output.SourceId, output.Pieces[piece]))
                            return RollBack();
                        replacedIds.Add(output.SourceId);
                        continue;
                    }

                    Guid added = doc.Objects.AddCurve(output.Pieces[piece], output.Attributes);
                    if (added == Guid.Empty)
                        return RollBack();
                    addedIds.Add(added);
                }
            }

            return true;
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }

        bool RollBack()
        {
            RhinoDocumentHelpers.DeleteObjects(doc, addedIds);
            var replaced = replacedIds.ToHashSet();
            foreach (var source in sources)
            {
                if (replaced.Contains(source.ObjectId))
                    doc.Objects.Replace(source.ObjectId, source.Curve);
            }

            return false;
        }
    }

    private static void Dispose(IEnumerable<GeometryBase> geometry)
    {
        foreach (GeometryBase item in geometry)
            item.Dispose();
    }
}
