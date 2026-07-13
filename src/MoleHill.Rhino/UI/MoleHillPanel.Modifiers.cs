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

// Modifiers tab: modifier card + body shell, work-area picker, boundary-peel editors.
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
            ? new ImageView { Image = iconImage, Size = new Size(16, 16) }
            : new Label { Text = typeLabel[..1], VerticalAlignment = VerticalAlignment.Center };
        var accent = ModifierTypeColor(kind);
        var iconPlate = CreateIconPlate(accent, iconControl);

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            modifier.Label,
            GetCollapsedSummary(modifier),
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
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };

        var meshQualityWarning = CreateModifierMeshQualityWarning(terrain, modifier);
        if (meshQualityWarning != null)
            layout.AddRow(meshQualityWarning);

        if (TryBuildSchemaModifierBody(layout, terrain, modifier))
            AppendBespokeModifierRows(layout, terrain, modifier);

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
    /// Adds the few card rows that can't be expressed by the parameter schema (custom-draw escape hatch):
    /// the Triangulate work-area picker and the geometry-input boundary-peel block. Runs after the
    /// schema-generated rows so row order matches the former hand-written cards. No-op for other types.
    /// </summary>
    private void AppendBespokeModifierRows(DynamicLayout layout, TerrainDefinition terrain, ModifierDefinition modifier)
    {
        switch (modifier)
        {
            case TriangulateModifierDefinition triangulate:
                layout.AddRow(CreateWorkAreaRow(terrain.TerrainId, modifier.Id));
                AddBoundaryPeelEditors(layout, terrain, triangulate);
                break;
            case AddGeometryModifierDefinition addGeometry:
                AddBoundaryPeelEditors(layout, terrain, addGeometry);
                break;
            case SculptModifierDefinition:
                layout.AddRow(CreateSculptSessionRow(terrain.TerrainId, modifier.Id));
                break;
        }
    }

    private Control CreateSculptSessionRow(Guid terrainId, Guid modifierId)
    {
        var sculptButton = MakeInlineButton("Sculpt", (_, _) =>
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
        }, "Start sculpting in the viewport. Drag to sculpt, Ctrl inverts, Shift smooths; Enter or Esc ends the session.");

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

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Sculpting", VerticalAlignment = VerticalAlignment.Center },
                sculptButton,
                clearButton
            }
        };
    }

    private Control CreateWorkAreaRow(Guid terrainId, Guid modifierId)
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

                _controller.SetModifierBoundaryRectangle(doc, terrainId, modifierId);
            });
        }, "Drag a rectangle to limit terrain computation to that area (work fast on part of the terrain).");

        var clearButton = MakeInlineButton("Clear", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.ClearModifierBoundary(doc, terrainId, modifierId);
        }, "Clear the work area and rebuild the full terrain.");

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Work area", VerticalAlignment = VerticalAlignment.Center },
                rectangleButton,
                clearButton
            }
        };
    }

    private void AddBoundaryPeelEditors(DynamicLayout layout, TerrainDefinition terrain, GeometryInputModifierDefinition modifier)
    {
        layout.AddRow(CreateCheckEditor(
            "Peel Border",
            modifier.PeelBoundaryTriangles,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).PeelBoundaryTriangles = value),
            "Remove unwanted triangles only from the current TIN boundary. Interior faces are not candidates."));
        layout.AddRow(CreateNumericEditor(
            "Max Edge",
            modifier.MaxBoundaryEdgeLength,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).MaxBoundaryEdgeLength = value),
            help: "Boundary peeling edge threshold. 0 chooses an automatic threshold from mesh edge lengths.",
            minValue: 0));
        layout.AddRow(CreateNumericEditor(
            "Max Angle",
            modifier.MaxBoundaryAngleDegrees,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).MaxBoundaryAngleDegrees = value),
            help: "Boundary triangles with a longer-than-threshold edge and an interior angle at or above this value are peeled.",
            minValue: 0,
            maxValue: 180));
        layout.AddRow(CreateNumericEditor(
            "Slope Limit",
            modifier.MaxBoundarySlopeDegrees,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).MaxBoundarySlopeDegrees = value),
            help: "Boundary triangles with slope at or above this angle are peeled. 0 disables slope-based peeling.",
            minValue: 0,
            maxValue: 90));
    }

}
