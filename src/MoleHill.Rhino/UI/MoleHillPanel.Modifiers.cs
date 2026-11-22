using Eto.Drawing;
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
        Guid terrainId = terrain.TerrainId, id = modifier.Id;
        string kind = GetModifierKind(modifier);
        string typeName = GetModifierTypeLabel(modifier);
        bool pinned = modifier is TriangulateModifierDefinition && terrain.Modifiers[0].Id == id;

        void Duplicate()
        {
            if (RhinoDoc.ActiveDoc is { } doc)
                _controller.DuplicateModifier(doc, terrainId, id);
        }

        void Delete()
        {
            if (RhinoDoc.ActiveDoc is { } doc)
                _controller.RemoveModifier(doc, terrainId, id);
        }

        return CreateCard(new CardSpec
        {
            Stack = _modifierCards,
            Id = id,
            Name = modifier.Label,
            NameHelp = "Modifier label. Press Enter or click away to rename.",
            Rename = text => MutateModifier(terrainId, id, item => item.Label = text, scheduleRebuild: false),
            IsEnabled = modifier.IsEnabled,
            EnableHelp = "Enable or disable this modifier without deleting it.",
            SetEnabled = value => MutateModifier(terrainId, id, item => item.IsEnabled = value),
            TypeName = typeName,
            TypeIcon = CreateCardIconControl(GetModifierIconName(kind), typeName[..1]),
            Summary = () => AppendRuntimeDiagnosticSummary(
                GetCollapsedSummary(modifier),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, id)),
            Description = GetModifierSubtitle(modifier, pinned),
            HandleHelp = pinned
                ? "The base triangulate modifier stays at the bottom of the stack."
                : "Drag to reorder this modifier.",
            Reorderable = !pinned,
            Status = pinned ? CreateCardStatusLabel("Pinned", ModifierTypeColor(kind)) : null,
            Duplicate = pinned ? null : (Duplicate, "Duplicate this modifier."),
            Delete = pinned ? null : (Delete, "Delete this modifier."),
            Body = () => CreateModifierBody(terrain, modifier),
            HeaderBackground = pinned ? UiTheme.BaseCardBackground : UiTheme.HeaderBackground,
            CardBackground = pinned ? UiTheme.BaseCardBackground : UiTheme.CardBackground
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

        // What the stage measured last run, for any modifier that reports (Displace's sampling check).
        ModifierStageReport? stageReport = RhinoDoc.ActiveDoc is { } reportDoc
            ? _controller.GetModifierStageReport(reportDoc, terrain.TerrainId, modifier.Id)
            : null;
        if (stageReport?.Warning is { } stageWarning)
            layout.AddRow(CreateCardWarning(stageWarning));

        if (modifier is GradePathModifierDefinition gradePath)
            layout.AddRow(CreateGradePathGeometryGroup(terrain, gradePath));

        if (modifier is ProjectToModifierDefinition projectTo)
            layout.AddRow(CreateProjectToTargetGroup(terrain, projectTo));

        if (TryBuildSchemaModifierBody(layout, terrain, modifier))
            AppendBespokeModifierRows(layout, terrain, modifier);

        AddResultRows(layout, GetModifierKind(modifier), () => stageReport?.Rows);

        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id));
        if (diagnosticsRow != null)
            layout.AddRow(diagnosticsRow);

        return layout;
    }

    private Control? CreateModifierMeshQualityWarning(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        // Round Creases splits its own band, so it needs no Remesh advice.
        if (modifier is not (SmoothModifierDefinition { Mode: not SmoothModes.Round } or SculptModifierDefinition))
            return null;

        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return null;

        string? warning = _controller.GetModifierMeshQualityWarning(doc, terrain.TerrainId, modifier.Id);
        return string.IsNullOrWhiteSpace(warning) ? null : CreateCardWarning(warning);
    }

    /// <summary>A card's warning surface: one wrapped line on the warning background.</summary>
    private static Control CreateCardWarning(string warning)
    {
        return new Panel
        {
            BackgroundColor = UiTheme.WarningBackground,
            Padding = new Padding(UiMetrics.SpaceLarge, UiMetrics.SpaceMedium),
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
                AppendTriangulateSections(layout, terrain, triangulate);
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
            Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceXSmall, UiMetrics.SpaceMedium, UiMetrics.SpaceMedium)
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

    /// <summary>
    /// Triangulate below its three everyday sources (design review UI-01 to UI-04): the rarer sources, the
    /// boundary roles and the settings each sit in a closed section, whose summary names what is set.
    /// </summary>
    private void AppendTriangulateSections(DynamicLayout layout, TerrainDefinition terrain, TriangulateModifierDefinition modifier)
    {
        const string Kind = "triangulate";

        static string? Named(params (string Name, bool Set)[] roles)
        {
            string[] set = roles.Where(role => role.Set).Select(role => role.Name).ToArray();
            return set.Length == 0 ? null : string.Join(", ", set);
        }

        var additional = new DynamicLayout { DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceMedium) };
        if (BuildBespokePositionedModifierRow(terrain, modifier, "TinMesh") is { } tinRow)
            additional.AddRow(tinRow);
        additional.AddRow(CreateDemSurfaceRow(terrain, modifier));
        layout.AddRow(new CardSection(Kind, "additionalSources", "Additional Sources", false,
            Named(("TIN", modifier.TinMesh.HasReferences), ("DEM", modifier.DemSurface.HasReferences)),
            additional));

        layout.AddRow(new CardSection(Kind, "boundaries", "Boundaries", false,
            Named(
                ("Outer", modifier.OuterBoundaries.HasReferences),
                ("Hide", modifier.HideBoundaries.HasReferences),
                ("Show", modifier.ShowBoundaries.HasReferences),
                ("Data Clip", modifier.DataClipBoundaries.HasReferences)),
            CreateBoundaryRolesGroup(terrain, modifier)));

        var settings = new DynamicLayout { DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceMedium) };
        if (BuildBespokePositionedModifierRow(terrain, modifier, "ContourMode") is { } contourModeRow)
            settings.AddRow(contourModeRow);
        settings.AddRow(CreateBoundaryPeelSettingsGroup(terrain, modifier));
        layout.AddRow(new CardSection(Kind, "settings", "Settings", false, null, settings));
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
            DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceMedium)
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

        return content;
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
            DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceMedium),
            Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceXSmall, UiMetrics.SpaceMedium, UiMetrics.SpaceMedium)
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

    private static Color ModifierTypeColor(string kind) =>
        UiTheme.TypeAccent(TerrainTypeRegistry.ForModifierKind(kind)?.AccentArgb);

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

    private static string GetCollapsedSummary(ModifierDefinition modifier) =>
        TerrainTypeRegistry.ForModifierType(modifier.GetType())?.Summarize(modifier) ?? string.Empty;
}
