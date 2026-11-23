using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

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

        BuildSchemaModifierBody(layout, terrain, modifier);

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
    /// A Sources row that takes exactly one object and no layers (Project To's target mesh): a "Select"
    /// pill, "1 Object" once picked, and an x that clears it.
    /// </summary>
    private Control CreateSingleObjectEditor(
        string label,
        SourceReferenceSet sources,
        Action<Action<SourceReferenceSet>> mutate,
        RhinoObjectType objectFilter,
        string? help)
    {
        RhinoDoc? activeDoc = RhinoDoc.ActiveDoc;
        bool picked = sources.ObjectIds.Any(id => id != Guid.Empty && (activeDoc == null || activeDoc.Objects.FindId(id) != null));
        string noun = objectFilter == RhinoObjectType.Mesh ? "Mesh" : "Object";

        var pickButton = MakePillButton(
            picked ? $"1 {noun}" : $"Select {noun}",
            $"Select exactly one {noun.ToLowerInvariant()}. Press Enter to accept.");
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
                    sources.ObjectIds,
                    objectFilter,
                    $"Select exactly one {noun.ToLowerInvariant()}. Press Enter to accept.");
                if (selected == null)
                    return;

                Guid[] ids = selected.Where(id => id != Guid.Empty).Distinct().ToArray();
                if (ids.Length > 1)
                {
                    MessageBox.Show(this, $"{label} accepts exactly one {noun.ToLowerInvariant()}.", label, MessageBoxButtons.OK, MessageBoxType.Warning);
                    return;
                }

                mutate(set =>
                {
                    set.ReplaceObjects(ids);
                    set.ReplaceLayers(Array.Empty<string>());
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
        if (picked)
        {
            controls.Items.Add(MakeIconButton(PanelButtonIcon.Clear, (_, _) =>
            {
                mutate(set =>
                {
                    set.ReplaceObjects(Array.Empty<Guid>());
                    set.ReplaceLayers(Array.Empty<string>());
                });
                RefreshUi();
            }, $"Clear the {noun.ToLowerInvariant()}."));
        }

        return new PropertyRow(CreateHelpLabel(label, help ?? string.Empty, 0), controls, expandWidget: true);
    }

    /// <summary>
    /// A declared row's extra button (<see cref="RowAction"/>). The actions are document interactions, so
    /// they live here by key; the descriptor only names them.
    /// </summary>
    private Control? CreateRowActionButton<TDef>(RowAction action, TerrainDefinition terrain, TDef definition)
        where TDef : class
    {
        Guid ownerId = (definition as ModifierDefinition)?.Id ?? Guid.Empty;
        Action<RhinoDoc>? run = action.Key switch
        {
            "dataClipRectangle" => doc => _controller.SetDataClipRectangle(doc, terrain.TerrainId, ownerId),
            "importGeoTiff" => doc =>
            {
                if (DocumentCommandService.RunImportGeoTiffSurface(doc, terrain.TerrainId, ownerId) == global::Rhino.Commands.Result.Success)
                    RebuildModifierLayout(_controller.GetSelectedTerrain(doc));
            },
            _ => null,
        };
        if (run == null)
            return null;

        return MakeInlineButton(action.Label, (_, _) =>
        {
            if (RhinoDoc.ActiveDoc is not { } doc)
                return;

            // After the click returns, so the command's own prompts are not nested inside the button event.
            Application.Instance.AsyncInvoke(() =>
            {
                if (!IsDisposed)
                    run(doc);
            });
        }, action.Help);
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
