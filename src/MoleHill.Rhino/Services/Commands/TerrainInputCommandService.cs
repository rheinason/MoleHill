// Rhino document workflows for terrain-input validation, draping, splitting, and wall rails.
using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using DrawingColor = System.Drawing.Color;

namespace MoleHill.Rhino.Services;

internal static class TerrainInputCommandService
{
    private static DrawingColor TrackingColor => global::Rhino.ApplicationSettings.AppearanceSettings.TrackingColor;

    private static DrawingColor FeedbackColor => global::Rhino.ApplicationSettings.AppearanceSettings.FeedbackColor;

    public static Result RunValidateTerrainInputs(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return Result.Failure;

        var getObjects = new GetObject();
        getObjects.SetCommandPrompt("Select terrain points and curves to validate");
        getObjects.GeometryFilter = ObjectType.Point | ObjectType.Curve;
        getObjects.EnablePreSelect(true, true);
        getObjects.GroupSelect = true;
        getObjects.GetMultiple(1, 0);
        if (getObjects.CommandResult() != Result.Success)
            return getObjects.CommandResult();

        var selected = new List<TerrainInputGeometry>();
        for (int i = 0; i < getObjects.ObjectCount; i++)
        {
            ObjRef? reference = getObjects.Object(i);
            RhinoObject? rhinoObject = reference?.Object();
            if (reference == null || rhinoObject == null)
                continue;

            ObjectAttributes attributes = rhinoObject.Attributes.Duplicate();
            switch (rhinoObject.Geometry)
            {
                case Curve curve:
                    selected.Add(new TerrainInputGeometry
                    {
                        ObjectId = reference.ObjectId,
                        Curve = curve.DuplicateCurve(),
                        Attributes = attributes
                    });
                    break;
                case Point point:
                    selected.Add(new TerrainInputGeometry
                    {
                        ObjectId = reference.ObjectId,
                        Point = point.Location,
                        Attributes = attributes
                    });
                    break;
            }
        }

        if (selected.Count == 0)
        {
            RhinoApp.WriteLine("No terrain points or curves were selected.");
            return Result.Nothing;
        }

        double defaultTolerance = doc.ModelAbsoluteTolerance;
        TerrainValidationDialogResult dialogResult = TerrainInputValidationDialog.Show(doc, selected, defaultTolerance);
        if (!dialogResult.Accepted)
        {
            foreach (TerrainInputGeometry item in selected)
                item.Curve?.Dispose();
            return Result.Cancel;
        }

        TerrainValidationPreparation preparation = dialogResult.Preparation ??
            TerrainInputCommandAlgorithms.PrepareValidation(selected, dialogResult.Options);

        uint undoRecord = doc.BeginUndoRecord("Validate Terrain Inputs");
        try
        {
            var outputIds = new List<Guid>(preparation.Objects.Count);
            var retainedIds = new HashSet<Guid>();
            var replacedIds = new List<Guid>();
            var addedIds = new List<Guid>();
            foreach (TerrainInputGeometry item in preparation.Objects)
            {
                Guid id = item.ObjectId;
                if (id != Guid.Empty)
                {
                    bool replaced = true;
                    if (item.GeometryChanged && item.Curve != null)
                        replaced = doc.Objects.Replace(id, item.Curve);
                    else if (item.GeometryChanged && item.Point is { } replacementPoint)
                        replaced = doc.Objects.Replace(id, replacementPoint);

                    if (!replaced)
                    {
                        RollBackTerrainValidation(doc, selected, replacedIds, addedIds);
                        RhinoApp.WriteLine($"Could not replace terrain input {id}; no object settings were reassigned.");
                        return Result.Failure;
                    }

                    if (item.GeometryChanged)
                        replacedIds.Add(id);
                    retainedIds.Add(id);
                }
                else if (item.Curve != null)
                {
                    id = doc.Objects.AddCurve(item.Curve, item.Attributes);
                }
                else if (item.Point is { } point)
                {
                    id = doc.Objects.AddPoint(point, item.Attributes);
                }

                if (id == Guid.Empty)
                {
                    RollBackTerrainValidation(doc, selected, replacedIds, addedIds);
                    RhinoApp.WriteLine("Could not add a prepared terrain input; the original selection was restored.");
                    return Result.Failure;
                }

                if (item.ObjectId == Guid.Empty)
                    addedIds.Add(id);
                outputIds.Add(id);
            }

            foreach (TerrainInputGeometry item in selected)
            {
                if (!retainedIds.Contains(item.ObjectId))
                    doc.Objects.Delete(item.ObjectId, quiet: true);
            }

            doc.Objects.Select(outputIds);
            RhinoApp.WriteLine(FormatValidationSummary(preparation.Summary));
            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            foreach (TerrainInputGeometry item in selected)
                item.Curve?.Dispose();
            foreach (TerrainInputGeometry item in preparation.Objects)
                item.Curve?.Dispose();
            doc.EndUndoRecord(undoRecord);
        }
    }

    public static Result RunSplitAtIntersections(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return Result.Failure;

        var getCurves = new GetObject();
        getCurves.SetCommandPrompt("Select curves to split at intersections");
        getCurves.GeometryFilter = ObjectType.Curve;
        getCurves.EnablePreSelect(true, true);
        getCurves.GroupSelect = true;
        getCurves.GetMultiple(2, 0);
        if (getCurves.CommandResult() != Result.Success)
            return getCurves.CommandResult();

        double tolerance = doc.ModelAbsoluteTolerance;
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

        if (sources.Count < 2)
        {
            foreach (var source in sources)
                source.Curve.Dispose();
            RhinoApp.WriteLine("Select at least two curves.");
            return Result.Nothing;
        }

        var parameters = Enumerable.Range(0, sources.Count)
            .Select(_ => new List<double>())
            .ToArray();
        for (int i = 0; i < sources.Count; i++)
        {
            for (int j = i + 1; j < sources.Count; j++)
            {
                TerrainCurveSplitParameters first = TerrainInputCommandAlgorithms.GetIntersectionSplitParameters(
                    sources[i].Curve,
                    sources[j].Curve,
                    tolerance);
                TerrainCurveSplitParameters second = TerrainInputCommandAlgorithms.GetIntersectionSplitParameters(
                    sources[j].Curve,
                    sources[i].Curve,
                    tolerance);
                parameters[i].AddRange(first.Parameters);
                parameters[j].AddRange(second.Parameters);
            }
        }

        var replacements = new List<(Guid ObjectId, ObjectAttributes Attributes, Curve[] Segments)>();
        int splitCurveCount = 0;
        int segmentCount = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            double[] unique = TerrainInputCommandAlgorithms.NormalizeSplitParameters(
                sources[i].Curve,
                parameters[i],
                tolerance);
            Curve[] segments = unique.Length == 0
                ? Array.Empty<Curve>()
                : sources[i].Curve.Split(unique);

            if (segments.Length == 0)
                continue;

            splitCurveCount++;
            segmentCount += segments.Length;
            replacements.Add((sources[i].ObjectId, sources[i].Attributes, segments));
        }

        if (splitCurveCount == 0)
        {
            foreach (var source in sources)
                source.Curve.Dispose();
            RhinoApp.WriteLine("No intersections were found among the selected curves.");
            return Result.Success;
        }

        uint undoRecord = doc.BeginUndoRecord("Split Curves At Intersections");
        try
        {
            var replacedIds = new List<Guid>();
            var addedIds = new List<Guid>();
            foreach (var replacement in replacements)
            {
                if (!doc.Objects.Replace(replacement.ObjectId, replacement.Segments[0]))
                {
                    RollBackCurveOutputs(doc, sources, replacedIds, addedIds);
                    RhinoApp.WriteLine("Could not replace a source curve; the original curves were restored.");
                    return Result.Failure;
                }

                replacedIds.Add(replacement.ObjectId);
                for (int segmentIndex = 1; segmentIndex < replacement.Segments.Length; segmentIndex++)
                {
                    Guid addedId = doc.Objects.AddCurve(replacement.Segments[segmentIndex], replacement.Attributes);
                    if (addedId == Guid.Empty)
                    {
                        RollBackCurveOutputs(doc, sources, replacedIds, addedIds);
                        RhinoApp.WriteLine("Could not add a split segment; the original curves were restored.");
                        return Result.Failure;
                    }

                    addedIds.Add(addedId);
                }
            }

            RhinoApp.WriteLine($"Split {splitCurveCount:N0} curves into {segmentCount:N0} segments.");
            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            foreach (var source in sources)
                source.Curve.Dispose();
            foreach (var replacement in replacements)
                foreach (Curve segment in replacement.Segments)
                    segment.Dispose();
            doc.EndUndoRecord(undoRecord);
        }
    }

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

        doc.Objects.UnselectAll();
        var getTarget = new GetObject();
        getTarget.SetCommandPrompt("Select one mesh or surface to drape onto");
        getTarget.GeometryFilter = ObjectType.Mesh | ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
        getTarget.EnablePreSelect(true, true);
        int activeTerrainOption = getTarget.AddOption("ActiveMoleHillTerrain");

        List<Mesh> meshes;
        GetResult targetResult = getTarget.Get();
        if (targetResult == GetResult.Option && getTarget.OptionIndex() == activeTerrainOption)
        {
            TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
            if (terrain == null)
            {
                DisposeSourceCurves(sources);
                RhinoApp.WriteLine("Select an active MoleHill terrain before draping.");
                return Result.Nothing;
            }

            Mesh? finalMesh = TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
            if (finalMesh == null)
            {
                DisposeSourceCurves(sources);
                RhinoApp.WriteLine($"Active terrain '{terrain.Name}' has no current final build. Rebuild it before draping.");
                return Result.Nothing;
            }

            meshes = new List<Mesh> { finalMesh };
        }
        else if (targetResult == GetResult.Object)
        {
            ObjRef targetReference = getTarget.Object(0);
            RhinoObject? targetObject = targetReference.Object();
            if (targetObject == null)
            {
                DisposeSourceCurves(sources);
                return Result.Failure;
            }

            meshes = CreateProjectionMeshes(targetObject.Geometry);
        }
        else
        {
            DisposeSourceCurves(sources);
            return getTarget.CommandResult();
        }

        if (meshes.Count == 0)
        {
            DisposeSourceCurves(sources);
            RhinoApp.WriteLine("The selected object could not be converted into a usable mesh.");
            return Result.Failure;
        }

        const string spacingKey = "MoleHill.DrapeCurve.Spacing";
        double automaticSpacing = Math.Max(
            doc.ModelAbsoluteTolerance * 10.0,
            sources.Max(item => item.Curve.GetLength()) / 100.0);
        double spacing = CommandOptionCache.GetLengthFromModelDefault(spacingKey, unitContext, automaticSpacing);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.DrapeCurve.ReplaceInput", false);

        while (true)
        {
            var getOptions = new GetOption();
            getOptions.SetCommandPrompt("Adjust drape options or press Enter to accept");
            getOptions.AcceptNothing(true);
            var spacingOption = new OptionDouble(spacing, doc.ModelAbsoluteTolerance, 1000000000.0);
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            getOptions.AddOptionDouble("Spacing", ref spacingOption);
            getOptions.AddOptionToggle("Output", ref replaceOption);

            GetResult optionResult = getOptions.Get();
            if (optionResult == GetResult.Option)
            {
                spacing = spacingOption.CurrentValue;
                replaceInput = replaceOption.CurrentValue;
                continue;
            }

            if (optionResult != GetResult.Nothing)
            {
                DisposeSourceCurves(sources);
                DisposeMeshes(meshes);
                return getOptions.CommandResult();
            }

            spacing = spacingOption.CurrentValue;
            replaceInput = replaceOption.CurrentValue;
            break;
        }

        CommandOptionCache.SetLength(spacingKey, unitContext, spacing);
        CommandOptionCache.SetValue("MoleHill.DrapeCurve.ReplaceInput", replaceInput);

        var outputs = new List<(Guid ObjectId, ObjectAttributes Attributes, PolylineCurve Curve)>();
        foreach (var source in sources)
        {
            if (!TerrainInputCommandAlgorithms.TryCreateDrapedPolyline(
                    source.Curve,
                    meshes,
                    spacing,
                    doc.ModelAbsoluteTolerance,
                    out Polyline polyline,
                    out string? error))
            {
                DisposeOutputCurves(outputs);
                DisposeSourceCurves(sources);
                DisposeMeshes(meshes);
                RhinoApp.WriteLine($"Drape failed for one curve: {error}");
                return Result.Failure;
            }

            outputs.Add((source.ObjectId, source.Attributes, new PolylineCurve(polyline)));
        }

        uint undoRecord = doc.BeginUndoRecord("Drape Curves");
        try
        {
            var replacedIds = new List<Guid>();
            var addedIds = new List<Guid>();
            foreach (var output in outputs)
            {
                bool succeeded;
                if (replaceInput)
                {
                    succeeded = doc.Objects.Replace(output.ObjectId, output.Curve);
                    if (succeeded)
                        replacedIds.Add(output.ObjectId);
                }
                else
                {
                    Guid addedId = doc.Objects.AddCurve(output.Curve, output.Attributes);
                    succeeded = addedId != Guid.Empty;
                    if (succeeded)
                        addedIds.Add(addedId);
                }

                if (succeeded)
                    continue;

                RollBackCurveOutputs(doc, sources, replacedIds, addedIds);
                RhinoApp.WriteLine("Could not commit all draped curves; the original document state was restored.");
                return Result.Failure;
            }

            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            DisposeOutputCurves(outputs);
            DisposeSourceCurves(sources);
            DisposeMeshes(meshes);
            doc.EndUndoRecord(undoRecord);
        }
    }

    public static Result RunCreateWall(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        const string offsetKey = "MoleHill.CreateWall.PlanOffset";
        const string heightKey = "MoleHill.CreateWall.HeightOffset";
        double planOffset = CommandOptionCache.GetLength(offsetKey, unitContext, 1.0);
        double heightOffset = CommandOptionCache.GetLength(heightKey, unitContext, 1.0);
        bool isLeft = CommandOptionCache.GetValue("MoleHill.CreateWall.IsLeft", false);

        var points = new List<Point3d>();
        while (true)
        {
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt(points.Count == 0 ? "Start wall rail" : "Place wall rail point or press Enter to finish");
            getPoint.AcceptNothing(points.Count >= 2);
            if (points.Count > 0)
                getPoint.SetBasePoint(points[^1], true);

            var offsetOption = new OptionDouble(planOffset, RhinoMath.ZeroTolerance, 1000000000.0);
            var heightOption = new OptionDouble(heightOffset, -1000000000.0, 1000000000.0);
            var sideOption = new OptionToggle(isLeft, "Right", "Left");
            getPoint.AddOptionDouble("PlanOffset", ref offsetOption);
            getPoint.AddOptionDouble("HeightOffset", ref heightOption);
            getPoint.AddOptionToggle("Side", ref sideOption);
            getPoint.DynamicDraw += (_, e) =>
            {
                if (points.Count == 0)
                    return;

                var previewPoints = new List<Point3d>(points) { e.CurrentPoint };
                double signedOffset = GeometryCommandAlgorithms.GetSignedOffsetDistanceForSide(
                    sideOption.CurrentValue,
                    offsetOption.CurrentValue);
                if (!TerrainInputCommandAlgorithms.TryCreateWallRails(
                        previewPoints,
                        signedOffset,
                        heightOption.CurrentValue,
                        doc.ModelAbsoluteTolerance,
                        out Polyline sourceRail,
                        out Polyline generatedRail,
                        out string? _))
                    return;

                e.Display.DrawPolyline(sourceRail, TrackingColor, 2);
                e.Display.DrawPolyline(generatedRail, FeedbackColor, 2);
                e.Display.DrawDot(
                    e.CurrentPoint,
                    $"{offsetOption.CurrentValue:G4} {(sideOption.CurrentValue ? "Left" : "Right")}, ΔZ {heightOption.CurrentValue:G4}",
                    TrackingColor,
                    FeedbackColor);
            };

            GetResult pointResult = getPoint.Get();
            if (pointResult == GetResult.Option)
            {
                planOffset = Math.Abs(offsetOption.CurrentValue);
                heightOffset = heightOption.CurrentValue;
                isLeft = sideOption.CurrentValue;
                continue;
            }

            if (pointResult == GetResult.Nothing)
                break;

            if (pointResult != GetResult.Point)
                return getPoint.CommandResult();

            planOffset = Math.Abs(offsetOption.CurrentValue);
            heightOffset = heightOption.CurrentValue;
            isLeft = sideOption.CurrentValue;
            points.Add(getPoint.Point());
        }

        if (points.Count < 2)
        {
            RhinoApp.WriteLine("A wall requires at least two points.");
            return Result.Nothing;
        }

        if (planOffset <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("Plan offset must be greater than zero.");
            return Result.Failure;
        }

        CommandOptionCache.SetLength(offsetKey, unitContext, planOffset);
        CommandOptionCache.SetLength(heightKey, unitContext, heightOffset);
        CommandOptionCache.SetValue("MoleHill.CreateWall.IsLeft", isLeft);

        double signedPlanOffset = GeometryCommandAlgorithms.GetSignedOffsetDistanceForSide(isLeft, planOffset);
        if (!TerrainInputCommandAlgorithms.TryCreateWallRails(
                points,
                signedPlanOffset,
                heightOffset,
                doc.ModelAbsoluteTolerance,
                out Polyline sourceRailFinal,
                out Polyline generatedRailFinal,
                out string? errorMessage))
        {
            RhinoApp.WriteLine(errorMessage ?? "Failed to create the wall rails.");
            return Result.Failure;
        }

        uint undoRecord = doc.BeginUndoRecord("Create Wall Rails");
        try
        {
            Guid first = doc.Objects.AddPolyline(sourceRailFinal);
            Guid second = doc.Objects.AddPolyline(generatedRailFinal);
            if (first == Guid.Empty || second == Guid.Empty)
            {
                if (first != Guid.Empty)
                    doc.Objects.Delete(first, quiet: true);
                if (second != Guid.Empty)
                    doc.Objects.Delete(second, quiet: true);
                return Result.Failure;
            }

            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }
    }

    private static List<Mesh> CreateProjectionMeshes(GeometryBase geometry)
    {
        var meshes = new List<Mesh>();
        switch (geometry)
        {
            case Mesh mesh:
                meshes.Add(mesh.DuplicateMesh());
                break;
            case Brep brep:
                meshes.AddRange(Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>());
                break;
            case Extrusion extrusion:
                using (Brep? extrusionBrep = extrusion.ToBrep())
                {
                    if (extrusionBrep != null)
                    {
                        meshes.AddRange(
                            Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.FastRenderMesh) ??
                            Array.Empty<Mesh>());
                    }
                }
                break;
            case Surface surface:
                using (Brep? surfaceBrep = Brep.CreateFromSurface(surface))
                {
                    if (surfaceBrep != null)
                    {
                        meshes.AddRange(
                            Mesh.CreateFromBrep(surfaceBrep, MeshingParameters.FastRenderMesh) ??
                            Array.Empty<Mesh>());
                    }
                }
                break;
        }

        return meshes;
    }

    private static void DisposeSourceCurves(IEnumerable<(Guid ObjectId, Curve Curve, ObjectAttributes Attributes)> sources)
    {
        foreach (var source in sources)
            source.Curve.Dispose();
    }

    private static void DisposeMeshes(IEnumerable<Mesh> meshes)
    {
        foreach (Mesh mesh in meshes)
            mesh.Dispose();
    }

    private static void DisposeOutputCurves(IEnumerable<(Guid ObjectId, ObjectAttributes Attributes, PolylineCurve Curve)> outputs)
    {
        foreach (var output in outputs)
            output.Curve.Dispose();
    }

    private static void RollBackTerrainValidation(
        RhinoDoc doc,
        IReadOnlyList<TerrainInputGeometry> originals,
        IReadOnlyList<Guid> replacedIds,
        IReadOnlyList<Guid> addedIds)
    {
        RhinoDocumentHelpers.DeleteObjects(doc, addedIds);
        var replaced = replacedIds.ToHashSet();
        foreach (TerrainInputGeometry original in originals)
        {
            if (!replaced.Contains(original.ObjectId))
                continue;

            if (original.Curve != null)
                doc.Objects.Replace(original.ObjectId, original.Curve);
            else if (original.Point is { } point)
                doc.Objects.Replace(original.ObjectId, point);
        }
    }

    private static void RollBackCurveOutputs(
        RhinoDoc doc,
        IReadOnlyList<(Guid ObjectId, Curve Curve, ObjectAttributes Attributes)> originals,
        IReadOnlyList<Guid> replacedIds,
        IReadOnlyList<Guid> addedIds)
    {
        RhinoDocumentHelpers.DeleteObjects(doc, addedIds);
        var replaced = replacedIds.ToHashSet();
        foreach (var original in originals)
        {
            if (replaced.Contains(original.ObjectId))
                doc.Objects.Replace(original.ObjectId, original.Curve);
        }
    }

    private static string FormatValidationSummary(TerrainValidationSummary summary)
    {
        return $"Terrain input validation: {summary.SelectedObjects:N0} selected, " +
               $"{summary.OutputObjects:N0} output; removed {summary.DuplicateObjectsRemoved:N0} duplicates and " +
               $"{summary.DuplicateSegmentsRemoved:N0} duplicate segments, " +
               $"joined {summary.JoinedCurveCount:N0} curve links, collapsed {summary.ShortSegmentsCollapsed:N0} short segments, " +
               $"removed {summary.DuplicateVerticesRemoved:N0} duplicate vertices.";
    }
}
