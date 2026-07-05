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

        foreach (var descriptor in ObjectTypeRegistry.Objects)
        {
            string kind = descriptor.Kind;
            var item = new ButtonMenuItem { Text = descriptor.DisplayName, ToolTip = descriptor.Subtitle };
            item.Click += (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                    return;

                _controller.AddObjectDefinition(doc, terrain.TerrainId, kind);
                RebuildObjectsLayout(_controller.GetSelectedTerrain(doc));
            };
            menu.Items.Add(item);
        }

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
        var nameBox = new TextBox { Text = definition.Name };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Object definition label. Press Enter or click away to rename.");
        BindCommittedText(nameBox, () => definition.Name, text =>
            MutateObjectDefinition(capturedTerrainId, capturedDefinitionId, item => item.Name = text, scheduleRebuild: false));

        var handle = CreateDisabledReorderHandle("Object definition cards use the modifier card layout. Reordering is not enabled yet.");

        var accent = TerrainObjectTypeColor(kind);
        var iconPlate = CreateIconPlate(accent,
            CreateCardIconControl(GetTerrainObjectIconName(definition), GetTerrainObjectIconLabel(definition)));

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            definition.Name,
            GetTerrainObjectCollapsedSummary(definition),
            nameBox,
            GetTerrainObjectSubtitle(definition));

        var duplicateButton = MakeDuplicateIconButton(() =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.DuplicateObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Duplicate this object definition.");
        var deleteButton = MakeDeleteIconButton(() =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RemoveObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Delete this object definition and restore any currently placed objects.");

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
            StatusControls = Array.Empty<Control>(),
            ActionControls = new Control[]
            {
                duplicateButton,
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
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMinDegrees = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Minimum random rotation in degrees. Rotation is applied per object around its placement up axis and stays stable between rebuilds."));
        layout.AddRow(CreateSliderNumericEditor(
            "Rotate Max",
            definition.RandomRotationMaxDegrees,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMaxDegrees = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Maximum random rotation in degrees. Set min and max equal to disable rotation variation."));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Min",
            definition.RandomScaleMin,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMin = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Minimum random uniform scale. Scaling happens around the placement anchor and stays stable between rebuilds."));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Max",
            definition.RandomScaleMax,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMax = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Maximum random uniform scale. Set min and max to 1.0 for no scale variation."));
        layout.AddRow(CreateNumericEditor(
            "Seed",
            definition.RandomSeed,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomSeed = (int)Math.Round(value),
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            decimalPlaces: 0,
            help: "Stable random seed for this object card. Change it to reroll all matched objects.",
            liveEdit: true));
        layout.AddRow(CreateNumericEditor(
            "Z Offset",
            definition.ZOffset,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.ZOffset = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            decimalPlaces: 3,
            help: "Lift or sink placed objects. Project mode offsets in world Z; Surface mode offsets along the terrain normal.",
            minValue: null,
            liveEdit: true));
        layout.AddRow(CreateReadOnlyValueRow(
            "Bindings",
            $"{CountReferences(definition.Sources)} source refs",
            "Explicit picks plus watched layers drive the objects in this definition. Objects keep their original Rhino layers."));
        return layout;
    }

    private Control CreateScatterObjectBody(TerrainDefinition terrain, ScatterObjectDefinition scatter)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };

        // liveScrub defers the document save and the immediate panel refresh for continuous edits
        // (sliders + steppers), so dragging/typing doesn't write the .3dm every tick or tear the card
        // down mid-edit. The slider editor and the liveEdit numeric editor bracket the gesture in the
        // controller refresh deferral, which applies one refresh when the gesture ends.
        void Mutate(Action<ScatterObjectDefinition> apply, bool scheduleRebuild = true, bool liveScrub = false) =>
            MutateObjectDefinition(terrain.TerrainId, scatter.Id, item =>
            {
                if (item is ScatterObjectDefinition target)
                    apply(target);
            }, scheduleRebuild, deferDocumentSave: liveScrub, suppressImmediateUiRefresh: liveScrub);

        void MutateAndRefresh(Action<ScatterObjectDefinition> apply, bool scheduleRebuild = true)
        {
            Mutate(apply, scheduleRebuild);
            var doc = RhinoDoc.ActiveDoc;
            RebuildObjectsLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        }

        layout.AddRow(CreateDropDownEditor(
            "Source",
            new (string, string)[] { ("Region", "Within region"), ("Curve", "Along curve") },
            scatter.SourceMode.ToString(),
            key => MutateAndRefresh(s =>
            {
                s.SourceMode = Enum.Parse<ScatterSourceMode>(key);
                // Coerce density modes that don't apply to the new source so the dropdown stays valid.
                if (s.SourceMode == ScatterSourceMode.Curve && s.DensityMode == ScatterDensityMode.PerArea)
                    s.DensityMode = ScatterDensityMode.Count;
                if (s.SourceMode == ScatterSourceMode.Region && s.DensityMode == ScatterDensityMode.EdgeToEdge)
                    s.DensityMode = ScatterDensityMode.Spacing;
            }),
            "Region: fill inside closed boundaries. Curve: distribute along open curves."));

        if (scatter.SourceMode == ScatterSourceMode.Curve)
        {
            layout.AddRow(CreateSourceEditor("Curves", scatter.Paths,
                apply => Mutate(s => apply(s.Paths)),
                RhinoObjectType.Curve,
                doc => _controller.GetSelectedLayerPaths(doc),
                "Open curves and/or layers to distribute instances along."));
        }
        else
        {
            layout.AddRow(CreateSourceEditor("Boundaries", scatter.Boundaries,
                apply => Mutate(s => apply(s.Boundaries)),
                RhinoObjectType.Curve,
                doc => _controller.GetSelectedLayerPaths(doc),
                "Closed boundary curves and/or layers the scatter fills. Multiple boundaries are accepted."));
        }

        layout.AddRow(CreateScatterBlockMixHeader(scatter.Blocks.Count, (_, _) => AddScatterBlocksFromSelector(terrain, scatter.Id)));

        // Weighted block mix: keep the common name/weight/remove path compact, while legacy
        // instance-source entries still expose the source picker they were created with.
        for (int blockIndex = 0; blockIndex < scatter.Blocks.Count; blockIndex++)
        {
            int index = blockIndex;
            ScatterBlockEntry entry = scatter.Blocks[index];
            if (!string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
            {
                layout.AddRow(CreateScatterBlockRow(
                    entry.BlockDefinitionName!,
                    entry.Weight,
                    value => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].Weight = value; }),
                    () => Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); })));
            }
            else
            {
                var sourceEditor = CreateSourceEditor($"Block {index + 1}", entry.Source,
                    apply => Mutate(s => { if (index < s.Blocks.Count) apply(s.Blocks[index].Source); }),
                    RhinoObjectType.InstanceReference,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    "Block instance(s) to scatter for this entry.");
                layout.AddRow(CreateLegacyScatterBlockRow(
                    sourceEditor,
                    entry.Weight,
                    value => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].Weight = value; }),
                    () => Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); })));
            }
        }

        if (scatter.Blocks.Count == 0)
            layout.AddRow(CreateScatterBlockEmptyState());

        // A spatial "pattern" only makes sense filling a 2-D region; along a 1-D curve, evenness +
        // randomness + block order are the meaningful controls (added below in the curve block).
        if (scatter.SourceMode == ScatterSourceMode.Region)
        {
            layout.AddRow(CreateDropDownEditor(
                "Pattern",
                new (string, string)[] { ("Random", "Random"), ("Grid", "Grid"), ("JitteredGrid", "Jittered Grid"), ("PoissonDisk", "Poisson") },
                scatter.Pattern.ToString(),
                key => Mutate(s => s.Pattern = Enum.Parse<ScatterPattern>(key)),
                "How instances are arranged inside the boundary."));
        }

        bool curveMode = scatter.SourceMode == ScatterSourceMode.Curve;
        var densityOptions = curveMode
            ? new (string, string)[] { ("Count", "Total count"), ("Spacing", "Centre spacing"), ("EdgeToEdge", "Edge-to-edge") }
            : new (string, string)[] { ("Count", "Total count"), ("PerArea", "Per area"), ("Spacing", "Min spacing") };
        layout.AddRow(CreateDropDownEditor(
            "Density Mode",
            densityOptions,
            scatter.DensityMode.ToString(),
            key => MutateAndRefresh(s => s.DensityMode = Enum.Parse<ScatterDensityMode>(key)),
            curveMode
                ? "Count: total along the curve(s). Centre spacing: centre-to-centre distance. Edge-to-edge: by block footprint + gap."
                : "Count: a total number. Per area: instances per unit area. Min spacing: blue-noise radius."));

        switch (scatter.DensityMode)
        {
            case ScatterDensityMode.PerArea:
                layout.AddRow(CreateNumericEditor("Per Area", scatter.PerAreaDensity,
                    value => Mutate(s => s.PerAreaDensity = value, liveScrub: true), decimalPlaces: 4,
                    help: "Instances per square model unit.", minValue: 0.0, liveEdit: true));
                break;
            case ScatterDensityMode.Spacing:
                layout.AddRow(CreateNumericEditor("Spacing", scatter.Spacing,
                    value => Mutate(s => s.Spacing = value, liveScrub: true), decimalPlaces: 3,
                    help: curveMode
                        ? "Centre-to-centre distance between instances along the curve."
                        : "Minimum centre-to-centre distance between instances.",
                    minValue: 0.0, liveEdit: true));
                break;
            case ScatterDensityMode.EdgeToEdge:
                layout.AddRow(CreateNumericEditor("Edge Gap", scatter.EdgeGap,
                    value => Mutate(s => s.EdgeGap = value, liveScrub: true), decimalPlaces: 3,
                    help: "Gap left between block footprints (model units). Spacing follows each block's size.",
                    minValue: 0.0, liveEdit: true));
                break;
            default:
                layout.AddRow(CreateSliderNumericEditor("Count", scatter.Count,
                    value => Mutate(s => s.Count = value, liveScrub: true), softMin: 1.0, softMax: 1000.0,
                    decimalPlaces: 0, hardMin: 0.0, help: "Total number of instances to scatter."));
                break;
        }

        if (curveMode)
        {
            if (scatter.DensityMode != ScatterDensityMode.EdgeToEdge)
            {
                layout.AddRow(CreateSliderNumericEditor("Randomness", scatter.AlongJitter,
                    value => Mutate(s => s.AlongJitter = value, liveScrub: true), softMin: 0.0, softMax: 1.0,
                    decimalPlaces: 2, hardMin: 0.0, hardMax: 1.0,
                    help: "Along-curve randomness: 0 = perfectly even, 1 = each item may shift up to ±half the spacing."));
            }

            if (scatter.Blocks.Count > 1)
            {
                layout.AddRow(CreateDropDownEditor(
                    "Block order",
                    new (string, string)[] { ("Random", "Random (by weight)"), ("Sequence", "In sequence") },
                    scatter.BlockOrder.ToString(),
                    key => Mutate(s => s.BlockOrder = Enum.Parse<ScatterBlockOrder>(key)),
                    "Random: each slot picks a block by weight. Sequence: cycle the block list in order (A→B→C→A…)."));
            }

            layout.AddRow(CreateSliderNumericEditor("XY Jitter", scatter.JitterXy,
                value => Mutate(s => s.JitterXy = value, liveScrub: true), softMin: 0.0, softMax: 5.0,
                decimalPlaces: 3, hardMin: 0.0, help: "Random XY offset radius applied to each on-curve point (widens the line into a band)."));
            layout.AddRow(CreateCheckEditor("Align to tangent", scatter.AlignToTangent,
                value => Mutate(s => s.AlignToTangent = value),
                "Orient instances to follow the curve direction. Random rotation still adds on top."));
        }

        layout.AddRow(CreateCheckEditor("Align to slope", scatter.AlignToSlope,
            value => Mutate(s => s.AlignToSlope = value),
            "Orient instances to the terrain normal. When off, instances stay upright (world Z)."));

        layout.AddRow(CreateCheckEditor("Slope filter", scatter.SlopeFilterEnabled,
            value => MutateAndRefresh(s => s.SlopeFilterEnabled = value),
            "Only place instances where the terrain slope is within the range below."));
        if (scatter.SlopeFilterEnabled)
        {
            layout.AddRow(CreateSliderNumericEditor("Slope Min", scatter.SlopeMinDegrees,
                value => Mutate(s => s.SlopeMinDegrees = value, liveScrub: true), softMin: 0.0, softMax: 90.0,
                decimalPlaces: 1, hardMin: 0.0, hardMax: 90.0, help: "Minimum terrain slope in degrees."));
            layout.AddRow(CreateSliderNumericEditor("Slope Max", scatter.SlopeMaxDegrees,
                value => Mutate(s => s.SlopeMaxDegrees = value, liveScrub: true), softMin: 0.0, softMax: 90.0,
                decimalPlaces: 1, hardMin: 0.0, hardMax: 90.0, help: "Maximum terrain slope in degrees."));
        }

        layout.AddRow(CreateCheckEditor("Elevation filter", scatter.ElevationFilterEnabled,
            value => MutateAndRefresh(s => s.ElevationFilterEnabled = value),
            "Only place instances where the terrain elevation is within the range below."));
        if (scatter.ElevationFilterEnabled)
        {
            layout.AddRow(CreateNumericEditor("Elevation Min", scatter.ElevationMin,
                value => Mutate(s => s.ElevationMin = value, liveScrub: true), decimalPlaces: 3, help: "Minimum terrain elevation.", minValue: null, liveEdit: true));
            layout.AddRow(CreateNumericEditor("Elevation Max", scatter.ElevationMax,
                value => Mutate(s => s.ElevationMax = value, liveScrub: true), decimalPlaces: 3, help: "Maximum terrain elevation.", minValue: null, liveEdit: true));
        }

        layout.AddRow(CreateSliderNumericEditor("Rotate Min", scatter.RandomRotationMinDegrees,
            value => Mutate(s => s.RandomRotationMinDegrees = value, liveScrub: true), softMin: 0.0, softMax: 360.0,
            decimalPlaces: 1, hardMin: 0.0, hardMax: 360.0, help: "Minimum random rotation about the placement up axis."));
        layout.AddRow(CreateSliderNumericEditor("Rotate Max", scatter.RandomRotationMaxDegrees,
            value => Mutate(s => s.RandomRotationMaxDegrees = value, liveScrub: true), softMin: 0.0, softMax: 360.0,
            decimalPlaces: 1, hardMin: 0.0, hardMax: 360.0, help: "Maximum random rotation. Set equal to min to disable."));
        layout.AddRow(CreateSliderNumericEditor("Scale Min", scatter.RandomScaleMin,
            value => Mutate(s => s.RandomScaleMin = value, liveScrub: true), softMin: 0.25, softMax: 2.0,
            decimalPlaces: 3, hardMin: 0.01, help: "Minimum random uniform scale."));
        layout.AddRow(CreateSliderNumericEditor("Scale Max", scatter.RandomScaleMax,
            value => Mutate(s => s.RandomScaleMax = value, liveScrub: true), softMin: 0.25, softMax: 2.0,
            decimalPlaces: 3, hardMin: 0.01, help: "Maximum random uniform scale. Set both to 1 for no variation."));
        layout.AddRow(CreateNumericEditor("Z Offset", scatter.ZOffset,
            value => Mutate(s => s.ZOffset = value, liveScrub: true), decimalPlaces: 3,
            help: "Lift or sink instances along the placement up axis.", minValue: null, liveEdit: true));
        layout.AddRow(CreateNumericEditor("Seed", scatter.RandomSeed,
            value => Mutate(s => s.RandomSeed = (int)Math.Round(value), liveScrub: true), decimalPlaces: 0,
            help: "Stable random seed. Change it to reroll the whole scatter.", liveEdit: true));

        layout.AddRow(CreateDropDownEditor(
            "Preview",
            new (string, string)[] { ("Points", "Point cloud"), ("ShapePoints", "Shape points"), ("BoundingBox", "Bounding boxes"), ("Instances", "Real (capped)") },
            scatter.PreviewMode.ToString(),
            key => Mutate(s => s.PreviewMode = Enum.Parse<ScatterPreviewMode>(key), scheduleRebuild: false),
            "How the scatter draws while editing. Bake always produces real block instances."));
        layout.AddRow(CreateNumericEditor("Preview Cap", scatter.PreviewCap,
            value => Mutate(s => s.PreviewCap = (int)Math.Round(value), scheduleRebuild: false, liveScrub: true), decimalPlaces: 0,
            help: "Maximum scatter items drawn in live preview. Set to 0 for no cap.", minValue: 0.0, liveEdit: true));

        return layout;
    }

    private Control CreateScatterBlockRow(
        string blockName,
        double weight,
        Action<double> onWeightChanged,
        Action onRemove)
    {
        const string weightHelp = "Relative likelihood this block is chosen per instance.";
        const string removeHelp = "Remove this block from the scatter mix.";

        var nameLabel = new Label
        {
            Text = TruncateMiddle(blockName, 22),
            TextColor = UiTheme.InputText,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.None
        };
        ApplyHelp(nameLabel, blockName);

        var weightEditor = CreateCompactScatterWeightEditor(weight, onWeightChanged, weightHelp);
        var removeButton = MakeMiniButton("X", (_, _) => onRemove(), removeHelp, width: 28);

        var editor = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(weightEditor, expand: true),
                removeButton
            }
        };
        return new PropertyRow(nameLabel, editor, expandWidget: true);
    }

    private Control CreateScatterBlockMixHeader(int blockCount, EventHandler<EventArgs> addBlocks)
    {
        string countText = blockCount == 1 ? "1 block" : $"{blockCount} blocks";
        var label = new Label
        {
            Text = "Block Mix",
            TextColor = UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        var count = new Label
        {
            Text = countText,
            TextColor = UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        var addButton = MakeMiniButton("Add", addBlocks, "Pick block definitions to add to the weighted mix.", width: 44);

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Padding(0, 5, 0, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                label,
                new StackLayoutItem(new Panel(), expand: true),
                count,
                addButton
            }
        };
    }

    private static Control CreateScatterBlockEmptyState()
    {
        return new Panel
        {
            BackgroundColor = UiTheme.InputBackground,
            Padding = new Padding(8, 6),
            Content = new Label
            {
                Text = "No blocks selected",
                TextColor = UiTheme.MutedText,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private Control CreateLegacyScatterBlockRow(
        Control sourceEditor,
        double weight,
        Action<double> onWeightChanged,
        Action onRemove)
    {
        var weightEditor = CreateCompactScatterWeightEditor(
            weight,
            onWeightChanged,
            "Relative likelihood this legacy source block is chosen per instance.");
        var removeButton = MakeMiniButton("X", (_, _) => onRemove(), "Remove this block from the scatter mix.", width: 28);

        var controls = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(weightEditor, expand: true),
                removeButton
            }
        };

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(0, 3),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(sourceEditor, HorizontalAlignment.Stretch),
                new StackLayoutItem(controls, HorizontalAlignment.Stretch)
            }
        };
    }

    private Control CreateCompactScatterWeightEditor(double value, Action<double> onChanged, string help)
    {
        double currentMin = 0.0;
        double currentMax = 10.0;
        ExpandSliderRange(value, ref currentMin, ref currentMax);

        var slider = new Slider
        {
            MinValue = 0,
            MaxValue = 1000,
            Width = UiMetrics.SliderMin
        };

        var valueLabel = new Label
        {
            Width = UiMetrics.ShortLabel,
            TextColor = UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };

        ApplyHelp(slider, help);
        ApplyHelp(valueLabel, help);

        double committedValue = ClampSliderValue(value, 0.0, null);
        double pendingValue = committedValue;
        bool syncing = false;
        bool sliderEditActive = false;
        var timer = new UITimer { Interval = 0.12 };

        void BeginSliderEdit()
        {
            if (sliderEditActive)
                return;

            sliderEditActive = true;
            BeginControllerRefreshDeferral();
        }

        void EndSliderEdit()
        {
            if (!sliderEditActive)
                return;

            sliderEditActive = false;
            EndControllerRefreshDeferral();
        }

        void SyncControls(double numericValue)
        {
            numericValue = ClampSliderValue(numericValue, 0.0, null);
            syncing = true;
            ExpandSliderRange(numericValue, ref currentMin, ref currentMax);
            slider.Value = ToSliderValue(numericValue, currentMin, currentMax, slider.MaxValue);
            valueLabel.Text = FormatSliderValue(numericValue, 2);
            syncing = false;
        }

        void Commit(double numericValue)
        {
            timer.Stop();
            numericValue = ClampSliderValue(numericValue, 0.0, null);
            pendingValue = numericValue;
            SyncControls(numericValue);
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, numericValue))
                return;

            committedValue = numericValue;
            onChanged(numericValue);
        }

        timer.Elapsed += (_, _) => Commit(pendingValue);
        slider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                BeginSliderEdit();
        };
        slider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                EndSliderEdit();
        };
        slider.LostFocus += (_, _) => EndSliderEdit();
        slider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || syncing)
                return;

            BeginSliderEdit();
            pendingValue = FromSliderValue(slider.Value, currentMin, currentMax, slider.MaxValue);
            SyncControls(pendingValue);
            timer.Stop();
            timer.Start();
        };
        slider.UnLoad += (_, _) => timer.Stop();

        SyncControls(committedValue);
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(slider, expand: true),
                valueLabel
            }
        };
    }

    private static string TruncateMiddle(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength || maxLength < 5)
            return value;

        int left = (maxLength - 3) / 2;
        int right = maxLength - 3 - left;
        return string.Concat(value.AsSpan(0, left), "...", value.AsSpan(value.Length - right, right));
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
