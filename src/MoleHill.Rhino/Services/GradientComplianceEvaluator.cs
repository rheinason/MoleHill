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

    /// <summary>Both checks' results for one card on one mesh.</summary>
    internal sealed record Evaluation(
        GradientComplianceAnalyzer.Result LevelAreas,
        GradientComplianceAnalyzer.RouteResult Routes);

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

        GradientComplianceAnalyzer.RouteResult routes = rules.RouteMode == GradientRuleMode.Off
            ? GradientComplianceAnalyzer.RouteResult.Empty(faceCount)
            : GradientComplianceAnalyzer.EvaluateRoutes(
                vertices,
                vertexCount,
                faces,
                faceCount,
                ToXyPolylines(routeCurves, tolerance),
                new GradientComplianceAnalyzer.RouteOptions
                {
                    WalkMaxRunningRatio = ToRatio(rules.WalkMaxSlopeDegrees),
                    RampMaxRunningRatio = ToRatio(rules.RampMaxSlopeDegrees),
                    MaxCrossRatio = ToRatio(rules.CrossMaxSlopeDegrees),
                    Width = Math.Max(0.0, analysis.RouteWidth),
                    MeasurementLength = measurementLength,
                    CancellationRequested = shouldCancel,
                });

        return new Evaluation(levelAreas, routes);
    }

    /// <summary>
    /// Per-face RGB for the preview, as <c>[r0, g0, b0, r1, …]</c>. A face checked as a level area takes
    /// that verdict over its route verdict: a landing on a route is held to the landing's stricter rule.
    /// </summary>
    public static byte[] BuildFaceColors(Evaluation evaluation, GradientRuleSet rules)
    {
        var levelExceed = rules.LevelAreaMode == GradientRuleMode.Warn ? WarnColor : ReportColor;
        var routeExceed = rules.RouteMode == GradientRuleMode.Warn ? WarnColor : ReportColor;
        var level = evaluation.LevelAreas.Verdicts;
        var route = evaluation.Routes.Verdicts;
        var colors = new byte[level.Length * 3];
        for (int face = 0; face < level.Length; face++)
        {
            var color = level[face] switch
            {
                GradientComplianceAnalyzer.FaceVerdict.Pass => PassColor,
                GradientComplianceAnalyzer.FaceVerdict.Exceeds => levelExceed,
                _ => route[face] switch
                {
                    GradientComplianceAnalyzer.RouteVerdict.Walk => PassColor,
                    GradientComplianceAnalyzer.RouteVerdict.Ramp => RampColor,
                    GradientComplianceAnalyzer.RouteVerdict.RunningExceeds => routeExceed,
                    GradientComplianceAnalyzer.RouteVerdict.CrossExceeds => routeExceed,
                    _ => UncheckedColor,
                },
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
