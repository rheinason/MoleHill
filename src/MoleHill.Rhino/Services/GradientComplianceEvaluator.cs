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

    public static GradientComplianceAnalyzer.Result Evaluate(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<Curve> levelAreaCurves,
        GradientComplianceAnalysisDefinition analysis,
        double tolerance,
        Func<bool>? shouldCancel = null)
    {
        if (analysis.Rules.LevelAreaMode == GradientRuleMode.Off)
            return GradientComplianceAnalyzer.Result.Empty(faceCount);

        return GradientComplianceAnalyzer.EvaluateLevelAreas(
            vertices,
            vertexCount,
            faces,
            faceCount,
            ToXyLoops(levelAreaCurves, tolerance),
            new GradientComplianceAnalyzer.Options
            {
                LevelAreaMaxSlopeRatio = SlopeAnalyzer.ConvertUnitToRatio(
                    analysis.Rules.LevelAreaMaxSlopeDegrees, SlopeAnalyzer.SlopeUnit.Degrees),
                MeasurementLength = Math.Max(0.0, analysis.MeasurementLength),
                CancellationRequested = shouldCancel,
            });
    }

    /// <summary>Per-face RGB for the preview, as <c>[r0, g0, b0, r1, …]</c>.</summary>
    public static byte[] BuildFaceColors(GradientComplianceAnalyzer.Result result, GradientRuleMode mode)
    {
        var exceedColor = mode == GradientRuleMode.Warn ? WarnColor : ReportColor;
        var colors = new byte[result.Verdicts.Length * 3];
        for (int face = 0; face < result.Verdicts.Length; face++)
        {
            var color = result.Verdicts[face] switch
            {
                GradientComplianceAnalyzer.FaceVerdict.Pass => PassColor,
                GradientComplianceAnalyzer.FaceVerdict.Exceeds => exceedColor,
                _ => UncheckedColor,
            };
            colors[face * 3] = color.R;
            colors[(face * 3) + 1] = color.G;
            colors[(face * 3) + 2] = color.B;
        }

        return colors;
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
