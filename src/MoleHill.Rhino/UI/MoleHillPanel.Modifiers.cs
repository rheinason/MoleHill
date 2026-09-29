﻿using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

// Modifiers tab: modifier card + body shell, boundary-role inputs, boundary-peel editors.
public sealed partial class MoleHillPanel
{
    private Panel CreateModifierCard(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        bool collapsed = _collapsedModifiers.Contains(modifier.Id);
        var collapseLabel = CreateCollapseChevron(collapsed);

        var enabledCheck = new CheckBox { Checked = modifier.IsEnabled };
        enabledCheck.CheckedChanged += (_, _) =>
            MutateModifier(terrain.TerrainId, modifier.Id, item => item.IsEnabled = enabledCheck.Checked == true);

        var capturedModifierId = modifier.Id;
        var capturedTerrainId = terrain.TerrainId;
        string kind = GetModifierKind(modifier);
        string typeLabel = GetModifierTypeLabel(modifier);
        bool isPinnedBaseTriangulate = modifier is TriangulateModifierDefinition &&
                                       terrain.Modifiers.Count > 0 &&
                                       terrain.Modifiers[0].Id == modifier.Id;

        var nameBox = new TextBox { Text = modifier.Label };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Modifier label. Press Enter or click away to rename.");
        BindCommittedText(nameBox, () => modifier.Label, text =>
            MutateModifier(capturedTerrainId, capturedModifierId, item => item.Label = text, scheduleRebuild: false));

        var handle = isPinnedBaseTriangulate
            ? CreateDisabledReorderHandle("The base triangulate modifier stays at the bottom of the stack.")
            : CreateReorderHandle(capturedModifierId, "modifier-drag", "Drag to reorder this modifier.");

        var iconImage = PanelIcons.Load(GetModifierIconName(kind));
        Control iconControl = iconImage != null
            ? new ImageView { Image = iconImage, Size = new Size(UiMetrics.IconSize, UiMetrics.IconSize) }
            : new Label { Text = typeLabel[..1], VerticalAlignment = VerticalAlignment.Center };
        var accent = ModifierTypeColor(kind);
        var iconPlate = CreateIconPlate(accent, iconControl);

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            modifier.Label,
            AppendRuntimeDiagnosticSummary(
                GetCollapsedSummary(modifier),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id)),
            nameBox,
            GetModifierSubtitle(modifier, isPinnedBaseTriangulate));

        Control[] statusControls = isPinnedBaseTriangulate
            ? new Control[] { CreateCardStatusLabel("Pinned", accent) }
            : Array.Empty<Control>();

        Control[] actionControls = Array.Empty<Control>();
        if (!isPinnedBaseTriangulate)
        {
            actionControls = new Control[]
            {
                MakeDuplicateIconButton(() =>
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc != null)
                        _controller.DuplicateModifier(doc, capturedTerrainId, capturedModifierId);
                }, "Duplicate this modifier."),
                MakeDeleteIconButton(() =>
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc != null)
                        _controller.RemoveModifier(doc, capturedTerrainId, capturedModifierId);
                }, "Delete this modifier.")
            };
        }

        void ToggleCollapsed(bool ctrlHeld) => ToggleCardCollapsed(
            _collapsedModifiers,
            capturedModifierId,
            ctrlHeld,
            t => t.Modifiers.Select(m => m.Id),
            RebuildModifierLayout);

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = statusControls,
            ActionControls = actionControls,
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateModifierBody(terrain, modifier),
            HeaderBackground = isPinnedBaseTriangulate ? UiTheme.BaseCardBackground : UiTheme.HeaderBackground,
            CardBackground = isPinnedBaseTriangulate ? UiTheme.BaseCardBackground : UiTheme.CardBackground
        });
    }

    private Control CreateModifierBody(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        var layout = UiLayouts.CardBody();

        if (modifier is SculptModifierDefinition sculptModifier)
            layout.AddRow(CreateSculptSessionRow(terrain.TerrainId, sculptModifier.Id));

        var meshQualityWarning = CreateModifierMeshQualityWarning(terrain, modifier);
        if (meshQualityWarning != null)
            layout.AddRow(meshQualityWarning);

        if (modifier is GradePathModifierDefinition gradePath)
            layout.AddRow(CreateGradePathGeometryGroup(terrain, gradePath));

        if (modifier is ProjectToModifierDefinition projectTo)
            layout.AddRow(CreateProjectToTargetGroup(terrain, projectTo));

        if (TryBuildSchemaModifierBody(layout, terrain, modifier))
            AppendBespokeModifierRows(layout, terrain, modifier);

        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id));
        if (diagnosticsRow != null)
            layout.AddRow(diagnosticsRow);

        return layout;
    }

    private Control? CreateModifierMeshQualityWarning(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        if (modifier is not (SmoothModifierDefinition or SculptModifierDefinition))
            return null;

        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return null;

        string? warning = _controller.GetModifierMeshQualityWarning(doc, terrain.TerrainId, modifier.Id);
        if (string.IsNullOrWhiteSpace(warning))
            return null;

        return new Panel
        {
            BackgroundColor = UiTheme.WarningBackground,
            Padding = new Padding(8, 6),
            Content = new Label
            {
                Text = $"⚠ {warning}",
                TextColor = UiTheme.WarningText,
                Wrap = WrapMode.Word
            }
        };
    }

    /// <summary>
    /// Adds the few card rows that need custom placement or can't be expressed by the parameter schema
    /// (custom-draw escape hatch): the Triangulate boundary group, its deferred Contour Mode row, and
    /// the geometry-input boundary-peel group. No-op for other types.
    /// </summary>
    private void AppendBespokeModifierRows(DynamicLayout layout, TerrainDefinition terrain, ModifierDefinition modifier)
    {
        switch (modifier)
        {
            case TriangulateModifierDefinition triangulate:
                layout.AddRow(CreateDemSurfaceRow(terrain, triangulate));
                layout.AddRow(CreateBoundaryRolesGroup(terrain, triangulate));
                Control? contourModeRow = BuildBespokePositionedModifierRow(terrain, modifier, "ContourMode");
                if (contourModeRow != null)
                    layout.AddRow(contourModeRow);
                layout.AddRow(CreateBoundaryPeelSettingsGroup(terrain, triangulate));
                break;
            case AddGeometryModifierDefinition addGeometry:
                layout.AddRow(CreateBoundaryPeelSettingsGroup(terrain, addGeometry));
                break;
            case SculptModifierDefinition:
                break;
            case GradePathModifierDefinition:
                break;
        }
    }

    private Control CreateProjectToTargetGroup(TerrainDefinition owner, ProjectToModifierDefinition modifier)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);

        var options = new List<(string Key, string Label)> { ("", "None") };
        options.AddRange(terrains
            .Where(item => item.TerrainId != owner.TerrainId &&
                           !ProjectToTerrainDependsOn(terrains, item.TerrainId, owner.TerrainId, new HashSet<Guid>()))
            .Select(item => (item.TerrainId.ToString(), item.Name)));

        Control terrainEditor = CreateDropDownEditor(
            "Target Terrain",
            options,
            modifier.TargetTerrainId?.ToString() ?? "",
            value => MutateModifier(owner.TerrainId, modifier.Id, item =>
            {
                var projectTo = (ProjectToModifierDefinition)item;
                projectTo.TargetTerrainId = string.IsNullOrEmpty(value) ? null : Guid.Parse(value);
                if (projectTo.TargetTerrainId.HasValue)
                {
                    projectTo.TargetMesh.ReplaceObjects(Array.Empty<Guid>());
                    projectTo.TargetMesh.ReplaceLayers(Array.Empty<string>());
                }
            }),
            "Another MoleHill terrain's latest completed final mesh. Selecting it clears Target Mesh.");

        ModifierTypeDescriptor descriptor = TerrainTypeRegistry.ForModifierType(typeof(ProjectToModifierDefinition))!;
        ParameterDescriptor<ModifierDefinition> targetParameter = descriptor.Parameters.First(parameter => parameter.Key == "TargetMesh");
        Control meshEditor = CreateProjectToMeshTargetEditor(owner, modifier, targetParameter);

        var content = new DynamicLayout
        {
            DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceMedium),
            Padding = new Padding(6, 2, 6, 6)
        };
        content.AddRow(terrainEditor);
        content.AddRow(meshEditor);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(CreateSectionRule("Target"), HorizontalAlignment.Stretch),
                new StackLayoutItem(content, HorizontalAlignment.Stretch)
            }
        };
    }

    private Control CreateProjectToMeshTargetEditor(
        TerrainDefinition owner,
        ProjectToModifierDefinition modifier,
        ParameterDescriptor<ModifierDefinition> parameter)
    {
        RhinoDoc? activeDoc = RhinoDoc.ActiveDoc;
        Guid? liveTargetId = modifier.TargetMesh.ObjectIds
            .FirstOrDefault(id => activeDoc == null || activeDoc.Objects.FindId(id) != null);
        if (liveTargetId == Guid.Empty)
            liveTargetId = null;

        var pickButton = MakePillButton(
            liveTargetId.HasValue ? "1 Mesh" : "Select Mesh",
            "Select exactly one Rhino mesh target. Press Enter to accept.");
        pickButton.Click += (_, _) =>
        {
            RhinoDoc? doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            Application.Instance.AsyncInvoke(() =>
            {
                if (IsDisposed)
                    return;

                IReadOnlyList<Guid>? selected = _controller.EditSourceObjectIds(
                    doc,
                    modifier.TargetMesh.ObjectIds,
                    parameter.ObjectFilter,
                    "Select exactly one target mesh. Press Enter to accept.");
                if (selected == null)
                    return;

                Guid[] ids = selected.Where(id => id != Guid.Empty).Distinct().ToArray();
                if (ids.Length > 1)
                {
                    MessageBox.Show(this, "Project To accepts exactly one target mesh.", "Project To", MessageBoxButtons.OK, MessageBoxType.Warning);
                    return;
                }

                MutateModifier(owner.TerrainId, modifier.Id, item =>
                {
                    var projectTo = (ProjectToModifierDefinition)item;
                    projectTo.TargetMesh.ReplaceObjects(ids);
                    projectTo.TargetMesh.ReplaceLayers(Array.Empty<string>());
                    if (ids.Length == 1)
                        projectTo.TargetTerrainId = null;
                });
                RefreshUi();
            });
        };

        var controls = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(pickButton, expand: true) }
        };
        if (liveTargetId.HasValue)
        {
            controls.Items.Add(MakeIconButton(PanelButtonIcon.Clear, (_, _) =>
            {
                MutateModifier(owner.TerrainId, modifier.Id, item =>
                {
                    var projectTo = (ProjectToModifierDefinition)item;
                    projectTo.TargetMesh.ReplaceObjects(Array.Empty<Guid>());
                    projectTo.TargetMesh.ReplaceLayers(Array.Empty<string>());
                });
                RefreshUi();
            }, "Clear the target mesh."));
        }

        return new PropertyRow(
            CreateHelpLabel(parameter.Label, parameter.Help ?? "One Rhino mesh to project toward.", 0),
            controls,
            expandWidget: true);
    }

    private static bool ProjectToTerrainDependsOn(
        IReadOnlyList<TerrainDefinition> terrains,
        Guid terrainId,
        Guid soughtTerrainId,
        HashSet<Guid> visited)
    {
        if (terrainId == soughtTerrainId)
            return true;
        if (!visited.Add(terrainId))
            return false;

        TerrainDefinition? terrain = terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return false;

        foreach (Guid targetId in terrain.Modifiers
                     .OfType<ProjectToModifierDefinition>()
                     .Where(item => item.IsEnabled && !item.TargetMesh.HasReferences && item.TargetTerrainId.HasValue)
                     .Select(item => item.TargetTerrainId!.Value))
        {
            if (ProjectToTerrainDependsOn(terrains, targetId, soughtTerrainId, visited))
                return true;
        }

        return false;
    }

    private Control CreateDemSurfaceRow(TerrainDefinition terrain, TriangulateModifierDefinition modifier)
    {
        Control? sourceEditor = BuildBespokePositionedModifierRow(terrain, modifier, "DemSurface");
        string buttonText = modifier.DemSurface.HasReferences ? "Replace GeoTIFF..." : "Import GeoTIFF...";
        var importButton = MakeInlineButton(
            buttonText,
            (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                    return;

                if (DocumentCommandService.RunImportGeoTiffSurface(doc, terrain.TerrainId, modifier.Id) == global::Rhino.Commands.Result.Success)
                    RebuildModifierLayout(_controller.GetSelectedTerrain(doc));
            },
            "Create a georeferenced Rhino surface textured with a numeric single-band GeoTIFF and assign it as the DEM source.");

        var layout = new DynamicLayout { DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall) };
        if (sourceEditor != null)
            layout.AddRow(sourceEditor);
        layout.AddRow(new PropertyRow(
            CreateHelpLabel("GeoTIFF", "Import a textured surface, then move that surface to align geographic and project coordinates.", 0),
            importButton,
            expandWidget: true));
        return layout;
    }

    private Control CreateGradePathGeometryGroup(TerrainDefinition terrain, GradePathModifierDefinition modifier)
    {
        var content = new DynamicLayout
        {
            DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceMedium),
            Padding = new Padding(0)
        };
        // Constant-width first, then the opt-in toggle, then the rows it unlocks — so the card reads
        // "a path has a Width; variable width is something you switch on", not "fill in these curves".
        foreach (string key in new[] { "Paths", "Width", "UseVariableWidth", "WidthEdges", "MaxEdgeDistance" })
        {
            Control? row = BuildBespokePositionedModifierRow(terrain, modifier, key);
            if (row != null)
                content.AddRow(row);
        }
        // No box and no "Geometry" caption: these four rows are the first thing on a Grade Path card, so
        // the frame was drawing a border around "the inputs" and naming them after their data type. The
        // rows already say Centerlines / Width / Variable Width.
        return content;
    }

    private Control CreateSculptSessionRow(Guid terrainId, Guid modifierId)
    {
        var sculptButton = MakeToolbarButton("Start", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            Application.Instance.AsyncInvoke(() =>
            {
                if (IsDisposed)
                    return;

                SculptSessionController.Instance.BeginSession(doc, terrainId, modifierId);
            });
        }, "Start sculpting in the viewport. Drag to sculpt, Ctrl inverts, Shift smooths; choose Erase to remove strokes locally; Enter or Esc ends the session.");
        sculptButton.MinimumSize = new Size(0, UiMetrics.ControlHeight);
        sculptButton.BackgroundColor = UiTheme.ListSelectionBackground;

        var clearButton = MakeInlineButton("Clear", (_, _) =>
        {
            if (SculptSessionController.Instance.IsActive)
                return;

            var confirm = MessageBox.Show(
                this,
                "Delete all sculpt strokes stored on this modifier?",
                "Clear Sculpt",
                MessageBoxButtons.YesNo,
                MessageBoxType.Question);
            if (confirm != DialogResult.Yes)
                return;

            MutateModifier(terrainId, modifierId, item => ((SculptModifierDefinition)item).Tiles.Clear());
        }, "Delete every sculpt stroke stored on this modifier.");

        // A PropertyRow like the rest of the card, so Sculpt starts on the same column as Protect
        // and Feather below it instead of hanging off the end of its own caption.
        return new PropertyRow(
            CreateHelpLabel(
                "Sculpt",
                "Sculpt terrain heights by hand in the viewport, or clear every stored stroke.",
                0),
            new AdaptiveColumns(UiMetrics.SpaceSmall, UiMetrics.Chs(8), sculptButton, clearButton),
            expandWidget: true);
    }

    private Control CreateBoundaryRolesGroup(TerrainDefinition terrain, TriangulateModifierDefinition modifier)
    {
        var rectangleButton = MakeInlineButton("Rectangle", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            Application.Instance.AsyncInvoke(() =>
            {
                if (IsDisposed)
                    return;

                _controller.SetDataClipRectangle(doc, terrain.TerrainId, modifier.Id);
            });
        }, "Draw a closed rectangle and use it as the Data Clip object reference.");

        var content = new DynamicLayout
        {
            DefaultSpacing = new Size(6, 6),
            Padding = new Padding(6, 2, 6, 6)
        };
        foreach (string key in new[] { "OuterBoundaries", "HideBoundaries", "ShowBoundaries" })
        {
            Control? row = BuildBespokePositionedModifierRow(terrain, modifier, key);
            if (row != null)
                content.AddRow(row);
        }

        ModifierTypeDescriptor descriptor = TerrainTypeRegistry.ForModifierType(typeof(TriangulateModifierDefinition))!;
        ParameterDescriptor<ModifierDefinition>? dataClip = descriptor.Parameters.FirstOrDefault(parameter => parameter.Key == "DataClipBoundaries");
        if (dataClip != null)
        {
            content.AddRow(CreateSourceEditor(
                dataClip.Label,
                modifier.DataClipBoundaries,
                apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((TriangulateModifierDefinition)item).DataClipBoundaries)),
                dataClip.ObjectFilter,
                doc => _controller.GetSelectedLayerPaths(doc),
                dataClip.Help,
                rectangleButton));
        }

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(CreateSectionRule("Boundaries"), HorizontalAlignment.Stretch),
                new StackLayoutItem(content, HorizontalAlignment.Stretch)
            }
        };
    }

    /// <summary>
    /// The "Peel Border" group. The four rows are declared in
    /// <see cref="GeometryInputParameterCatalog.BoundaryPeel"/> and built through the shared schema row
    /// builder — this method owns only their <em>placement</em> inside the section rule. They were
    /// hand-written until the slope-unit work, which is how "Slope Limit" escaped the schema guard and
    /// shipped as an unlabelled degrees field.
    /// </summary>
    private Control CreateBoundaryPeelSettingsGroup(
        TerrainDefinition terrain,
        GeometryInputModifierDefinition modifier)
    {
        var settings = new DynamicLayout
        {
            DefaultSpacing = new Size(6, 6),
            Padding = new Padding(6, 2, 6, 6)
        };

        foreach (string key in GeometryInputParameterCatalog.BoundaryPeelKeys)
        {
            Control? row = BuildBespokePositionedModifierRow(terrain, modifier, key);
            if (row != null)
                settings.AddRow(row);
        }

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(CreateSectionRule("Peel Border"), HorizontalAlignment.Stretch),
                new StackLayoutItem(settings, HorizontalAlignment.Stretch)
            }
        };
    }

    private static Color ModifierTypeColor(string kind) => kind switch
    {
        "triangulate"    => Color.FromArgb(25, 118, 210),
        "add-geometry"   => Color.FromArgb(2, 136, 209),
        "remesh"         => Color.FromArgb(56, 142, 60),
        "smooth"         => Color.FromArgb(123, 31, 162),
        "project-to"     => Color.FromArgb(0, 121, 140),
        "retaining-wall" => Color.FromArgb(230, 74, 25),
        "simplify"       => Color.FromArgb(0, 137, 123),
        "grade-pad"      => Color.FromArgb(245, 124, 0),
        "grade-path"     => Color.FromArgb(93, 64, 55),
        "in-situ-stair"  => Color.FromArgb(0, 121, 107),
        _                => Color.FromArgb(120, 120, 120)
    };

    private static string GetModifierKind(ModifierDefinition modifier) =>
        TerrainTypeRegistry.ForModifierType(modifier.GetType())?.Kind ?? string.Empty;

    private static string GetModifierTypeLabel(ModifierDefinition modifier) =>
        TerrainTypeRegistry.ForModifierType(modifier.GetType())?.DisplayName ?? "Modifier";

    private static string GetModifierIconName(string kind) =>
        TerrainTypeRegistry.ForModifierKind(kind)?.IconName ?? "ModTriangulate";

    private static string GetModifierSubtitle(ModifierDefinition modifier, bool isPinnedBaseTriangulate)
    {
        if (isPinnedBaseTriangulate && modifier is TriangulateModifierDefinition)
            return "Base geometry";

        return TerrainTypeRegistry.ForModifierType(modifier.GetType())?.Subtitle ?? "Modifier";
    }

    private static int CountSources(SourceReferenceSet sources) =>
        sources.ObjectIds.Count + sources.LayerPaths.Count;

    private static string GetCollapsedSummary(ModifierDefinition modifier)
    {
        switch (modifier)
        {
            case TriangulateModifierDefinition t:
                return $"{CountSources(t.DemSurface)} DEM | {CountSources(t.Points)} points | " +
                    $"{CountSources(t.Breaklines)} breaklines | {CountSources(t.Contours)} contours";
            case AddGeometryModifierDefinition a:
                return $"{CountSources(a.Points)} points | {CountSources(a.Breaklines)} breaklines | " +
                    $"{CountSources(a.Contours)} contours";
            case RemeshModifierDefinition r:
                string remeshEdge = r.EdgeLength > 0 ? $"Edge Length {r.EdgeLength:G4}" : "Edge Length auto";
                return r.CreaseAngle > 0
                    ? $"{remeshEdge} | Crease Angle {r.CreaseAngle:G4} deg"
                    : remeshEdge;
            case RetopoModifierDefinition retopo:
                string retopoEdge = retopo.TargetEdgeLength > 0 ? $"Edge Length {retopo.TargetEdgeLength:G4}" : "Edge Length auto";
                return $"{retopoEdge} | {(retopo.Quads ? "Quads on" : "field preview")}";
            case SimplifyModifierDefinition simplify:
                return simplify.Mode switch
                {
                    SimplifyModifierDefinition.TargetVertexCountMode => $"At most {simplify.TargetVertexCount:N0} vertices",
                    SimplifyModifierDefinition.RetainPercentageMode => $"Retain {simplify.RetainPercentage:G4}%",
                    _ => $"Max Deviation {simplify.MaximumDeviation:G4}"
                };
            case SmoothModifierDefinition s:
                return $"{s.Iterations} iterations | Strength {s.Strength:G3} | {CountSources(s.Breaklines)} protect curves";
            case SculptModifierDefinition sculpt:
                return sculpt.Tiles.Count == 0
                    ? "no strokes"
                    : $"{sculpt.Tiles.Count} tiles | {CountSources(sculpt.Constraints)} protect curves";
            case ProjectToModifierDefinition projectTo:
                string target = projectTo.TargetMesh.HasReferences
                    ? "target mesh"
                    : projectTo.TargetTerrainId.HasValue ? "target terrain" : "no target";
                return $"{target} | {CountSources(projectTo.Boundaries)} boundaries | Strength {projectTo.Strength:G3}";
            case GradePadModifierDefinition p:
                return $"{CountSources(p.Boundaries)} boundaries | Fill {FormatSlopeDegrees(p.SlopeAngle)}";
            case GradePathModifierDefinition path:
                int paths = CountSources(path.Paths);
                if (!path.UseVariableWidth)
                    return $"{paths} centerlines | Width {path.Width:G4}";
                return $"{paths} centerlines | variable width, {CountSources(path.WidthEdges)} width edges | Width {path.Width:G4} fallback";
            case GradeLineModifierDefinition line:
                int lineCount = CountSources(line.Lines);
                return line.UseAsymmetricSides
                    ? $"{lineCount} design lines | asymmetric sides"
                    : $"{lineCount} design lines | Fill {FormatSlopeDegrees(line.SlopeAngle)}";
            case RetainingWallModifierDefinition w:
                int curves = CountSources(w.WallCurves);
                return w.GradesTerrain
                    ? $"{curves} wall curves | grade terrain, {(w.UseAsymmetricSides ? "asymmetric sides" : $"Fill {FormatSlopeDegrees(w.SlopeAngle)}")}"
                    : $"{curves} wall curves | breaklines only";
            case InSituStairModifierDefinition stair:
                int refs = CountSources(stair.ReferenceSurface);
                return !string.IsNullOrWhiteSpace(stair.ComputedTreadDepthSummary)
                    ? $"{stair.ComputedSurfaceCount ?? refs} surfaces | Tread Depth {stair.ComputedTreadDepthSummary}"
                    : $"{refs} reference surfaces | Riser Height {stair.RiserHeight:G4}";
            default:
                return string.Empty;
        }
    }
}
