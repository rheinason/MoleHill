using System.Drawing;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

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

        // Was a hardcoded Promille option. The slope is the same quantity mhSlopeCurve and mhOffsetFeature
        // ask for, so it is asked for the same way — in whichever unit the user works in.
        var slope = new SlopeCommandOption("MoleHill.GradientInterpolation.Slope", 0.1);

        while (true)
        {
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt($"Place point or press Enter to finish (slope {slope.Description})");
            getPoint.AcceptNothing(true);
            slope.AddTo(getPoint);
            getPoint.DynamicDraw += (_, e) =>
            {
                Point3d previewPoint = e.CurrentPoint;
                double elevation = GeometryCommandAlgorithms.InterpolateGradientElevation(
                    basePoint,
                    previewPoint,
                    SlopeAnalyzer.ConvertRatioToUnit(slope.Ratio, SlopeAnalyzer.SlopeUnit.Promille));
                var liftedPoint = new Point3d(previewPoint.X, previewPoint.Y, elevation);
                e.Display.DrawPoint(liftedPoint);
                e.Display.DrawDot(liftedPoint, elevation.ToString("F3"), TrackingColor, FeedbackColor);
            };

            GetResult result = getPoint.Get();
            if (result == GetResult.Option)
            {
                slope.Commit(getPoint, result);
                continue;
            }

            if (result == GetResult.Nothing)
                return Result.Success;

            if (result != GetResult.Point)
                return getPoint.CommandResult();

            slope.Commit(getPoint, result);

            Point3d point = getPoint.Point();
            double z = GeometryCommandAlgorithms.InterpolateGradientElevation(
                basePoint,
                point,
                SlopeAnalyzer.ConvertRatioToUnit(slope.Ratio, SlopeAnalyzer.SlopeUnit.Promille));
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
        var slope = new SlopeCommandOption("MoleHill.SlopeCurve.Slope", 0.05);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.SlopeCurve.ReplaceInput", true);

        while (true)
        {
            var getOption = new GetPoint();
            getOption.SetCommandPrompt(
                usePercentage
                    ? $"Adjust slope options or press Enter to accept (slope {slope.Description})"
                    : "Curve already has slope; press Enter to use endpoints");
            getOption.AcceptNothing(true);
            getOption.AcceptPoint(false);
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            if (usePercentage)
                slope.AddTo(getOption);

            getOption.AddOptionToggle("ReplaceInput", ref replaceOption);
            getOption.DynamicDraw += (_, e) =>
            {
                Curve? previewCurve;
                double previewSlopeRatio;
                string? previewError;
                bool previewSucceeded = usePercentage
                    ? GeometryCommandAlgorithms.TryCreateSlopeCurve(
                        sourceCurve,
                        slope.Ratio,
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
                    slope.Commit(getOption, optionResult);

                replaceInput = replaceOption.CurrentValue;
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
            ? GeometryCommandAlgorithms.TryCreateSlopeCurve(sourceCurve, slope.Ratio, out resultCurve, out error)
            : GeometryCommandAlgorithms.TryCreateSlopeCurveFromEndPoints(sourceCurve, out resultCurve, out slopeRatio, out error);

        if (!succeeded || resultCurve == null)
        {
            RhinoApp.WriteLine(error ?? "Failed to slope the selected curve.");
            return Result.Failure;
        }

        // Report the measured slope in the unit the user works in, not a hardcoded percent.
        if (!usePercentage)
            RhinoApp.WriteLine($"Slope = {SlopeInput.FormatWithUnit(slopeRatio, SlopeUnitPreference.Current)}");

        // The curve is re-elevated by moving its Greville points, which only interpolates the intended
        // grade. Measure against the rule that was applied — start elevation plus slope by plan station,
        // the same thing TryCreateSlopeCurve builds from — and say so when it strays.
        double appliedRatio = usePercentage ? slope.Ratio : slopeRatio;
        double startElevation = sourceCurve.PointAtStart.Z;
        ReportSlopeDeviation(
            GeometryCommandAlgorithms.MeasureElevationDeviation(
                resultCurve, station => startElevation + (station * appliedRatio)),
            doc.ModelAbsoluteTolerance,
            "curve");

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

    /// <summary>
    /// Re-grades a picked stretch of a curve. Every option is previewed live and every mode shows only
    /// the options it actually uses — the command used to offer a blend factor while grading and a
    /// grade while blending, and showed neither result until after it had been accepted.
    /// </summary>
    public static Result RunSlopeCurveSection(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
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

        Point3d firstPoint = firstPick.Point();

        var secondPick = new GetPoint();
        secondPick.SetCommandPrompt("Pick second point on curve (the grade runs towards it)");
        secondPick.Constrain(source, false);
        secondPick.DynamicDraw += (_, e) => e.Display.DrawPoint(firstPoint, TrackingColor);
        if (secondPick.Get() != GetResult.Point)
            return secondPick.CommandResult();

        Point3d secondPoint = secondPick.Point();

        if (!source.ClosestPoint(firstPoint, out double firstParameter) ||
            !source.ClosestPoint(secondPoint, out double secondParameter))
            return Result.Failure;

        // The picked section's own slope is a better opening value than one remembered from whatever
        // curve was graded last: it makes pressing Enter straight away a no-op, where the cached
        // default used to open at zero and flatten the section on first use.
        double pickedPlanLength = GeometryCommandAlgorithms.CalculatePlanLength(
            source, firstParameter, secondParameter);
        double measuredRatio = pickedPlanLength > RhinoMath.ZeroTolerance
            ? (source.PointAt(secondParameter).Z - source.PointAt(firstParameter).Z) / pickedPlanLength
            : 0.0;

        int modeIndex = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.Mode", 0);
        var slope = new SlopeCommandOption(
            "MoleHill.SlopeCurveSection.Slope", measuredRatio, ignoreCachedValue: true);
        double blendPercent = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.Blend", 100.0);
        bool replaceInput = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.ReplaceInput", true);
        int anchorIndex = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.Anchor", 0);
        const string transitionKey = "MoleHill.SlopeCurveSection.Transition";
        double transitionLength = CommandOptionCache.GetLength(transitionKey, unitContext, 5.0);
        bool smoothFalloff = CommandOptionCache.GetValue("MoleHill.SlopeCurveSection.SmoothFalloff", true);

        // Resolved once and only if a mode asks for it, because the preview asks on every mouse move.
        Mesh? activeTerrain = null;
        bool terrainResolved = false;
        string? terrainError = null;

        Mesh? ResolveTerrain()
        {
            if (terrainResolved)
                return activeTerrain;

            terrainResolved = true;
            TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
            if (terrain == null)
            {
                terrainError = "Select an active MoleHill terrain before blending to terrain.";
                return null;
            }

            activeTerrain = TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
            if (activeTerrain == null)
            {
                terrainError =
                    $"Active terrain '{terrain.Name}' has no current final build. Rebuild it before blending.";
            }

            return activeTerrain;
        }

        bool TryBuildCandidate(
            out Curve? candidate,
            out CurveSectionEditResult candidateReport,
            out string? candidateError,
            bool measureDeviation = true)
        {
            var mode = (CurveSectionEditMode)Math.Clamp(modeIndex, 0, 2);
            Mesh? terrain = mode == CurveSectionEditMode.BlendToTerrain ? ResolveTerrain() : null;
            if (mode == CurveSectionEditMode.BlendToTerrain && terrain == null)
            {
                candidate = null;
                candidateReport = default;
                candidateError = terrainError;
                return false;
            }

            return GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
                source,
                firstParameter,
                secondParameter,
                mode,
                SlopeAnalyzer.ConvertRatioToUnit(slope.Ratio, SlopeAnalyzer.SlopeUnit.Percent),
                blendPercent,
                terrain,
                (CurveSectionAnchor)Math.Clamp(anchorIndex, 0, 2),
                transitionLength,
                smoothFalloff ? SoftEditFalloff.Smooth : SoftEditFalloff.Linear,
                measureDeviation,
                doc.ModelAbsoluteTolerance,
                out candidate,
                out candidateReport,
                out candidateError);
        }

        try
        {
            while (true)
            {
                var mode = (CurveSectionEditMode)Math.Clamp(modeIndex, 0, 2);
                bool isGrade = mode == CurveSectionEditMode.GradePercent;
                bool isBlend = mode == CurveSectionEditMode.BlendToTerrain;

                var options = new GetPoint();
                options.SetCommandPrompt(
                    $"Section edit mode {SectionModeLabels[(int)mode]} — " +
                    $"{DescribeSectionEdit(TryBuildCandidate, doc.ModelAbsoluteTolerance)}. " +
                    "Press Enter to accept");
                options.AcceptNothing(true);

                // A stray click in the viewport is not a reason to abandon the edit.
                options.AcceptPoint(false);

                var blendOption = new OptionDouble(blendPercent, 0.0, 100.0);
                var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
                var transitionOption = new OptionDouble(transitionLength, true, 0.0);
                var falloffOption = new OptionToggle(smoothFalloff, "Linear", "Smooth");

                int modeOptionIndex = options.AddOptionList(
                    "Mode",
                    new[] { "Grade", "BetweenCurrentElevations", "BlendToTerrain" },
                    (int)mode);

                // Each mode adds only what it reads. An option on screen that the current mode ignores
                // reads as a setting that did nothing.
                int anchorOptionIndex = -1;
                if (isGrade)
                {
                    slope.AddTo(options);
                    anchorOptionIndex = options.AddOptionList(
                        "Anchor",
                        new[] { "Start", "End", "Middle" },
                        Math.Clamp(anchorIndex, 0, 2));
                }

                if (isBlend)
                    options.AddOptionDouble("Blend", ref blendOption);

                if (isGrade || isBlend)
                {
                    options.AddOptionDouble("Transition", ref transitionOption);
                    options.AddOptionToggle("Falloff", ref falloffOption);
                }

                options.AddOptionToggle("Output", ref replaceOption);
                options.DynamicDraw += (_, e) =>
                {
                    e.Display.DrawPoint(firstPoint, TrackingColor);
                    e.Display.DrawPoint(secondPoint, TrackingColor);
                    if (!TryBuildCandidate(
                            out Curve? previewCurve,
                            out CurveSectionEditResult _,
                            out string? _,
                            measureDeviation: false) || previewCurve == null)
                        return;

                    e.Display.DrawCurve(previewCurve, TrackingColor, 2);
                    previewCurve.Dispose();
                };

                GetResult optionResult = options.Get();
                if (isGrade)
                    slope.Commit(options, optionResult);

                blendPercent = blendOption.CurrentValue;
                replaceInput = replaceOption.CurrentValue;
                transitionLength = Math.Max(0.0, transitionOption.CurrentValue);
                smoothFalloff = falloffOption.CurrentValue;

                if (optionResult == GetResult.Option)
                {
                    if (options.OptionIndex() == modeOptionIndex)
                        modeIndex = options.Option().CurrentListOptionIndex;
                    else if (anchorOptionIndex >= 0 && options.OptionIndex() == anchorOptionIndex)
                        anchorIndex = options.Option().CurrentListOptionIndex;

                    continue;
                }

                if (optionResult != GetResult.Nothing)
                    return options.CommandResult();

                break;
            }

            CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.Mode", modeIndex);
            CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.Blend", blendPercent);
            CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.ReplaceInput", replaceInput);
            CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.Anchor", anchorIndex);
            CommandOptionCache.SetLength(transitionKey, unitContext, transitionLength);
            CommandOptionCache.SetValue("MoleHill.SlopeCurveSection.SmoothFalloff", smoothFalloff);

            if (!TryBuildCandidate(out Curve? resultCurve, out CurveSectionEditResult report, out string? error) ||
                resultCurve == null)
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

            // What it came out as, not what was asked for: the anchor and the transition both move the
            // ends, so the achieved grade is the only number worth reporting.
            RhinoApp.WriteLine(
                $"Section graded at {SlopeInput.FormatWithUnit(report.SlopeRatio, SlopeUnitPreference.Current)} " +
                $"over {report.PlanLength:F3} " +
                $"({report.FirstElevation:F3} to {report.SecondElevation:F3}).");
            ReportSlopeDeviation(report.Deviation, doc.ModelAbsoluteTolerance, "section");

            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            activeTerrain?.Dispose();
        }
    }

    private static readonly string[] SectionModeLabels = { "Grade", "BetweenCurrentElevations", "BlendToTerrain" };

    private delegate bool SectionEditCandidateBuilder(
        out Curve? candidate,
        out CurveSectionEditResult report,
        out string? error,
        bool measureDeviation);

    /// <summary>
    /// The live outcome of the current settings, for the prompt. Building the candidate is the only way
    /// to know what the anchor and the transition actually produced, so the prompt is built from the
    /// same edit the preview draws.
    /// </summary>
    private static string DescribeSectionEdit(SectionEditCandidateBuilder build, double tolerance)
    {
        if (!build(out Curve? candidate, out CurveSectionEditResult report, out string? error,
                   measureDeviation: true) || candidate == null)
            return error ?? "not possible here";

        candidate.Dispose();
        string summary = $"grade {SlopeInput.FormatWithUnit(report.SlopeRatio, SlopeUnitPreference.Current)}, " +
                         $"ends {report.FirstElevation:F3} to {report.SecondElevation:F3}";

        // Only worth saying when it is worth acting on; an exact polyline edit would otherwise carry a
        // permanent "deviation 0.000" that trains the eye to skip the whole line.
        return report.Deviation.ExceedsTolerance(tolerance)
            ? summary + $", off by {report.Deviation.MaxDeviation:F3}"
            : summary;
    }

    /// <summary>
    /// Says how far a Greville-edited curve actually landed from the slope it was given, but only when
    /// that is more than the document calls coincident. The commands edit control points to keep the
    /// curve editable, so a curved or high-degree curve with few of them can read as a clean grade and
    /// still sag between them; silence here means the edit was exact, which for a polyline it is.
    /// </summary>
    private static void ReportSlopeDeviation(
        GeometryCommandAlgorithms.CurveSlopeDeviation deviation,
        double tolerance,
        string subject)
    {
        if (!deviation.ExceedsTolerance(tolerance))
            return;

        RhinoApp.WriteLine(
            $"The {subject} deviates up to {deviation.MaxDeviation:F3} from the prescribed slope, " +
            $"at station {deviation.Station:F3}. Control points were moved to keep the curve editable; " +
            "rebuild it with more points if that is too coarse.");
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

    /// <summary>
    /// The vertical is either an outright height change or a slope. It used to offer Percent, Degrees
    /// and Ratio as three separate modes, which made the command the odd one out — the same slope, asked
    /// for three ways, none of them the way mhSlopeCurve asked. They collapse into one Slope mode whose
    /// unit is the shared preference, and which accepts all three (and promille) through it.
    /// </summary>
    private static readonly (string Label, bool IsSlope)[] OffsetVerticalModes =
    {
        ("Elevation", false),
        ("Slope", true),
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
        var slope = new SlopeCommandOption("MoleHill.OffsetFeature.Slope", 0.0);
        bool useSourceLayer = CommandOptionCache.GetValue("MoleHill.OffsetFeature.UseSourceLayer", false);

        while (true)
        {
            bool verticalIsSlope = OffsetVerticalModes[verticalModeIndex].IsSlope;
            var getPoint = new GetPoint();
            getPoint.SetCommandPrompt(verticalIsSlope
                ? $"Pick offset side or type distance (slope {slope.Description})"
                : "Pick offset side or type distance");
            getPoint.AcceptNumber(true, false);
            var distanceOption = new OptionDouble(offsetDistance, RhinoMath.ZeroTolerance, 1000000000.0);
            getPoint.AddOptionDouble("Distance", ref distanceOption);

            int verticalListIndex = getPoint.AddOptionList(
                "Vertical",
                OffsetVerticalModes.Select(mode => mode.Label),
                verticalModeIndex);

            // The GetPoint is rebuilt every iteration, so the vertical shows either a height field or
            // the shared slope pair, never both.
            var verticalOption = new OptionDouble(elevationValue);
            if (verticalIsSlope)
                slope.AddTo(getPoint);
            else
                getPoint.AddOptionDouble("DeltaZ", ref verticalOption);

            var layerOption = new OptionToggle(useSourceLayer, "Current", "Source");
            getPoint.AddOptionToggle("Layer", ref layerOption);

            getPoint.DynamicDraw += (_, e) =>
            {
                if (!TryResolveOffsetVertical(
                        verticalIsSlope,
                        slope.Ratio,
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
                CommitOptions(pointResult);
                if (getPoint.OptionIndex() == verticalListIndex)
                    verticalModeIndex = getPoint.Option().CurrentListOptionIndex;

                CommandOptionCache.SetValue("MoleHill.OffsetFeature.VerticalMode", verticalModeIndex);
                continue;
            }

            if (pointResult != GetResult.Point)
                return getPoint.CommandResult();

            CommitOptions(pointResult);

            if (!TryResolveOffsetVertical(
                    verticalIsSlope,
                    slope.Ratio,
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

            void CommitOptions(GetResult commitResult)
            {
                offsetDistance = Math.Abs(distanceOption.CurrentValue);
                CommandOptionCache.SetLength(offsetDistanceKey, unitContext, offsetDistance);

                useSourceLayer = layerOption.CurrentValue;
                CommandOptionCache.SetValue("MoleHill.OffsetFeature.UseSourceLayer", useSourceLayer);

                if (verticalIsSlope)
                {
                    slope.Commit(getPoint, commitResult);
                }
                else
                {
                    elevationValue = verticalOption.CurrentValue;
                    CommandOptionCache.SetLength(offsetElevationKey, unitContext, elevationValue);
                }
            }
        }
    }


    /// <summary>
    /// The slope value + unit option pair every slope-taking command shares, so mhSlopeCurve,
    /// mhGradientInterpolation and mhOffsetFeature no longer each demand a different unit — one asked
    /// for percent, one for promille and one for degrees, and none of them said which at the prompt.
    ///
    /// <para>The command holds a slope as a ratio; the option shows and reads it in the user's slope
    /// unit (<see cref="SlopeUnitPreference"/>), the same preference the panel's cards use. Switching
    /// units here therefore switches them in the panel too — it is one setting, reachable from wherever
    /// the user happens to be. Under the Ratio unit the value is the run <c>n</c> of 1:n, which is how
    /// that unit has always been entered at the command line.</para>
    /// </summary>
    private sealed class SlopeCommandOption
    {
        /// <summary>Steepest ratio still expressible as an angle, so switching to Degrees cannot fail.</summary>
        private static readonly double MaxRatio = Math.Tan(RhinoMath.ToRadians(SlopeInput.MaxSlopeDegrees));

        private static readonly (string Label, SlopeAnalyzer.SlopeUnit Unit)[] UnitChoices =
        {
            ("Percent", SlopeAnalyzer.SlopeUnit.Percent),
            ("Promille", SlopeAnalyzer.SlopeUnit.Promille),
            ("Ratio1toN", SlopeAnalyzer.SlopeUnit.Ratio),
            ("Degrees", SlopeAnalyzer.SlopeUnit.Degrees),
        };

        private readonly string _cacheKey;
        private SlopeAnalyzer.SlopeUnit _unit;
        private OptionDouble _value = new(0.0);
        private int _unitOptionIndex = -1;
        private double _ratio;

        /// <param name="ignoreCachedValue">Opens at <paramref name="defaultRatio" /> rather than at the
        /// value last accepted. Set it when the default is measured from the geometry in hand — a slope
        /// read off the curve being edited describes that curve, and a value remembered from the last
        /// one does not.</param>
        public SlopeCommandOption(string cacheKey, double defaultRatio, bool ignoreCachedValue = false)
        {
            _cacheKey = cacheKey;
            _ratio = ignoreCachedValue ? defaultRatio : CommandOptionCache.GetValue(cacheKey, defaultRatio);
            _unit = SlopeUnitPreference.Current;
        }

        /// <summary>The slope as rise/run — what the geometry actually needs.</summary>
        public double Ratio => _ratio;

        /// <summary>Human-readable slope for a prompt or a report line.</summary>
        public string Description => SlopeInput.FormatWithUnit(_ratio, _unit);

        /// <summary>
        /// Adds the pair to a getter. Getters are rebuilt on every option loop, so this re-reads the
        /// preference each time and relabels itself when the unit changed on the previous pass.
        /// </summary>
        public void AddTo(GetBaseClass get)
        {
            _unit = SlopeUnitPreference.Current;

            // Deliberately the unbounded constructor. A slope option must accept negatives (a falling
            // batter) and has no sensible fixed ceiling once the unit can be percent, promille or the
            // run of 1:n — the ratio is clamped in Commit instead. Note the three-argument overload
            // OptionDouble(value, setLowerLimit, limit) makes `limit` an *upper* bound when
            // setLowerLimit is false, which silently capped this option at zero.
            _value = new OptionDouble(ToDisplayValue(_ratio, _unit));
            get.AddOptionDouble("Slope", ref _value);
            _unitOptionIndex = get.AddOptionList("Units", UnitChoices.Select(choice => choice.Label), IndexOf(_unit));
        }

        /// <summary>
        /// Reads both options back after an interaction with the getter. The typed number is always
        /// interpreted in the unit that was on screen when it was typed, and only then is a newly picked
        /// unit applied — otherwise switching from Percent to Degrees would read "25" as 25 degrees.
        ///
        /// <para><paramref name="result"/> gates the unit list deliberately: <c>OptionIndex</c> reports
        /// the last option touched and is not cleared by a subsequent point or Enter, so reading it
        /// unconditionally would re-apply a unit chosen on an earlier pass.</para>
        /// </summary>
        public void Commit(GetBaseClass get, GetResult result)
        {
            // A number the unit cannot hold (an angle past MaxSlopeDegrees, which tan() would wrap into a
            // falling slope) is rejected rather than clamped, exactly as a panel field rejects it: the
            // previous slope stays and is shown again on the next pass.
            if (SlopeInput.TryConvertDisplayNumber(_value.CurrentValue, _unit, out double ratio))
                _ratio = Math.Clamp(ratio, -MaxRatio, MaxRatio);
            CommandOptionCache.SetValue(_cacheKey, _ratio);

            if (result != GetResult.Option || _unitOptionIndex < 0 || get.OptionIndex() != _unitOptionIndex)
                return;

            int picked = get.Option().CurrentListOptionIndex;
            if (picked >= 0 && picked < UnitChoices.Length)
                SlopeUnitPreference.Current = UnitChoices[picked].Unit;
        }

        private static int IndexOf(SlopeAnalyzer.SlopeUnit unit)
        {
            for (int index = 0; index < UnitChoices.Length; index++)
            {
                if (UnitChoices[index].Unit == unit)
                    return index;
            }

            return 0;
        }

        /// <summary>Ratio to the number the option shows. Ratio unit shows the run n of 1:n.</summary>
        private static double ToDisplayValue(double ratio, SlopeAnalyzer.SlopeUnit unit) =>
            SlopeInput.ToDisplayNumber(ratio, unit);
    }

    /// <summary>
    /// Resolves the offset line's vertical drop or rise from whichever vertical mode is active. The
    /// slope arrives as a ratio from the shared option pair and is handed to the algorithm as a percent,
    /// which is the mode that takes a ratio directly — the per-unit modes below it exist only for the
    /// callers and tests that still speak in degrees or 1:n.
    /// </summary>
    private static bool TryResolveOffsetVertical(
        bool verticalIsSlope,
        double slopeRatio,
        double elevationValue,
        double horizontalDistance,
        out double verticalDelta,
        out string? error)
    {
        return verticalIsSlope
            ? GeometryCommandAlgorithms.TryResolveVerticalDelta(
                OffsetVerticalMode.Percent,
                SlopeAnalyzer.ConvertRatioToUnit(slopeRatio, SlopeAnalyzer.SlopeUnit.Percent),
                horizontalDistance,
                out verticalDelta,
                out error)
            : GeometryCommandAlgorithms.TryResolveVerticalDelta(
                OffsetVerticalMode.Elevation,
                elevationValue,
                horizontalDistance,
                out verticalDelta,
                out error);
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
