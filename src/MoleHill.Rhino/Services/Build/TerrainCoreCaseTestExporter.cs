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
        IReadOnlyList<TerrainCoreCaseTestExport> exports = CreateAll(snapshot);
        export = exports.Count > 0 ? exports[0] : null;
        return export != null;
    }

    /// <summary>
    /// One test per recorded grading call, not just the "best" one. A build commonly grades several
    /// times — a Retaining Wall's rails, then a Grade Pad — and <see cref="TerrainCoreCaseRecorder
    /// .SelectBestRecord"/> returns a single record, so the later stage used to evict the earlier one
    /// from the bundle. That is how a failing wall grade shipped a Grade Pad test instead.
    /// </summary>
    public static IReadOnlyList<TerrainCoreCaseTestExport> CreateAll(TerrainBuildSnapshot snapshot)
    {
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

        string terrainName = string.IsNullOrWhiteSpace(snapshot.Terrain.Name)
            ? "Terrain"
            : snapshot.Terrain.Name;

        var exports = new List<TerrainCoreCaseTestExport>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (TerrainCoreCaseRecord record in recorder.Records)
        {
            string caseName = $"{SanitizeIdentifier(terrainName)}_{SanitizeIdentifier(record.StageName)}";
            // Several stages can share a label (a wall grades every rail under one modifier name), so
            // disambiguate rather than overwrite — an evicted record is an unreproducible case.
            string uniqueName = caseName;
            for (int suffix = 2; !usedNames.Add(uniqueName); suffix++)
                uniqueName = $"{caseName}_{suffix.ToString(CultureInfo.InvariantCulture)}";

            string className = $"{uniqueName}_CopiedCaseTests";
            string methodName = $"{uniqueName}_CopiedCase";
            exports.Add(new TerrainCoreCaseTestExport(
                $"{methodName}.cs",
                GenerateSource(className, methodName, record)));
        }

        // The record the old selector would have picked stays first, so the Copy Case clipboard and
        // the manifest's primary entry keep their existing meaning.
        TerrainCoreCaseRecord? best = recorder.SelectBestRecord();
        if (best != null)
        {
            int bestIndex = recorder.Records.ToList().IndexOf(best);
            if (bestIndex > 0)
            {
                TerrainCoreCaseTestExport primary = exports[bestIndex];
                exports.RemoveAt(bestIndex);
                exports.Insert(0, primary);
            }
        }

        return exports;
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
        builder.AppendLine("        GradeOutcome outcome = PathGrader.Grade(new PathGradeRequest");
        builder.AppendLine("        {");
        builder.AppendLine("            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),");
        builder.AppendLine("            Paths = paths,");
        builder.AppendLine("            HardConstraints = hardConstraints,");
        // Both of these default on the request, and both change which tier runs — a replay that omits
        // them grades at the wrong tolerance under the wrong topology strategy.
        builder.AppendLine($"            ModelTolerance = {FormatDouble(record.ModelTolerance)},");
        builder.AppendLine($"            PreferSplitKeep = {(record.PreferSplitKeep ? "true" : "false")}");
        builder.AppendLine("        });");
        builder.AppendLine("        GradingResult? result = outcome.Result;");
        builder.AppendLine("        string? errorMessage = outcome.ErrorMessage;");
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
        builder.AppendLine("        GradeOutcome outcome = PadGrader.Grade(new PadGradeRequest");
        builder.AppendLine("        {");
        builder.AppendLine("            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),");
        builder.AppendLine("            Pads = pads,");
        builder.AppendLine("            LockCurves = lockCurves ?? Array.Empty<PadGrader.LockCurve>()");
        builder.AppendLine("        });");
        builder.AppendLine("        GradingResult? result = outcome.Result;");
        builder.AppendLine("        string? errorMessage = outcome.ErrorMessage;");
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
            builder.AppendLine($"                {FormatDouble(path.MaxDistance)},");
            builder.AppendLine($"                {FormatDouble(path.FillSlopeAngleDeg)},");
            if (path.LeftEdgeXy is null)
                builder.AppendLine("                null,");
            else
                AppendInlineDoubleArray(builder, path.LeftEdgeXy, 16, trailingComma: true);
            if (path.RightEdgeXy is null)
                builder.AppendLine("                null,");
            else
                AppendInlineDoubleArray(builder, path.RightEdgeXy, 16, trailingComma: true);
            builder.AppendLine($"                {(path.IsClosed ? "true" : "false")},");
            builder.AppendLine($"                {FormatDouble(path.LeftCutSlopeAngleDeg)},");
            builder.AppendLine($"                {FormatDouble(path.LeftFillSlopeAngleDeg)},");
            builder.AppendLine($"                {FormatDouble(path.RightCutSlopeAngleDeg)},");
            builder.AppendLine($"                {FormatDouble(path.RightFillSlopeAngleDeg)},");
            // The one-sidedness of a retaining-wall rail lives here and nowhere else.
            if (path.OutwardNormals is null)
                builder.AppendLine("                null),");
            else
                AppendInlineDoubleArray(builder, path.OutwardNormals, 16, trailingComma: false, suffix: "),");
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
            builder.AppendLine($"                {FormatDouble(pad.MaxDistance)},");
            builder.AppendLine($"                stitchApronDistance: {FormatDouble(pad.StitchApronDistance)},");
            builder.AppendLine($"                fillSlopeAngleDeg: {FormatDouble(pad.FillSlopeAngleDeg)}),");
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

    private static void AppendConstraintArray(StringBuilder builder, ConstraintPolyline[] constraints)
    {
        if (constraints.Length == 0)
        {
            builder.AppendLine("        var hardConstraints = Array.Empty<ConstraintPolyline>();");
            return;
        }

        builder.AppendLine("        var hardConstraints = new[]");
        builder.AppendLine("        {");
        foreach (ConstraintPolyline constraint in constraints)
        {
            builder.AppendLine("            new ConstraintPolyline(");
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

    private static void AppendInlineDoubleArray(
        StringBuilder builder,
        double[] values,
        int valuesPerLine,
        bool trailingComma,
        string? suffix = null)
    {
        // Explicitly double[], never an inferred new[]. FormatDouble writes a whole number as "0", so an
        // array of whole numbers infers int[] and the generated case does not compile — and a wall rail's
        // outward normals are almost always exactly 0 and 1.
        builder.AppendLine("                new double[]");
        builder.AppendLine("                {");
        AppendArrayValues(builder, values.Select(FormatDouble), valuesPerLine, indent: "                    ");
        builder.Append("                }");
        builder.AppendLine(suffix ?? (trailingComma ? "," : string.Empty));
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
