using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Runs a gradient compliance card against mesh arrays, for both the build stage and the preview.
/// The build stage summarizes and the preview builder colours; sharing this means the two cannot
/// disagree about which ground fails.
/// </summary>
internal static class GradientComplianceEvaluator
{
    /// <summary>Ground within the limit.</summary>
    internal static readonly (byte R, byte G, byte B) PassColor = (67, 160, 71);

    /// <summary>Ground over a limit whose rule is set to Report: information, not a failure.</summary>
    internal static readonly (byte R, byte G, byte B) ReportColor = (249, 168, 37);

    /// <summary>Ground over a limit whose rule is set to Warn.</summary>
    internal static readonly (byte R, byte G, byte B) WarnColor = (211, 47, 47);

    /// <summary>Ground no rule applies to.</summary>
    internal static readonly (byte R, byte G, byte B) UncheckedColor = (130, 130, 130);

    /// <summary>Route ground in the ramp band: allowed, so not a failure, but not a walk either.</summary>
    internal static readonly (byte R, byte G, byte B) RampColor = (30, 136, 229);

    /// <summary>A detected landing on a route that passes: shown so the detection can be seen and trusted.</summary>
    internal static readonly (byte R, byte G, byte B) LandingColor = (0, 137, 123);

    /// <summary>
    /// Every check's results for one card on one mesh. <see cref="FaceRuns"/> maps each face to the
    /// index in <see cref="Runs"/> of the run beside it, or -1.
    /// </summary>
    internal sealed record Evaluation(
        GradientComplianceAnalyzer.Result LevelAreas,
        GradientComplianceAnalyzer.RouteResult Routes,
        IReadOnlyList<RouteRunAnalyzer.Run> Runs,
        int[] FaceRuns)
    {
        public Evaluation(GradientComplianceAnalyzer.Result levelAreas, GradientComplianceAnalyzer.RouteResult routes)
            : this(levelAreas, routes, Array.Empty<RouteRunAnalyzer.Run>(), Enumerable.Repeat(-1, levelAreas.Verdicts.Length).ToArray())
        {
        }
    }

    public static Evaluation Evaluate(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<Curve> levelAreaCurves,
        IReadOnlyList<Curve> routeCurves,
        GradientComplianceAnalysisDefinition analysis,
        double tolerance,
        Func<bool>? shouldCancel = null)
    {
        GradientRuleSet rules = analysis.Rules;
        double measurementLength = Math.Max(0.0, analysis.MeasurementLength);

        GradientComplianceAnalyzer.Result levelAreas = rules.LevelAreaMode == GradientRuleMode.Off
            ? GradientComplianceAnalyzer.Result.Empty(faceCount)
            : GradientComplianceAnalyzer.EvaluateLevelAreas(
                vertices,
                vertexCount,
                faces,
                faceCount,
                ToXyLoops(levelAreaCurves, tolerance),
                new GradientComplianceAnalyzer.Options
                {
                    LevelAreaMaxSlopeRatio = ToRatio(rules.LevelAreaMaxSlopeDegrees),
                    MeasurementLength = measurementLength,
                    CancellationRequested = shouldCancel,
                });

        List<double[]> routePolylines = rules.RouteMode == GradientRuleMode.Off
            ? new List<double[]>()
            : ToXyPolylines(routeCurves, tolerance);
        GradientComplianceAnalyzer.RouteResult routes = rules.RouteMode == GradientRuleMode.Off
            ? GradientComplianceAnalyzer.RouteResult.Empty(faceCount)
            : GradientComplianceAnalyzer.EvaluateRoutes(
                vertices,
                vertexCount,
                faces,
                faceCount,
                routePolylines,
                new GradientComplianceAnalyzer.RouteOptions
                {
                    WalkMaxRunningRatio = ToRatio(rules.WalkMaxSlopeDegrees),
                    RampMaxRunningRatio = ToRatio(rules.RampMaxSlopeDegrees),
                    MaxCrossRatio = ToRatio(rules.CrossMaxSlopeDegrees),
                    Width = Math.Max(0.0, analysis.RouteWidth),
                    MeasurementLength = measurementLength,
                    CancellationRequested = shouldCancel,
                });

        if (routePolylines.Count == 0 || faceCount == 0)
            return new Evaluation(levelAreas, routes);

        // Runs follow each route along its length. Landings are detected as level stretches, level by
        // the same limit a drawn level area is held to.
        var projector = new MeshHeightProjector(vertices, vertexCount, faces, faceCount);
        double spacing = Math.Max(measurementLength, analysis.RouteWidth) / 10.0;
        IReadOnlyList<RouteRunAnalyzer.Run> runs = RouteRunAnalyzer.Analyze(
            projector,
            routePolylines,
            new RouteRunAnalyzer.Options
            {
                LandingMaxRatio = ToRatio(rules.LevelAreaMaxSlopeDegrees),
                WalkMaxRatio = ToRatio(rules.WalkMaxSlopeDegrees),
                LandingMinLength = Math.Max(0.0, rules.LandingMinLength),
                WalkMaxRise = rules.WalkMaxRise > 0.0 ? rules.WalkMaxRise : double.PositiveInfinity,
                RampMaxRise = rules.RampMaxRise > 0.0 ? rules.RampMaxRise : double.PositiveInfinity,
                RampGoingLimits = rules.RampGoingLimits
                    .Select(limit => new RouteRunAnalyzer.GoingLimit(ToRatio(limit.SlopeDegrees), limit.MaxGoing))
                    .ToArray(),
                InterpolateGoing = rules.InterpolateGoing,
                StationSpacing = spacing > 0.0 ? spacing : tolerance * 10.0,
                SmoothingLength = measurementLength,
                Tolerance = tolerance,
                CancellationRequested = shouldCancel,
            });
        int[] faceRuns = RouteRunAnalyzer.MapFacesToRuns(
            vertices, faces, faceCount, runs, Math.Max(0.0, analysis.RouteWidth) * 0.5);
        return new Evaluation(levelAreas, routes, runs, faceRuns);
    }

    /// <summary>
    /// Per-face RGB for the preview, as <c>[r0, g0, b0, r1, …]</c>. Precedence, strongest first: a drawn
    /// level area's verdict (a landing is held to the landing's stricter rule); a run that fails on rise
    /// or going; the face's own running or cross slope failure; a detected landing; ramp; walk.
    /// </summary>
    public static byte[] BuildFaceColors(Evaluation evaluation, GradientRuleSet rules)
    {
        var levelExceed = rules.LevelAreaMode == GradientRuleMode.Warn ? WarnColor : ReportColor;
        var routeExceed = rules.RouteMode == GradientRuleMode.Warn ? WarnColor : ReportColor;
        var level = evaluation.LevelAreas.Verdicts;
        var route = evaluation.Routes.Verdicts;
        var colors = new byte[level.Length * 3];

        (byte R, byte G, byte B) RouteColor(int face)
        {
            GradientComplianceAnalyzer.RouteVerdict verdict = route[face];
            if (verdict == GradientComplianceAnalyzer.RouteVerdict.Unchecked)
                return UncheckedColor;

            int runIndex = face < evaluation.FaceRuns.Length ? evaluation.FaceRuns[face] : -1;
            RouteRunAnalyzer.Run? run = runIndex >= 0 ? evaluation.Runs[runIndex] : null;
            if (run != null && run.Failures != RouteRunAnalyzer.RunFailure.None)
                return routeExceed;

            return verdict switch
            {
                GradientComplianceAnalyzer.RouteVerdict.RunningExceeds => routeExceed,
                GradientComplianceAnalyzer.RouteVerdict.CrossExceeds => routeExceed,
                _ when run?.Kind == RouteRunAnalyzer.RunKind.Landing => LandingColor,
                GradientComplianceAnalyzer.RouteVerdict.Ramp => RampColor,
                _ => PassColor,
            };
        }
        for (int face = 0; face < level.Length; face++)
        {
            var color = level[face] switch
            {
                GradientComplianceAnalyzer.FaceVerdict.Pass => PassColor,
                GradientComplianceAnalyzer.FaceVerdict.Exceeds => levelExceed,
                _ => RouteColor(face),
            };
            colors[face * 3] = color.R;
            colors[(face * 3) + 1] = color.G;
            colors[(face * 3) + 2] = color.B;
        }

        return colors;
    }

    private static double ToRatio(double degrees) =>
        SlopeAnalyzer.ConvertUnitToRatio(degrees, SlopeAnalyzer.SlopeUnit.Degrees);

    /// <summary>Route curves as flat XY polylines, open or closed. Degenerate curves are skipped.</summary>
    internal static List<double[]> ToXyPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        var polylines = new List<double[]>(curves.Count);
        foreach (Curve curve in curves)
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out Polyline polyline) ||
                polyline.Count < 2)
                continue;

            var xy = new double[polyline.Count * 2];
            for (int i = 0; i < polyline.Count; i++)
            {
                xy[i * 2] = polyline[i].X;
                xy[(i * 2) + 1] = polyline[i].Y;
            }

            polylines.Add(xy);
        }

        return polylines;
    }

    /// <summary>
    /// Closed curves as flat, non-repeating XY loops. Open or degenerate curves are skipped: an area that
    /// does not close has no inside to check.
    /// </summary>
    internal static List<double[]> ToXyLoops(IReadOnlyList<Curve> curves, double tolerance)
    {
        var loops = new List<double[]>(curves.Count);
        foreach (Curve curve in curves)
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out Polyline polyline))
                continue;

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[^1]) <= tolerance)
                count--;
            if (count < 3)
                continue;

            var xy = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xy[i * 2] = polyline[i].X;
                xy[(i * 2) + 1] = polyline[i].Y;
            }

            loops.Add(xy);
        }

        return loops;
    }
}
