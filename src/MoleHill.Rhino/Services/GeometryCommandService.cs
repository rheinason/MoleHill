using System.Drawing;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace MoleHill.Rhino.Services;

internal static class GeometryCommandService
{
    private static Color TrackingColor => global::Rhino.ApplicationSettings.AppearanceSettings.TrackingColor;

    private static Color FeedbackColor => global::Rhino.ApplicationSettings.AppearanceSettings.FeedbackColor;

    public static Result RunTwoPointInterpolation(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        var getLowPoint = new GetPoint();
        getLowPoint.SetCommandPrompt("Low point");
        if (getLowPoint.Get() != GetResult.Point)
            return getLowPoint.CommandResult();

        Point3d lowPoint = getLowPoint.Point();

        var getHighPoint = new GetPoint();
        getHighPoint.SetCommandPrompt("High point");
        if (getHighPoint.Get() != GetResult.Point)
            return getHighPoint.CommandResult();

        Point3d highPoint = getHighPoint.Point();

        while (true)
        {
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt("Place point or press Enter to finish");
            getPoint.AcceptNothing(true);
            getPoint.DynamicDraw += (_, e) =>
            {
                Point3d previewPoint = e.CurrentPoint;
                double elevation = GeometryCommandAlgorithms.InterpolateTwoPointElevation(lowPoint, highPoint, previewPoint);
                var liftedPoint = new Point3d(previewPoint.X, previewPoint.Y, elevation);
                e.Display.DrawPoint(liftedPoint);
                e.Display.DrawDot(liftedPoint, elevation.ToString("F3"), TrackingColor, FeedbackColor);
            };

            GetResult result = getPoint.Get();
            if (result == GetResult.Nothing)
                return Result.Success;

            if (result != GetResult.Point)
                return getPoint.CommandResult();

            Point3d point = getPoint.Point();
            double z = GeometryCommandAlgorithms.InterpolateTwoPointElevation(lowPoint, highPoint, point);
            doc.Objects.AddPoint(new Point3d(point.X, point.Y, z));
            doc.Views.Redraw();
        }
    }

    public static Result RunGradientInterpolation(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        var getBasePoint = new GetPoint();
        getBasePoint.SetCommandPrompt("Base point");
        if (getBasePoint.Get() != GetResult.Point)
            return getBasePoint.CommandResult();

        Point3d basePoint = getBasePoint.Point();
        double promille = CommandOptionCache.GetValue("MoleHill.GradientInterpolation.Promille", 100.0);

        while (true)
        {
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt("Place point or press Enter to finish");
            getPoint.AcceptNothing(true);
            var promilleOption = new OptionDouble(promille, -1000.0, 1000.0);
            getPoint.AddOptionDouble("Promille", ref promilleOption);
            getPoint.DynamicDraw += (_, e) =>
            {
                Point3d previewPoint = e.CurrentPoint;
                double elevation = GeometryCommandAlgorithms.InterpolateGradientElevation(basePoint, previewPoint, promilleOption.CurrentValue);
                var liftedPoint = new Point3d(previewPoint.X, previewPoint.Y, elevation);
                e.Display.DrawPoint(liftedPoint);
                e.Display.DrawDot(liftedPoint, elevation.ToString("F3"), TrackingColor, FeedbackColor);
            };

            GetResult result = getPoint.Get();
            if (result == GetResult.Option)
            {
                promille = promilleOption.CurrentValue;
                CommandOptionCache.SetValue("MoleHill.GradientInterpolation.Promille", promille);
                continue;
            }

            if (result == GetResult.Nothing)
                return Result.Success;

            if (result != GetResult.Point)
                return getPoint.CommandResult();

            promille = promilleOption.CurrentValue;
            CommandOptionCache.SetValue("MoleHill.GradientInterpolation.Promille", promille);

            Point3d point = getPoint.Point();
            double z = GeometryCommandAlgorithms.InterpolateGradientElevation(basePoint, point, promille);
            doc.Objects.AddPoint(new Point3d(point.X, point.Y, z));
            doc.Views.Redraw();
        }
    }

    public static Result RunSlopeCurve(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select curve to slope");
        getObject.GeometryFilter = ObjectType.Curve;
        getObject.EnablePreSelect(true, true);
        if (getObject.Get() != GetResult.Object)
            return getObject.CommandResult();

        ObjRef objRef = getObject.Object(0);
        Curve? sourceCurve = objRef.Curve();
        if (sourceCurve == null)
            return Result.Failure;

        bool usePercentage = GeometryCommandAlgorithms.ShouldUseSlopePercentageInput(
            sourceCurve.PointAtStart.Z,
            sourceCurve.PointAtEnd.Z,
            doc.ModelAbsoluteTolerance);
        double percentage = CommandOptionCache.GetValue("MoleHill.SlopeCurve.Percentage", 5.0);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.SlopeCurve.ReplaceInput", true);

        while (true)
        {
            var getOption = new GetPoint();
            getOption.SetCommandPrompt(
                usePercentage
                    ? "Adjust slope options or press Enter to accept"
                    : "Curve already has slope; press Enter to use endpoints");
            getOption.AcceptNothing(true);
            getOption.AcceptPoint(false);
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            var percentageOption = new OptionDouble(percentage, -100000.0, 100000.0);
            if (usePercentage)
                getOption.AddOptionDouble("Percent", ref percentageOption);

            getOption.AddOptionToggle("ReplaceInput", ref replaceOption);
            getOption.DynamicDraw += (_, e) =>
            {
                Curve? previewCurve;
                double previewSlopeRatio;
                string? previewError;
                bool previewSucceeded = usePercentage
                    ? GeometryCommandAlgorithms.TryCreateSlopeCurve(
                        sourceCurve,
                        percentageOption.CurrentValue / 100.0,
                        out previewCurve,
                        out previewError)
                    : GeometryCommandAlgorithms.TryCreateSlopeCurveFromEndPoints(
                        sourceCurve,
                        out previewCurve,
                        out previewSlopeRatio,
                        out previewError);

                if (!previewSucceeded || previewCurve == null)
                    return;

                e.Display.DrawCurve(previewCurve, TrackingColor, 2);
                previewCurve.Dispose();
            };

            GetResult optionResult = getOption.Get();
            if (optionResult == GetResult.Option)
            {
                if (usePercentage)
                    percentage = percentageOption.CurrentValue;

                replaceInput = replaceOption.CurrentValue;
                CommandOptionCache.SetValue("MoleHill.SlopeCurve.Percentage", percentage);
                CommandOptionCache.SetValue("MoleHill.SlopeCurve.ReplaceInput", replaceInput);
                continue;
            }

            if (optionResult != GetResult.Nothing)
                return getOption.CommandResult();

            break;
        }

        Curve? resultCurve;
        string? error;
        double slopeRatio = 0.0;
        bool succeeded = usePercentage
            ? GeometryCommandAlgorithms.TryCreateSlopeCurve(sourceCurve, percentage / 100.0, out resultCurve, out error)
            : GeometryCommandAlgorithms.TryCreateSlopeCurveFromEndPoints(sourceCurve, out resultCurve, out slopeRatio, out error);

        if (!succeeded || resultCurve == null)
        {
            RhinoApp.WriteLine(error ?? "Failed to slope the selected curve.");
            return Result.Failure;
        }

        if (!usePercentage)
            RhinoApp.WriteLine($"Slope = {(slopeRatio * 100.0):F2}%");

        if (replaceInput)
        {
            bool replaced = doc.Objects.Replace(objRef.ObjectId, resultCurve);
            if (!replaced)
                return Result.Failure;
        }
        else
        {
            if (doc.Objects.AddCurve(resultCurve) == Guid.Empty)
                return Result.Failure;
        }

        doc.Views.Redraw();
        return Result.Success;
    }

    public static Result RunSlopeCurveSection(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return Result.Failure;

        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select curve section to edit");
        getObject.GeometryFilter = ObjectType.Curve;
        if (getObject.Get() != GetResult.Object)
            return getObject.CommandResult();

        ObjRef reference = getObject.Object(0);
        Curve? source = reference.Curve();
        if (source == null)
            return Result.Failure;

        var firstPick = new GetPoint();
        firstPick.SetCommandPrompt("Pick first point on curve");
        firstPick.Constrain(source, false);
        if (firstPick.Get() != GetResult.Point)
            return firstPick.CommandResult();

        var secondPick = new GetPoint();
        secondPick.SetCommandPrompt("Pick second point on curve");
        secondPick.Constrain(source, false);
        if (secondPick.Get() != GetResult.Point)
            return secondPick.CommandResult();

        if (!source.ClosestPoint(firstPick.Point(), out double firstParameter) ||
            !source.ClosestPoint(secondPick.Point(), out double secondParameter))
            return Result.Failure;

        int modeIndex = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.Mode", 0);
        double gradePercent = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.Percent", 0.0);
        double blendPercent = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.Blend", 100.0);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.ReplaceInput", true);

        while (true)
        {
            var options = new GetPoint();
            options.SetCommandPrompt("Choose section edit mode or press Enter to accept");
            options.AcceptNothing(true);
            var gradeOption = new OptionDouble(gradePercent, -100000.0, 100000.0);
            var blendOption = new OptionDouble(blendPercent, 0.0, 100.0);
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            int modeOptionIndex = options.AddOptionList(
                "Mode",
                new[] { "GradePercent", "BetweenCurrentElevations", "BlendToTerrain" },
                Math.Clamp(modeIndex, 0, 2));
            options.AddOptionDouble("Grade", ref gradeOption);
            options.AddOptionDouble("Blend", ref blendOption);
            options.AddOptionToggle("Output", ref replaceOption);

            GetResult optionResult = options.Get();
            gradePercent = gradeOption.CurrentValue;
            blendPercent = blendOption.CurrentValue;
            replaceInput = replaceOption.CurrentValue;
            if (optionResult == GetResult.Option)
            {
                if (options.OptionIndex() == modeOptionIndex)
                    modeIndex = options.Option().CurrentListOptionIndex;
                continue;
            }

            if (optionResult != GetResult.Nothing)
                return options.CommandResult();

            break;
        }

        CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.Mode", modeIndex);
        CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.Percent", gradePercent);
        CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.Blend", blendPercent);
        CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.ReplaceInput", replaceInput);

        Mesh? activeTerrain = null;
        if (modeIndex == 2)
        {
            TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
            if (terrain == null)
            {
                RhinoApp.WriteLine("Select an active MoleHill terrain before blending to terrain.");
                return Result.Nothing;
            }

            activeTerrain = TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
            if (activeTerrain == null)
            {
                RhinoApp.WriteLine($"Active terrain '{terrain.Name}' has no current final build. Rebuild it before blending.");
                return Result.Nothing;
            }
        }

        try
        {
            if (!GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
                    source, firstParameter, secondParameter, (CurveSectionEditMode)modeIndex,
                    gradePercent, blendPercent, activeTerrain, doc.ModelAbsoluteTolerance,
                    out Curve? resultCurve, out string? error) || resultCurve == null)
            {
                RhinoApp.WriteLine(error ?? "Failed to edit the curve section.");
                return Result.Failure;
            }

            uint undoRecord = doc.BeginUndoRecord("Slope Curve Section");
            try
            {
                bool committed = replaceInput
                    ? doc.Objects.Replace(reference.ObjectId, resultCurve)
                    : doc.Objects.AddCurve(resultCurve) != Guid.Empty;
                if (!committed)
                {
                    resultCurve.Dispose();
                    return Result.Failure;
                }
            }
            finally
            {
                doc.EndUndoRecord(undoRecord);
            }

            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            activeTerrain?.Dispose();
        }
    }

    public static Result RunSlopeCheckAndMark(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext)) return Result.Failure;
        var getCurve = new GetObject();
        getCurve.SetCommandPrompt("Select a curve");
        getCurve.GeometryFilter = ObjectType.Curve;
        getCurve.SubObjectSelect = true;
        getCurve.EnablePreSelect(true, true);
        if (getCurve.Get() != GetResult.Object)
            return getCurve.CommandResult();

        Curve? curve = getCurve.Object(0).Curve();
        if (curve == null)
            return Result.Failure;

        const string vectorScaleKey = "MoleHill.SlopeCheck.VectorScale";
        double vectorScale = CommandOptionCache.GetLength(vectorScaleKey, unitContext, 1.0);
        bool addLabels = CommandOptionCache.GetValue("MoleHill.SlopeCheck.AddLabels", false);
        bool addLines = CommandOptionCache.GetValue("MoleHill.SlopeCheck.AddLines", false);

        while (true)
        {
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt("Pick location on curve or press Enter to finish");
            getPoint.AcceptNothing(true);
            getPoint.AcceptNumber(true, false);
            getPoint.Constrain(curve, false);

            var scaleOption = new OptionDouble(vectorScale);
            var labelsOption = new OptionToggle(addLabels, "No", "Yes");
            var linesOption = new OptionToggle(addLines, "No", "Yes");
            getPoint.AddOptionDouble("LineScale", ref scaleOption);
            getPoint.AddOptionToggle("AddLabels", ref labelsOption);
            getPoint.AddOptionToggle("TangentLines", ref linesOption);

            getPoint.DynamicDraw += (_, e) =>
            {
                if (!TryGetCurveSlopePreview(curve, e.CurrentPoint, scaleOption.CurrentValue, out Point3d lineEnd, out double slopePercent))
                    return;

                e.Display.DrawPoint(e.CurrentPoint);
                e.Display.DrawLine(e.CurrentPoint, lineEnd, FeedbackColor, 2);
                Point3d labelPoint = (e.CurrentPoint + lineEnd) * 0.5;
                e.Display.DrawDot(labelPoint, $"{slopePercent:F1}%", TrackingColor, FeedbackColor);
            };

            GetResult pointResult = getPoint.Get();
            if (pointResult == GetResult.Option || pointResult == GetResult.Number)
            {
                vectorScale = pointResult == GetResult.Number ? getPoint.Number() : scaleOption.CurrentValue;
                addLabels = labelsOption.CurrentValue;
                addLines = linesOption.CurrentValue;
                CommandOptionCache.SetLength(vectorScaleKey, unitContext, vectorScale);
                CommandOptionCache.SetValue("MoleHill.SlopeCheck.AddLabels", addLabels);
                CommandOptionCache.SetValue("MoleHill.SlopeCheck.AddLines", addLines);
                continue;
            }

            if (pointResult == GetResult.Nothing)
                return Result.Success;

            if (pointResult != GetResult.Point)
                return getPoint.CommandResult();

            vectorScale = scaleOption.CurrentValue;
            addLabels = labelsOption.CurrentValue;
            addLines = linesOption.CurrentValue;
            CommandOptionCache.SetLength(vectorScaleKey, unitContext, vectorScale);
            CommandOptionCache.SetValue("MoleHill.SlopeCheck.AddLabels", addLabels);
            CommandOptionCache.SetValue("MoleHill.SlopeCheck.AddLines", addLines);

            Point3d point = getPoint.Point();
            if (!TryGetCurveSlopePreview(curve, point, vectorScale, out Point3d lineEnd, out double slopePercent))
                continue;

            RhinoApp.WriteLine($"Curve slope = {slopePercent:F1}% at the pick location.");
            if (addLabels)
            {
                var dot = new TextDot($"{slopePercent:F1}%", point);
                doc.Objects.AddTextDot(dot);
            }

            if (addLines)
                doc.Objects.AddLine(point, lineEnd);

            doc.Views.Redraw();
        }
    }

    public static Result RunLiftCurvesWithLine(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext)) return Result.Failure;
        var getCurves = new GetObject();
        getCurves.SetCommandPrompt("Select curves to process");
        getCurves.GeometryFilter = ObjectType.Curve;
        getCurves.EnablePreSelect(true, true);
        getCurves.GroupSelect = true;
        getCurves.GetMultiple(1, 0);
        if (getCurves.CommandResult() != Result.Success)
            return getCurves.CommandResult();

        Result lineResult = RhinoGet.GetLine(out Line line);
        if (lineResult != Result.Success)
            return lineResult;

        const string liftFactorKey = "MoleHill.LiftCurvesWithLine.LiftFactor";
        double liftFactor = CommandOptionCache.GetLength(liftFactorKey, unitContext, 2.0);
        bool addOrderDots = CommandOptionCache.GetValue("MoleHill.LiftCurvesWithLine.AddOrderDots", true);

        while (true)
        {
            var getOption = new GetOption();
            getOption.SetCommandPrompt("Adjust lift options or press Enter to accept");
            getOption.AcceptNothing(true);

            var factorOption = new OptionDouble(liftFactor);
            var dotOption = new OptionToggle(addOrderDots, "No", "Yes");
            getOption.AddOptionDouble("LiftFactor", ref factorOption);
            getOption.AddOptionToggle("AddOrderDots", ref dotOption);

            GetResult optionResult = getOption.Get();
            if (optionResult == GetResult.Option)
            {
                liftFactor = factorOption.CurrentValue;
                addOrderDots = dotOption.CurrentValue;
                CommandOptionCache.SetLength(liftFactorKey, unitContext, liftFactor);
                CommandOptionCache.SetValue("MoleHill.LiftCurvesWithLine.AddOrderDots", addOrderDots);
                continue;
            }

            if (optionResult != GetResult.Nothing)
                return getOption.CommandResult();

            liftFactor = factorOption.CurrentValue;
            addOrderDots = dotOption.CurrentValue;
            break;
        }

        CommandOptionCache.SetLength(liftFactorKey, unitContext, liftFactor);
        CommandOptionCache.SetValue("MoleHill.LiftCurvesWithLine.AddOrderDots", addOrderDots);

        var lineCurve = new LineCurve(line);
        var orderedCurves = new List<(double Parameter, Point3d AnchorPoint, Guid ObjectId)>();
        for (int i = 0; i < getCurves.ObjectCount; i++)
        {
            ObjRef? objRef = getCurves.Object(i);
            Curve? curve = objRef?.Curve();
            if (objRef == null || curve == null)
                continue;

            CurveIntersections intersections = Intersection.CurveCurve(lineCurve, curve, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
            if (intersections.Count == 0)
                continue;

            double? parameter = intersections
                .Select(intersection => intersection.ParameterA)
                .OrderBy(value => value)
                .Cast<double?>()
                .FirstOrDefault();

            if (parameter.HasValue)
                orderedCurves.Add((parameter.Value, line.PointAt(parameter.Value), objRef.ObjectId));
        }

        if (orderedCurves.Count == 0)
        {
            RhinoApp.WriteLine("No curve intersections were found along the picked line.");
            return Result.Nothing;
        }

        orderedCurves.Sort((a, b) => a.Parameter.CompareTo(b.Parameter));
        for (int i = 0; i < orderedCurves.Count; i++)
        {
            double liftAmount = (i + 1) * liftFactor;
            Transform transform = Transform.Translation(0.0, 0.0, liftAmount);
            if (doc.Objects.Transform(orderedCurves[i].ObjectId, transform, deleteOriginal: true) == Guid.Empty)
                return Result.Failure;

            if (addOrderDots)
            {
                Point3d dotPoint = orderedCurves[i].AnchorPoint + new Vector3d(0.0, 0.0, liftAmount);
                var dot = new TextDot((i + 1).ToString(), dotPoint)
                {
                    FontHeight = 10
                };

                if (doc.Objects.AddTextDot(dot) == Guid.Empty)
                    return Result.Failure;
            }
        }

        doc.Views.Redraw();
        return Result.Success;
    }

    private static readonly OffsetVerticalMode[] OffsetVerticalModes =
    {
        OffsetVerticalMode.Elevation,
        OffsetVerticalMode.Percent,
        OffsetVerticalMode.Degrees,
        OffsetVerticalMode.Ratio
    };

    public static Result RunOffsetFeature(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext)) return Result.Failure;
        var getCurve = new GetObject();
        getCurve.SetCommandPrompt("Select a feature line to offset");
        getCurve.GeometryFilter = ObjectType.Curve;
        getCurve.EnablePreSelect(true, true);
        if (getCurve.Get() != GetResult.Object)
            return getCurve.CommandResult();

        ObjRef sourceRef = getCurve.Object(0);
        Curve? sourceCurve = sourceRef.Curve();
        if (sourceCurve == null)
            return Result.Failure;

        Curve? projectedCurve = Curve.ProjectToPlane(sourceCurve, Plane.WorldXY);
        if (projectedCurve == null)
        {
            RhinoApp.WriteLine("Failed to project the input curve.");
            return Result.Failure;
        }

        const string offsetDistanceKey = "MoleHill.OffsetFeature.Distance";
        const string offsetElevationKey = "MoleHill.OffsetFeature.Elevation";
        double offsetDistance = CommandOptionCache.GetLength(offsetDistanceKey, unitContext, 1.0);
        Result numberResult = RhinoGet.GetNumber("Offset distance", false, ref offsetDistance);
        if (numberResult != Result.Success)
            return numberResult;

        offsetDistance = Math.Abs(offsetDistance);
        if (offsetDistance <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("Offset distance must be greater than zero.");
            return Result.Failure;
        }

        CommandOptionCache.SetLength(offsetDistanceKey, unitContext, offsetDistance);

        int verticalModeIndex = CommandOptionCache.GetValue("MoleHill.OffsetFeature.VerticalMode", 0);
        verticalModeIndex = Math.Clamp(verticalModeIndex, 0, OffsetVerticalModes.Length - 1);
        double elevationValue = CommandOptionCache.GetLength(offsetElevationKey, unitContext, 0.0);
        double percentValue = CommandOptionCache.GetValue("MoleHill.OffsetFeature.Percent", 0.0);
        double degreesValue = CommandOptionCache.GetValue("MoleHill.OffsetFeature.Degrees", 0.0);
        double ratioValue = CommandOptionCache.GetValue("MoleHill.OffsetFeature.Ratio", 10.0);
        bool useSourceLayer = CommandOptionCache.GetValue("MoleHill.OffsetFeature.UseSourceLayer", false);

        while (true)
        {
            OffsetVerticalMode verticalMode = OffsetVerticalModes[verticalModeIndex];
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt("Pick offset side or type distance");
            getPoint.AcceptNumber(true, false);
            var distanceOption = new OptionDouble(offsetDistance, RhinoMath.ZeroTolerance, 1000000000.0);
            getPoint.AddOptionDouble("Distance", ref distanceOption);

            int verticalListIndex = getPoint.AddOptionList(
                "Vertical",
                new[] { "Elevation", "Percent", "Degrees", "Ratio" },
                verticalModeIndex);

            // The GetPoint is rebuilt every iteration, so the numeric option can be labelled for
            // whichever vertical mode is active.
            var verticalOption = new OptionDouble(GetVerticalOptionValue(
                verticalMode,
                elevationValue,
                percentValue,
                degreesValue,
                ratioValue));
            getPoint.AddOptionDouble(GetVerticalOptionName(verticalMode), ref verticalOption);

            var layerOption = new OptionToggle(useSourceLayer, "Current", "Source");
            getPoint.AddOptionToggle("Layer", ref layerOption);

            getPoint.DynamicDraw += (_, e) =>
            {
                if (!GeometryCommandAlgorithms.TryResolveVerticalDelta(
                        verticalMode,
                        verticalOption.CurrentValue,
                        distanceOption.CurrentValue,
                        out double previewVerticalDelta,
                        out string? previewVerticalError))
                    return;

                if (!TryBuildOffsetPreview(
                        sourceCurve,
                        projectedCurve,
                        e.CurrentPoint,
                        distanceOption.CurrentValue,
                        previewVerticalDelta,
                        doc.ModelAbsoluteTolerance,
                        out Polyline previewPolyline,
                        out double signedDistance))
                    return;

                e.Display.DrawPolyline(previewPolyline, TrackingColor, 2);
                string side = signedDistance <= 0.0 ? "Left" : "Right";
                e.Display.DrawDot(
                    e.CurrentPoint,
                    $"{distanceOption.CurrentValue:F3} {side}  dZ {previewVerticalDelta:F3}",
                    TrackingColor,
                    FeedbackColor);
            };

            GetResult pointResult = getPoint.Get();
            if (pointResult == GetResult.Number)
            {
                offsetDistance = Math.Abs(getPoint.Number());
                if (offsetDistance <= RhinoMath.ZeroTolerance)
                {
                    RhinoApp.WriteLine("Offset distance must be greater than zero.");
                    return Result.Failure;
                }

                CommandOptionCache.SetLength(offsetDistanceKey, unitContext, offsetDistance);
                continue;
            }

            if (pointResult == GetResult.Option)
            {
                CommitOptions();
                if (getPoint.OptionIndex() == verticalListIndex)
                    verticalModeIndex = getPoint.Option().CurrentListOptionIndex;

                CommandOptionCache.SetValue("MoleHill.OffsetFeature.VerticalMode", verticalModeIndex);
                continue;
            }

            if (pointResult != GetResult.Point)
                return getPoint.CommandResult();

            CommitOptions();

            if (!GeometryCommandAlgorithms.TryResolveVerticalDelta(
                    verticalMode,
                    verticalOption.CurrentValue,
                    offsetDistance,
                    out double verticalDelta,
                    out string? verticalError))
            {
                RhinoApp.WriteLine(verticalError ?? "Failed to resolve the vertical offset.");
                return Result.Failure;
            }

            if (!TryBuildOffsetPreview(
                    sourceCurve,
                    projectedCurve,
                    getPoint.Point(),
                    offsetDistance,
                    verticalDelta,
                    doc.ModelAbsoluteTolerance,
                    out Polyline resultPolyline,
                    out _))
            {
                RhinoApp.WriteLine("Failed to offset the selected feature line.");
                return Result.Failure;
            }

            Guid addedId = useSourceLayer && sourceRef.Object() is RhinoObject sourceObject
                ? doc.Objects.AddPolyline(resultPolyline, sourceObject.Attributes.Duplicate())
                : doc.Objects.AddPolyline(resultPolyline);

            if (addedId == Guid.Empty)
                return Result.Failure;

            doc.Views.Redraw();
            return Result.Success;

            void CommitOptions()
            {
                offsetDistance = Math.Abs(distanceOption.CurrentValue);
                CommandOptionCache.SetLength(offsetDistanceKey, unitContext, offsetDistance);

                useSourceLayer = layerOption.CurrentValue;
                CommandOptionCache.SetValue("MoleHill.OffsetFeature.UseSourceLayer", useSourceLayer);

                switch (verticalMode)
                {
                    case OffsetVerticalMode.Elevation:
                        elevationValue = verticalOption.CurrentValue;
                        CommandOptionCache.SetLength(offsetElevationKey, unitContext, elevationValue);
                        break;
                    case OffsetVerticalMode.Percent:
                        percentValue = verticalOption.CurrentValue;
                        CommandOptionCache.SetValue("MoleHill.OffsetFeature.Percent", percentValue);
                        break;
                    case OffsetVerticalMode.Degrees:
                        degreesValue = verticalOption.CurrentValue;
                        CommandOptionCache.SetValue("MoleHill.OffsetFeature.Degrees", degreesValue);
                        break;
                    case OffsetVerticalMode.Ratio:
                        ratioValue = verticalOption.CurrentValue;
                        CommandOptionCache.SetValue("MoleHill.OffsetFeature.Ratio", ratioValue);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Label for the numeric option paired with the active vertical mode. These deliberately differ
    /// from the "Vertical" list values so neither is ambiguous when typed at the command line.
    /// </summary>
    private static string GetVerticalOptionName(OffsetVerticalMode mode)
    {
        return mode switch
        {
            OffsetVerticalMode.Percent => "Grade",
            OffsetVerticalMode.Degrees => "Angle",
            OffsetVerticalMode.Ratio => "Run",
            _ => "DeltaZ"
        };
    }

    private static double GetVerticalOptionValue(
        OffsetVerticalMode mode,
        double elevationValue,
        double percentValue,
        double degreesValue,
        double ratioValue)
    {
        return mode switch
        {
            OffsetVerticalMode.Percent => percentValue,
            OffsetVerticalMode.Degrees => degreesValue,
            OffsetVerticalMode.Ratio => ratioValue,
            _ => elevationValue
        };
    }

    public static Result RunReplaceCurveSection(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        var getBaseCurve = new GetObject();
        getBaseCurve.SetCommandPrompt("Select base curve");
        getBaseCurve.GeometryFilter = ObjectType.Curve;
        getBaseCurve.EnablePreSelect(true, true);
        if (getBaseCurve.Get() != GetResult.Object)
            return getBaseCurve.CommandResult();

        ObjRef baseRef = getBaseCurve.Object(0);
        doc.Objects.UnselectAll();

        var getReplacementCurve = new GetObject();
        getReplacementCurve.SetCommandPrompt("Select replacement curve");
        getReplacementCurve.GeometryFilter = ObjectType.Curve;
        getReplacementCurve.EnablePreSelect(false, true);
        if (getReplacementCurve.Get() != GetResult.Object)
            return getReplacementCurve.CommandResult();

        ObjRef replacementRef = getReplacementCurve.Object(0);
        if (replacementRef.ObjectId == baseRef.ObjectId)
        {
            RhinoApp.WriteLine("Select a different curve as the replacement section.");
            return Result.Nothing;
        }

        Curve? baseCurve = baseRef.Curve();
        Curve? replacementCurve = replacementRef.Curve();
        if (baseCurve == null || replacementCurve == null)
            return Result.Failure;

        if (!baseCurve.ClosestPoint(replacementCurve.PointAtStart, out double t0) ||
            !baseCurve.ClosestPoint(replacementCurve.PointAtEnd, out double t1))
        {
            RhinoApp.WriteLine("Failed to locate the replacement endpoints on the base curve.");
            return Result.Failure;
        }

        if (t0 > t1)
            (t0, t1) = (t1, t0);

        double tolerance = doc.ModelAbsoluteTolerance;
        if (Math.Abs(t0 - t1) <= tolerance)
        {
            RhinoApp.WriteLine("Replacement endpoints collapse to the same location on the base curve.");
            return Result.Failure;
        }

        Curve[] joined = TryJoinReplacementCurveSection(baseCurve, replacementCurve, t0, t1, tolerance);
        if (joined.Length != 1)
        {
            RhinoApp.WriteLine("Failed to join the replacement curve into the base curve.");
            return Result.Failure;
        }

        if (!doc.Objects.Replace(baseRef.ObjectId, joined[0]))
            return Result.Failure;

        doc.Objects.Delete(replacementRef.ObjectId, quiet: true);
        doc.Views.Redraw();
        return Result.Success;
    }

    public static Result RunSoftEditCurves(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext)) return Result.Failure;
        var getCurves = new GetObject();
        getCurves.SetCommandPrompt("Select curves to soft edit");
        getCurves.GeometryFilter = ObjectType.Curve;
        getCurves.EnablePreSelect(true, true);
        getCurves.GroupSelect = true;
        getCurves.GetMultiple(1, 0);
        if (getCurves.CommandResult() != Result.Success)
            return getCurves.CommandResult();

        double tolerance = doc.ModelAbsoluteTolerance;
        var curveData = new List<(Guid ObjectId, Curve SourceCurve, ObjectAttributes Attributes, BoundingBox Bounds)>();
        for (int i = 0; i < getCurves.ObjectCount; i++)
        {
            ObjRef? objRef = getCurves.Object(i);
            Curve? curve = objRef?.Curve();
            RhinoObject? rhinoObject = objRef?.Object();
            if (objRef == null || curve == null || rhinoObject == null)
                continue;

            curveData.Add((
                objRef.ObjectId,
                curve,
                rhinoObject.Attributes.Duplicate(),
                curve.GetBoundingBox(accurate: true)));
        }

        if (curveData.Count == 0)
        {
            RhinoApp.WriteLine("No valid curves were found for soft editing.");
            return Result.Nothing;
        }

        var getBasePoint = new GetPoint();
        getBasePoint.SetCommandPrompt("Select base point");
        if (getBasePoint.Get() != GetResult.Point)
            return getBasePoint.CommandResult();

        Point3d basePoint = getBasePoint.Point();

        const string radiusKey = "MoleHill.SoftEditCurves.Radius";
        double falloffRadius = CommandOptionCache.GetLength(radiusKey, unitContext, 10.0);
        var getRadius = new GetPoint();
        getRadius.SetCommandPrompt($"Select radius for falloff or press Enter <{falloffRadius:G6}>");
        getRadius.AcceptNothing(true);
        getRadius.AcceptNumber(true, false);
        getRadius.DynamicDraw += (_, e) =>
        {
            double radius = GeometryCommandAlgorithms.CalculatePlanDistance(basePoint, e.CurrentPoint);
            if (radius > RhinoMath.ZeroTolerance)
                e.Display.DrawCircle(new Circle(basePoint, radius), TrackingColor);
        };

        GetResult radiusResult = getRadius.Get();
        if (radiusResult == GetResult.Point)
            falloffRadius = GeometryCommandAlgorithms.CalculatePlanDistance(basePoint, getRadius.Point());
        else if (radiusResult == GetResult.Number)
            falloffRadius = Math.Abs(getRadius.Number());
        else if (radiusResult != GetResult.Nothing)
            return getRadius.CommandResult();

        if (falloffRadius <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("Radius must be greater than zero.");
            return Result.Failure;
        }

        CommandOptionCache.SetLength(radiusKey, unitContext, falloffRadius);

        curveData.RemoveAll(item => !GeometryCommandAlgorithms.BoundingBoxIntersectsPlanRadius(
            item.Bounds,
            basePoint,
            falloffRadius));
        if (curveData.Count == 0)
        {
            RhinoApp.WriteLine("The falloff radius does not reach any selected curves.");
            return Result.Nothing;
        }

        bool useSmoothFalloff = CommandOptionCache.GetValue("MoleHill.SoftEditCurves.SmoothFalloff", true);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.SoftEditCurves.ReplaceInput", true);
        bool fixEnds = CommandOptionCache.GetValue("MoleHill.SoftEditCurves.FixEnds", true);
        bool constrainToCPlane = CommandOptionCache.GetValue("MoleHill.SoftEditCurves.ConstrainToCPlane", true);
        Plane movementPlane = doc.Views.ActiveView?.MainViewport.ConstructionPlane() ?? Plane.WorldXY;
        movementPlane.Origin = basePoint;
        Point3d offsetPoint;
        while (true)
        {
            var getVector = new GetPoint();
            getVector.SetCommandPrompt("Select offset direction and magnitude");
            getVector.SetBasePoint(basePoint, true);
            if (constrainToCPlane)
                getVector.Constrain(movementPlane, allowElevator: false);

            var falloffOption = new OptionToggle(useSmoothFalloff, "Linear", "Smooth");
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            var fixEndsOption = new OptionToggle(fixEnds, "No", "Yes");
            var movementOption = new OptionToggle(constrainToCPlane, "Free", "CPlane");
            getVector.AddOptionToggle("Falloff", ref falloffOption);
            getVector.AddOptionToggle("Output", ref replaceOption);
            getVector.AddOptionToggle("FixEnds", ref fixEndsOption);
            getVector.AddOptionToggle("Movement", ref movementOption);
            getVector.DynamicDraw += (_, e) =>
            {
                Vector3d vector = e.CurrentPoint - basePoint;
                SoftEditFalloff falloff = falloffOption.CurrentValue
                    ? SoftEditFalloff.Smooth
                    : SoftEditFalloff.Linear;

                e.Display.DrawCircle(new Circle(basePoint, falloffRadius), TrackingColor);
                e.Display.DrawLine(basePoint, e.CurrentPoint, TrackingColor, 2);
                for (int i = 0; i < curveData.Count; i++)
                {
                    if (GeometryCommandAlgorithms.TryCreateSoftEditedCurve(
                            curveData[i].SourceCurve,
                            basePoint,
                            falloffRadius,
                            vector,
                            falloff,
                            fixEndsOption.CurrentValue,
                            tolerance,
                            quickPreview: true,
                            out Curve? previewCurve,
                            out string? _)
                        && previewCurve != null)
                    {
                        e.Display.DrawCurve(previewCurve, FeedbackColor, 2);
                        previewCurve.Dispose();
                    }
                }
            };

            GetResult vectorResult = getVector.Get();
            useSmoothFalloff = falloffOption.CurrentValue;
            replaceInput = replaceOption.CurrentValue;
            fixEnds = fixEndsOption.CurrentValue;
            constrainToCPlane = movementOption.CurrentValue;
            CommandOptionCache.SetValue("MoleHill.SoftEditCurves.SmoothFalloff", useSmoothFalloff);
            CommandOptionCache.SetValue("MoleHill.SoftEditCurves.ReplaceInput", replaceInput);
            CommandOptionCache.SetValue("MoleHill.SoftEditCurves.FixEnds", fixEnds);
            CommandOptionCache.SetValue("MoleHill.SoftEditCurves.ConstrainToCPlane", constrainToCPlane);

            if (vectorResult == GetResult.Option)
                continue;

            if (vectorResult != GetResult.Point)
                return getVector.CommandResult();

            offsetPoint = getVector.Point();
            break;
        }

        Vector3d offsetVector = offsetPoint - basePoint;
        if (offsetVector.IsTiny())
        {
            RhinoApp.WriteLine("Offset must be greater than zero.");
            return Result.Nothing;
        }

        SoftEditFalloff selectedFalloff = useSmoothFalloff
            ? SoftEditFalloff.Smooth
            : SoftEditFalloff.Linear;
        var editedCurves = new List<(Guid ObjectId, ObjectAttributes Attributes, Curve Curve)>(curveData.Count);
        for (int i = 0; i < curveData.Count; i++)
        {
            var item = curveData[i];
            if (!GeometryCommandAlgorithms.TryCreateSoftEditedCurve(
                    item.SourceCurve,
                    basePoint,
                    falloffRadius,
                    offsetVector,
                    selectedFalloff,
                    fixEnds,
                    tolerance,
                    quickPreview: false,
                    out Curve? resultCurve,
                    out string? error)
                || resultCurve == null)
            {
                foreach (var edited in editedCurves)
                    edited.Curve.Dispose();

                RhinoApp.WriteLine(error ?? "Failed to soft-edit the curve.");
                return Result.Failure;
            }

            editedCurves.Add((item.ObjectId, item.Attributes, resultCurve));
        }

        var originalCurves = curveData.ToDictionary(
            item => item.ObjectId,
            item => item.SourceCurve.DuplicateCurve());
        var replacedIds = new List<Guid>();
        var addedIds = new List<Guid>();
        uint undoRecord = doc.BeginUndoRecord("Soft Edit Curves");
        try
        {
            for (int i = 0; i < editedCurves.Count; i++)
            {
                var edited = editedCurves[i];
                bool succeeded;
                if (replaceInput)
                {
                    succeeded = doc.Objects.Replace(edited.ObjectId, edited.Curve);
                    if (succeeded)
                        replacedIds.Add(edited.ObjectId);
                }
                else
                {
                    Guid addedId = doc.Objects.AddCurve(edited.Curve, edited.Attributes);
                    succeeded = addedId != Guid.Empty;
                    if (succeeded)
                        addedIds.Add(addedId);
                }

                edited.Curve.Dispose();
                if (succeeded)
                    continue;

                for (int remaining = i + 1; remaining < editedCurves.Count; remaining++)
                    editedCurves[remaining].Curve.Dispose();

                foreach (Guid addedId in addedIds)
                {
                    RhinoObject? addedObject = doc.Objects.FindId(addedId);
                    if (addedObject != null)
                        doc.Objects.Delete(addedObject, quiet: true, ignoreModes: true);
                }

                foreach (Guid replacedId in replacedIds)
                    doc.Objects.Replace(replacedId, originalCurves[replacedId]);

                RhinoApp.WriteLine("Could not commit all soft-edited curves; the original document state was restored.");
                return Result.Failure;
            }

            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            foreach (Curve original in originalCurves.Values)
                original.Dispose();
            doc.EndUndoRecord(undoRecord);
        }
    }

    public static Result RunTrimBoundary(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        var getBoundaries = new GetObject();
        getBoundaries.SetCommandPrompt("Select closed boundary curves for trimming");
        getBoundaries.GeometryFilter = ObjectType.Curve;
        getBoundaries.EnablePreSelect(false, true);
        getBoundaries.GetMultiple(1, 0);
        if (getBoundaries.CommandResult() != Result.Success)
            return getBoundaries.CommandResult();

        var boundaryRefs = Enumerable.Range(0, getBoundaries.ObjectCount)
            .Select(index => getBoundaries.Object(index))
            .Where(objRef => objRef?.Curve() != null && objRef.Curve()!.IsClosed)
            .ToList();

        if (boundaryRefs.Count != getBoundaries.ObjectCount)
        {
            RhinoApp.WriteLine("All boundary curves must be closed.");
            return Result.Failure;
        }

        // Lock boundaries so they can't be immediately grabbed as preselected input
        foreach (ObjRef? objRef in boundaryRefs)
            doc.Objects.Lock(objRef!.ObjectId, true);

        var getCurves = new GetObject();
        getCurves.SetCommandPrompt("Select curves to trim");
        getCurves.GeometryFilter = ObjectType.Curve;
        getCurves.EnablePreSelect(true, false);
        getCurves.GetMultiple(1, 0);
        Result curvesResult = getCurves.CommandResult();

        foreach (ObjRef? objRef in boundaryRefs)
            doc.Objects.Unlock(objRef!.ObjectId, true);

        if (curvesResult != Result.Success)
            return curvesResult;

        bool trimInside = CommandOptionCache.GetValue("MoleHill.TrimBoundary.TrimInside", true);
        while (true)
        {
            var getOption = new GetOption();
            getOption.SetCommandPrompt("Side to trim away");
            getOption.AcceptNothing(true);
            var trimToggle = new OptionToggle(trimInside, "Outside", "Inside");
            getOption.AddOptionToggle("Trim", ref trimToggle);
            GetResult optionResult = getOption.Get();
            if (optionResult == GetResult.Option)
            {
                trimInside = trimToggle.CurrentValue;
                CommandOptionCache.SetValue("MoleHill.TrimBoundary.TrimInside", trimInside);
                continue;
            }

            if (optionResult != GetResult.Nothing)
                return getOption.CommandResult();

            break;
        }

        Plane activePlane = doc.Views.ActiveView?.MainViewport.ConstructionPlane() ?? Plane.WorldXY;
        Transform toWorldXY = Transform.PlaneToPlane(activePlane, Plane.WorldXY);
        double tolerance = doc.ModelAbsoluteTolerance;

        var boundaryCurves = new List<Curve>(boundaryRefs.Count);
        foreach (ObjRef? objRef in boundaryRefs)
        {
            Curve? projectedBoundary = ProjectCurveToWorldXY(objRef!.Curve()!, activePlane, toWorldXY);
            if (projectedBoundary != null)
                boundaryCurves.Add(projectedBoundary);
        }

        if (boundaryCurves.Count == 0)
            return Result.Nothing;

        var curvesToDelete = new List<Guid>();
        var curvesToAdd = new List<(Curve Curve, ObjectAttributes Attributes)>();

        for (int i = 0; i < getCurves.ObjectCount; i++)
        {
            ObjRef? objRef = getCurves.Object(i);
            Curve? sourceCurve = objRef?.Curve();
            RhinoObject? sourceObject = objRef?.Object();
            if (objRef == null || sourceCurve == null || sourceObject == null)
                continue;

            Curve? projectedSource = ProjectCurveToWorldXY(sourceCurve, activePlane, toWorldXY);
            if (projectedSource == null)
                continue;

            double[] splitParameters = GetUniqueSplitParameters(projectedSource, boundaryCurves, tolerance);
            Curve[] segments = splitParameters.Length > 0
                ? sourceCurve.Split(splitParameters)
                : new[] { sourceCurve.DuplicateCurve() };

            if (segments.Length == 0)
                continue;

            bool replacedOriginal = splitParameters.Length > 0;
            bool keepOriginal = false;
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                Curve segment = segments[segmentIndex];
                Point3d midPoint = segment.PointAt(segment.Domain.Mid);
                Point3d projectedMidPoint = activePlane.ClosestPoint(midPoint);
                projectedMidPoint.Transform(toWorldXY);

                bool inside = GeometryCommandAlgorithms.IsPointInsideNestedBoundaries(projectedMidPoint, boundaryCurves, tolerance);
                bool deleteSegment = (inside && trimInside) || (!inside && !trimInside);
                if (deleteSegment)
                    continue;

                if (!replacedOriginal && segmentIndex == 0)
                {
                    keepOriginal = true;
                    continue;
                }

                curvesToAdd.Add((segment, sourceObject.Attributes.Duplicate()));
            }

            if (replacedOriginal || !keepOriginal)
                curvesToDelete.Add(objRef.ObjectId);
        }

        foreach (var item in curvesToAdd)
            doc.Objects.AddCurve(item.Curve, item.Attributes);

        foreach (Guid objectId in curvesToDelete.Distinct())
            doc.Objects.Delete(objectId, quiet: true);

        doc.Views.Redraw();
        return Result.Success;
    }

    private static bool TryGetCurveSlopePreview(
        Curve curve,
        Point3d point,
        double scale,
        out Point3d lineEnd,
        out double slopePercent)
    {
        lineEnd = point;
        slopePercent = 0.0;

        if (!curve.ClosestPoint(point, out double parameter))
            return false;

        Vector3d tangent = curve.TangentAt(parameter);
        slopePercent = GeometryCommandAlgorithms.CalculateSlopePercentMagnitude(tangent);
        if (!tangent.Unitize())
            return false;

        Vector3d previewVector = tangent * scale;
        lineEnd = point + previewVector;
        return true;
    }

    private static Curve[] TryJoinReplacementCurveSection(
        Curve baseCurve,
        Curve replacementCurve,
        double t0,
        double t1,
        double tolerance)
    {
        List<Curve>? pieces = BuildReplacementCurvePieces(baseCurve, t0, t1, tolerance);
        if (pieces == null)
            return Array.Empty<Curve>();

        return TryJoinReplacementCurveSectionWithDirections(baseCurve, replacementCurve, pieces, tolerance);
    }

    private static List<Curve>? BuildReplacementCurvePieces(
        Curve baseCurve,
        double t0,
        double t1,
        double tolerance)
    {
        if (baseCurve.IsClosed)
        {
            Curve seamCurve = baseCurve.DuplicateCurve();
            if (!seamCurve.ChangeClosedCurveSeam(t0))
                return null;

            if (!seamCurve.ClosestPoint(baseCurve.PointAt(t1), out double seamT1))
                return null;

            Interval seamDomain = seamCurve.Domain;
            var pieces = new List<Curve>();
            if (seamT1 < seamDomain.T1 - tolerance)
            {
                Curve? remainder = seamCurve.Trim(seamT1, seamDomain.T1);
                if (remainder == null)
                    return null;

                pieces.Add(remainder);
            }

            return pieces.Count == 0 ? null : pieces;
        }

        Interval domain = baseCurve.Domain;
        var openPieces = new List<Curve>();
        if (t0 > domain.T0 + tolerance)
        {
            Curve? start = baseCurve.Trim(domain.T0, t0);
            if (start == null)
                return null;

            openPieces.Add(start);
        }

        if (t1 < domain.T1 - tolerance)
        {
            Curve? end = baseCurve.Trim(t1, domain.T1);
            if (end == null)
                return null;

            openPieces.Add(end);
        }

        return openPieces.Count == 0 ? null : openPieces;
    }

    private static Curve[] TryJoinReplacementCurveSectionWithDirections(
        Curve baseCurve,
        Curve replacementCurve,
        List<Curve> basePieces,
        double tolerance)
    {
        foreach (bool reverseReplacement in new[] { false, true })
        {
            Curve candidateReplacement = replacementCurve.DuplicateCurve();
            if (reverseReplacement)
                candidateReplacement.Reverse();

            var joinPieces = new List<Curve>(basePieces.Count + 1);
            if (baseCurve.IsClosed)
            {
                joinPieces.Add(candidateReplacement);
                joinPieces.AddRange(basePieces.Select(piece => piece.DuplicateCurve()));
            }
            else
            {
                if (basePieces.Count == 2)
                {
                    joinPieces.Add(basePieces[0].DuplicateCurve());
                    joinPieces.Add(candidateReplacement);
                    joinPieces.Add(basePieces[1].DuplicateCurve());
                }
                else
                {
                    Curve onlyPiece = basePieces[0];
                    bool isStartPiece =
                        onlyPiece.PointAtStart.DistanceTo(baseCurve.PointAtStart) <= tolerance ||
                        onlyPiece.PointAtEnd.DistanceTo(baseCurve.PointAtStart) <= tolerance;

                    if (isStartPiece)
                    {
                        joinPieces.Add(onlyPiece.DuplicateCurve());
                        joinPieces.Add(candidateReplacement);
                    }
                    else
                    {
                        joinPieces.Add(candidateReplacement);
                        joinPieces.Add(onlyPiece.DuplicateCurve());
                    }
                }
            }

            Curve[] joined = Curve.JoinCurves(joinPieces, tolerance);
            if (joined.Length == 1 && IsValidReplacementResult(baseCurve, joined[0], tolerance))
                return joined;
        }

        return Array.Empty<Curve>();
    }

    private static bool IsValidReplacementResult(Curve baseCurve, Curve resultCurve, double tolerance)
    {
        if (baseCurve.IsClosed)
            return resultCurve.IsClosed;

        Point3d baseStart = baseCurve.PointAtStart;
        Point3d baseEnd = baseCurve.PointAtEnd;
        Point3d resultStart = resultCurve.PointAtStart;
        Point3d resultEnd = resultCurve.PointAtEnd;

        bool sameDirection =
            resultStart.DistanceTo(baseStart) <= tolerance &&
            resultEnd.DistanceTo(baseEnd) <= tolerance;
        bool reversedDirection =
            resultStart.DistanceTo(baseEnd) <= tolerance &&
            resultEnd.DistanceTo(baseStart) <= tolerance;

        return sameDirection || reversedDirection;
    }

    private static bool TryBuildOffsetPreview(
        Curve sourceCurve,
        Curve projectedCurve,
        Point3d referencePoint,
        double offsetDistance,
        double verticalDelta,
        double tolerance,
        out Polyline polyline,
        out double signedDistance)
    {
        polyline = new Polyline();
        signedDistance = 0.0;

        if (!GeometryCommandAlgorithms.TryGetSignedOffsetDistance(projectedCurve, referencePoint, offsetDistance, out signedDistance))
            return false;

        string? offsetError;
        return GeometryCommandAlgorithms.TryGetOffsetFeaturePolyline(
            sourceCurve,
            signedDistance,
            verticalDelta,
            tolerance,
            out polyline,
            out offsetError);
    }

    private static Curve? ProjectCurveToWorldXY(Curve curve, Plane plane, Transform toWorldXY)
    {
        Curve? projectedCurve = Curve.ProjectToPlane(curve, plane);
        if (projectedCurve == null)
            return null;

        Curve duplicate = projectedCurve.DuplicateCurve();
        duplicate.Transform(toWorldXY);
        return duplicate;
    }

    private static double[] GetUniqueSplitParameters(Curve projectedCurve, IReadOnlyList<Curve> boundaries, double tolerance)
    {
        var parameters = new List<double>();
        Interval domain = projectedCurve.Domain;
        for (int boundaryIndex = 0; boundaryIndex < boundaries.Count; boundaryIndex++)
        {
            CurveIntersections intersections = Intersection.CurveCurve(projectedCurve, boundaries[boundaryIndex], tolerance, tolerance);
            for (int i = 0; i < intersections.Count; i++)
            {
                IntersectionEvent intersection = intersections[i];
                if (intersection.IsPoint)
                {
                    AddParameter(intersection.ParameterA);
                    continue;
                }

                AddParameter(intersection.OverlapA.T0);
                AddParameter(intersection.OverlapA.T1);
            }
        }

        return TerrainInputCommandAlgorithms.NormalizeSplitParameters(
            projectedCurve,
            parameters,
            tolerance);

        void AddParameter(double parameter)
        {
            Point3d point = projectedCurve.PointAt(parameter);
            if (parameter <= domain.T0 ||
                parameter >= domain.T1 ||
                point.DistanceTo(projectedCurve.PointAtStart) <= tolerance ||
                point.DistanceTo(projectedCurve.PointAtEnd) <= tolerance)
                return;

            parameters.Add(parameter);
        }
    }
}
