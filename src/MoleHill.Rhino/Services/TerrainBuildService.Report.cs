using System.Diagnostics;
using MoleHill.Core.Reporting;
using MoleHill.Rhino.Model;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Report stage: the drawn quantity table. It runs after every stage that measures something, because it
// reports what they measured and computes nothing itself.
internal sealed partial class TerrainBuildService
{
    private static void BuildReportTables(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh currentMesh,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        List<ReportTableAnnotationDefinition> tables = terrain.Annotations
            .OfType<ReportTableAnnotationDefinition>()
            .Where(static annotation => annotation.IsEnabled)
            .ToList();
        if (tables.Count == 0)
            return;

        var timer = Stopwatch.StartNew();
        foreach (ReportTableAnnotationDefinition annotation in tables)
        {
            ThrowIfCancellationRequested(shouldCancel);

            ReportDocument report = TerrainReportBuilder.Build(
                terrain,
                build.ZoneAnalysisResults,
                build.AnalysisResults,
                snapshot.ResolvedUnitContext,
                annotation.Unit,
                DateTime.Now,
                ResolveSections(annotation));

            TerrainAnalysisSummary summary = TerrainReportTableBuilder.Build(
                report,
                annotation,
                currentMesh,
                ResolveReportTextHeight(snapshot, annotation),
                snapshot.LayerRoles,
                build);
            build.AnalysisResults.Add(summary);
        }

        timer.Stop();
        build.RecordTiming("Report Tables", timer.Elapsed, $"{tables.Count:N0} table(s)");
    }

    private static TerrainReportSections ResolveSections(ReportTableAnnotationDefinition annotation)
    {
        TerrainReportSections sections = TerrainReportSections.None;
        if (annotation.IncludeOverview)
            sections |= TerrainReportSections.Overview;
        if (annotation.IncludeZones)
            sections |= TerrainReportSections.Zones;
        if (annotation.IncludeEarthworks)
            sections |= TerrainReportSections.Earthworks;
        if (annotation.IncludePonding)
            sections |= TerrainReportSections.Ponding;
        if (annotation.IncludeCatchments)
            sections |= TerrainReportSections.Catchments;
        if (annotation.IncludeGradientCompliance)
            sections |= TerrainReportSections.GradientCompliance;
        return sections;
    }

    private static double ResolveReportTextHeight(TerrainBuildSnapshot snapshot, ReportTableAnnotationDefinition annotation) =>
        annotation.FollowsAnnotationStyle ? snapshot.AnnotationStyle.TextHeight : annotation.TextHeight;
}
