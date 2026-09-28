using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.UI;

// Analysis tab: card + body builders, palette/range editors. Annotation cards live in
// MoleHillPanel.Annotations.cs.
public sealed partial class MoleHillPanel
{
    private Panel CreateAnalysisCard(TerrainDefinition terrain, AnalysisDefinition analysis, bool isActive)
    {
        bool collapsed = _collapsedAnalyses.Contains(analysis.Id);
        var collapseLabel = CreateCollapseChevron(collapsed);

        var nameBox = new TextBox { Text = analysis.Label };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Friendly analysis name shown in the panel.");
        BindCommittedText(nameBox, () => analysis.Label, text =>
            MutateAnalysis(terrain.TerrainId, analysis.Id, item => item.Label = text, scheduleRebuild: false));

        var enabledCheck = new CheckBox
        {
            Checked = analysis.IsEnabled
        };
        ApplyHelp(enabledCheck, "Enable or disable this analysis card without deleting it.");
        enabledCheck.CheckedChanged += (_, _) =>
        {
            MutateAnalysis(terrain.TerrainId, analysis.Id, item => item.IsEnabled = enabledCheck.Checked == true, scheduleRebuild: false);
            RefreshTerrainPreview(terrain.TerrainId);
        };

        string kind = GetAnalysisKind(analysis);
        bool supportsPreview = TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysis);
        bool producesOutput = TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis);
        string statusText = isActive
            ? "Preview"
            : analysis.IsEnabled
                ? supportsPreview ? "Enabled" : producesOutput ? "Output" : "Summary"
                : "Disabled";
        var badge = CreateCardStatusLabel(statusText, isActive ? UiTheme.ActiveBadge : UiTheme.MutedText);

        var handle = CreateReorderHandle(analysis.Id, "analysis-drag", "Drag to reorder this analysis.");

        var accent = AnalysisTypeColor(kind);
        var iconPlate = CreateIconPlate(accent,
            CreateCardIconControl(GetAnalysisIconName(analysis), GetAnalysisIconLabel(analysis)));

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            analysis.Label,
            AppendRuntimeDiagnosticSummary(
                GetAnalysisCollapsedSummary(terrain, analysis),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, analysis.Id)),
            nameBox,
            GetAnalysisSubtitle(analysis, isActive));

        var duplicateButton = MakeDuplicateIconButton(() =>
        {
            DuplicateAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Duplicate this analysis card.");
        var deleteButton = MakeDeleteIconButton(() =>
        {
            RemoveAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Delete this analysis card.");

        void ToggleCollapsed(bool ctrlHeld)
        {
            bool nowCollapsed = !_collapsedAnalyses.Contains(analysis.Id);
            if (ctrlHeld)
            {
                var doc2 = RhinoDoc.ActiveDoc;
                var t = doc2 == null ? null : _controller.GetSelectedTerrain(doc2);
                if (t != null)
                {
                    var visibleAnalyses = t.Analyses;

                    if (nowCollapsed)
                    {
                        foreach (var item in visibleAnalyses)
                            _collapsedAnalyses.Add(item.Id);
                    }
                    else
                    {
                        foreach (var item in visibleAnalyses)
                            _collapsedAnalyses.Remove(item.Id);
                    }
                }
            }
            else
            {
                if (nowCollapsed)
                    _collapsedAnalyses.Add(analysis.Id);
                else
                    _collapsedAnalyses.Remove(analysis.Id);
            }

            var doc = RhinoDoc.ActiveDoc;
            RebuildAnalysisLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        }

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = new Control[] { badge },
            ActionControls = new Control[]
            {
                duplicateButton,
                deleteButton
            },
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateAnalysisBody(terrain, analysis)
        });
    }

    private Control CreateAnalysisBody(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var layout = UiLayouts.CardBody();
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysis.Id);

        // Above the controls, not below them: if this analysis cannot do anything yet, that is the first
        // thing to say, and it names which control to reach for. Otherwise state what it is measuring —
        // a good default is invisible, and a card that says nothing about it reads as unconfigured.
        var descriptor = AnalysisTypeRegistry.ForType(analysis.GetType());
        string? blocker = descriptor?.DescribeBlocker(terrain, analysis);
        if (blocker != null)
            layout.AddRow(CreateWarningRow(blocker));
        else if (descriptor?.DescribeBasis(terrain, analysis) is { } basis)
            layout.AddRow(CreateNoteRow(basis));

        AppendBespokeAnalysisRowsBefore(layout, terrain, analysis);
        TryBuildSchemaAnalysisBody(layout, terrain, analysis);
        AppendBespokeAnalysisRowsAfter(layout, terrain, analysis, summary);
        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, analysis.Id));
        if (diagnosticsRow != null)
            layout.AddRow(diagnosticsRow);

        return layout;
    }

    /// <summary>
    /// Rows that must run before the schema-generated rows to preserve the original card layout: the slope
    /// unit selector (converts Range values when changed, so it can't be a plain schema row) and the shared
    /// terrain-section block (sources + insertion origin + text height + output layer + color — the
    /// insertion-origin picker needs an interactive GetPoint, so the whole block stays bespoke).
    /// </summary>
    private void AppendBespokeAnalysisRowsBefore(DynamicLayout layout, TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        switch (analysis)
        {
            case SlopeAnalysisDefinition slope:
                layout.AddRow(CreateSlopeUnitEditor(terrain.TerrainId, slope));
                break;

            case ReferenceComparisonAnalysisDefinition referenceComparison:
                layout.AddRow(CreateAnalysisReferenceTerrainEditor(terrain, referenceComparison));
                break;

        }
    }

    /// <summary>
    /// Lets Earthworks/Cut-Fill compare against another MoleHill terrain's finished mesh directly, without
    /// requiring it to be baked to Rhino geometry first. Sits above the schema-generated "Reference" row,
    /// which takes precedence when it has objects or layers assigned.
    /// </summary>
    private Control CreateAnalysisReferenceTerrainEditor(TerrainDefinition owner, ReferenceComparisonAnalysisDefinition analysis)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);

        var options = new List<(string Key, string Label)> { ("", "None — estimate from base triangulation") };
        options.AddRange(terrains
            .Where(item => item.TerrainId != owner.TerrainId)
            .Select(item => (item.TerrainId.ToString(), item.Name)));

        string selectedKey = analysis.ReferenceTerrainId?.ToString() ?? "";
        return CreateDropDownEditor(
            "Compare To Terrain",
            options,
            selectedKey,
            value => MutateAnalysis(owner.TerrainId, analysis.Id, item =>
            {
                if (item is ReferenceComparisonAnalysisDefinition compare)
                    compare.ReferenceTerrainId = string.IsNullOrEmpty(value) ? null : Guid.Parse(value);
            }, scheduleRebuild: true),
            "Another terrain's finished mesh to compare against, without baking it to Rhino geometry first. " +
            "Ignored when “Reference” below has objects or layers assigned - those take precedence.");
    }

    /// <summary>An inline caution on a card: a setting is on but cannot take effect yet.</summary>
    /// <summary>
    /// A quiet statement of fact about how the card is set up. Muted and unadorned, so it reads as an
    /// answer to "what is this measuring?" rather than as something demanding attention.
    /// </summary>
    private static Control CreateNoteRow(string message)
    {
        var label = UiControls.Label(message, UiLabelRole.Meta, WrapMode.Word);
        label.VerticalAlignment = VerticalAlignment.Top;
        return label;
    }

    /// <summary>
    /// An inline notice on a card: a setting is on but cannot take effect, or a prerequisite is missing.
    /// Given the warning surface so it reads as a notice rather than a stray sentence between controls.
    /// </summary>
    private static Control CreateWarningRow(string message)
    {
        return new Panel
        {
            BackgroundColor = UiTheme.WarningBackground,
            Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall),
            Content = new Label
            {
                Text = message,
                TextColor = UiTheme.WarningText,
                Wrap = WrapMode.Word
            }
        };
    }

    /// <summary>
    /// Hatch patterns offered on a section card: MoleHill's built-ins plus whatever the document already
    /// defines, so an office standard pattern is selectable without leaving the panel.
    /// </summary>
    private static AnalysisRange ResolveLegendRange(
        TerrainAnalysisSummary? summary,
        AnalysisDefinition analysis,
        RangeShape shape)
    {
        if (summary?.DisplayRangeLow is { } low && summary.DisplayRangeHigh is { } high && high > low)
            return new AnalysisRange(low, high, analysis.AutoColorRange);

        if (!analysis.AutoColorRange)
            return AnalysisRange.FromRequested(analysis.RangeLow, analysis.RangeHigh, shape);

        // No colouring yet: show the configured bounds if they are usable, otherwise a neutral placeholder.
        return AnalysisRange.FromRequested(analysis.RangeLow, analysis.RangeHigh, shape) with { IsAuto = true };
    }

    /// <summary>
    /// Rows that can't be expressed by the parameter schema (custom-draw escape hatch): computed summaries,
    /// legends, and read-only stats from the last terrain build. Runs after the schema-generated rows so
    /// row order matches the former hand-written cards.
    /// </summary>
    private void AppendBespokeAnalysisRowsAfter(DynamicLayout layout, TerrainDefinition terrain, AnalysisDefinition analysis, TerrainAnalysisSummary? summary)
    {
        switch (analysis)
        {
            case EarthworkAnalysisDefinition earthwork:
                if (summary != null)
                {
                    string summaryText =
                        $"Cut: {FormatVolume(summary.CutVolume)}{Environment.NewLine}" +
                        $"Fill: {FormatVolume(summary.FillVolume)}{Environment.NewLine}" +
                        $"Net: {FormatVolume(summary.NetVolume)}{Environment.NewLine}" +
                        $"Mode: {(summary.EarthworkIsEstimated ? "Estimated from terrain delta" : "Exact")}";
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        summaryText,
                        "Earthwork summary from the last terrain build. Click into the field to select and copy values."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to populate earthwork values.",
                        minHeight: 42));
                }
                break;

            // The mapped range and the ramp itself are the colour card's job now; what is left here is the
            // statistic the card cannot know — what the terrain actually measured on the last build.
            case SlopeAnalysisDefinition slope:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        $"{FormatSlopeSummaryValue(summary.SlopeMinPercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeAveragePercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeMaxPercent, slope.Unit)}",
                        "Current terrain slope summary from the last build."));
                }

                break;
            }

            case AspectAnalysisDefinition:
            {
                if (summary != null)
                {
                    string dominant = summary.AspectDominantBearing is { } bearing
                        ? $"{AspectAnalyzer.SectorName(bearing)} ({bearing:F0}°)"
                        : "None — no mean direction";
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Facing",
                        dominant,
                        "The plan-area-weighted mean direction the terrain drains towards, from the last build."));

                    string flat = summary.AspectFaceCount > 0
                        ? $"{summary.AspectFlatFaceCount:N0} of {summary.AspectFaceCount:N0} " +
                          $"({(double)summary.AspectFlatFaceCount / summary.AspectFaceCount:P0})"
                        : "—";
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Flat",
                        flat,
                        "Faces too flat to have an aspect. These are drawn neutral grey, not given a direction."));
                }

                break;
            }

            case ElevationAnalysisDefinition elevation:
            {
                if (summary != null)
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Area",
                        FormatArea(summary.SurfaceArea),
                        "Terrain surface area from the last build."));

                break;
            }

            case CutFillAnalysisDefinition cutFill:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow("Cut / Fill / Net",
                        $"{summary.CutVolume:F2} / {summary.FillVolume:F2} / {summary.NetVolume:F2}",
                        "Current earthworks summary from the last build."));

                    if (cutFill.DrawsDeltaOutput)
                    {
                        string balance = summary.CutFillBalanceCurveCount > 0
                            ? $"{summary.CutFillBalanceCurveCount:N0} curve(s)"
                            : cutFill.ShowBalanceLine ? "None — no sign change" : "Off";
                        layout.AddRow(CreateReadOnlyValueRow(
                            "Drawn",
                            $"{summary.CutFillDeltaContourCount:N0} delta contour(s) | balance: {balance}",
                            "Delta lines drawn on the last build. A balance line only exists where the " +
                            "delta changes sign — a site that is all fill has none."));
                    }
                }

                break;
            }

            case CatchmentAnalysisDefinition catchment:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Catchments",
                        summary.CatchmentLargestArea is { } largest
                            ? $"{summary.CatchmentBasinCount:N0} | largest {FormatArea(largest)}"
                            : $"{summary.CatchmentBasinCount:N0}",
                        "Catchments resolved on the last build, after any small ones were merged. " +
                        "Raise “Merge Below” if there are too many to read."));

                    // Surfaced on this card deliberately: the routing already knows the terrain holds
                    // water somewhere, and saying nothing until a ponding card exists would be withholding
                    // the one thing here that indicates a mistake rather than describing the design.
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Closed depressions",
                        summary.CatchmentSinkCount > 0
                            ? $"{summary.CatchmentSinkCount:N0} — water has nowhere to go"
                            : "None",
                        "Catchments with no outlet. Water reaching one stays there, which is usually a " +
                        "grading mistake rather than a design."));

                    if (catchment.ShowBoundaries || catchment.ShowFlowPaths)
                    {
                        layout.AddRow(CreateReadOnlyValueRow(
                            "Drawn",
                            $"{summary.GeneratedOutputCount:N0} curve(s)",
                            "Boundary and flow-path curves drawn on the last build."));
                    }
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to resolve catchments.",
                        minHeight: 42));
                }

                break;
            }

            case GradientComplianceAnalysisDefinition compliance:
            {
                if (summary == null)
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to check the level areas.",
                        minHeight: 42));
                }
                else if (summary.LevelAreaCheckedArea is { } checkedArea &&
                         summary.LevelAreaSteepestSlopeDegrees is { } steepestDegrees)
                {
                    double exceeding = summary.LevelAreaExceedingArea ?? 0.0;
                    string verdict = compliance.Rules.LevelAreaMode == GradientRuleMode.Warn ? "fails" : "over the limit";

                    // Phrased as an answer, like Ponding's: "all within" is what people are looking for.
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Level Result",
                        exceeding > 0.0
                            ? $"{FormatArea(exceeding)} of {FormatArea(checkedArea)} {verdict}"
                            : $"All {FormatArea(checkedArea)} within the limit",
                        "Plan area of level-area ground checked on the last build, and how much of it is " +
                        "steeper than the limit when measured over “Measure Over”."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Level Steepest",
                        SlopeInput.FormatWithUnit(
                            Math.Tan(steepestDegrees * Math.PI / 180.0), SlopeUnitPreference.Current),
                        "The steepest averaged gradient found in any level area, in any direction."));
                }
                else if (summary.RouteCheckedArea == null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Result",
                        "None checked",
                        "No terrain lies inside a closed level-area curve or a route corridor, or the rules are off."));
                }

                if (summary?.RouteCheckedArea is { } routeArea &&
                    summary.RouteSteepestRunningDegrees is { } runningDegrees &&
                    summary.RouteSteepestCrossDegrees is { } crossDegrees)
                {
                    double runningFails = summary.RouteRunningExceedingArea ?? 0.0;
                    double crossFails = summary.RouteCrossExceedingArea ?? 0.0;
                    string routeVerdict = compliance.Rules.RouteMode == GradientRuleMode.Warn ? "fail" : "over";
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Route Result",
                        runningFails > 0.0 || crossFails > 0.0
                            ? $"{FormatArea(runningFails)} running, {FormatArea(crossFails)} cross {routeVerdict}, of {FormatArea(routeArea)}"
                            : $"All {FormatArea(routeArea)} within the limits",
                        "Route corridor checked on the last build. Running slope past the ramp limit and cross " +
                        "slope past the cross limit are counted separately; ground failing both counts in each. Ground " +
                        "that is also a level area is counted here too, though the preview colours it by the " +
                        "level-area rule."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Route Ramps",
                        FormatArea(summary.RouteRampArea ?? 0.0),
                        "Route ground steeper than a walk but within the ramp limit. Allowed; stage three will " +
                        "hold it to ramp rules on rise and landings."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Route Steepest",
                        $"{FormatRouteSlope(runningDegrees)} running, {FormatRouteSlope(crossDegrees)} cross",
                        "The steepest averaged running and cross slope found on any route."));
                }

                break;
            }

            case PondingAnalysisDefinition ponding:
            {
                if (summary != null)
                {
                    // Phrased as an answer, not a count. "None" is the result people are looking for, and
                    // it should read as reassurance rather than as an empty field.
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Standing water",
                        summary.PondCount == 0
                            ? "None — everywhere drains"
                            : $"{summary.PondCount:N0} depression(s)",
                        "Closed depressions deeper than “Ignore Below”. Water reaching one stays " +
                        "there."));

                    if (summary.PondCount > 0)
                    {
                        layout.AddRow(CreateReadOnlyValueRow(
                            "Deepest / Volume",
                            $"{FormatZoneLength(summary.PondMaxDepth ?? 0.0)} / {FormatVolume(summary.PondTotalVolume ?? 0.0)}",
                            "Deepest standing water, and the total held across every depression, from the " +
                            "last build."));
                        layout.AddRow(CreateReadOnlyValueRow(
                            "Wet area",
                            FormatArea(summary.PondTotalArea ?? 0.0),
                            "Total water-surface area at the level each depression overflows at."));
                    }
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to check for standing water.",
                        minHeight: 42));
                }

                break;
            }

            case WaterflowAnalysisDefinition waterflow:
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Points / Paths",
                        $"{summary.SampleSourceCount} point(s) -> {summary.GeneratedOutputCount} path(s)",
                        "Waterflow paths traced from the point sources across the final terrain."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Ends",
                        $"{summary.WaterflowBoundaryCount} boundary | {summary.WaterflowSinkCount} sink | {summary.WaterflowRejectedCount} outside",
                        "How the point paths ended during the last terrain build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate waterflow paths.",
                        minHeight: 42));
                }
                break;

        }

    }

    private static string FormatArea(double value)
    {
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return unitContext.FormatArea(value);
    }

    private void RemoveAnalysis(Guid terrainId, Guid analysisId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Analyses.RemoveAll(item => item.Id == analysisId);
        }, scheduleRebuild: false);
        RefreshUi();
    }

    private void AddAnalysis(string kind)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        IReadOnlyList<TerrainDefinition> documentTerrains = _controller.GetTerrains(doc);
        MutateSelectedTerrain(terrain =>
        {
            AnalysisDefinition? analysis = AnalysisTypeRegistry.Create(
                kind,
                MoleHill.Shared.ModelUnitContext.FromDocument(doc));
            if (analysis is GradientComplianceAnalysisDefinition compliance &&
                GradientRulePresets.FindDocumentStandard(terrain, documentTerrains) is { } standard)
            {
                compliance.Rules = standard;
            }

            if (analysis != null)
                terrain.Analyses.Insert(0, analysis);
        }, scheduleRebuild: false);

        var terrain = _controller.GetSelectedTerrain(doc);
        if (terrain != null)
        {
            RefreshUi();
            RefreshTerrainPreview(terrain.TerrainId);
        }
    }

    private static string FormatRouteSlope(double degrees) =>
        SlopeInput.FormatWithUnit(Math.Tan(degrees * Math.PI / 180.0), SlopeUnitPreference.Current);

    private static TerrainAnalysisSummary? GetAnalysisSummary(TerrainDefinition terrain, Guid analysisId)
    {
        return terrain.LastAnalysisResults.FirstOrDefault(item => item.AnalysisId == analysisId);
    }

    private static string GetAnalysisTypeLabel(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.TypeLabel ?? "Analysis";

    private static string GetAnalysisKind(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.Kind ?? string.Empty;

    private static Color AnalysisTypeColor(string kind)
    {
        int argb = AnalysisTypeRegistry.ForKind(kind)?.AccentArgb ?? unchecked((int)0xFF787878);
        return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
    }

    private static string GetAnalysisIconLabel(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.IconLabel ?? "A";

    private static string? GetAnalysisIconName(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.IconName;

    private static string GetAnalysisSubtitle(AnalysisDefinition analysis, bool isActive)
    {
        var descriptor = AnalysisTypeRegistry.ForType(analysis.GetType());
        if (descriptor == null)
            return "Analysis";

        return isActive && descriptor.ActiveSubtitle != null ? descriptor.ActiveSubtitle : descriptor.Subtitle;
    }

    private static string GetAnalysisCollapsedSummary(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysis.Id);
        return analysis switch
        {
            EarthworkAnalysisDefinition earthwork => summary != null
                ? $"Net {FormatVolume(summary.NetVolume)} | {(summary.EarthworkIsEstimated ? "Estimated" : "Exact")}"
                : $"{CountReferences(earthwork.Reference)} refs | {CountReferences(earthwork.Boundary)} bounds",
            SlopeAnalysisDefinition slope => $"{FormatSlopeValue(slope.RangeLow, slope.Unit)} to {(slope.RangeHigh > slope.RangeLow ? FormatSlopeValue(slope.RangeHigh, slope.Unit) : "Auto")}",
            ElevationAnalysisDefinition elevation => $"{elevation.RangeLow:G4} to {(elevation.RangeHigh > elevation.RangeLow ? elevation.RangeHigh.ToString("G4") : "Auto")}",
            CutFillAnalysisDefinition cutFill => summary != null
                ? $"{summary.CutVolume:F2} / {summary.FillVolume:F2} / {summary.NetVolume:F2}"
                : $"{CountReferences(cutFill.Reference)} refs | {CountReferences(cutFill.Boundary)} bounds",
            WaterflowAnalysisDefinition waterflow => summary != null
                ? $"{summary.GeneratedOutputCount} paths | {summary.WaterflowBoundaryCount} boundary"
                : $"{CountReferences(waterflow.Sources)} refs | downhill paths",
            _ => string.Empty
        };
    }

    private void DuplicateAnalysis(Guid terrainId, Guid analysisId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
            _controller.DuplicateAnalysis(doc, terrainId, analysisId);
            RefreshUi();
        }
    }

    private Control CreateSlopeUnitEditor(Guid terrainId, SlopeAnalysisDefinition slope)
    {
        var options = new[]
        {
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Percent), "Percent"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Promille), "Promille"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Ratio), "Ratio"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Degrees), "Degrees")
        };

        return CreateDropDownEditor(
            "Units",
            options,
            GetSlopeUnitKey(slope.Unit),
            value =>
            {
                var nextUnit = ParseSlopeUnit(value);
                if (nextUnit == slope.Unit)
                    return;

                double rangeLow = ConvertSlopeValue(slope.RangeLow, slope.Unit, nextUnit);
                double rangeHigh = ConvertSlopeValue(slope.RangeHigh, slope.Unit, nextUnit);
                double interval = ConvertSlopeValue(slope.ColorInterval, slope.Unit, nextUnit);
                MutateAndRefreshAnalysis(terrainId, slope.Id, item =>
                {
                    if (item is not SlopeAnalysisDefinition target)
                        return;

                    target.Unit = nextUnit;
                    target.RangeLow = rangeLow;
                    target.RangeHigh = rangeHigh;
                    target.ColorInterval = interval;
                });
            },
            "Show slope values as percent, promille, rise/run ratio, or degrees.");
    }

    private static string GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit unit) => AnalysisFormatting.GetSlopeUnitKey(unit);

    private static SlopeAnalyzer.SlopeUnit ParseSlopeUnit(string key) => AnalysisFormatting.ParseSlopeUnit(key);

    private static string FormatSlopeSummaryValue(double percentValue, SlopeAnalyzer.SlopeUnit unit) =>
        AnalysisFormatting.FormatSlopeSummaryValue(percentValue, unit);

    private static string FormatSlopeValue(double value, SlopeAnalyzer.SlopeUnit unit) =>
        AnalysisFormatting.FormatSlopeValue(value, unit);

    private static string FormatAnalysisValue(double value, string? format)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "G4" : format;
        try
        {
            return value.ToString(effectiveFormat, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return value.ToString("G4", CultureInfo.CurrentCulture);
        }
    }

    private static double ConvertSlopeValue(double value, SlopeAnalyzer.SlopeUnit fromUnit, SlopeAnalyzer.SlopeUnit toUnit) =>
        AnalysisFormatting.ConvertSlopeValue(value, fromUnit, toUnit);
}
