using System.Globalization;
using MoleHill.Core.Analysis;
using MoleHill.Core.Reporting;
using MoleHill.Rhino.Model;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Assembles what the last build measured into a <see cref="ReportDocument"/> — the deliverable form of
/// the numbers the panel shows.
///
/// <para>This is the single place a terrain's figures are rounded and labelled. Both report outputs read
/// the document it returns: <c>mhExportTerrainReport</c> writes it as CSV, and the Report Table
/// annotation draws it into the model. They cannot disagree about a volume because neither computes
/// one.</para>
///
/// <para>Every figure is unit-bearing and the unit is named on its column, never left to the reader.
/// Lengths, areas and volumes are in model units — the numbers are already in them, and converting to
/// some canonical unit would print figures no dimension in the drawing agrees with. Slope is shown in
/// the user's slope unit preference, like every other slope in the product.</para>
///
/// <para>It reports what was measured and nothing else. A table with no rows is dropped rather than
/// printed empty, and a quantity that was not measured is blank rather than zero — a zero in a quantity
/// report is a claim, and "no ponding analysis is switched on" must not read as "the terrain holds no
/// water".</para>
/// </summary>
internal static class TerrainReportBuilder
{
    public static ReportDocument Build(
        TerrainDefinition terrain,
        IReadOnlyList<ZoneAnalysisSummary> zoneSummaries,
        IReadOnlyList<TerrainAnalysisSummary> analysisSummaries,
        ModelUnitContext unitContext,
        SlopeAnalyzer.SlopeUnit slopeUnit,
        DateTime generatedAt,
        TerrainReportSections sections = TerrainReportSections.All)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        zoneSummaries ??= Array.Empty<ZoneAnalysisSummary>();
        analysisSummaries ??= Array.Empty<TerrainAnalysisSummary>();

        var document = new ReportDocument($"{terrain.Name} — quantity report");
        if (sections.HasFlag(TerrainReportSections.Overview))
            AppendOverview(document, terrain, analysisSummaries, unitContext, generatedAt);
        if (sections.HasFlag(TerrainReportSections.Zones))
            AppendZones(document, terrain, zoneSummaries, unitContext, slopeUnit);
        if (sections.HasFlag(TerrainReportSections.Earthworks))
            AppendEarthworks(document, terrain, analysisSummaries, unitContext);
        if (sections.HasFlag(TerrainReportSections.Ponding))
            AppendPonding(document, terrain, analysisSummaries, unitContext);
        if (sections.HasFlag(TerrainReportSections.Catchments))
            AppendCatchments(document, terrain, analysisSummaries, unitContext);
        document.RemoveEmptyTables();
        return document;
    }

    private static void AppendOverview(
        ReportDocument document,
        TerrainDefinition terrain,
        IReadOnlyList<TerrainAnalysisSummary> analysisSummaries,
        ModelUnitContext unitContext,
        DateTime generatedAt)
    {
        ReportTable table = document.AddTable(
            "Terrain",
            new ReportColumn("Property"),
            new ReportColumn("Value", Alignment: ReportAlignment.Right),
            new ReportColumn("Unit"));

        table.AddRow("Terrain", terrain.Name, string.Empty);
        table.AddRow("Generated", generatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), string.Empty);
        table.AddRow("Model units", unitContext.Abbreviation, string.Empty);

        // Surface area and elevation range are properties of the terrain, but only an analysis ever
        // measures them, so they appear here only when one did.
        TerrainAnalysisSummary? withArea = analysisSummaries.FirstOrDefault(static summary => summary.SurfaceArea > 0.0);
        if (withArea != null)
            table.AddRow("Surface area", CsvWriter.Number(withArea.SurfaceArea), AreaUnit(unitContext));

        TerrainAnalysisSummary? withElevation = analysisSummaries
            .FirstOrDefault(static summary => summary.ElevationMaxZ > summary.ElevationMinZ);
        if (withElevation != null)
        {
            table.AddRow("Lowest point", CsvWriter.Number(withElevation.ElevationMinZ), unitContext.Abbreviation);
            table.AddRow("Highest point", CsvWriter.Number(withElevation.ElevationMaxZ), unitContext.Abbreviation);
        }

        table.AddRow("Zones", terrain.Zones.Count.ToString(CultureInfo.InvariantCulture), string.Empty);
    }

    private static void AppendZones(
        ReportDocument document,
        TerrainDefinition terrain,
        IReadOnlyList<ZoneAnalysisSummary> zoneSummaries,
        ModelUnitContext unitContext,
        SlopeAnalyzer.SlopeUnit slopeUnit)
    {
        if (zoneSummaries.Count == 0)
            return;

        string slopeUnitLabel = SlopeInput.Suffix(slopeUnit);
        ReportTable table = document.AddTable(
            "Zones",
            new ReportColumn("Zone"),
            new ReportColumn("Plan Area", AreaUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Surface Area", AreaUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Min Level", unitContext.Abbreviation, ReportAlignment.Right),
            new ReportColumn("Mean Level", unitContext.Abbreviation, ReportAlignment.Right),
            new ReportColumn("Max Level", unitContext.Abbreviation, ReportAlignment.Right),
            new ReportColumn("Min Slope", slopeUnitLabel, ReportAlignment.Right),
            new ReportColumn("Mean Slope", slopeUnitLabel, ReportAlignment.Right),
            new ReportColumn("Max Slope", slopeUnitLabel, ReportAlignment.Right),
            new ReportColumn("Triangles", Alignment: ReportAlignment.Right),
            new ReportColumn("Cut", VolumeUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Fill", VolumeUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Net", VolumeUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Earthwork basis"));

        // Zone order in the report is zone order in the panel — later zones win, so reading the table top
        // to bottom is reading the priority the terrain was resolved with.
        var byId = new Dictionary<Guid, ZoneAnalysisSummary>();
        foreach (ZoneAnalysisSummary summary in zoneSummaries)
            byId[summary.ZoneId] = summary;

        double cutTotal = 0.0;
        double fillTotal = 0.0;
        bool anyEarthwork = false;
        bool anyEstimated = false;

        foreach (CollageZoneDefinition zone in terrain.Zones)
        {
            if (!byId.TryGetValue(zone.ZoneId, out ZoneAnalysisSummary? summary))
                continue;

            bool hasEarthwork = summary.HasEarthwork;
            if (hasEarthwork)
            {
                anyEarthwork = true;
                anyEstimated |= summary.EarthworkIsEstimated;
                cutTotal += summary.CutVolume;
                fillTotal += summary.FillVolume;
            }

            table.AddRow(
                zone.Name,
                CsvWriter.Number(summary.PlanArea),
                CsvWriter.Number(summary.SurfaceArea),
                CsvWriter.Number(summary.ElevationMinZ),
                CsvWriter.Number(summary.ElevationAverageZ),
                CsvWriter.Number(summary.ElevationMaxZ),
                FormatSlope(summary.SlopeMinPercent, slopeUnit),
                FormatSlope(summary.SlopeAveragePercent, slopeUnit),
                FormatSlope(summary.SlopeMaxPercent, slopeUnit),
                summary.TriangleCount.ToString(CultureInfo.InvariantCulture),
                hasEarthwork ? CsvWriter.Number(summary.CutVolume) : string.Empty,
                hasEarthwork ? CsvWriter.Number(summary.FillVolume) : string.Empty,
                hasEarthwork ? CsvWriter.Number(summary.NetVolume) : string.Empty,
                hasEarthwork ? (summary.EarthworkIsEstimated ? "Estimated" : "Exact") : "Not measured");
        }

        if (!table.HasRows)
            return;

        // The totals row is the line a quantity surveyor reads first. It sums only the zones that were
        // measured, and says so, rather than treating an unmeasured zone as zero.
        table.AddRow(
            "Total",
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty,
            anyEarthwork ? CsvWriter.Number(cutTotal) : string.Empty,
            anyEarthwork ? CsvWriter.Number(fillTotal) : string.Empty,
            anyEarthwork ? CsvWriter.Number(cutTotal - fillTotal) : string.Empty,
            anyEarthwork ? (anyEstimated ? "Estimated" : "Exact") : "Not measured");
    }

    private static void AppendEarthworks(
        ReportDocument document,
        TerrainDefinition terrain,
        IReadOnlyList<TerrainAnalysisSummary> analysisSummaries,
        ModelUnitContext unitContext)
    {
        ReportTable table = document.AddTable(
            "Earthworks",
            new ReportColumn("Analysis"),
            new ReportColumn("Cut", VolumeUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Fill", VolumeUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Net", VolumeUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Basis"));

        // Cut / Fill measures the same volumes as Earthworks (both compare against a reference), so both
        // report here. Each analysis is its own row under its own label and the table carries no total, so
        // a terrain with both is never summed twice.
        foreach (ReferenceComparisonAnalysisDefinition analysis in terrain.Analyses.OfType<ReferenceComparisonAnalysisDefinition>())
        {
            TerrainAnalysisSummary? summary = Find(analysisSummaries, analysis.Id);
            if (summary == null)
                continue;

            table.AddRow(
                analysis.Label,
                CsvWriter.Number(summary.CutVolume),
                CsvWriter.Number(summary.FillVolume),
                CsvWriter.Number(summary.NetVolume),
                summary.EarthworkIsEstimated ? "Estimated" : "Exact");
        }
    }

    private static void AppendPonding(
        ReportDocument document,
        TerrainDefinition terrain,
        IReadOnlyList<TerrainAnalysisSummary> analysisSummaries,
        ModelUnitContext unitContext)
    {
        ReportTable table = document.AddTable(
            "Ponding",
            new ReportColumn("Analysis"),
            new ReportColumn("Ponds", Alignment: ReportAlignment.Right),
            new ReportColumn("Impounded", VolumeUnit(unitContext), ReportAlignment.Right),
            new ReportColumn("Max Depth", unitContext.Abbreviation, ReportAlignment.Right),
            new ReportColumn("Water Area", AreaUnit(unitContext), ReportAlignment.Right));

        foreach (PondingAnalysisDefinition analysis in terrain.Analyses.OfType<PondingAnalysisDefinition>())
        {
            TerrainAnalysisSummary? summary = Find(analysisSummaries, analysis.Id);
            if (summary == null)
                continue;

            // The nullable quantities stay blank where nothing was measured: "no depression was found"
            // and "a depression holding nothing" are different answers, and only one is reassuring.
            table.AddRow(
                analysis.Label,
                summary.PondCount.ToString(CultureInfo.InvariantCulture),
                Optional(summary.PondTotalVolume),
                Optional(summary.PondMaxDepth),
                Optional(summary.PondTotalArea));
        }
    }

    private static void AppendCatchments(
        ReportDocument document,
        TerrainDefinition terrain,
        IReadOnlyList<TerrainAnalysisSummary> analysisSummaries,
        ModelUnitContext unitContext)
    {
        ReportTable table = document.AddTable(
            "Catchments",
            new ReportColumn("Analysis"),
            new ReportColumn("Basins", Alignment: ReportAlignment.Right),
            new ReportColumn("Closed Depressions", Alignment: ReportAlignment.Right),
            new ReportColumn("Largest Basin", AreaUnit(unitContext), ReportAlignment.Right));

        foreach (CatchmentAnalysisDefinition analysis in terrain.Analyses.OfType<CatchmentAnalysisDefinition>())
        {
            TerrainAnalysisSummary? summary = Find(analysisSummaries, analysis.Id);
            if (summary == null)
                continue;

            table.AddRow(
                analysis.Label,
                summary.CatchmentBasinCount.ToString(CultureInfo.InvariantCulture),
                summary.CatchmentSinkCount.ToString(CultureInfo.InvariantCulture),
                Optional(summary.CatchmentLargestArea));
        }
    }

    private static TerrainAnalysisSummary? Find(IReadOnlyList<TerrainAnalysisSummary> summaries, Guid analysisId)
    {
        foreach (TerrainAnalysisSummary summary in summaries)
        {
            if (summary.AnalysisId == analysisId)
                return summary;
        }

        return null;
    }

    private static string Optional(double? value) =>
        value.HasValue ? CsvWriter.Number(value.Value) : string.Empty;

    /// <summary>Slope summaries are stored in percent; the report shows the requested slope unit, like
    /// every other slope in the product. Ratio formats as "1:3" in the cell, so its column unit is "V:H".
    /// Fixed decimals and invariant culture, so a column of slopes lines up and the file parses anywhere.</summary>
    private static string FormatSlope(double percent, SlopeAnalyzer.SlopeUnit unit) =>
        double.IsFinite(percent) ? SlopeInput.FormatValueForReport(percent / 100.0, unit) : string.Empty;

    private static string AreaUnit(ModelUnitContext unitContext) => $"{unitContext.Abbreviation}²";

    private static string VolumeUnit(ModelUnitContext unitContext) => $"{unitContext.Abbreviation}³";
}
