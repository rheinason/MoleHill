using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;

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
        AppendAnnotationTextSizeRows(layout, terrain, annotation);
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

    /// <summary>
    /// What follows the schema rows. The insertion-origin pickers are the escape hatch: each needs an
    /// interactive GetPoint and, for the Legend, a <c>refreshOnly</c> callback, so they are real controls
    /// and cannot be rows. Everything the last build measured is data on the descriptor
    /// (<see cref="AnnotationTypeDescriptor.DescribeResult"/>) and is drawn generically below.
    /// </summary>
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
                break;
            }

            case LegendAnnotationDefinition legend:
            {
                layout.AddRow(CreateInsertionOriginEditor(
                    terrain,
                    legend.Id,
                    legend.HasInsertionPlane,
                    legend.InsertionOriginX,
                    legend.InsertionOriginY,
                    legend.InsertionOriginZ,
                    "Top-left corner of the drawn key. Auto places it beside the terrain, level with its foot.",
                    "Pick the top-left corner where the key will be drawn.",
                    "Clear the origin and let the key place itself beside the terrain.",
                    (item, picked) =>
                    {
                        if (item is not LegendAnnotationDefinition key)
                            return;

                        key.InsertionOriginX = picked.X;
                        key.InsertionOriginY = picked.Y;
                        key.InsertionOriginZ = picked.Z;
                        key.HasInsertionPlane = true;
                    },
                    item =>
                    {
                        if (item is LegendAnnotationDefinition key)
                            key.HasInsertionPlane = false;
                    },
                    refreshOnly: true));
                break;
            }
        }

        AddResultRows(
            layout,
            AnnotationTypeRegistry.ForType(annotation.GetType())?.DescribeResult(annotation, summary, ResultFormats));
    }

    /// <summary>The unit-aware formatters result rows need, bound to the active document and the user's slope unit.</summary>
    private static readonly ResultFormatter ResultFormats = new(FormatArea, FormatVolume, FormatZoneLength, FormatSlopeDegrees);

    private void AddResultRows(DynamicLayout layout, IReadOnlyList<ResultRow>? rows)
    {
        if (rows == null)
            return;

        foreach (ResultRow row in rows)
        {
            layout.AddRow(row.Kind == ResultRowKind.SelectableSummary
                ? CreateSelectableSummaryEditor(row.Label, row.Value, row.Help, row.MinHeight)
                : CreateReadOnlyValueRow(row.Label, row.Value, row.Help));
        }
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
        layout.AddRow(CreateCheckEditor(
            "Cut / Fill",
            annotation.ShowCutFillRegions,
            value => MutateSection(item => item.ShowCutFillRegions = value),
            "Shade cut and fill between this terrain and the ground it is compared to below."));

        // The same comparison rows, in the same order, as the Cut/Fill and Earthworks cards: a terrain
        // to compare to, then Rhino geometry that takes precedence over it.
        layout.AddRow(CreateCompareTerrainEditor(
            terrain,
            annotation.CutFillReferenceTerrainId,
            id => MutateSection(item => SetSectionCompareTerrain(item, id)),
            "Another terrain to draw on this section as existing ground, and to shade cut and fill against. " +
            "None shades against this terrain's own initial triangulation — the ground before any modifier moved it."));
        if (annotation.CutFillReferenceTerrainId is { } compareId)
        {
            if (annotation.CutFillReference.HasReferences)
            {
                layout.AddRow(CreateNoteRow(
                    "Drawn, but not used for cut/fill: the “Compare To” objects below take precedence."));
            }
            else if (CreateCompareTerrainStatusRow(
                         terrain,
                         compareId,
                         "its profile and the cut/fill shading are left off this section") is { } status)
            {
                layout.AddRow(status);
            }
        }

        layout.AddRow(CreateSourceEditor(
            "Compare To",
            annotation.CutFillReference,
            apply => MutateSection(item => apply(item.CutFillReference)),
            RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
            doc => _controller.GetSelectedLayerPaths(doc),
            "Existing ground as Rhino geometry — a survey mesh or surface — sliced along the same section " +
            "line. Takes precedence over Compare To Terrain for cut/fill."));
        AddSectionAlsoDrawRows(layout, terrain, annotation);

        // No hatch pattern, scale or angle rows: cut and fill appearance comes from the
        // SectionsCutFillCut/Fill layer roles (edited in the layer template), so preview and bake
        // match every other generated output. The definition's hatch fields remain only as the
        // legacy fallback the builder reads when a role supplies no pattern.
        layout.AddRow(CreateInsertionOriginEditor(terrain, annotation));
    }

    /// <summary>
    /// The compared terrain is drawn as well as compared against, so the dropdown owns that profile: the
    /// previous choice's profile goes with it, and the new one joins the drawn terrains (the cut/fill
    /// reference must be one of them — see <c>TerrainSerializer</c>).
    /// </summary>
    private static void SetSectionCompareTerrain(TerrainSectionAnnotationDefinitionBase section, Guid? terrainId)
    {
        if (section.CutFillReferenceTerrainId is { } previous)
            section.ComparisonTerrainIds.RemoveAll(id => id == previous);

        section.CutFillReferenceTerrainId = terrainId;
        if (terrainId is { } added && !section.ComparisonTerrainIds.Contains(added))
            section.ComparisonTerrainIds.Add(added);
    }

    /// <summary>
    /// Further terrains to draw on the section as profiles, without comparing against them. Shown only
    /// when there is another terrain to offer; most sections compare one proposed surface to one existing.
    /// </summary>
    private void AddSectionAlsoDrawRows(
        DynamicLayout layout,
        TerrainDefinition owner,
        TerrainSectionAnnotationDefinitionBase annotation)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);
        Guid? compareId = annotation.CutFillReferenceTerrainId;
        var others = terrains
            .Where(item => item.TerrainId != owner.TerrainId && item.TerrainId != compareId)
            .ToList();
        var drawn = annotation.ComparisonTerrainIds.Where(id => id != compareId).Distinct().ToList();
        if (others.Count == 0 && drawn.Count == 0)
            return;

        const string help = "Other terrains to draw on this section as profiles, in their own colours, without " +
            "comparing against them.";
        var list = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceXSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach (TerrainDefinition other in others)
        {
            Guid otherId = other.TerrainId;
            var check = new CheckBox { Text = other.Name, Checked = drawn.Contains(otherId) };
            ApplyHelp(check, help);
            check.CheckedChanged += (_, _) =>
            {
                if (_isRefreshing)
                    return;

                bool selected = check.Checked == true;
                MutateAnnotation(owner.TerrainId, annotation.Id, item =>
                {
                    if (item is not TerrainSectionAnnotationDefinitionBase section)
                        return;

                    section.ComparisonTerrainIds.RemoveAll(id => id == otherId);
                    if (selected)
                        section.ComparisonTerrainIds.Add(otherId);
                }, scheduleRebuild: true);
                RefreshUi();
            };
            var swatch = new Panel
            {
                Size = new Size(12, 12),
                BackgroundColor = ToEtoColor(System.Drawing.Color.FromArgb(other.TerrainColorArgb))
            };
            list.Items.Add(new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceSmall,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { swatch, check }
            });
        }

        layout.AddRow(new PropertyRow(CreateHelpLabel("Also Draw", help, 0), list, expandWidget: true));
        foreach (Guid drawnId in drawn)
        {
            if (CreateCompareTerrainStatusRow(owner, drawnId, "its profile is left off this section") is { } status)
                layout.AddRow(status);
        }
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
        Action<AnnotationDefinition> applyAuto,
        bool refreshOnly = false)
    {
        // Something drawn with the preview (the legend) only needs redrawing where it is; anything the
        // build lays out needs the build.
        void Commit(Action<AnnotationDefinition> apply)
        {
            if (refreshOnly)
                MutateAndRefreshAnnotation(terrain.TerrainId, annotationId, apply);
            else
                MutateAnnotation(terrain.TerrainId, annotationId, apply, scheduleRebuild: true);
        }

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
            Commit(item => applyPick(item, picked));
            RefreshUi();
        }, pickHelp);

        var resetButton = MakeInlineButton("Auto", (_, _) =>
        {
            Commit(applyAuto);
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
            LegendAnnotationDefinition => TerrainAnalysisPreviewBuilder.FindColoringAnalysis(terrain) is { } coloring
                && TerrainLegendBuilder.DescribeUnavailable(terrain) == null
                    ? $"keys {coloring.Label}"
                    : "nothing to key",
            _ => string.Empty
        };
    }
}
