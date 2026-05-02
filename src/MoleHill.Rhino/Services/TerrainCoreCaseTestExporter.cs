using System.Globalization;
using System.Text;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Rhino.Services;

internal sealed record TerrainCoreCaseTestExport(string FileName, string SourceCode);

internal static class TerrainCoreCaseTestExporter
{
    public static bool TryCreate(TerrainBuildSnapshot snapshot, out TerrainCoreCaseTestExport? export)
    {
        export = null;

        var recorder = new TerrainCoreCaseRecorder();
        var runtimeCache = new TerrainRuntimeCache
        {
            CoreCaseRecorder = recorder
        };

        try
        {
            var service = new TerrainBuildService();
            service.Build(snapshot, runtimeCache, TerrainBuildMode.Final);
        }
        catch
        {
            // Keep case export useful even when the dry build throws; recorded calls before
            // the exception can still become a focused regression test.
        }

        TerrainCoreCaseRecord? record = recorder.SelectBestRecord();
        if (record == null)
            return false;

        string terrainName = string.IsNullOrWhiteSpace(snapshot.Terrain.Name)
            ? "Terrain"
            : snapshot.Terrain.Name;
        string caseName = $"{SanitizeIdentifier(terrainName)}_{SanitizeIdentifier(record.StageName)}";
        string className = $"{caseName}_CopiedCaseTests";
        string methodName = $"{caseName}_CopiedCase";
        string fileName = $"{methodName}.cs";
        export = new TerrainCoreCaseTestExport(fileName, GenerateSource(className, methodName, record));
        return true;
    }

    private static string GenerateSource(string className, string methodName, TerrainCoreCaseRecord record)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using MoleHill.Core.Engine;");
        builder.AppendLine("using MoleHill.Core.Grading;");
        builder.AppendLine("using Xunit;");
        builder.AppendLine();
        builder.AppendLine("namespace MoleHill.Core.Tests;");
        builder.AppendLine();
        builder.AppendLine($"public class {className}");
        builder.AppendLine("{");
        builder.AppendLine("    [Fact]");
        builder.AppendLine($"    public void {methodName}()");
        builder.AppendLine("    {");

        switch (record)
        {
            case TerrainCoreTinCaseRecord tin:
                AppendTinTestBody(builder, tin);
                break;
            case TerrainCorePathCaseRecord path:
                AppendPathTestBody(builder, path);
                break;
            case TerrainCorePadCaseRecord pad:
                AppendPadTestBody(builder, pad);
                break;
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendTinTestBody(StringBuilder builder, TerrainCoreTinCaseRecord record)
    {
        AppendDoubleArray(builder, "xyCoords", record.XyCoords, 2);
        AppendDoubleArray(builder, "zValues", record.ZValues, 1);
        AppendIntArray(builder, "segments", record.Segments, 2);
        builder.AppendLine();
        builder.AppendLine("        TinResult? result = new TinEngine().Build(");
        builder.AppendLine("            xyCoords,");
        builder.AppendLine("            zValues,");
        builder.AppendLine("            segments,");
        builder.AppendLine("            QualitySettings.None,");
        builder.AppendLine("            out string? message,");
        builder.AppendLine($"            useConvexHull: {FormatBool(record.UseConvexHull)},");
        builder.AppendLine("            boundaryPeelSettings: new BoundaryTrianglePeelSettings");
        builder.AppendLine("            {");
        builder.AppendLine($"                Enabled = {FormatBool(record.BoundaryPeelSettings.Enabled)},");
        builder.AppendLine($"                MaxBoundaryEdgeLength = {FormatDouble(record.BoundaryPeelSettings.MaxBoundaryEdgeLength)},");
        builder.AppendLine($"                MaxInteriorAngleDegrees = {FormatDouble(record.BoundaryPeelSettings.MaxInteriorAngleDegrees)},");
        builder.AppendLine($"                MaxSlopeAngleDegrees = {FormatDouble(record.BoundaryPeelSettings.MaxSlopeAngleDegrees)}");
        builder.AppendLine("            });");
        builder.AppendLine();
        AppendCommonAssertions(builder, "result", "message", record);
    }

    private static void AppendPathTestBody(StringBuilder builder, TerrainCorePathCaseRecord record)
    {
        AppendDoubleArray(builder, "vertices", record.Vertices, 3);
        builder.AppendLine($"        int vertexCount = {record.VertexCount.ToString(CultureInfo.InvariantCulture)};");
        AppendIntArray(builder, "faces", record.Faces, 3);
        builder.AppendLine($"        int faceCount = {record.FaceCount.ToString(CultureInfo.InvariantCulture)};");
        AppendPathArray(builder, record.Paths);
        AppendConstraintArray(builder, record.HardConstraints);
        builder.AppendLine();
        builder.AppendLine("        GradingResult? result = PathGrader.Grade(");
        builder.AppendLine("            vertices,");
        builder.AppendLine("            vertexCount,");
        builder.AppendLine("            faces,");
        builder.AppendLine("            faceCount,");
        builder.AppendLine("            paths,");
        builder.AppendLine("            hardConstraints,");
        builder.AppendLine("            out string? errorMessage);");
        builder.AppendLine();
        AppendCommonAssertions(builder, "result", "errorMessage", record);
    }

    private static void AppendPadTestBody(StringBuilder builder, TerrainCorePadCaseRecord record)
    {
        AppendDoubleArray(builder, "vertices", record.Vertices, 3);
        builder.AppendLine($"        int vertexCount = {record.VertexCount.ToString(CultureInfo.InvariantCulture)};");
        AppendIntArray(builder, "faces", record.Faces, 3);
        builder.AppendLine($"        int faceCount = {record.FaceCount.ToString(CultureInfo.InvariantCulture)};");
        AppendPadArray(builder, record.Pads);
        AppendLockArray(builder, record.LockCurves);
        builder.AppendLine();
        builder.AppendLine("        GradingResult? result = PadGrader.Grade(");
        builder.AppendLine("            vertices,");
        builder.AppendLine("            vertexCount,");
        builder.AppendLine("            faces,");
        builder.AppendLine("            faceCount,");
        builder.AppendLine("            pads,");
        builder.AppendLine("            lockCurves,");
        builder.AppendLine("            out string? errorMessage);");
        builder.AppendLine();
        AppendCommonAssertions(builder, "result", "errorMessage", record);
    }

    private static void AppendCommonAssertions(
        StringBuilder builder,
        string resultVariable,
        string messageVariable,
        TerrainCoreCaseRecord record)
    {
        builder.AppendLine($"        Assert.NotNull({resultVariable});");
        builder.AppendLine($"        Assert.True(string.IsNullOrWhiteSpace({messageVariable}) || !{messageVariable}.Contains(\"failed\", StringComparison.OrdinalIgnoreCase), {messageVariable});");
        if (record.ResultVertexCount.HasValue)
            builder.AppendLine($"        Assert.Equal({record.ResultVertexCount.Value.ToString(CultureInfo.InvariantCulture)}, {resultVariable}!.VertexCount);");
        if (record.ResultFaceCount.HasValue)
            builder.AppendLine($"        Assert.Equal({record.ResultFaceCount.Value.ToString(CultureInfo.InvariantCulture)}, {resultVariable}!.FaceCount);");
    }

    private static void AppendPathArray(StringBuilder builder, PathGrader.PathDefinition[] paths)
    {
        builder.AppendLine("        var paths = new[]");
        builder.AppendLine("        {");
        foreach (PathGrader.PathDefinition path in paths)
        {
            builder.AppendLine("            new PathGrader.PathDefinition(");
            AppendInlineDoubleArray(builder, path.XyVertices, 16, trailingComma: true);
            AppendInlineDoubleArray(builder, path.ZValues, 16, trailingComma: true);
            builder.AppendLine($"                {path.VertexCount.ToString(CultureInfo.InvariantCulture)},");
            builder.AppendLine($"                {FormatDouble(path.Width)},");
            builder.AppendLine($"                {FormatDouble(path.SlopeAngleDeg)},");
            builder.AppendLine($"                {FormatDouble(path.MaxDistance)}),");
        }
        builder.AppendLine("        };");
    }

    private static void AppendPadArray(StringBuilder builder, PadGrader.PadBoundary[] pads)
    {
        builder.AppendLine("        var pads = new[]");
        builder.AppendLine("        {");
        foreach (PadGrader.PadBoundary pad in pads)
        {
            builder.AppendLine("            PadGrader.PadBoundary.CreatePlanar(");
            AppendInlineDoubleArray(builder, pad.BoundaryVertices, 16, trailingComma: true);
            builder.AppendLine($"                {pad.VertexCount.ToString(CultureInfo.InvariantCulture)},");
            builder.AppendLine($"                {FormatDouble(pad.PlaneXCoeff)},");
            builder.AppendLine($"                {FormatDouble(pad.PlaneYCoeff)},");
            builder.AppendLine($"                {FormatDouble(pad.PlaneConstant)},");
            builder.AppendLine($"                {FormatDouble(pad.SlopeAngleDeg)},");
            builder.AppendLine($"                {FormatDouble(pad.MaxDistance)}),");
        }
        builder.AppendLine("        };");
    }

    private static void AppendLockArray(StringBuilder builder, PadGrader.LockCurve[]? lockCurves)
    {
        if (lockCurves == null || lockCurves.Length == 0)
        {
            builder.AppendLine("        PadGrader.LockCurve[]? lockCurves = null;");
            return;
        }

        builder.AppendLine("        PadGrader.LockCurve[]? lockCurves = new[]");
        builder.AppendLine("        {");
        foreach (PadGrader.LockCurve lockCurve in lockCurves)
        {
            builder.AppendLine("            new PadGrader.LockCurve(");
            AppendInlineDoubleArray(builder, lockCurve.XyVertices, 16, trailingComma: true);
            builder.AppendLine($"                {lockCurve.VertexCount.ToString(CultureInfo.InvariantCulture)}),");
        }
        builder.AppendLine("        };");
    }

    private static void AppendConstraintArray(StringBuilder builder, SurfaceRemesher.ConstraintPolyline[] constraints)
    {
        if (constraints.Length == 0)
        {
            builder.AppendLine("        var hardConstraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>();");
            return;
        }

        builder.AppendLine("        var hardConstraints = new[]");
        builder.AppendLine("        {");
        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            builder.AppendLine("            new SurfaceRemesher.ConstraintPolyline(");
            AppendInlineDoubleArray(builder, constraint.Points, 16, trailingComma: true);
            builder.AppendLine($"                {constraint.PointCount.ToString(CultureInfo.InvariantCulture)},");
            builder.AppendLine($"                {FormatBool(constraint.IsClosed)},");
            builder.AppendLine($"                {FormatBool(constraint.PreserveInputElevation)}),");
        }
        builder.AppendLine("        };");
    }

    private static void AppendDoubleArray(StringBuilder builder, string name, double[] values, int groupSize)
    {
        builder.AppendLine($"        double[] {name} =");
        builder.AppendLine("        {");
        AppendArrayValues(builder, values.Select(FormatDouble), groupSize);
        builder.AppendLine("        };");
    }

    private static void AppendIntArray(StringBuilder builder, string name, int[] values, int groupSize)
    {
        builder.AppendLine($"        int[] {name} =");
        builder.AppendLine("        {");
        AppendArrayValues(builder, values.Select(static value => value.ToString(CultureInfo.InvariantCulture)), groupSize);
        builder.AppendLine("        };");
    }

    private static void AppendInlineDoubleArray(StringBuilder builder, double[] values, int valuesPerLine, bool trailingComma)
    {
        builder.AppendLine("                new[]");
        builder.AppendLine("                {");
        AppendArrayValues(builder, values.Select(FormatDouble), valuesPerLine, indent: "                    ");
        builder.Append("                }");
        builder.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void AppendArrayValues(
        StringBuilder builder,
        IEnumerable<string> values,
        int groupSize,
        string indent = "            ")
    {
        var lineValues = new List<string>(groupSize);
        foreach (string value in values)
        {
            lineValues.Add(value);
            if (lineValues.Count < groupSize)
                continue;

            builder.Append(indent);
            builder.AppendLine(string.Join(", ", lineValues) + ",");
            lineValues.Clear();
        }

        if (lineValues.Count > 0)
        {
            builder.Append(indent);
            builder.AppendLine(string.Join(", ", lineValues) + ",");
        }
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

    private static string FormatBool(bool value) => value ? "true" : "false";

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        string result = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
            result = "Terrain";
        if (char.IsDigit(result[0]))
            result = "_" + result;

        return result;
    }
}
