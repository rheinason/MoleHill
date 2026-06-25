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

// Objects tab: add buttons, object/scatter card + body builders, scatter block selection.
public sealed partial class MoleHillPanel
{
    private Control BuildObjectAddButtons(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Object", (_, _) => { }, "Add a terrain object definition.", width: 100);
        var menu = new ContextMenu();

        var projectItem = new ButtonMenuItem
        {
            Text = "Plant"
        };
        projectItem.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.AddObjectDefinition(doc, terrain.TerrainId, "lowest-point");
        };
        menu.Items.Add(projectItem);

        var surfaceItem = new ButtonMenuItem
        {
            Text = "Orient"
        };
        surfaceItem.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.AddObjectDefinition(doc, terrain.TerrainId, "surface-oriented");
        };
        menu.Items.Add(surfaceItem);

        var scatterItem = new ButtonMenuItem
        {
            Text = "Scatter"
        };
        scatterItem.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.AddObjectDefinition(doc, terrain.TerrainId, "scatter");
        };
        menu.Items.Add(scatterItem);
        addButton.Click += (_, _) => menu.Show(addButton);

        return CreateSectionToolbar(
            "TERRAIN OBJECTS",
            addButton);
    }

    private Panel CreateObjectCard(TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        bool collapsed = _collapsedObjects.Contains(definition.Id);
        var collapseLabel = CreateCollapseChevron(collapsed);

        var enabledCheck = new CheckBox { Checked = definition.IsEnabled };
        enabledCheck.CheckedChanged += (_, _) =>
            MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.IsEnabled = enabledCheck.Checked == true);

        var capturedDefinitionId = definition.Id;
        var capturedTerrainId = terrain.TerrainId;
        string kind = GetTerrainObjectKind(definition);
        string typeLabel = GetTerrainObjectTypeLabel(definition);

        var nameBox = new TextBox { Text = definition.Name };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Object definition label. Press Enter or click away to rename.");
        BindCommittedText(nameBox, () => definition.Name, text =>
            MutateObjectDefinition(capturedTerrainId, capturedDefinitionId, item => item.Name = text, scheduleRebuild: false));

        var handle = CreateDisabledReorderHandle("Object definition cards use the modifier card layout. Reordering is not enabled yet.");

        var accent = TerrainObjectTypeColor(kind);
        var iconPlate = CreateIconPlate(accent, new Label
        {
            Text = GetTerrainObjectIconLabel(definition),
            VerticalAlignment = VerticalAlignment.Center
        });

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            definition.Name,
            GetTerrainObjectCollapsedSummary(definition),
            nameBox,
            GetTerrainObjectSubtitle(definition));

        var copyButton = MakeMiniButton("Copy", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.DuplicateObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Duplicate this object definition.", width: 46);
        var deleteButton = MakeMiniButton("Del", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RemoveObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Delete this object definition and restore any currently placed objects.", width: 38);

        void ToggleCollapsed(bool ctrlHeld) => ToggleCardCollapsed(
            _collapsedObjects,
            capturedDefinitionId,
            ctrlHeld,
            t => t.Objects.Select(item => item.Id),
            RebuildObjectsLayout);

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = new Control[]
            {
                CreateCardStatusLabel(typeLabel)
            },
            ActionControls = new Control[]
            {
                copyButton,
                deleteButton
            },
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateObjectBody(terrain, definition)
        });
    }

    private Control CreateObjectBody(TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        if (definition is ScatterObjectDefinition scatter)
            return CreateScatterObjectBody(terrain, scatter);

        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };
        layout.AddRow(CreateSourceEditor("Sources", definition.Sources,
            apply => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => apply(item.Sources)),
            0,
            doc => _controller.GetSelectedLayerPaths(doc)));
        layout.AddRow(CreateSliderNumericEditor(
            "Rotate Min",
            definition.RandomRotationMinDegrees,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMinDegrees = value),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Minimum random rotation in degrees. Rotation is applied per object around its placement up axis and stays stable between rebuilds."));
        layout.AddRow(CreateSliderNumericEditor(
            "Rotate Max",
            definition.RandomRotationMaxDegrees,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMaxDegrees = value),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Maximum random rotation in degrees. Set min and max equal to disable rotation variation."));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Min",
            definition.RandomScaleMin,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMin = value),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Minimum random uniform scale. Scaling happens around the placement anchor and stays stable between rebuilds."));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Max",
            definition.RandomScaleMax,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMax = value),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Maximum random uniform scale. Set min and max to 1.0 for no scale variation."));
        layout.AddRow(CreateNumericEditor(
            "Seed",
            definition.RandomSeed,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomSeed = (int)Math.Round(value)),
            decimalPlaces: 0,
            help: "Stable random seed for this object card. Change it to reroll all matched objects."));
        layout.AddRow(CreateNumericEditor(
            "Z Offset",
            definition.ZOffset,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.ZOffset = value),
            decimalPlaces: 3,
            help: "Lift or sink placed objects. Project mode offsets in world Z; Surface mode offsets along the terrain normal.",
            minValue: null));
        layout.AddRow(CreateReadOnlyValueRow(
            "Bindings",
            $"{CountReferences(definition.Sources)} source refs",
            "Explicit picks plus watched layers drive the objects in this definition. Objects keep their original Rhino layers."));
        return layout;
    }

    private Control CreateScatterObjectBody(TerrainDefinition terrain, ScatterObjectDefinition scatter)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };

        void Mutate(Action<ScatterObjectDefinition> apply, bool scheduleRebuild = true) =>
            MutateObjectDefinition(terrain.TerrainId, scatter.Id, item =>
            {
                if (item is ScatterObjectDefinition target)
                    apply(target);
            }, scheduleRebuild);

        layout.AddRow(CreateSourceEditor("Boundaries", scatter.Boundaries,
            apply => Mutate(s => apply(s.Boundaries)),
            RhinoObjectType.Curve,
            doc => _controller.GetSelectedLayerPaths(doc),
            "Closed boundary curves and/or layers the scatter fills. Multiple boundaries are accepted."));

        // Weighted block mix: one block + weight per entry, plus add/remove. Name-based entries (from
        // the block selector) show the block name; legacy instance-based entries keep a source editor.
        for (int blockIndex = 0; blockIndex < scatter.Blocks.Count; blockIndex++)
        {
            int index = blockIndex;
            ScatterBlockEntry entry = scatter.Blocks[index];
            if (!string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
            {
                layout.AddRow(CreateReadOnlyValueRow(
                    $"Block {index + 1}",
                    entry.BlockDefinitionName!,
                    "Block definition scattered for this entry."));
            }
            else
            {
                layout.AddRow(CreateSourceEditor($"Block {index + 1}", entry.Source,
                    apply => Mutate(s => { if (index < s.Blocks.Count) apply(s.Blocks[index].Source); }),
                    RhinoObjectType.InstanceReference,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    "Block instance(s) to scatter for this entry."));
            }

            layout.AddRow(CreateSliderNumericEditor(
                $"Weight {index + 1}",
                entry.Weight,
                value => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].Weight = value; }),
                softMin: 0.0,
                softMax: 10.0,
                decimalPlaces: 2,
                hardMin: 0.0,
                help: "Relative likelihood this block is chosen per instance."));
            layout.AddRow(MakeMiniButton($"Remove Block {index + 1}", (_, _) =>
                Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); }), "Remove this block from the mix.", width: 120));
        }

        layout.AddRow(MakeToolbarButton("Add Blocks…", (_, _) => AddScatterBlocksFromSelector(terrain, scatter.Id),
            "Pick block definitions to add to the weighted mix.", width: 120));

        layout.AddRow(CreateDropDownEditor(
            "Pattern",
            new (string, string)[] { ("Random", "Random"), ("Grid", "Grid"), ("JitteredGrid", "Jittered Grid"), ("PoissonDisk", "Poisson") },
            scatter.Pattern.ToString(),
            key => Mutate(s => s.Pattern = Enum.Parse<ScatterPattern>(key)),
            "How instances are arranged inside the boundary."));

        layout.AddRow(CreateDropDownEditor(
            "Density Mode",
            new (string, string)[] { ("Count", "Total count"), ("PerArea", "Per area"), ("Spacing", "Min spacing") },
            scatter.DensityMode.ToString(),
            key => Mutate(s => s.DensityMode = Enum.Parse<ScatterDensityMode>(key)),
            "Count: a total number. Per area: instances per unit area. Min spacing: blue-noise radius."));

        switch (scatter.DensityMode)
        {
            case ScatterDensityMode.PerArea:
                layout.AddRow(CreateNumericEditor("Per Area", scatter.PerAreaDensity,
                    value => Mutate(s => s.PerAreaDensity = value), decimalPlaces: 4,
                    help: "Instances per square model unit.", minValue: 0.0));
                break;
            case ScatterDensityMode.Spacing:
                layout.AddRow(CreateNumericEditor("Spacing", scatter.Spacing,
                    value => Mutate(s => s.Spacing = value), decimalPlaces: 3,
                    help: "Minimum centre-to-centre distance between instances.", minValue: 0.0));
                break;
            default:
                layout.AddRow(CreateSliderNumericEditor("Count", scatter.Count,
                    value => Mutate(s => s.Count = value), softMin: 1.0, softMax: 1000.0,
                    decimalPlaces: 0, hardMin: 0.0, help: "Total number of instances to scatter."));
                break;
        }

        layout.AddRow(CreateCheckEditor("Align to slope", scatter.AlignToSlope,
            value => Mutate(s => s.AlignToSlope = value),
            "Orient instances to the terrain normal. When off, instances stay upright (world Z)."));

        layout.AddRow(CreateCheckEditor("Slope filter", scatter.SlopeFilterEnabled,
            value => Mutate(s => s.SlopeFilterEnabled = value),
            "Only place instances where the terrain slope is within the range below."));
        if (scatter.SlopeFilterEnabled)
        {
            layout.AddRow(CreateSliderNumericEditor("Slope Min", scatter.SlopeMinDegrees,
                value => Mutate(s => s.SlopeMinDegrees = value), softMin: 0.0, softMax: 90.0,
                decimalPlaces: 1, hardMin: 0.0, hardMax: 90.0, help: "Minimum terrain slope in degrees."));
            layout.AddRow(CreateSliderNumericEditor("Slope Max", scatter.SlopeMaxDegrees,
                value => Mutate(s => s.SlopeMaxDegrees = value), softMin: 0.0, softMax: 90.0,
                decimalPlaces: 1, hardMin: 0.0, hardMax: 90.0, help: "Maximum terrain slope in degrees."));
        }

        layout.AddRow(CreateCheckEditor("Elevation filter", scatter.ElevationFilterEnabled,
            value => Mutate(s => s.ElevationFilterEnabled = value),
            "Only place instances where the terrain elevation is within the range below."));
        if (scatter.ElevationFilterEnabled)
        {
            layout.AddRow(CreateNumericEditor("Elevation Min", scatter.ElevationMin,
                value => Mutate(s => s.ElevationMin = value), decimalPlaces: 3, help: "Minimum terrain elevation.", minValue: null));
            layout.AddRow(CreateNumericEditor("Elevation Max", scatter.ElevationMax,
                value => Mutate(s => s.ElevationMax = value), decimalPlaces: 3, help: "Maximum terrain elevation.", minValue: null));
        }

        layout.AddRow(CreateSliderNumericEditor("Rotate Min", scatter.RandomRotationMinDegrees,
            value => Mutate(s => s.RandomRotationMinDegrees = value), softMin: 0.0, softMax: 360.0,
            decimalPlaces: 1, hardMin: 0.0, hardMax: 360.0, help: "Minimum random rotation about the placement up axis."));
        layout.AddRow(CreateSliderNumericEditor("Rotate Max", scatter.RandomRotationMaxDegrees,
            value => Mutate(s => s.RandomRotationMaxDegrees = value), softMin: 0.0, softMax: 360.0,
            decimalPlaces: 1, hardMin: 0.0, hardMax: 360.0, help: "Maximum random rotation. Set equal to min to disable."));
        layout.AddRow(CreateSliderNumericEditor("Scale Min", scatter.RandomScaleMin,
            value => Mutate(s => s.RandomScaleMin = value), softMin: 0.25, softMax: 2.0,
            decimalPlaces: 3, hardMin: 0.01, help: "Minimum random uniform scale."));
        layout.AddRow(CreateSliderNumericEditor("Scale Max", scatter.RandomScaleMax,
            value => Mutate(s => s.RandomScaleMax = value), softMin: 0.25, softMax: 2.0,
            decimalPlaces: 3, hardMin: 0.01, help: "Maximum random uniform scale. Set both to 1 for no variation."));
        layout.AddRow(CreateNumericEditor("Z Offset", scatter.ZOffset,
            value => Mutate(s => s.ZOffset = value), decimalPlaces: 3,
            help: "Lift or sink instances along the placement up axis.", minValue: null));
        layout.AddRow(CreateNumericEditor("Seed", scatter.RandomSeed,
            value => Mutate(s => s.RandomSeed = (int)Math.Round(value)), decimalPlaces: 0,
            help: "Stable random seed. Change it to reroll the whole scatter."));

        layout.AddRow(CreateDropDownEditor(
            "Preview",
            new (string, string)[] { ("Points", "Point cloud"), ("BoundingBox", "Bounding boxes"), ("Instances", "Real (capped)") },
            scatter.PreviewMode.ToString(),
            key => Mutate(s => s.PreviewMode = Enum.Parse<ScatterPreviewMode>(key), scheduleRebuild: false),
            "How the scatter draws while editing. Bake always produces real block instances."));
        layout.AddRow(CreateNumericEditor("Preview Cap", scatter.PreviewCap,
            value => Mutate(s => s.PreviewCap = (int)Math.Round(value), scheduleRebuild: false), decimalPlaces: 0,
            help: "Maximum real instances drawn in 'Real (capped)' preview mode.", minValue: 0.0));

        return layout;
    }

    private void AddScatterBlocksFromSelector(TerrainDefinition terrain, Guid scatterId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new List<(string Name, Eto.Drawing.Bitmap? Thumbnail)>();
        foreach (var definition in doc.InstanceDefinitions)
        {
            if (definition == null || definition.IsDeleted || string.IsNullOrWhiteSpace(definition.Name))
                continue;
            if (!seen.Add(definition.Name))
                continue;

            blocks.Add((definition.Name, BlockThumbnailRenderer.Get(definition, 40)));
        }

        blocks.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var selected = BlockSelectorDialog.Show(doc, blocks);
        if (selected == null || selected.Count == 0)
            return;

        MutateObjectDefinition(terrain.TerrainId, scatterId, item =>
        {
            if (item is not ScatterObjectDefinition scatter)
                return;

            var existing = new HashSet<string>(
                scatter.Blocks
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
                    .Select(entry => entry.BlockDefinitionName!),
                StringComparer.OrdinalIgnoreCase);

            foreach (string name in selected)
            {
                if (existing.Add(name))
                    scatter.Blocks.Add(new ScatterBlockEntry { BlockDefinitionName = name, Weight = 1.0 });
            }
        });
    }

}
