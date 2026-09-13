using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

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
        var sculptButton = MakeToolbarButton("Sculpt", (_, _) =>
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
        }, "Start sculpting in the viewport. Drag to sculpt, Ctrl inverts, Shift smooths; choose Erase to remove sculpting locally; Enter or Esc ends the session.");
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
        }, "Delete all stored sculpt displacement for this modifier.");

        // A PropertyRow like the rest of the card, so Sculpt starts on the same column as Constraints
        // and Feather below it instead of hanging off the end of its own caption.
        return new PropertyRow(
            CreateHelpLabel(
                "Sculpting",
                "Sculpt terrain elevation by hand in the viewport, or clear every stored stroke.",
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

}
