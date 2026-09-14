using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

namespace MoleHill.Rhino.UI;

// Annotation cards: the Annotations tab's counterpart to MoleHillPanel.Analysis.cs. Split from it when
// annotations became their own content family — the two tabs already had separate stacks, toolbars and
// card maps, and now they have separate card builders over separate types too.
public sealed partial class MoleHillPanel
{
    private Control CreateAnnotationBody(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var layout = UiLayouts.CardBody();
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, annotation.Id);

        // Above the controls, not below them: if this annotation cannot draw anything yet, that is the
        // first thing to say, and it names which control to reach for.
        var descriptor = AnnotationTypeRegistry.ForType(annotation.GetType());
        string? blocker = descriptor?.DescribeBlocker(terrain, annotation);
        if (blocker != null)
            layout.AddRow(CreateWarningRow(blocker));
        else if (descriptor?.DescribeBasis(terrain, annotation) is { } basis)
            layout.AddRow(CreateNoteRow(basis));

        AppendBespokeAnnotationRowsBefore(layout, terrain, annotation);
        TryBuildSchemaAnnotationBody(layout, terrain, annotation);
        AppendBespokeAnnotationRowsAfter(layout, terrain, annotation, summary);
        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, annotation.Id));
        if (diagnosticsRow != null)
            layout.AddRow(diagnosticsRow);

        return layout;
    }

    /// <summary>
    /// Rows that must run before the schema-generated rows to preserve the card layout: the shared
    /// terrain-section block (sources + insertion origin + text height + colour). The insertion-origin
    /// picker needs an interactive GetPoint, so the whole block stays bespoke.
    /// </summary>
    private void AppendBespokeAnnotationRowsBefore(DynamicLayout layout, TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        switch (annotation)
        {
            case TerrainSectionAnnotationDefinition terrainSection:
                AddTerrainSectionCommonRows(layout, terrain, terrainSection,
                    "Curves used as cut lines through the terrain. Each curve produces one profile.");
                break;

            case CrossSectionStationAnnotationDefinition crossSection:
                AddTerrainSectionCommonRows(layout, terrain, crossSection,
                    "Alignment curve sampled at regular stations. The first curve resolved is used.");
                break;

            case LongitudinalSectionAnnotationDefinition longitudinal:
                AddTerrainSectionCommonRows(layout, terrain, longitudinal,
                    "Curve sampled along its length. Terrain elevation is read at each sample.");
                break;
        }
    }

    private void AppendBespokeAnnotationRowsAfter(DynamicLayout layout, TerrainDefinition terrain, AnnotationDefinition annotation, TerrainAnalysisSummary? summary)
    {
        switch (annotation)
        {
            case ReportTableAnnotationDefinition reportTable:
            {
                layout.AddRow(CreateInsertionOriginEditor(
                    terrain,
                    reportTable.Id,
                    reportTable.HasInsertionPlane,
                    reportTable.InsertionOriginX,
                    reportTable.InsertionOriginY,
                    reportTable.InsertionOriginZ,
                    "Top-left corner of the drawn table. Auto places it beside the terrain.",
                    "Pick the top-left corner where the table will be drawn.",
                    "Clear the origin and let the table place itself beside the terrain.",
                    (item, picked) =>
                    {
                        if (item is not ReportTableAnnotationDefinition table)
                            return;

                        table.InsertionOriginX = picked.X;
                        table.InsertionOriginY = picked.Y;
                        table.InsertionOriginZ = picked.Z;
                        table.HasInsertionPlane = true;
                    },
                    item =>
                    {
                        if (item is ReportTableAnnotationDefinition table)
                            table.HasInsertionPlane = false;
                    }));

                layout.AddRow(summary != null
                    ? CreateReadOnlyValueRow(
                        "Drawn",
                        summary.ReportRowCount > 0
                            ? $"{summary.ReportTableCount} table(s) | {summary.ReportRowCount} row(s)"
                            : "Nothing measured yet",
                        "Tables and data rows drawn by the last build. A section that measured nothing is not drawn.")
                    : CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to draw the report table.",
                        minHeight: 42));
                break;
            }

            case CurveElevationLabelAnnotationDefinition curveElevation:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Labels",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Curve sources resolved and elevation annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, curveElevation.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, curveElevation.ValueFormat)}"
                            : "No samples",
                        "Terrain elevations sampled along the source curves during the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate curve elevation annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case CurveSlopeLabelAnnotationDefinition curveSlope:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Labels",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Curve sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, curveSlope.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, curveSlope.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, curveSlope.Unit)}"
                            : "No samples",
                        "Terrain-projected curve slope values from the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate curve slope annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case ProjectedElevationLabelAnnotationDefinition projectedElevation:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Sources / Labels",
                        $"{summary.SampleSourceCount} source(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Point and curve sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, projectedElevation.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, projectedElevation.ValueFormat)}"
                            : "No samples",
                        "Projected terrain elevations from the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate projected elevation annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case PointSlopeLabelAnnotationDefinition pointSlope:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Points / Labels",
                        $"{summary.SampleSourceCount} point(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Point sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, pointSlope.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, pointSlope.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, pointSlope.Unit)}"
                            : "No samples",
                        "Local terrain slope values sampled at the projected points."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate point slope annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case SlopeArrowAnnotationDefinition slopeArrows:
            {

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Arrows",
                        $"{summary.GeneratedOutputCount} arrow(s)",
                        "Flow arrows emitted across the terrain by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, slopeArrows.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, slopeArrows.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, slopeArrows.Unit)}"
                            : "No samples",
                        "Slope magnitudes sampled across the grid during the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate flow arrows.",
                        minHeight: 42));
                }

                break;
            }

            case GradeBetweenPointsAnnotationDefinition gradeCallout:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Lines / Callouts",
                        $"{summary.SampleSourceCount} line(s) -> {summary.GeneratedOutputCount} callout(s)",
                        "Source lines resolved and grade callouts emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max %",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, gradeCallout.ValueFormat)} / {FormatAnalysisValue(summary.SampleAverageValue, gradeCallout.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, gradeCallout.ValueFormat)}"
                            : "No samples",
                        "Grade percentages computed for the source lines during the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate grade callouts.",
                        minHeight: 42));
                }

                break;
            }

            case ContourAnnotationDefinition contour:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves",
                        $"{summary.ContourCurveCount} curve(s) across {summary.ContourLevelCount} level(s)",
                        "Contour output generated from the last terrain build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Levels",
                        summary.ContourLevelCount > 0
                            ? $"{summary.ContourFirstLevel:G4} to {summary.ContourLastLevel:G4}"
                            : "No contour levels intersected the terrain",
                        "First and last contour elevations emitted by the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate contour curves.",
                        minHeight: 42));
                }
                break;
            }

            case TerrainSectionAnnotationDefinition:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Cuts / Terrains / C-F",
                        $"{summary.SampleSourceCount} / {summary.SectionTerrainCount} / {summary.SectionCutRegionCount}-{summary.SectionFillRegionCount}",
                        $"Cut curves, available terrain profiles, and cut-fill regions from the last build ({summary.GeneratedOutputCount} objects)."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate the section profile.",
                        minHeight: 42));
                }
                break;
            }

            case CrossSectionStationAnnotationDefinition:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Alignments / Terrains / C-F",
                        $"{summary.SampleSourceCount} / {summary.SectionTerrainCount} / {summary.SectionCutRegionCount}-{summary.SectionFillRegionCount}",
                        $"Alignments, available terrain profiles, and cut-fill regions from the last build ({summary.GeneratedOutputCount} objects)."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate cross-sections.",
                        minHeight: 42));
                }
                break;
            }

            case LongitudinalSectionAnnotationDefinition:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Terrains / C-F",
                        $"{summary.SampleSourceCount} / {summary.SectionTerrainCount} / {summary.SectionCutRegionCount}-{summary.SectionFillRegionCount}",
                        $"Curves, available terrain profiles, and cut-fill regions from the last build ({summary.GeneratedOutputCount} objects)."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate the longitudinal section.",
                        minHeight: 42));
                }
                break;
            }
        }
    }

    private static IReadOnlyList<(string Key, string Label)> GetHatchPatternOptions()
    {
        var names = new List<string>(HatchPatternService.BuiltInPatternNames);
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
            foreach (var pattern in doc.HatchPatterns)
            {
                if (pattern != null && !pattern.IsDeleted &&
                    !names.Contains(pattern.Name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(pattern.Name);
                }
            }
        }

        return names.Select(name => (name, name)).ToList();
    }

    /// <summary>
    /// The range the legend should describe: the one the preview mesh was actually coloured with, stamped
    /// onto the summary when the mesh was built. Falls back to resolving the configured bounds directly,
    /// which is correct for an explicit range and is the best guess before the first colouring.
    /// </summary>
    private void AddTerrainSectionCommonRows(
        DynamicLayout layout,
        TerrainDefinition terrain,
        TerrainSectionAnnotationDefinitionBase annotation,
        string sourceHelp)
    {
        void MutateSection(Action<TerrainSectionAnnotationDefinitionBase> apply) =>
            MutateAnnotation(terrain.TerrainId, annotation.Id, item =>
            {
                if (item is TerrainSectionAnnotationDefinitionBase section)
                    apply(section);
            }, scheduleRebuild: true);

        layout.AddRow(CreateSourceEditor(
            "Sources",
            annotation.Sources,
            apply => MutateSection(item => apply(item.Sources)),
            RhinoObjectType.Curve,
            doc => _controller.GetSelectedLayerPaths(doc),
            sourceHelp));
        layout.AddRow(CreateTerrainSectionTerrainEditor(terrain, annotation));
        layout.AddRow(CreateCheckEditor(
            "Cut / Fill",
            annotation.ShowCutFillRegions,
            value => MutateSection(item => item.ShowCutFillRegions = value),
            "Shade cut and fill between this terrain and the reference below."));
        layout.AddRow(CreateSourceEditor(
            "C/F Reference",
            annotation.CutFillReference,
            apply => MutateSection(item => apply(item.CutFillReference)),
            RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
            doc => _controller.GetSelectedLayerPaths(doc),
            "Existing ground for cut/fill shading: a survey mesh or surface, sliced along the same section " +
            "line. Leave empty to compare against this terrain's own initial triangulation instead."));

        layout.AddRow(CreateDropDownEditor(
            "Cut Hatch",
            GetHatchPatternOptions(),
            HatchPatternService.ResolvePatternName(annotation.CutHatchPatternName, HatchPatternService.DefaultCutPatternName),
            value => MutateSection(item => item.CutHatchPatternName = value),
            "Hatch pattern for cut regions. By drafting convention cut reads denser than fill."));
        layout.AddRow(CreateDropDownEditor(
            "Fill Hatch",
            GetHatchPatternOptions(),
            HatchPatternService.ResolvePatternName(annotation.FillHatchPatternName, HatchPatternService.DefaultFillPatternName),
            value => MutateSection(item => item.FillHatchPatternName = value),
            "Hatch pattern for fill regions."));
        layout.AddRow(CreateNumericEditor(
            "Hatch Scale",
            annotation.HatchScale,
            value => MutateSection(item => item.HatchScale = Math.Max(0.0, value)),
            decimalPlaces: 3,
            help: "Pattern scale. 0 derives a scale from the text height so the fill reads as a texture at " +
                  "the drawing's scale — a pattern's own spacing is arbitrary, so a fixed 1 prints solid black.",
            minValue: 0.0));
        layout.AddRow(CreateNumericEditor(
            "Hatch Angle",
            annotation.HatchRotationDegrees,
            value => MutateSection(item => item.HatchRotationDegrees = value),
            decimalPlaces: 1,
            help: "Pattern rotation.",
            unitSuffix: ResolveUnitSuffix(ParameterUnit.Degrees)));
        layout.AddRow(CreateInsertionOriginEditor(terrain, annotation));
    }

    private Control CreateTerrainSectionTerrainEditor(
        TerrainDefinition owner,
        TerrainSectionAnnotationDefinitionBase annotation)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);
        var selectedIds = annotation.ComparisonTerrainIds.ToHashSet();
        var list = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        list.Items.Add(CreateTerrainSectionChoiceRow(owner.TerrainId, owner, isOwner: true, isSelected: true, isAvailable: true, annotation));
        foreach (TerrainDefinition terrain in terrains.Where(item => item.TerrainId != owner.TerrainId))
            list.Items.Add(CreateTerrainSectionChoiceRow(
                owner.TerrainId,
                terrain,
                isOwner: false,
                isSelected: selectedIds.Contains(terrain.TerrainId),
                isAvailable: doc != null && _controller.HasCompletedFinalTerrainMesh(doc, terrain.TerrainId),
                annotation));

        foreach (Guid missingId in annotation.ComparisonTerrainIds.Where(id => terrains.All(t => t.TerrainId != id)))
        {
            list.Items.Add(new Label
            {
                Text = $"Unavailable terrain ({missingId})",
                TextColor = UiTheme.WarningText,
                Wrap = WrapMode.Word
            });
        }

        var referenceOptions = new List<(Guid? Id, string Label)> { (null, "None") };
        referenceOptions.AddRange(terrains
            .Where(item => item.TerrainId != owner.TerrainId && selectedIds.Contains(item.TerrainId))
            .Select(item => ((Guid?)item.TerrainId, item.Name)));
        var referenceDropDown = new DropDown { Width = UiMetrics.DropDown, Enabled = referenceOptions.Count > 1 };
        foreach (var option in referenceOptions)
            referenceDropDown.Items.Add(new ListItem { Text = option.Label });
        int selectedReferenceIndex = referenceOptions.FindIndex(
            option => option.Id == annotation.CutFillReferenceTerrainId);
        referenceDropDown.SelectedIndex = Math.Max(0, selectedReferenceIndex);
        ApplyHelp(referenceDropDown, "Terrain treated as existing ground for cut/fill shading.");
        referenceDropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;
            int index = referenceDropDown.SelectedIndex;
            if (index < 0 || index >= referenceOptions.Count)
                return;
            MutateAnnotation(owner.TerrainId, annotation.Id, item =>
            {
                if (item is TerrainSectionAnnotationDefinitionBase section)
                    section.CutFillReferenceTerrainId = referenceOptions[index].Id;
            }, scheduleRebuild: true);
            RefreshUi();
        };

        list.Items.Add(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Reference", TextColor = UiTheme.MutedText },
                referenceDropDown
            }
        });

        const string help = "Terrains drawn in this section. The owning terrain is always proposed; select other terrains for comparison.";
        return new PropertyRow(CreateHelpLabel("Terrains", help, 0), list, expandWidget: true);
    }

    private Control CreateTerrainSectionChoiceRow(
        Guid ownerTerrainId,
        TerrainDefinition terrain,
        bool isOwner,
        bool isSelected,
        bool isAvailable,
        TerrainSectionAnnotationDefinitionBase annotation)
    {
        var check = new CheckBox
        {
            Text = isOwner
                ? $"{terrain.Name} (Proposed)"
                : isAvailable ? terrain.Name : $"{terrain.Name} (awaiting final build)",
            Checked = isSelected,
            Enabled = !isOwner,
            TextColor = isAvailable || isOwner ? UiTheme.PrimaryText : UiTheme.WarningText
        };
        var swatch = new Panel
        {
            Size = new Size(12, 12),
            BackgroundColor = ToEtoColor(System.Drawing.Color.FromArgb(
                isOwner && annotation.ColorArgb.HasValue ? annotation.ColorArgb.Value : terrain.TerrainColorArgb))
        };

        if (!isOwner)
        {
            check.CheckedChanged += (_, _) =>
            {
                if (_isRefreshing)
                    return;
                bool selected = check.Checked == true;
                MutateAnnotation(
                    ownerTerrainId,
                    annotation.Id,
                    item =>
                    {
                        if (item is not TerrainSectionAnnotationDefinitionBase section)
                            return;
                        if (selected && !section.ComparisonTerrainIds.Contains(terrain.TerrainId))
                            section.ComparisonTerrainIds.Add(terrain.TerrainId);
                        else if (!selected)
                        {
                            section.ComparisonTerrainIds.RemoveAll(id => id == terrain.TerrainId);
                            if (section.CutFillReferenceTerrainId == terrain.TerrainId)
                                section.CutFillReferenceTerrainId = null;
                        }
                    },
                    scheduleRebuild: true);
                RefreshUi();
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { swatch, check }
        };
    }

    private Control CreateInsertionOriginEditor(
        TerrainDefinition terrain,
        TerrainSectionAnnotationDefinitionBase annotation) =>
        CreateInsertionOriginEditor(
            terrain,
            annotation.Id,
            annotation.HasInsertionPlane,
            annotation.InsertionOriginX,
            annotation.InsertionOriginY,
            annotation.InsertionOriginZ,
            "Insertion origin where the laid-out section is placed. World X/Z axes are used for direction.",
            "Pick the origin point where the laid-out section will be placed.",
            "Clear the insertion origin and let the section auto-position next to the terrain.",
            (item, picked) =>
            {
                if (item is not TerrainSectionAnnotationDefinitionBase section)
                    return;

                section.InsertionOriginX = picked.X;
                section.InsertionOriginY = picked.Y;
                section.InsertionOriginZ = picked.Z;
                section.InsertionXAxisX = 1.0;
                section.InsertionXAxisY = 0.0;
                section.InsertionXAxisZ = 0.0;
                section.InsertionYAxisX = 0.0;
                section.InsertionYAxisY = 1.0;
                section.InsertionYAxisZ = 0.0;
                section.HasInsertionPlane = true;
            },
            item =>
            {
                if (item is TerrainSectionAnnotationDefinitionBase section)
                    section.HasInsertionPlane = false;
            });

    /// <summary>
    /// The shared origin-picker row: a read-out, a Pick that runs an interactive GetPoint, and an Auto
    /// that hands placement back to the builder. It stays bespoke rather than schema-declared because the
    /// pick is a document interaction, and it takes accessors rather than a base type because the
    /// annotations that place something in the drawing — sections and the report table — have no common
    /// base and should not be given one just to share a row.
    /// </summary>
    private Control CreateInsertionOriginEditor(
        TerrainDefinition terrain,
        Guid annotationId,
        bool hasOrigin,
        double originX,
        double originY,
        double originZ,
        string help,
        string pickHelp,
        string autoHelp,
        Action<AnnotationDefinition, RhinoPoint3d> applyPick,
        Action<AnnotationDefinition> applyAuto)
    {
        string text = hasOrigin
            ? $"({originX:F2}, {originY:F2}, {originZ:F2})"
            : "(auto: placed beside the terrain)";

        var summary = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = hasOrigin ? UiTheme.PrimaryText : UiTheme.MutedText,
            Wrap = WrapMode.None
        };
        ApplyHelp(summary, help);

        var pickButton = MakeInlineButton("Pick", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            var gp = new RhinoGetPoint();
            gp.SetCommandPrompt("Pick insertion origin");
            if (gp.Get() != RhinoGetResult.Point)
                return;

            RhinoPoint3d picked = gp.Point();
            MutateAnnotation(terrain.TerrainId, annotationId, item => applyPick(item, picked), scheduleRebuild: true);
            RefreshUi();
        }, pickHelp);

        var resetButton = MakeInlineButton("Auto", (_, _) =>
        {
            MutateAnnotation(terrain.TerrainId, annotationId, applyAuto, scheduleRebuild: true);
            RefreshUi();
        }, autoHelp);

        var fields = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(summary, expand: true),
                pickButton,
                resetButton
            }
        };

        return new PropertyRow(
            CreateHelpLabel("Insertion", help, 0),
            fields,
            expandWidget: true);
    }

    // ---- Annotation-family twins of the analysis card infrastructure ----

    private void MutateAnnotation(
        Guid terrainId,
        Guid annotationId,
        Action<AnnotationDefinition> mutator,
        bool scheduleRebuild = false,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var annotation = terrain.Annotations.FirstOrDefault(item => item.Id == annotationId);
            if (annotation != null)
                mutator(annotation);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateAndRefreshAnnotation(Guid terrainId, Guid annotationId, Action<AnnotationDefinition> mutator)
    {
        MutateAnnotation(terrainId, annotationId, mutator, scheduleRebuild: false);
        RefreshTerrainPreview(terrainId);
    }

    private void AddAnnotation(string kind)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        MutateSelectedTerrain(terrain =>
        {
            AnnotationDefinition? annotation = AnnotationTypeRegistry.Create(
                kind,
                ModelUnitContext.FromDocument(doc));
            if (annotation != null)
                terrain.Annotations.Insert(0, annotation);
        }, scheduleRebuild: false);

        var terrain = _controller.GetSelectedTerrain(doc);
        if (terrain != null)
        {
            RefreshUi();
            RefreshTerrainPreview(terrain.TerrainId);
        }
    }

    private void RemoveAnnotation(Guid terrainId, Guid annotationId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Annotations.RemoveAll(item => item.Id == annotationId);
        }, scheduleRebuild: false);
        RefreshUi();
    }

    private void DuplicateAnnotation(Guid terrainId, Guid annotationId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
            _controller.DuplicateAnnotation(doc, terrainId, annotationId);
            RefreshUi();
        }
    }

    private static string GetAnnotationKind(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.Kind ?? string.Empty;

    private static string GetAnnotationIconLabel(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.IconLabel ?? "?";

    private static string? GetAnnotationIconName(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.IconName;

    private static string GetAnnotationSubtitle(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.Subtitle ?? string.Empty;

    private static Color AnnotationTypeColor(string kind)
    {
        var descriptor = AnnotationTypeRegistry.ForKind(kind);
        return descriptor == null ? UiTheme.MutedText : Color.FromArgb(descriptor.AccentArgb);
    }

    private Panel CreateAnnotationCard(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        bool collapsed = _collapsedAnalyses.Contains(annotation.Id);
        var collapseLabel = CreateCollapseChevron(collapsed);

        var nameBox = new TextBox { Text = annotation.Label };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Friendly annotation name shown in the panel.");
        BindCommittedText(nameBox, () => annotation.Label, text =>
            MutateAnnotation(terrain.TerrainId, annotation.Id, item => item.Label = text, scheduleRebuild: false));

        var enabledCheck = new CheckBox
        {
            Checked = annotation.IsEnabled
        };
        ApplyHelp(enabledCheck, "Enable or disable this annotation card without deleting it.");
        enabledCheck.CheckedChanged += (_, _) =>
        {
            MutateAnnotation(terrain.TerrainId, annotation.Id, item => item.IsEnabled = enabledCheck.Checked == true, scheduleRebuild: false);
            RefreshTerrainPreview(terrain.TerrainId);
        };

        string kind = GetAnnotationKind(annotation);
        string statusText = annotation.IsEnabled ? "Output" : "Disabled";
        var badge = CreateCardStatusLabel(statusText, UiTheme.MutedText);

        var handle = CreateReorderHandle(annotation.Id, "annotation-drag", "Drag to reorder this annotation.");

        var accent = AnnotationTypeColor(kind);
        var iconPlate = CreateIconPlate(accent,
            CreateCardIconControl(GetAnnotationIconName(annotation), GetAnnotationIconLabel(annotation)));

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            annotation.Label,
            AppendRuntimeDiagnosticSummary(
                GetAnnotationCollapsedSummary(terrain, annotation),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, annotation.Id)),
            nameBox,
            GetAnnotationSubtitle(annotation));

        var duplicateButton = MakeDuplicateIconButton(() =>
        {
            DuplicateAnnotation(terrain.TerrainId, annotation.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Duplicate this annotation card.");
        var deleteButton = MakeDeleteIconButton(() =>
        {
            RemoveAnnotation(terrain.TerrainId, annotation.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Delete this annotation card.");

        void ToggleCollapsed(bool ctrlHeld)
        {
            bool nowCollapsed = !_collapsedAnalyses.Contains(annotation.Id);
            if (ctrlHeld)
            {
                var doc2 = RhinoDoc.ActiveDoc;
                var t = doc2 == null ? null : _controller.GetSelectedTerrain(doc2);
                if (t != null)
                {
                    var visibleAnalyses = t.Annotations;

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
                    _collapsedAnalyses.Add(annotation.Id);
                else
                    _collapsedAnalyses.Remove(annotation.Id);
            }

            var doc = RhinoDoc.ActiveDoc;
            RebuildAnnotationLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
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
            Body = collapsed ? null : CreateAnnotationBody(terrain, annotation)
        });
    }

    private static string GetAnnotationCollapsedSummary(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, annotation.Id);
        return annotation switch
        {
            ContourAnnotationDefinition contour => summary != null
                ? $"{summary.ContourCurveCount} curves | {contour.Interval:G4} @ {contour.StartZ:G4}"
                : $"{contour.Interval:G4} every | start {contour.StartZ:G4}",
            CurveElevationLabelAnnotationDefinition curveElevation => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatAnalysisValue(summary.SampleAverageValue, curveElevation.ValueFormat)} avg"
                    : "0 labels"
                : $"{CountReferences(curveElevation.Sources)} refs | {curveElevation.Interval:G4} every",
            CurveSlopeLabelAnnotationDefinition curveSlope => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatSlopeValue(summary.SampleAverageValue, curveSlope.Unit)} avg"
                    : "0 labels"
                : $"{CountReferences(curveSlope.Sources)} refs | {curveSlope.Interval:G4} every",
            ProjectedElevationLabelAnnotationDefinition projectedElevation => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatAnalysisValue(summary.SampleMinValue, projectedElevation.ValueFormat)} to {FormatAnalysisValue(summary.SampleMaxValue, projectedElevation.ValueFormat)}"
                    : "0 labels"
                : $"{CountReferences(projectedElevation.Sources)} refs | projected Z",
            PointSlopeLabelAnnotationDefinition pointSlope => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatSlopeValue(summary.SampleAverageValue, pointSlope.Unit)} avg"
                    : "0 labels"
                : $"{CountReferences(pointSlope.Sources)} refs | terrain slope",
            TerrainSectionAnnotationDefinition section => summary != null
                ? $"{summary.GeneratedOutputCount} objects | {summary.SampleSourceCount} cuts"
                : $"{CountReferences(section.Sources)} refs | profile",
            CrossSectionStationAnnotationDefinition crossSection => summary != null
                ? $"{summary.GeneratedOutputCount} objects | {crossSection.StationInterval:G4} every"
                : $"{CountReferences(crossSection.Sources)} refs | {crossSection.StationInterval:G4} stations",
            LongitudinalSectionAnnotationDefinition longitudinal => summary != null
                ? $"{summary.GeneratedOutputCount} objects | V exag {longitudinal.VerticalExaggeration:G3}"
                : $"{CountReferences(longitudinal.Sources)} refs | sample {longitudinal.SampleInterval:G4}",
            ReportTableAnnotationDefinition => summary != null
                ? $"{summary.ReportTableCount} tables | {summary.ReportRowCount} rows"
                : "measured quantities",
            _ => string.Empty
        };
    }
}
