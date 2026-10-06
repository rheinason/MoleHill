using MoleHill.Core.Reporting;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Lays a <see cref="ReportDocument"/> out as drawn geometry: one text entity per cell, plus the rules
/// that make it read as a table.
///
/// <para>Rhino has no table object, so a table here is text placed on a grid — which is also what a
/// baked Civil 3D summary amounts to. Column widths are measured from the longest cell rather than
/// declared, because a fixed width either clips a long zone name or wastes half the sheet.</para>
///
/// <para>Everything is sized in multiples of the text height, so the whole table rescales with the
/// annotation style instead of coming apart when the drawing scale changes.</para>
///
/// <para>Every piece of text is drawn at exactly that height — no larger title, no emphasised table
/// headings. Baking binds generated text to the terrain's annotation style, and a style governs size, so
/// a cell authored at 1.4× the style height previews large and bakes at 1×: the two would disagree, and
/// preview and bake disagreeing is the one thing this pipeline does not do. Hierarchy comes from the
/// rules and the spacing instead, which the style rescales along with everything else.</para>
/// </summary>
internal static class TerrainReportTableBuilder
{
    /// <summary>
    /// Advance width of one character as a share of the text height. An estimate, not a measurement:
    /// glyph widths need a font and a device, and the build runs off the document thread. Deliberately
    /// generous — a slightly wide table costs nothing, while an underestimate runs a right-aligned
    /// heading back over the column beside it, which is what 0.62 did on a live run.
    /// </summary>
    private const double CharacterWidthRatio = 0.72;

    public static TerrainAnalysisSummary Build(
        ReportDocument report,
        ReportTableAnnotationDefinition annotation,
        RhinoMesh terrainMesh,
        double textHeight,
        LayerRoleTable layerRoles,
        TerrainBuildResult build)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(build);

        var summary = new TerrainAnalysisSummary { AnalysisId = annotation.Id };
        if (textHeight <= 0.0)
            textHeight = 1.0;

        IReadOnlyList<ReportTable> tables = report.Tables;
        if (tables.Count == 0)
        {
            build.Diagnostics.Add($"{annotation.Label}: nothing selected to report.");
            return summary;
        }

        Plane plane = ResolvePlane(annotation, terrainMesh, textHeight);
        string? layerPath = layerRoles.Path(LayerRole.ReportTable);
        double columnGap = Math.Max(0.25, annotation.ColumnGap) * textHeight;
        double rowSpacing = Math.Max(1.0, annotation.RowSpacing) * textHeight;

        int outputCount = 0;
        int rowCount = 0;
        double cursorY = 0.0;

        if (!string.IsNullOrWhiteSpace(report.Title))
        {
            AddText(build, annotation, layerPath, plane, 0.0, cursorY, report.Title, textHeight, TextJustification.BottomLeft);
            outputCount++;
            cursorY -= rowSpacing * 1.6;
        }

        foreach (ReportTable table in tables)
        {
            double[] columnWidths = MeasureColumns(table, textHeight);
            double[] columnStarts = new double[columnWidths.Length];
            double tableWidth = 0.0;
            for (int i = 0; i < columnWidths.Length; i++)
            {
                columnStarts[i] = tableWidth;
                tableWidth += columnWidths[i] + (i < columnWidths.Length - 1 ? columnGap : 0.0);
            }

            if (!string.IsNullOrWhiteSpace(table.Title))
            {
                AddText(build, annotation, layerPath, plane, 0.0, cursorY, table.Title, textHeight, TextJustification.BottomLeft);
                outputCount++;
                cursorY -= rowSpacing;
            }

            for (int i = 0; i < table.Columns.Count; i++)
            {
                AddCell(build, annotation, layerPath, plane, table.Columns[i].DisplayHeading,
                    columnStarts[i], columnWidths[i], cursorY, textHeight, table.Columns[i].Alignment);
                outputCount++;
            }

            cursorY -= rowSpacing;
            if (annotation.ShowGridLines)
            {
                // The rule sits between the headings and the first row, at the text baseline the row
                // above descends to — a quarter-height below the baseline, clear of descenders.
                AddRule(build, annotation, layerPath, plane, 0.0, tableWidth, cursorY + (rowSpacing - textHeight * 0.35));
                outputCount++;
            }

            foreach (IReadOnlyList<string> row in table.Rows)
            {
                for (int i = 0; i < table.Columns.Count && i < row.Count; i++)
                {
                    if (string.IsNullOrEmpty(row[i]))
                        continue;

                    AddCell(build, annotation, layerPath, plane, row[i],
                        columnStarts[i], columnWidths[i], cursorY, textHeight, table.Columns[i].Alignment);
                    outputCount++;
                }

                rowCount++;
                cursorY -= rowSpacing;
            }

            if (annotation.ShowGridLines)
            {
                AddRule(build, annotation, layerPath, plane, 0.0, tableWidth, cursorY + (rowSpacing - textHeight * 0.35));
                outputCount++;
            }

            cursorY -= rowSpacing * 0.6;
        }

        summary.GeneratedOutputCount = outputCount;
        summary.ReportRowCount = rowCount;
        summary.ReportTableCount = tables.Count;
        return summary;
    }

    private static double[] MeasureColumns(ReportTable table, double textHeight)
    {
        var widths = new double[table.Columns.Count];
        for (int i = 0; i < table.Columns.Count; i++)
            widths[i] = EstimateWidth(table.Columns[i].DisplayHeading, textHeight);

        foreach (IReadOnlyList<string> row in table.Rows)
        {
            for (int i = 0; i < widths.Length && i < row.Count; i++)
                widths[i] = Math.Max(widths[i], EstimateWidth(row[i], textHeight));
        }

        return widths;
    }

    private static double EstimateWidth(string? text, double textHeight) =>
        (text?.Length ?? 0) * textHeight * CharacterWidthRatio;

    /// <summary>
    /// Where the table is placed: the picked origin, or beside the terrain's bounding box when none has
    /// been picked. The auto position is to the right of the terrain rather than below it, because a
    /// report reads alongside the plan it describes and the terrain's own annotation — sections, which
    /// auto-place below — should not land on top of it.
    /// </summary>
    private static Plane ResolvePlane(ReportTableAnnotationDefinition annotation, RhinoMesh mesh, double textHeight)
    {
        if (annotation.HasInsertionPlane)
        {
            return new Plane(
                new Point3d(annotation.InsertionOriginX, annotation.InsertionOriginY, annotation.InsertionOriginZ),
                Vector3d.XAxis,
                Vector3d.YAxis);
        }

        BoundingBox bounds = mesh.GetBoundingBox(true);
        if (!bounds.IsValid)
            return Plane.WorldXY;

        double gap = Math.Max((bounds.Max.X - bounds.Min.X) * 0.05, textHeight * 4.0);
        return new Plane(new Point3d(bounds.Max.X + gap, bounds.Max.Y, bounds.Min.Z), Vector3d.XAxis, Vector3d.YAxis);
    }

    private static void AddCell(
        TerrainBuildResult build,
        ReportTableAnnotationDefinition annotation,
        string? layerPath,
        Plane plane,
        string text,
        double columnStart,
        double columnWidth,
        double y,
        double textHeight,
        ReportAlignment alignment)
    {
        bool rightAligned = alignment == ReportAlignment.Right;
        double x = rightAligned ? columnStart + columnWidth : columnStart;
        AddText(build, annotation, layerPath, plane, x, y, text, textHeight,
            rightAligned ? TextJustification.BottomRight : TextJustification.BottomLeft);
    }

    private static void AddText(
        TerrainBuildResult build,
        ReportTableAnnotationDefinition annotation,
        string? layerPath,
        Plane plane,
        double x,
        double y,
        string text,
        double textHeight,
        TextJustification justification)
    {
        var textPlane = new Plane(plane.PointAt(x, y), plane.XAxis, plane.YAxis);
        var entity = new TextEntity
        {
            Plane = textPlane,
            PlainText = text,
            TextHeight = textHeight,
            Justification = justification
        };

        build.AuxiliaryObjects.Add(new GeneratedRhinoObject
        {
            Role = LayerRole.ReportTable,
            Geometry = entity,
            Name = $"{annotation.Label} text",
            AnalysisId = annotation.Id,
            ColorArgb = annotation.ColorArgb,
            AppearanceSource = annotation.ColorArgb.HasValue
                ? GeneratedAppearanceSource.Object
                : GeneratedAppearanceSource.Layer,
            LayerPath = layerPath
        });
    }

    private static void AddRule(
        TerrainBuildResult build,
        ReportTableAnnotationDefinition annotation,
        string? layerPath,
        Plane plane,
        double startX,
        double endX,
        double y)
    {
        var line = new Line(plane.PointAt(startX, y), plane.PointAt(endX, y));
        build.AuxiliaryObjects.Add(new GeneratedRhinoObject
        {
            Role = LayerRole.ReportTable,
            Geometry = new LineCurve(line),
            Name = $"{annotation.Label} rule",
            AnalysisId = annotation.Id,
            ColorArgb = annotation.ColorArgb,
            AppearanceSource = annotation.ColorArgb.HasValue
                ? GeneratedAppearanceSource.Object
                : GeneratedAppearanceSource.Layer,
            LayerPath = layerPath
        });
    }
}
