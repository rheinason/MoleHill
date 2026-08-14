using System.Globalization;
using System.Text;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed record RetainingWallPlannerCaseTestExport(string FileName, string SourceCode);

internal static class RetainingWallPlannerCaseTestExporter
{
    public static IReadOnlyList<RetainingWallPlannerCaseTestExport> Create(TerrainBuildSnapshot snapshot)
    {
        var exports = new List<RetainingWallPlannerCaseTestExport>();
        int index = 1;
        foreach (RetainingWallModifierDefinition modifier in snapshot.Terrain.Modifiers.OfType<RetainingWallModifierDefinition>())
        {
            if (!modifier.IsEnabled)
                continue;

            IReadOnlyList<Curve> curves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.WallCurves);
            if (curves.Count == 0)
                continue;

            TerrainTolerancePolicy.Profile toleranceProfile = TerrainTolerancePolicy.Create(
                snapshot.Terrain.GlobalTolerance,
                snapshot.ModelAbsoluteTolerance,
                snapshot.ResolvedUnitContext);
            double wallTolerance = toleranceProfile.RetainingWallTolerance(modifier.MaxWallWidth);
            double maxWallWidth = Math.Max(wallTolerance, modifier.MaxWallWidth);
            if (!TryConvertCurves(
                    curves,
                    Math.Max(wallTolerance, snapshot.ResolvedUnitContext.FromMeters(1e-9)),
                    out List<Point3d[]> polylines))
                continue;

            string label = string.IsNullOrWhiteSpace(modifier.Label) ? "RetainingWall" : modifier.Label;
            string caseName = $"Terrain_{SanitizeIdentifier(snapshot.Terrain.Name)}_{SanitizeIdentifier(label)}_{index.ToString(CultureInfo.InvariantCulture)}";
            exports.Add(new RetainingWallPlannerCaseTestExport(
                $"{caseName}_PlannerCopiedCase.cs",
                GenerateSource(caseName, polylines, maxWallWidth, wallTolerance)));
            index++;
        }

        return exports;
    }

    private static bool TryConvertCurves(IReadOnlyList<Curve> curves, double tolerance, out List<Point3d[]> polylines)
    {
        polylines = new List<Point3d[]>(curves.Count);
        double chordTol = Math.Max(tolerance, 1e-9);
        double angleTol = 5.0 * Math.PI / 180.0;
        double maxEdgeLength = chordTol * 8.0;
        foreach (Curve curve in curves)
        {
            Polyline polyline;
            if (!curve.TryGetPolyline(out polyline))
            {
                using PolylineCurve? converted = curve.ToPolyline(chordTol, angleTol, 0.0, maxEdgeLength);
                if (converted == null || !converted.TryGetPolyline(out polyline))
                    return false;
            }

            var points = new Point3d[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
                points[i] = polyline[i];

            if (points.Length < 2)
                return false;

            polylines.Add(points);
        }

        return polylines.Count > 0;
    }

    private static string GenerateSource(
        string caseName,
        IReadOnlyList<Point3d[]> polylines,
        double maxWallWidth,
        double curveParsingTolerance)
    {
        string className = $"{caseName}_PlannerCopiedCaseTests";
        string methodName = $"{caseName}_PlannerCopiedCase";
        var builder = new StringBuilder();
        builder.AppendLine("using MoleHill.Shared;");
        builder.AppendLine("using Rhino.Geometry;");
        builder.AppendLine("using Xunit;");
        builder.AppendLine();
        builder.AppendLine("namespace MoleHill.Grasshopper.Tests;");
        builder.AppendLine();
        builder.AppendLine($"public class {className}");
        builder.AppendLine("{");
        builder.AppendLine("    [Fact]");
        builder.AppendLine($"    public void {methodName}()");
        builder.AppendLine("    {");
        AppendCurves(builder, polylines);
        builder.AppendLine();
        builder.AppendLine("        var plan = RetainingWallPlannerCore.Plan(");
        builder.AppendLine("            curves,");
        builder.AppendLine($"            {FormatDouble(maxWallWidth)},");
        builder.AppendLine($"            curveParsingTolerance: {FormatDouble(curveParsingTolerance)});");
        builder.AppendLine();
        builder.AppendLine("        string report = string.Join(System.Environment.NewLine, plan.Report.Select(entry => entry.ToString()));");
        builder.AppendLine("        Assert.True(plan.Walls.Count > 0, report);");
        builder.AppendLine("        Assert.DoesNotContain(plan.Report, entry => entry.Level == RetainingWallPlannerCore.ReportLevel.Error);");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendCurves(StringBuilder builder, IReadOnlyList<Point3d[]> polylines)
    {
        builder.AppendLine("        Curve[] curves =");
        builder.AppendLine("        {");
        foreach (Point3d[] polyline in polylines)
        {
            builder.AppendLine("            new PolylineCurve(new[]");
            builder.AppendLine("            {");
            foreach (Point3d point in polyline)
            {
                builder.AppendLine(
                    $"                new Point3d({FormatDouble(point.X)}, {FormatDouble(point.Y)}, {FormatDouble(point.Z)}),");
            }

            builder.AppendLine("            }),");
        }

        builder.AppendLine("        };");
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
            return "double.NaN";
        if (double.IsPositiveInfinity(value))
            return "double.PositiveInfinity";
        if (double.IsNegativeInfinity(value))
            return "double.NegativeInfinity";

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            value = "Terrain";

        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        string result = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
            result = "Terrain";
        if (char.IsDigit(result[0]))
            result = "_" + result;

        return result;
    }
}
